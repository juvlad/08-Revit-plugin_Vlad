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
    /// Accepts changes from the coordination file: puts the project's grids and levels where they
    /// now stand in the link, and renames them to follow it.
    ///
    /// In Revit this is "Collaborate → Coordination Review → Select Link" and then one change at a
    /// time: expand a node, pick an action, repeat. On a building where a dozen grids have shifted
    /// this is dozens of clicks, and it is done again after every new base file is issued.
    ///
    /// **The button cannot press "Accept" inside "Coordination Review" itself.** That dialog does
    /// not exist in the Revit API at all: of the whole monitoring feature only
    /// <c>Element.IsMonitoringLinkElement</c>, <c>IsMonitoringLocalElement</c>,
    /// <c>GetMonitoredLinkElementIds</c> and <c>GetMonitoredLocalElementIds</c> are exposed —
    /// checked by reflection against RevitAPI.dll 2022, 2024 and 2025, and there is neither a way
    /// to read the change list nor to act on it. So the command comes at this from the other side:
    /// it computes the differences itself (<see cref="CoordinationCatalog"/>) and moves the
    /// elements itself — that is, it does exactly what the "Move" action in the dialog would do.
    /// Once an element is back in place, Revit stops counting it as a difference, and
    /// "Coordination Review" empties itself.
    ///
    /// What the command does not do: it does not delete grids and levels that vanished from the
    /// coordination file (a level would take everything standing on it with it), and it does not
    /// set up monitoring on new link elements — creating monitoring links is not in the API either.
    /// Both are shown in the window as a line, to be sorted out by hand.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AcceptCoordinationCommand : IExternalCommand
    {
        private const string DialogTitle = "Accept Coordination Changes";

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
                    "Coordination-file monitoring lives in a discipline model, not in a family.");
                return Result.Cancelled;
            }

            try
            {
                var failures = new List<string>();
                var scans = CoordinationCatalog.Scan(doc, failures);

                if (scans.Count == 0)
                {
                    TaskDialog.Show(DialogTitle,
                        "The project has no grid or level monitoring a link.\n\n" +
                        "There is nothing to compare against: the button works off monitoring links, and " +
                        "those are only ever set up by hand — \"Collaborate → Copy/Monitor → Select Link\" " +
                        "(the \"Base File\" button also opens this mode as its last step)." +
                        Note(failures));
                    return Result.Cancelled;
                }

                var window = new AcceptCoordinationWindow(scans);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var rows = window.Selected;
                var chosen = window.Chosen;

                var done = new List<string>();
                var warnings = new WarningSuppressor();

                using (var transaction = new Transaction(doc, "Accept coordination changes"))
                {
                    transaction.Start();

                    // Shifting a level drags everything standing on it along, and Revit will almost
                    // certainly warn about something: broken joins, elements left behind. A modal
                    // dialog for each would turn one button into clicking through dialogs.
                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(warnings);
                    transaction.SetFailureHandlingOptions(options);

                    foreach (var row in rows.Where(item => item.Kind == CoordinationChangeKind.Position))
                        Move(doc, row, done, failures);

                    Rename(doc, rows.Where(item => item.Kind == CoordinationChangeKind.Name).ToList(), done, failures);

                    if (done.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                if (window.OpenReview)
                    OpenReview(commandData.Application, done, failures);

                Report(chosen, done, failures, warnings.Messages);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── the edit ─────────────────────────────

        /// <summary>
        /// Puts a grid or a level where it now stands in the link.
        ///
        /// For a grid this is a rotation about its midpoint plus a sideways shift — it is the
        /// infinite line that is aligned, not the segment: a grid's length in the project is
        /// trimmed to fit its own views and has nothing to do with coordination. For a level it is
        /// simply a new elevation.
        /// </summary>
        private static void Move(Document doc, CoordinationChangeRow row, List<string> done, List<string> failures)
        {
            var element = doc.GetElement(row.Update.HostId);
            if (element == null)
            {
                failures.Add(row.Title + " — this element is no longer in the project");
                return;
            }

            var pinned = false;

            try
            {
                // The grids and levels of a base file are usually pinned — otherwise someone drags
                // them with the mouse. Revit will not move a pinned element, so the pin is removed
                // for the duration of the edit and put back right after.
                pinned = element.Pinned;
                if (pinned)
                    element.Pinned = false;

                var level = element as Level;
                if (level != null)
                {
                    level.Elevation = row.Update.Elevation;
                }
                else
                {
                    if (row.Update.Angle != 0)
                    {
                        var axis = Line.CreateBound(row.Update.Center, row.Update.Center + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, element.Id, axis, row.Update.Angle);
                    }

                    if (row.Update.Translation != null)
                        ElementTransformUtils.MoveElement(doc, element.Id, row.Update.Translation);
                }

                done.Add(row.Title + " — " + row.Detail);
            }
            catch (Exception exception)
            {
                failures.Add(row.Title + " — could not be repositioned: " + LinkCatalog.Short(exception.Message));
            }
            finally
            {
                Repin(element, pinned, failures, row.Title);
            }
        }

        /// <summary>
        /// Puts the pin back. A failure here must not wreck the rest: the element is already in
        /// the right place, just unpinned — and that has to be said, not passed over in silence.
        /// </summary>
        private static void Repin(Element element, bool pinned, List<string> failures, string title)
        {
            if (!pinned)
                return;

            try
            {
                element.Pinned = true;
            }
            catch (Exception exception)
            {
                failures.Add(title + " — the pin did not come back: " + LinkCatalog.Short(exception.Message));
            }
        }

        /// <summary>
        /// Renames grids and levels to follow the link — in several passes, as with deleting
        /// parameters: while a name is held by a neighbour that is also being renamed, Revit will
        /// not release it. A pass without a single success means the trouble is not the queue —
        /// then it goes into the report.
        /// </summary>
        private static void Rename(
            Document doc,
            IReadOnlyList<CoordinationChangeRow> rows,
            List<string> done,
            List<string> failures)
        {
            var left = rows.ToList();

            while (left.Count > 0)
            {
                var stuck = new List<CoordinationChangeRow>();
                var reasons = new Dictionary<CoordinationChangeRow, string>();

                foreach (var row in left)
                {
                    var element = doc.GetElement(row.Update.HostId);
                    if (element == null)
                    {
                        failures.Add(row.Title + " — this element is no longer in the project");
                        continue;
                    }

                    try
                    {
                        element.Name = row.Update.NewName;
                        done.Add(row.Title + " — renamed to \"" + row.Update.NewName + "\"");
                    }
                    catch (Exception exception)
                    {
                        stuck.Add(row);
                        reasons[row] = LinkCatalog.Short(exception.Message);
                    }
                }

                if (stuck.Count == left.Count)
                {
                    foreach (var row in stuck)
                        failures.Add(row.Title + " — could not be renamed: " + reasons[row]);

                    return;
                }

                left = stuck;
            }
        }

        // ───────────────────────────── coordination review ─────────────────────────────

        /// <summary>
        /// Opens "Coordination Review → Select Link".
        ///
        /// Not a continuation of the work but a check on it: nothing in this dialog can be pressed
        /// through the API, but once the elements are back in line with the file, its list should
        /// be empty. The command is queued with Revit and fires after the report closes.
        /// </summary>
        private static void OpenReview(UIApplication application, List<string> done, List<string> failures)
        {
            try
            {
                var command = RevitCommandId.LookupPostableCommandId(PostableCommand.CoordinationSelectLink);
                if (command == null || !application.CanPostCommand(command))
                {
                    failures.Add("\"Coordination Review\" is not available right now — open it by hand " +
                                 "(\"Collaborate → Coordination Review\").");
                    return;
                }

                application.PostCommand(command);
                done.Add("Opening \"Coordination Review → Select Link\" — check that the list is empty");
            }
            catch (Exception exception)
            {
                failures.Add("Could not open \"Coordination Review\": " + LinkCatalog.Short(exception.Message));
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(
            CoordinationScan scan,
            IReadOnlyList<string> done,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
        {
            const int limit = 15;

            var text = scan != null ? "Coordination file: " + scan.LinkName + ".\n\n" : string.Empty;

            text += done.Count == 0
                ? "Nothing was done."
                : "Accepted (" + done.Count + "):\n• " + string.Join("\n• ", done.Take(limit)) +
                  (done.Count > limit ? "\n… and " + (done.Count - limit) + " more" : string.Empty);

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

        /// <summary>A note for the "nothing to compare" refusal: what could not be read must not stay unmentioned.</summary>
        private static string Note(IReadOnlyList<string> failures)
        {
            const int limit = 10;

            if (failures.Count == 0)
                return string.Empty;

            var text = "\n\nAlong the way, could not be read (" + failures.Count + "):\n• " +
                       string.Join("\n• ", failures.Take(limit));

            return failures.Count > limit ? text + "\n… and " + (failures.Count - limit) + " more" : text;
        }
    }
}
