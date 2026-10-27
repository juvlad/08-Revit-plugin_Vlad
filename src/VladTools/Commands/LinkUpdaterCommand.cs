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
    /// Points the open model's links at Revit Server: for every link, the model of the same name on the
    /// server the open model lies on, loaded through <c>RevitLinkType.LoadFrom</c>.
    ///
    /// Made for a move from BIM360 to Revit Server. The models arrive on the server, and every link in
    /// every one of them still points at the cloud — Revit's own way out is "Manage Links → Reload From"
    /// one link at a time, browsing the server tree for each. Here the server is searched once and the
    /// whole list is repointed in one go.
    ///
    /// <c>LoadFrom</c> keeps the link the same element — its instances, placement, pins, project
    /// workset, view overrides and Copy/Monitor relations all stay — which deleting the link and linking
    /// the server model anew would lose. Its price is the undo history: it is cleared, and the run takes
    /// no transaction at all (see "How a command is built", the <c>LinkUpdaterCommand</c> exception).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LinkUpdaterCommand : IExternalCommand
    {
        private const string DialogTitle = "Link Updater";

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
                    "A family holds no Revit links to repoint.");
                return Result.Cancelled;
            }

            try
            {
                int nested;
                var links = Collect(doc, out nested);

                if (links.Count == 0)
                {
                    TaskDialog.Show(DialogTitle,
                        "The model has no Revit links — there is nothing to repoint." +
                        (nested > 0 ? "\n\n(Nested links follow the models they are nested in.)" : string.Empty));
                    return Result.Cancelled;
                }

                var window = new LinkUpdaterWindow(links, LinkCatalog.Host(doc), nested);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var outcome = new Outcome();

                foreach (var row in Borrow(doc, window.Selected, outcome))
                {
                    try
                    {
                        Repoint(doc, row, outcome);
                    }
                    catch (Exception exception)
                    {
                        outcome.Failures.Add(row.Name + " — " + LinkCatalog.Explain(exception));
                    }
                }

                Report(doc, outcome);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── the snapshot ─────────────────────────────

        /// <summary>
        /// The top-level Revit links, as snapshots. Nested ones are counted and left out: their paths
        /// live in the model they are nested in, which this command does not touch.
        /// </summary>
        private static List<LinkPathInfo> Collect(Document doc, out int nested)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .ToList();

            nested = types.Count(type => type.IsNestedLink);

            var links = new List<LinkPathInfo>();

            foreach (var type in types.Where(type => !type.IsNestedLink).OrderBy(type => type.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var status = Status(type);
                var loaded = status == LinkedFileStatus.Loaded;
                var entry = LinkCatalog.Describe(doc, type);

                if (entry != null)
                {
                    links.Add(new LinkPathInfo(
                        type.Id,
                        entry,
                        Location(doc, type, entry),
                        Describe(status),
                        loaded,
                        entry.Origin == LinkOrigin.Cloud || IsDesktopConnector(entry.Path),
                        false,
                        Obstacle(doc, type, status)));
                    continue;
                }

                // No file path: a model linked from BIM360 / Autodesk Docs is kept by Revit as an
                // external resource of its cloud server, and ExternalFileUtils refuses it. The resource
                // reference still says where it is; the link's own name is what the server is searched
                // for, and its unique id keeps the key apart from any other link's.
                var reference = Resource(type);
                if (reference != null)
                {
                    links.Add(new LinkPathInfo(
                        type.Id,
                        LinkEntry.ForCloud(string.Empty, "resource", type.UniqueId, type.Name),
                        ResourceLocation(reference, type),
                        Describe(status),
                        loaded,
                        true,
                        true,
                        Obstacle(doc, type, status)));
                    continue;
                }

                // Revit says nothing about where it points. Listed rather than dropped — a link the
                // window leaves out reads as "nothing to fix".
                links.Add(new LinkPathInfo(
                    type.Id,
                    new LinkEntry(LinkOrigin.File, type.Name, "unknown:" + type.UniqueId, null, null, null),
                    "(Revit does not give the path)",
                    Describe(status),
                    loaded,
                    false,
                    false,
                    "Revit does not say where this link points — repoint it in Manage Links → Reload From"));
            }

            return links;
        }

        /// <summary>The link as an external resource — how Revit keeps a model linked from the cloud; null when it is not one.</summary>
        private static ExternalResourceReference Resource(RevitLinkType type)
        {
            try
            {
                return type.GetExternalResourceReference(ExternalResourceTypes.BuiltInExternalResourceTypes.RevitLink);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The resource as Revit shows it ("Autodesk Docs://Project/Model.rvt"), or the link's name when it shows nothing.</summary>
        private static string ResourceLocation(ExternalResourceReference reference, RevitLinkType type)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(reference.InSessionPath))
                    return reference.InSessionPath;
            }
            catch (Exception)
            {
                // Falls through to the name.
            }

            return "BIM360 · " + type.Name;
        }

        /// <summary>
        /// A path into Desktop Connector's mirror of BIM360 / Autodesk Docs (<c>…\DC\ACCDocs\…</c>,
        /// older <c>…\BIM 360\…</c>): Revit sees an ordinary file, but the model lives in the cloud.
        /// </summary>
        private static bool IsDesktopConnector(string path)
        {
            var text = (path ?? string.Empty).Replace('/', '\\');

            return text.IndexOf(@"\ACCDocs\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf(@"\BIM 360\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static LinkedFileStatus Status(RevitLinkType type)
        {
            try
            {
                return type.GetLinkedFileStatus();
            }
            catch (Exception)
            {
                return LinkedFileStatus.Invalid;
            }
        }

        private static string Describe(LinkedFileStatus status)
        {
            switch (status)
            {
                case LinkedFileStatus.Loaded:
                    return "Loaded";
                case LinkedFileStatus.NotFound:
                    return "Not found";
                case LinkedFileStatus.Unloaded:
                    return "Unloaded";
                case LinkedFileStatus.LocallyUnloaded:
                    return "Unloaded for you";
                case LinkedFileStatus.InClosedWorkset:
                    return "Closed workset";
                case LinkedFileStatus.Invalid:
                    return "Unknown";
                default:
                    return status.ToString();
            }
        }

        /// <summary>The place as Revit's "Manage Links" shows it; a cloud link reads "BIM 360://Project/Model.rvt".</summary>
        private static string Location(Document doc, RevitLinkType type, LinkEntry entry)
        {
            try
            {
                var path = ExternalFileUtils.GetExternalFileReference(doc, type.Id).GetAbsolutePath();
                var visible = ModelPathUtils.ConvertModelPathToUserVisiblePath(path);

                if (!string.IsNullOrWhiteSpace(visible))
                    return visible;
            }
            catch (Exception)
            {
                // A cloud path Revit will not spell out is still told apart by its name below.
            }

            return entry.Origin == LinkOrigin.Cloud ? "BIM360 · " + entry.Name : entry.Path;
        }

        /// <summary>
        /// What stops <c>LoadFrom</c> before it starts — both are refusals RevitAPI.xml lists for it: the
        /// link type held by another user in a local copy, and a link in a closed workset. Said in the
        /// window, where the fix can be made, rather than as a failure line after the run.
        /// </summary>
        private static string Obstacle(Document doc, RevitLinkType type, LinkedFileStatus status)
        {
            if (!doc.IsWorkshared)
                return string.Empty;

            try
            {
                string owner;
                if (WorksharingUtils.GetCheckoutStatus(doc, type.Id, out owner) == CheckoutStatus.OwnedByOtherUser)
                    return "Held by " + (string.IsNullOrEmpty(owner) ? "another user" : owner) +
                           " — they have to relinquish it first";
            }
            catch (Exception)
            {
                // Nothing could be found out — the checkout before the run will ask again.
            }

            try
            {
                var workset = doc.GetWorksetTable().GetWorkset(type.WorksetId);
                if (workset != null && !workset.IsOpen)
                    return "Its workset \"" + workset.Name + "\" is closed — open it in Worksets first";
            }
            catch (Exception)
            {
                // As above.
            }

            return status == LinkedFileStatus.InClosedWorkset
                ? "The link stands in a closed workset — open it in Worksets first"
                : string.Empty;
        }

        // ───────────────────────────── the run ─────────────────────────────

        /// <summary>
        /// Checks the link types out of the central model before reloading them — the recipe of "Move to
        /// Workset": an element held by another user or changed in the central model since the last
        /// reload is named and left out, instead of failing on Revit's own terms halfway through.
        /// </summary>
        private static List<LinkUpdateRow> Borrow(Document doc, IReadOnlyList<LinkUpdateRow> rows, Outcome outcome)
        {
            if (!doc.IsWorkshared || rows.Count == 0)
                return rows.ToList();

            HashSet<ElementId> owned = null;

            try
            {
                owned = new HashSet<ElementId>(WorksharingUtils.CheckoutElements(doc, rows.Select(row => row.Info.TypeId).ToList()));
            }
            catch (Exception exception)
            {
                outcome.Notes.Add("The links could not be checked out in one go (" + LinkCatalog.Short(exception.Message) +
                                  "); each one was still tried.");
            }

            var free = new List<LinkUpdateRow>();

            foreach (var row in rows)
            {
                var id = row.Info.TypeId;

                var updates = ModelUpdatesStatus.CurrentWithCentral;
                try
                {
                    updates = WorksharingUtils.GetModelUpdatesStatus(doc, id);
                }
                catch (Exception)
                {
                    // Nothing could be found out — let LoadFrom decide.
                }

                if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral)
                {
                    outcome.Failures.Add(row.Name + " — changed in the central model since your last reload: " +
                                         "Reload Latest, then run the button again");
                    continue;
                }

                if (owned != null && owned.Contains(id))
                {
                    free.Add(row);
                    continue;
                }

                var owner = string.Empty;
                var status = CheckoutStatus.NotOwned;

                try
                {
                    status = WorksharingUtils.GetCheckoutStatus(doc, id, out owner);
                }
                catch (Exception)
                {
                    // As above.
                }

                if (status == CheckoutStatus.OwnedByOtherUser)
                    outcome.Failures.Add(row.Name + " — held by " + (string.IsNullOrEmpty(owner) ? "another user" : owner));
                else
                    free.Add(row);
            }

            return free;
        }

        /// <summary>
        /// Loads one link from its server path, then checks what Revit did rather than trusting the
        /// result code: where the link points now, whether it loaded, and whether the worksets open
        /// inside it are the ones that were open before.
        ///
        /// The worksets go in as <c>null</c> — by RevitAPI.xml, "the same set of worksets the link
        /// previously had". A model moved to a server keeps its worksets, so that is the right request;
        /// whether Revit honoured it for a model from another place is exactly what the check is for. A
        /// link not loaded before has nothing to compare with: Revit's own memory of it is all there is,
        /// and the report says so rather than pretending it was checked.
        /// </summary>
        private static void Repoint(Document doc, LinkUpdateRow row, Outcome outcome)
        {
            var type = doc.GetElement(row.Info.TypeId) as RevitLinkType;
            if (type == null)
            {
                outcome.Failures.Add(row.Name + " — the link is no longer in the project");
                return;
            }

            var before = OpenWorksets(doc, type.Id);
            var path = ModelPathUtils.ConvertUserVisiblePathToModelPath(row.Target.RsnPath);

            Route route;
            string failure;
            if (!Load(doc, row, path, out route, out failure))
            {
                outcome.Failures.Add(row.Name + " — " + failure + " (" + row.Target.RsnPath + ")");
                return;
            }

            type = doc.GetElement(row.Info.TypeId) as RevitLinkType;
            var line = row.Name + " → " + row.Target.RsnPath;

            if (!RevitLinkType.IsLoaded(doc, type.Id))
            {
                outcome.NotLoaded.Add(line);
                return;
            }

            outcome.Repointed.Add(line);

            if (before == null)
            {
                if (!row.Info.IsLoaded)
                    outcome.Remembered.Add(row.Name);
                return;
            }

            var check = Compare(before, OpenWorksets(doc, type.Id));
            outcome.Absent.AddRange(check.Absent.Select(name => row.Name + " — " + name));

            if (check.Unknown)
            {
                outcome.Unverified.Add(row.Name);
                return;
            }

            if (check.Wrong.Count == 0)
                return;

            // Revit did not keep what was open: once more, now with the setup spelled out from the
            // server model's own worksets. The same "close all, open the listed ones" phrasing as
            // "Link Manager" — the one Revit is known to carry out for links.
            string reason;
            if (!Reload(doc, type, path, route, before, out reason))
            {
                outcome.Mismatched.Add(row.Name + " — " + string.Join(", ", check.Wrong) + " (" + reason + ")");
                return;
            }

            var second = Compare(before, OpenWorksets(doc, type.Id));
            if (second.Unknown)
                outcome.Unverified.Add(row.Name);
            else if (second.Wrong.Count == 0)
                outcome.Healed.Add(row.Name);
            else
                outcome.Mismatched.Add(row.Name + " — " + string.Join(", ", second.Wrong));
        }

        /// <summary>The two ways of telling <c>LoadFrom</c> where the model now is.</summary>
        private enum Route
        {
            /// <summary>The <c>RSN://</c> path — what every link that is a file path takes.</summary>
            Path,

            /// <summary>
            /// The same path as an external resource of Revit's own server
            /// (<c>ExternalResourceReference.CreateLocalResource</c>, <c>PathType.Server</c>) — the
            /// overload made for a link that is itself an external resource, as a BIM360 one is.
            /// </summary>
            Reference
        }

        /// <summary>
        /// Loads the link from its server path and confirms it points there now. A link linked from
        /// BIM360 is an external resource of Revit's cloud server, not a file path; whether the
        /// <c>ModelPath</c> overload takes it over is not something RevitAPI.xml says, so for such a
        /// link the resource overload is tried as well when the path does not take — refused, thrown,
        /// or "success" with the link still pointing at the cloud. The first route that leaves the
        /// link on the server path wins, and the reasons of every failed one go into the report.
        ///
        /// A loaded link that Revit refuses with "already opened directly" (<c>LinkOpenAsHost</c>) is
        /// unloaded and tried once more. Seen on this office's migration (October 2026): every link
        /// loaded from a Desktop Connector path failed that way while the unloaded ones in the same
        /// model went through. The server copy is a "Save As" of that very file, and the likeliest
        /// reading is that Revit takes it for the document the old link still holds in the session —
        /// RevitAPI.xml says <c>LoadFrom</c> unloads a loaded link itself, but evidently not before this
        /// check. The unload comes only on that refusal, never up front: on any other failure the link
        /// stays loaded where it was. If nothing takes after it, the link is loaded back from its old
        /// place, so a failed run never leaves a link unloaded that was loaded before it.
        /// </summary>
        private static bool Load(Document doc, LinkUpdateRow row, ModelPath path, out Route used, out string failure)
        {
            var routes = row.Info.IsResource ? new[] { Route.Path, Route.Reference } : new[] { Route.Path };
            var reasons = new List<string>();
            var unloaded = false;

            foreach (var route in routes)
            {
                var type = doc.GetElement(row.Info.TypeId) as RevitLinkType;
                if (type == null)
                {
                    reasons.Add("the link is no longer in the project");
                    break;
                }

                var said = routes.Length > 1 ? (route == Route.Path ? "by path: " : "as a resource: ") : string.Empty;

                try
                {
                    var result = LoadFrom(doc, type, path, route, null);

                    if (result.LoadResult == LinkLoadResultType.LinkOpenAsHost && !unloaded && RevitLinkType.IsLoaded(doc, type.Id))
                    {
                        type.Unload(null);
                        unloaded = true;

                        type = doc.GetElement(row.Info.TypeId) as RevitLinkType;
                        result = LoadFrom(doc, type, path, route, null);
                    }

                    if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                    {
                        reasons.Add(said + LinkCatalog.Describe(result.LoadResult) +
                                    (result.LoadResult == LinkLoadResultType.LinkOpenAsHost ? WhereOpen(doc, row.Target.Name) : string.Empty));
                        continue;
                    }
                }
                catch (Exception exception)
                {
                    reasons.Add(said + LinkCatalog.Explain(exception));
                    continue;
                }

                // "Success" is a code, not a path: read back where the link points now.
                type = doc.GetElement(row.Info.TypeId) as RevitLinkType;
                var now = type == null ? null : PathNow(doc, type);

                if (SamePath(now, row.Target.RsnPath))
                {
                    used = route;
                    failure = string.Empty;
                    return true;
                }

                reasons.Add(said + "Revit reported success, but the link points to " + (now ?? "a place it does not name"));
            }

            if (unloaded)
                reasons.Add(Restore(doc, row.Info.TypeId));

            used = Route.Path;
            failure = string.Join("; ", reasons);
            return false;
        }

        /// <summary>
        /// Loads a link this run unloaded back from where it pointed, and says how that went — the run
        /// must not end with a link unloaded that was loaded before it.
        /// </summary>
        private static string Restore(Document doc, ElementId typeId)
        {
            try
            {
                var type = doc.GetElement(typeId) as RevitLinkType;
                if (type != null && !RevitLinkType.IsLoaded(doc, typeId) &&
                    LinkLoadResult.IsCodeSuccess(type.Reload().LoadResult))
                    return "the link was loaded back from where it pointed";
            }
            catch (Exception)
            {
                // Said below: the link is left unloaded.
            }

            return RevitLinkType.IsLoaded(doc, typeId)
                ? "the link is loaded from where it pointed"
                : "the link is left UNLOADED — reload it in Manage Links";
        }

        /// <summary>
        /// Where a model Revit calls "already opened directly" stands in this session: open as a project,
        /// or loaded as a link of some other model (nested in another link, as a rule). Matched by the
        /// model's file name — the open documents' own names, their files' and their centrals'.
        /// </summary>
        private static string WhereOpen(Document doc, string name)
        {
            var wanted = ServerModelIndex.Bare(name);
            var asProject = false;
            var asLink = false;

            foreach (Document other in doc.Application.Documents)
            {
                try
                {
                    if (other.Equals(doc) || other.IsFamilyDocument)
                        continue;

                    var names = new[] { other.Title, FileName(other.PathName), FileName(LinkCatalog.VisiblePath(other)) };
                    if (!names.Any(candidate => string.Equals(ServerModelIndex.Bare(candidate), wanted, StringComparison.CurrentCultureIgnoreCase)))
                        continue;

                    if (other.IsLinked)
                        asLink = true;
                    else
                        asProject = true;
                }
                catch (Exception)
                {
                    // A document that will not say its name is not the one being looked for.
                }
            }

            if (asProject)
                return " — it is open in this Revit session as a project: close it and run the button again";

            if (asLink)
                return " — a model of this name is loaded in this session inside another link: " +
                       "repoint or unload that link first, then run the button again";

            return " — Revit takes the server copy for a model already in this session; " +
                   "restart Revit, open only this model and run the button again";
        }

        private static string FileName(string path)
        {
            var text = path ?? string.Empty;
            var cut = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\'));

            return cut >= 0 ? text.Substring(cut + 1) : text;
        }

        private static LinkLoadResult LoadFrom(Document doc, RevitLinkType type, ModelPath path, Route route, WorksetConfiguration configuration)
        {
            if (route == Route.Path)
                return type.LoadFrom(path, configuration);

            var reference = ExternalResourceReference.CreateLocalResource(
                doc, ExternalResourceTypes.BuiltInExternalResourceTypes.RevitLink, path, PathType.Server);

            return type.LoadFrom(reference, configuration);
        }

        /// <summary>Where the link points now: its file path, or — still an external resource — the resource's own display path.</summary>
        private static string PathNow(Document doc, RevitLinkType type)
        {
            var entry = LinkCatalog.Describe(doc, type);
            if (entry != null)
                return entry.Origin == LinkOrigin.Cloud ? "BIM360 · " + entry.Name : entry.Path;

            var reference = Resource(type);
            if (reference == null)
                return null;

            try
            {
                return reference.InSessionPath;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Two paths compared the way <see cref="LinkEntry.Key"/> compares them: case and slash direction aside.</summary>
        private static bool SamePath(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
                return false;

            return string.Equals(
                first.Trim().Replace('\\', '/').TrimEnd('/'),
                second.Trim().Replace('\\', '/').TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The second load: every workset that was open before, by name, opened in the server model;
        /// one the old link did not have keeps the state Revit gave it. Through the route that took the
        /// first time.
        /// </summary>
        private static bool Reload(Document doc, RevitLinkType type, ModelPath path, Route route, Dictionary<string, bool> before, out string reason)
        {
            reason = string.Empty;

            var linkDocument = LinkDocument(doc, type.Id);
            if (linkDocument == null)
            {
                reason = "the link did not hand over its document";
                return false;
            }

            var worksets = new FilteredWorksetCollector(linkDocument)
                .OfKind(WorksetKind.UserWorkset)
                .ToWorksets()
                .ToList();

            var open = worksets
                .Where(workset =>
                {
                    bool wasOpen;
                    return before.TryGetValue(LinkPreferences.NormalizeWorkset(workset.Name), out wasOpen) ? wasOpen : workset.IsOpen;
                })
                .Select(workset => workset.Id)
                .ToList();

            var configuration = new WorksetConfiguration(open.Count == worksets.Count
                ? WorksetConfigurationOption.OpenAllWorksets
                : WorksetConfigurationOption.CloseAllWorksets);

            using (configuration)
            {
                if (open.Count > 0 && open.Count < worksets.Count)
                    configuration.Open(open);

                var result = LoadFrom(doc, type, path, route, configuration);
                if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                {
                    reason = "the second load failed: " + LinkCatalog.Describe(result.LoadResult);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The user worksets of a loaded link, by name, and whether each is open — or null when there is
        /// nothing to read: the link is not loaded, or the model is not workshared.
        /// </summary>
        private static Dictionary<string, bool> OpenWorksets(Document doc, ElementId typeId)
        {
            try
            {
                var linkDocument = LinkDocument(doc, typeId);
                if (linkDocument == null || !linkDocument.IsWorkshared)
                    return null;

                // Names are compared the one way "Link Manager" compares them (LinkPreferences.SameWorkset):
                // trimmed, case ignored.
                var map = new Dictionary<string, bool>(StringComparer.CurrentCultureIgnoreCase);

                foreach (var workset in new FilteredWorksetCollector(linkDocument).OfKind(WorksetKind.UserWorkset).ToWorksets())
                    map[LinkPreferences.NormalizeWorkset(workset.Name)] = workset.IsOpen;

                return map;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Document LinkDocument(Document doc, ElementId typeId)
        {
            return LinkCatalog.Instances(doc, typeId).FirstOrDefault()?.GetLinkDocument();
        }

        /// <summary>
        /// The worksets that were open before against the ones open now. Three outcomes, as everywhere a
        /// workset setup is checked here: as before, these differ, or "could not be read" — never folded
        /// into "as before".
        /// </summary>
        private static WorksetCheck Compare(Dictionary<string, bool> before, Dictionary<string, bool> after)
        {
            var check = new WorksetCheck();

            if (after == null)
            {
                check.Unknown = true;
                return check;
            }

            foreach (var pair in before)
            {
                bool open;
                if (!after.TryGetValue(pair.Key, out open))
                {
                    if (pair.Value)
                        check.Absent.Add(pair.Key);
                    continue;
                }

                if (open != pair.Value)
                    check.Wrong.Add(pair.Key + (pair.Value ? " (was open)" : " (was closed)"));
            }

            return check;
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(Document doc, Outcome outcome)
        {
            var text = outcome.Repointed.Count + outcome.NotLoaded.Count == 0
                ? "Not a single link was repointed."
                : "Links pointed at Revit Server: " + (outcome.Repointed.Count + outcome.NotLoaded.Count) + ".";

            text += Section(null, outcome.Repointed);

            text += Section("Repointed, but Revit did not load the model from there — check the path in Manage Links",
                outcome.NotLoaded);

            text += Section("The worksets open inside the link came back as before only on a second load", outcome.Healed);

            text += Section("The worksets open inside the link differ from before — set them in Manage Links → Manage Worksets",
                outcome.Mismatched);

            text += Section("Open before, but the server model has no workset of that name", outcome.Absent);

            text += Section("The worksets inside the link could not be compared with before — look in Manage Worksets",
                outcome.Unverified);

            // Said, not counted as checked: a link that was not loaded had nothing to compare with.
            text += Section("Were not loaded before, so their worksets are as Revit remembered them for the link",
                outcome.Remembered);

            text += Section("Could not be done", outcome.Failures);
            text += Section("Notes", outcome.Notes);

            if (outcome.Repointed.Count + outcome.NotLoaded.Count > 0)
            {
                text += doc.IsWorkshared
                    ? "\n\nSynchronise with central: until then the new paths are only in your local copy."
                    : "\n\nSave the model: until then the new paths are only in this session.";
            }

            TaskDialog.Show(DialogTitle, text);
        }

        private static string Section(string title, IReadOnlyList<string> lines)
        {
            const int limit = 15;

            if (lines.Count == 0)
                return string.Empty;

            return "\n\n" + (title == null ? string.Empty : title + " (" + lines.Count + "):\n") +
                   "• " + string.Join("\n• ", lines.Take(limit)) +
                   (lines.Count > limit ? "\n… and " + (lines.Count - limit) + " more" : string.Empty);
        }

        /// <summary>What became of every checked link — sorted into the report's sections.</summary>
        private sealed class Outcome
        {
            public List<string> Repointed { get; } = new List<string>();
            public List<string> NotLoaded { get; } = new List<string>();
            public List<string> Healed { get; } = new List<string>();
            public List<string> Mismatched { get; } = new List<string>();
            public List<string> Absent { get; } = new List<string>();
            public List<string> Unverified { get; } = new List<string>();
            public List<string> Remembered { get; } = new List<string>();
            public List<string> Failures { get; } = new List<string>();
            public List<string> Notes { get; } = new List<string>();
        }

        private sealed class WorksetCheck
        {
            public bool Unknown { get; set; }
            public List<string> Wrong { get; } = new List<string>();
            public List<string> Absent { get; } = new List<string>();
        }
    }
}
