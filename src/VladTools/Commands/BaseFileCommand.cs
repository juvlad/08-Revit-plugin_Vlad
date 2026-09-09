using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VladTools.Infrastructure;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// Loading a coordination ("base") file into a discipline model together with the whole
    /// matching setup: shared coordinates, the site name, the pin, switching to a workset.
    ///
    /// In Revit this is five actions scattered across the ribbon, and the order between them
    /// matters: the link first, then coordinates from it, then the site, and only after switching
    /// to "00_Shared levels and grids" — copying the levels and grids, so the copies land in the
    /// right workset. Each action on its own takes half a minute, but it is done in every
    /// discipline of every project, and a forgotten step surfaces a month later.
    ///
    /// **The button does not do the actual copy-monitoring, and cannot.** The Revit API has no way
    /// to create monitoring links at all — only to read ones that already exist
    /// (<c>Element.IsMonitoringLinkElement</c>, <c>GetMonitoredLinkElementIds</c>); checked by
    /// reflection against RevitAPI.dll 2022, 2024 and 2025. So as its last step the command opens
    /// "Copy/Monitor → Select Link" itself (a <c>PostableCommand</c>), and picking the link and the
    /// elements is left to the user.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BaseFileCommand : IExternalCommand
    {
        private const string DialogTitle = "Base File";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
                return Result.Cancelled;

            var doc = uidoc.Document;
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "The command works only in a project.\n" +
                    "Revit links cannot be inserted into the family editor.");
                return Result.Cancelled;
            }

            try
            {
                // The window needs the open model for its guess: the building number comes from
                // its name, and where to search for that building's base file comes from its folder.
                var window = new BaseFileWindow(
                    LinkCatalog.Existing(doc),
                    LinkCatalog.HostWorksets(doc),
                    LinkCatalog.Host(doc));
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var row = window.Selected;
                var preferences = window.Preferences;

                var done = new List<string>();
                var failures = new List<string>();
                var warnings = new WarningSuppressor();

                var target = WorksetId.InvalidWorksetId;

                using (var transaction = new Transaction(doc, "Base file"))
                {
                    transaction.Start();

                    // Linking a coordination file almost always arrives with warnings — about
                    // coordinates, duplicate names, the file version. A modal dialog for each
                    // would turn one button into clicking through dialogs.
                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(warnings);
                    transaction.SetFailureHandlingOptions(options);

                    var instanceId = Link(doc, row, preferences, done, failures);

                    if (instanceId != ElementId.InvalidElementId)
                    {
                        // Without a regeneration the just-created instance is not yet geometry as
                        // far as Revit is concerned, and "Acquire Coordinates" works with exactly that.
                        doc.Regenerate();

                        if (preferences.Acquire)
                            Acquire(doc, instanceId, done, failures);

                        if (preferences.Pin)
                            Pin(doc, instanceId, row.Name, done, failures);
                    }

                    if (preferences.Rename)
                        Rename(doc, preferences.Site, done, failures);

                    if (preferences.Activate)
                    {
                        if (doc.IsWorkshared)
                            target = Ensure(doc, preferences.Workset, done, failures);
                        else
                            failures.Add("Cannot switch to a workset: the project is not workshared.");
                    }

                    if (done.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                // The active workset is not part of the document's contents but session state:
                // Ctrl+Z must not restore it, so the switch happens after Commit.
                if (preferences.Activate)
                    Activate(doc, target, preferences.Workset, done, failures);

                if (preferences.Monitor)
                    OpenMonitor(commandData.Application, done, failures);

                Report(done, failures, warnings.Messages);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── the link ─────────────────────────────

        /// <summary>
        /// Sets up the link to the coordination file and returns its instance's
        /// <see cref="ElementId"/> — that is exactly what both "Acquire Coordinates" and the pin need.
        /// If a link to this model is already in the project — we take it: a repeated <c>Create</c>
        /// would just answer "such a link already exists" anyway.
        /// </summary>
        private static ElementId Link(
            Document doc,
            LinkRow row,
            BaseFilePreferences preferences,
            List<string> done,
            List<string> failures)
        {
            var placement = LinkCatalog.Placement(preferences.Placement);

            // The link's workset is not looked up in the ready list but created if it does not
            // exist: "01_Link_BM" has not been created yet in a new discipline, and the link would
            // silently land in the active workset.
            var workset = Ensure(doc, row.Entry.Workset, done, failures);

            if (row.IsExisting)
            {
                var instances = LinkCatalog.Instances(doc, row.ExistingId);
                var instance = instances.FirstOrDefault();
                if (instance != null)
                {
                    done.Add("The link \"" + row.Name + "\" is already in the project — using it");
                    Move(doc, row, instances, workset, done, failures);

                    return instance.Id;
                }

                // The link type is loaded, but there is no instance in the model — this happens
                // after "Delete" on an instance. We insert an instance, no need to touch the type.
                return Place(doc, row, row.ExistingId, placement, workset,
                    "The link \"" + row.Name + "\" was inserted (the type was already loaded)", done, failures);
            }

            try
            {
                // A relative path exists only for a file: for Revit Server and the cloud the path
                // is always absolute, and Revit simply will not accept a relative one there.
                using (var options = new RevitLinkOptions(row.Entry.Origin == LinkOrigin.File))
                {
                    var result = RevitLinkType.Create(doc, LinkCatalog.ToModelPath(row.Entry), options);

                    if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                    {
                        failures.Add(row.Name + " — " + LinkCatalog.Describe(result.LoadResult));
                        return ElementId.InvalidElementId;
                    }

                    return Place(doc, row, result.ElementId, placement, workset,
                        "The link \"" + row.Name + "\" was loaded", done, failures);
                }
            }
            catch (Exception exception)
            {
                failures.Add(row.Name + " — could not be linked: " + LinkCatalog.Short(exception.Message));
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>
        /// Moves a link that is already in place to the workset chosen in the window. This cannot
        /// be silently skipped: the workset is shown in the window and can be changed, and a skipped
        /// edit would look like a completed one. Every instance of the link and its type move —
        /// the same as when it is created.
        /// </summary>
        private static void Move(
            Document doc,
            LinkRow row,
            IReadOnlyList<RevitLinkInstance> instances,
            WorksetId workset,
            List<string> done,
            List<string> failures)
        {
            if (workset == WorksetId.InvalidWorksetId)
                return;

            var moved = instances.Count(instance => LinkCatalog.Place(instance, workset, failures, row.Name));
            LinkCatalog.Place(doc.GetElement(row.ExistingId), workset, failures, row.Name);

            if (moved > 0)
                done.Add("The link \"" + row.Name + "\" was moved to the \"" + row.Entry.Workset + "\" workset");
        }

        /// <summary>Inserts a link instance and puts it into the chosen project workset.</summary>
        private static ElementId Place(
            Document doc,
            LinkRow row,
            ElementId typeId,
            ImportPlacement placement,
            WorksetId workset,
            string success,
            List<string> done,
            List<string> failures)
        {
            try
            {
                var instance = RevitLinkInstance.Create(doc, typeId, placement);

                // The workset is set on the elements that were just created, rather than through
                // the document's active workset: that way the link lands wherever it was asked to,
                // regardless of where the user is standing. Both the instance and the type are
                // placed — Revit itself does the same.
                if (workset != WorksetId.InvalidWorksetId)
                {
                    LinkCatalog.Place(instance, workset, failures, row.Name);
                    LinkCatalog.Place(doc.GetElement(typeId), workset, failures, row.Name);
                }

                done.Add(success);
                return instance.Id;
            }
            catch (Exception exception)
            {
                failures.Add(row.Name + " — could not insert the link: " + LinkCatalog.Short(exception.Message));
                return ElementId.InvalidElementId;
            }
        }

        // ───────────────────────────── project setup ─────────────────────────────

        /// <summary>
        /// "Coordinates → Acquire Coordinates": the project's shared coordinate system becomes the
        /// same as the base file's.
        /// </summary>
        private static void Acquire(Document doc, ElementId instanceId, List<string> done, List<string> failures)
        {
            try
            {
                doc.AcquireCoordinates(instanceId);
                done.Add("Shared coordinates acquired from the base file");
            }
            catch (Exception exception)
            {
                failures.Add("Could not acquire the shared coordinates: " + LinkCatalog.Short(exception.Message));
            }
        }

        /// <summary>
        /// Renaming the project site. The site name must be unique among the project's sites — a
        /// Revit refusal goes into the report, and it does not derail the other steps.
        /// </summary>
        private static void Rename(Document doc, string name, List<string> done, List<string> failures)
        {
            try
            {
                var location = doc.ActiveProjectLocation;
                if (location == null)
                {
                    failures.Add("Could not rename the site: the project has no active site.");
                    return;
                }

                if (string.Equals(location.Name, name, StringComparison.CurrentCulture))
                {
                    done.Add("The project site is already named \"" + name + "\"");
                    return;
                }

                location.Name = name;
                done.Add("The project site was renamed to \"" + name + "\"");
            }
            catch (Exception exception)
            {
                failures.Add("Could not rename the site: " + LinkCatalog.Short(exception.Message));
            }
        }

        /// <summary>
        /// Pinning the link. The base file is inserted by coordinates, and an accidental drag with
        /// the mouse is later hunted down by the whole team; removing a pin in Revit is one button,
        /// putting a link that drifted back in place is not.
        /// </summary>
        private static void Pin(Document doc, ElementId instanceId, string name, List<string> done, List<string> failures)
        {
            try
            {
                var instance = doc.GetElement(instanceId);
                if (instance == null)
                    return;

                if (instance.Pinned)
                {
                    done.Add("The link \"" + name + "\" is already pinned");
                    return;
                }

                instance.Pinned = true;
                done.Add("The link \"" + name + "\" was pinned");
            }
            catch (Exception exception)
            {
                failures.Add("Could not pin the link: " + LinkCatalog.Short(exception.Message));
            }
        }

        // ───────────────────────────── workset ─────────────────────────────

        /// <summary>
        /// Looks for a workset by name, and creates it if it is missing. Shared by both worksets
        /// the window uses: the one the link goes into ("01_Link_BM") and the one the user switches
        /// to ("00_Shared levels and grids"). Creating it here is not an overreach: a new discipline
        /// has neither one yet, and a silent skip would mean the button failed at its main job —
        /// the link would land in the active workset, and there would be nothing to switch to.
        ///
        /// An empty name, or a non-workshared project, is not a failure but "no workset is needed":
        /// whoever asked for the workset says so about a non-workshared project.
        /// </summary>
        private static WorksetId Ensure(Document doc, string name, List<string> done, List<string> failures)
        {
            if (!doc.IsWorkshared || string.IsNullOrEmpty(name))
                return WorksetId.InvalidWorksetId;

            WorksetId existing;
            if (LinkCatalog.WorksetIds(doc).TryGetValue(name, out existing))
                return existing;

            try
            {
                if (!WorksetTable.IsWorksetNameUnique(doc, name))
                {
                    failures.Add("Cannot create the workset \"" + name + "\": the name is already taken.");
                    return WorksetId.InvalidWorksetId;
                }

                var created = Workset.Create(doc, name);
                done.Add("Created the workset \"" + name + "\"");

                return created.Id;
            }
            catch (Exception exception)
            {
                failures.Add("Could not create the workset \"" + name + "\": " + LinkCatalog.Short(exception.Message));
                return WorksetId.InvalidWorksetId;
            }
        }

        /// <summary>
        /// Switching to a workset. This is the whole point of it all: the levels and grids copied
        /// next will land in whichever workset is active at the moment of the copy.
        /// </summary>
        private static void Activate(Document doc, WorksetId workset, string name, List<string> done, List<string> failures)
        {
            // The workset was not found and could not be created — the reason is already in the
            // report, no need to say it twice.
            if (workset == WorksetId.InvalidWorksetId)
                return;

            try
            {
                doc.GetWorksetTable().SetActiveWorksetId(workset);
                done.Add("Active workset — \"" + name + "\"");
            }
            catch (Exception exception)
            {
                failures.Add("Could not switch to the workset \"" + name + "\": " +
                             LinkCatalog.Short(exception.Message));
            }
        }

        // ───────────────────────────── copy-monitoring ─────────────────────────────

        /// <summary>
        /// Opens "Copy/Monitor → Select Link".
        ///
        /// Automation cannot go further than this: the Revit API cannot create monitoring links.
        /// The command is queued with Revit and fires after the report closes — all that is left
        /// for the user is to click the link and check off the levels and grids.
        /// </summary>
        private static void OpenMonitor(UIApplication application, List<string> done, List<string> failures)
        {
            try
            {
                var command = RevitCommandId.LookupPostableCommandId(PostableCommand.CopyMonitorSelectLink);
                if (command == null || !application.CanPostCommand(command))
                {
                    failures.Add("\"Copy/Monitor\" is not available right now — " +
                                 "switch to a floor plan and start it by hand.");
                    return;
                }

                application.PostCommand(command);
                done.Add("Opening \"Copy/Monitor → Select Link\": " +
                         "pick the base file and check off the levels and grids");
            }
            catch (Exception exception)
            {
                failures.Add("Could not open \"Copy/Monitor\": " +
                             LinkCatalog.Short(exception.Message));
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(
            IReadOnlyList<string> done,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
        {
            const int limit = 15;

            var text = done.Count == 0
                ? "Nothing was done."
                : "Done:\n• " + string.Join("\n• ", done);

            if (failures.Count > 0)
            {
                text += "\n\nCould not be done (" + failures.Count + "):\n• " +
                        string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… and " + (failures.Count - limit) + " more";
            }

            if (warnings.Count > 0)
            {
                text += "\n\nRevit warned (" + warnings.Count + "):\n• " +
                        string.Join("\n• ", warnings.Take(limit));

                if (warnings.Count > limit)
                    text += "\n… and " + (warnings.Count - limit) + " more";
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
