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
    /// The "Move to Workset" button: finds the elements standing in the wrong workset and moves them
    /// into the right one — by category, across the whole model or within the current selection.
    ///
    /// It was asked for after modelling with an AI tool put a run of ducts into "Параметры стадий" —
    /// by its name one of Revit's own project-standards worksets, a place no model element belongs.
    /// Revit has no "select everything in this workset", so finding them by hand means clicking
    /// through the model. The workset is the <c>ELEM_PARTITION_PARAM</c> parameter, and writing it is
    /// what this button does in bulk.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class MoveToWorksetCommand : IExternalCommand
    {
        private const string DialogTitle = "Move to Workset";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "There is no active document.";
                return Result.Cancelled;
            }

            var doc = uidoc.Document;
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "The command works only in a project.\n" +
                    "The family editor has no worksets: worksharing is a property of a project.");
                return Result.Cancelled;
            }

            if (!doc.IsWorkshared)
            {
                TaskDialog.Show(DialogTitle,
                    "The project is not workshared, so every element stands in one place and there is nothing to move.\n" +
                    "Worksets appear once \"Collaborate → Worksets\" turns worksharing on.");
                return Result.Cancelled;
            }

            try
            {
                var targets = UserWorksets(doc);
                if (targets.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "The project has no user workset to move elements into.");
                    return Result.Cancelled;
                }

                var selection = uidoc.Selection.GetElementIds();
                var selectionScan = selection.Count > 0 ? Scan(doc, selection) : null;

                var window = new MoveToWorksetWindow(selectionScan, () => Scan(doc, null), targets, doc.Title);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                if (window.WantsSelection)
                {
                    uidoc.Selection.SetElementIds(window.Selected
                        .SelectMany(row => row.Info.Movable.Concat(row.Info.InGroups).Concat(row.Info.Locked))
                        .Distinct()
                        .ToList());
                    return Result.Succeeded;
                }

                Move(doc, window);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── what the project holds ─────────────────────────────

        /// <summary>The project's user worksets — the only kind an element can be moved into.</summary>
        private static IReadOnlyList<WorksetInfo> UserWorksets(Document doc)
        {
            var active = doc.GetWorksetTable().GetActiveWorksetId();

            // The count is not needed here — the window only offers these as targets — so it is left
            // "not counted" rather than paid for with a collector per workset.
            return new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .ToWorksets()
                .OrderBy(workset => workset.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(workset => new WorksetInfo(
                    workset.UniqueId,
                    workset.Name,
                    0,
                    false,
                    workset.IsOpen,
                    workset.Id == active,
                    workset.IsEditable,
                    workset.Owner))
                .ToList();
        }

        /// <summary>Where an element stands as far as this button is concerned.</summary>
        private enum Fit
        {
            /// <summary>Not in any row: Revit decides its workset itself.</summary>
            Outside,
            Movable,
            InGroup,
            Locked
        }

        /// <summary>
        /// Reads the scope — the given ids, or the whole model when <paramref name="ids"/> is null —
        /// into workset × category rows. Revit's own worksets come first: they are what the button is for.
        /// </summary>
        private static WorksetCategoryScan Scan(Document doc, ICollection<ElementId> ids)
        {
            var table = doc.GetWorksetTable();
            var worksets = new Dictionary<int, Workset>();
            var rows = new Dictionary<string, Bucket>();

            IEnumerable<Element> scope = ids == null
                ? new FilteredElementCollector(doc).WhereElementIsNotElementType()
                : ids.Select(doc.GetElement).Where(element => element != null);

            var count = 0;
            var leftOut = 0;

            foreach (var element in scope)
            {
                count++;

                Workset workset;
                Category category;
                var fit = Classify(element, table, worksets, out workset, out category);

                if (fit == Fit.Outside)
                {
                    leftOut++;
                    continue;
                }

                var key = workset.UniqueId.ToString() + "\n" + category.Name;

                Bucket bucket;
                if (!rows.TryGetValue(key, out bucket))
                {
                    bucket = new Bucket(workset, category.Name);
                    rows.Add(key, bucket);
                }

                bucket.Add(fit, element.Id);
            }

            var closed = ids != null
                ? new List<string>()
                : new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToWorksets()
                    .Where(workset => !workset.IsOpen)
                    .Select(workset => workset.Name)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

            var infos = rows.Values
                .Select(bucket => bucket.ToInfo())
                .OrderBy(info => info.IsUserWorkset)
                .ThenBy(info => info.WorksetName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(info => info.Category, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return new WorksetCategoryScan(infos, count, leftOut, closed);
        }

        /// <summary>
        /// Decides whether an element belongs in the table, and how.
        ///
        /// **In a user workset the test is Revit's own: the "Workset" parameter is writable.** That is
        /// exactly what the Properties palette goes by, and it leaves out on its own everything whose
        /// workset Revit decides — curtain panels, nested parts, annotation belonging to a view.
        ///
        /// **In Revit's own worksets that test is not enough, and trusting it could be destructive.**
        /// A project-standards workset legitimately holds phases, materials, fill patterns, project
        /// information; a family workset holds the family itself. If any of those reported a writable
        /// workset parameter, the row would be checked by default and moved out of its place. So there
        /// an element counts only if it **stands in the model** — it has a location or a bounding box
        /// and belongs to no single view — which none of Revit's own contents does and every stray duct
        /// does. And there a read-only parameter does not hide the element: it becomes
        /// <see cref="Fit.Locked"/>, shown with no way to move it, because "nothing found" would read
        /// as "all is well".
        /// </summary>
        private static Fit Classify(
            Element element,
            WorksetTable table,
            Dictionary<int, Workset> worksets,
            out Workset workset,
            out Category category)
        {
            workset = null;
            category = null;

            try
            {
                if (element is ElementType || element is View || element is ProjectInfo)
                    return Fit.Outside;

                var worksetId = element.WorksetId;
                if (worksetId is null || worksetId == WorksetId.InvalidWorksetId)
                    return Fit.Outside;

                if (!worksets.TryGetValue(worksetId.IntegerValue, out workset))
                {
                    workset = table.GetWorkset(worksetId);
                    worksets[worksetId.IntegerValue] = workset;
                }

                if (workset == null)
                    return Fit.Outside;

                category = element.Category;
                if (category?.Parent != null)
                    category = category.Parent;

                // No category, or an internal one, is Revit's own bookkeeping — sketch planes and the
                // like — and nothing a person modelled.
                if (category == null || category.CategoryType == CategoryType.Internal || category.CategoryType == CategoryType.Invalid)
                    return Fit.Outside;

                var user = workset.Kind == WorksetKind.UserWorkset;
                if (!user && (element.ViewSpecific || !StandsInModel(element)))
                    return Fit.Outside;

                var parameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (parameter == null || parameter.IsReadOnly)
                    return user ? Fit.Outside : Fit.Locked;

                return element.GroupId != ElementId.InvalidElementId ? Fit.InGroup : Fit.Movable;
            }
            catch (Exception)
            {
                // An element that cannot even be read is not one to move.
                return Fit.Outside;
            }
        }

        /// <summary>The element occupies a place in the model — what separates a stray duct from a phase or a material.</summary>
        private static bool StandsInModel(Element element)
        {
            var location = element.Location;
            if (location is LocationPoint || location is LocationCurve)
                return true;

            try
            {
                return element.get_BoundingBox(null) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The ids of one workset × category row, gathered while the scope is read.</summary>
        private sealed class Bucket
        {
            private readonly Workset _workset;
            private readonly string _category;
            private readonly List<ElementId> _movable = new List<ElementId>();
            private readonly List<ElementId> _inGroups = new List<ElementId>();
            private readonly List<ElementId> _locked = new List<ElementId>();

            public Bucket(Workset workset, string category)
            {
                _workset = workset;
                _category = category;
            }

            public void Add(Fit fit, ElementId id)
            {
                switch (fit)
                {
                    case Fit.Movable:
                        _movable.Add(id);
                        break;
                    case Fit.InGroup:
                        _inGroups.Add(id);
                        break;
                    case Fit.Locked:
                        _locked.Add(id);
                        break;
                }
            }

            public WorksetCategoryInfo ToInfo()
            {
                return new WorksetCategoryInfo(
                    _workset.UniqueId,
                    _workset.Name,
                    KindCaption(_workset.Kind),
                    _workset.Kind == WorksetKind.UserWorkset,
                    _category,
                    _movable,
                    _inGroups,
                    _locked);
            }
        }

        /// <summary>The kind in the words of Revit's own Worksets dialog ("Show: User-Created / Families / Project Standards / Views").</summary>
        private static string KindCaption(WorksetKind kind)
        {
            switch (kind)
            {
                case WorksetKind.UserWorkset:
                    return "User";
                case WorksetKind.StandardWorkset:
                    return "Project standards";
                case WorksetKind.ViewWorkset:
                    return "View";
                case WorksetKind.FamilyWorkset:
                    return "Family";
                default:
                    return "Other";
            }
        }

        // ───────────────────────────── moving ─────────────────────────────

        /// <summary>
        /// Carries out what the window agreed to: checks the elements out, rewrites their workset in
        /// one transaction, and reads every one of them back afterwards.
        /// </summary>
        private static void Move(Document doc, MoveToWorksetWindow window)
        {
            // The id is resolved afresh from the GUID rather than carried in from the window: by
            // Autodesk's own documentation a WorksetId changes on synchronising with the central model.
            var target = doc.GetWorksetTable().GetWorkset(window.TargetId);
            if (target == null || target.Kind != WorksetKind.UserWorkset)
            {
                TaskDialog.Show(DialogTitle,
                    "The workset \"" + window.Target + "\" is no longer in the project. Nothing was moved.");
                return;
            }

            // Every id remembers the row it came from: the report speaks in rows, not ids.
            var origin = new Dictionary<ElementId, WorksetCategoryInfo>();
            foreach (var row in window.Selected.Where(row => row.Info.WorksetId != window.TargetId))
            {
                foreach (var id in row.Info.Movable)
                    origin[id] = row.Info;
            }

            var outcome = new Outcome(origin);

            var wanted = new List<ElementId>();
            foreach (var id in origin.Keys)
            {
                var element = doc.GetElement(id);
                if (element == null)
                    outcome.Gone++;
                else if (element.WorksetId == target.Id)
                    outcome.Already++;
                else
                    wanted.Add(id);
            }

            var free = Borrow(doc, wanted, outcome);

            var requested = new List<ElementId>();
            var status = TransactionStatus.Uninitialized;

            if (free.Count > 0)
            {
                using (var transaction = new Transaction(doc, "Move to workset \"" + target.Name + "\""))
                {
                    transaction.Start();

                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(outcome.Warnings);
                    transaction.SetFailureHandlingOptions(options);

                    foreach (var id in free)
                    {
                        if (Write(doc, id, target.Id, outcome))
                            requested.Add(id);
                    }

                    if (requested.Count == 0)
                        transaction.RollBack();
                    else
                        status = transaction.Commit();
                }
            }

            if (requested.Count > 0)
            {
                if (status == TransactionStatus.Committed)
                    Verify(doc, requested, target.Id, outcome);
                else
                    outcome.RolledBack = status;
            }

            Report(target.Name, outcome);
        }

        /// <summary>
        /// Checks the elements out of the central model before editing them.
        ///
        /// In a local copy this is not a formality: an element somebody else holds cannot be edited,
        /// and an element changed in the central model since the last reload cannot be edited until
        /// it is reloaded — either one, left to the commit, fails the whole transaction rather than
        /// itself. So both are taken out here and named in the report. This is the pattern Autodesk's
        /// own remarks on <c>WorksharingUtils</c> give: check out, then confirm the elements are up to
        /// date. A Revit standards workset counts as well: whoever last edited the phases may still
        /// own "Phase settings", and with it everything standing in it.
        /// </summary>
        private static List<ElementId> Borrow(Document doc, List<ElementId> wanted, Outcome outcome)
        {
            if (wanted.Count == 0)
                return wanted;

            HashSet<ElementId> owned = null;

            try
            {
                owned = new HashSet<ElementId>(WorksharingUtils.CheckoutElements(doc, wanted));
            }
            catch (Exception exception)
            {
                // A workshared model with no central yet, or the central out of reach. Not fatal by
                // itself: whoever the cached status names as the owner is still taken out below.
                outcome.Notes.Add("The elements could not be checked out in one go (" + LinkCatalog.Short(exception.Message) +
                                  "). Whatever Revit lets through is still moved.");
            }

            var free = new List<ElementId>();

            foreach (var id in wanted)
            {
                var updates = ModelUpdatesStatus.CurrentWithCentral;
                try
                {
                    updates = WorksharingUtils.GetModelUpdatesStatus(doc, id);
                }
                catch (Exception)
                {
                    // Nothing could be found out — let the edit itself decide.
                }

                if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral)
                {
                    outcome.Stale.Add(id);
                    continue;
                }

                if (owned != null && owned.Contains(id))
                {
                    free.Add(id);
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
                    // As above: the edit decides.
                }

                if (status == CheckoutStatus.OwnedByOtherUser)
                    outcome.Hold(string.IsNullOrEmpty(owner) ? "another user" : owner, id);
                else
                    free.Add(id);
            }

            return free;
        }

        /// <summary>
        /// Writes the workset parameter of one element. The parameter is checked again rather than
        /// trusted from the scan: the window may have been open a while, and an element's state is
        /// the document's, not the snapshot's.
        /// </summary>
        private static bool Write(Document doc, ElementId id, WorksetId target, Outcome outcome)
        {
            try
            {
                var element = doc.GetElement(id);
                if (element == null)
                {
                    outcome.Gone++;
                    return false;
                }

                var parameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (parameter == null || parameter.IsReadOnly)
                {
                    outcome.Fail(id, "Revit does not let its workset be changed");
                    return false;
                }

                if (!parameter.Set(target.IntegerValue))
                {
                    outcome.Fail(id, "Revit did not accept the new workset");
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                outcome.Fail(id, LinkCatalog.Short(exception.Message));
                return false;
            }
        }

        /// <summary>
        /// Reads every element back after the commit. A parameter that took the value without an
        /// error is not yet an element in the new workset — the same rule as "Accept Changes" keeps
        /// for a move, and the only honest basis for the count in the report.
        /// </summary>
        private static void Verify(Document doc, IEnumerable<ElementId> requested, WorksetId target, Outcome outcome)
        {
            foreach (var id in requested)
            {
                Element element = null;

                try
                {
                    element = doc.GetElement(id);
                }
                catch (Exception)
                {
                    // Treated as not moved below.
                }

                if (element != null && element.WorksetId == target)
                    outcome.Moved.Add(id);
                else
                    outcome.Fail(id, element == null
                        ? "the element is gone from the model"
                        : "Revit took the value, but the element is still in its old workset");
            }
        }

        /// <summary>Everything one run found out, kept in ids so the report can speak in rows.</summary>
        private sealed class Outcome
        {
            private readonly Dictionary<ElementId, WorksetCategoryInfo> _origin;

            public Outcome(Dictionary<ElementId, WorksetCategoryInfo> origin)
            {
                _origin = origin;
            }

            public WarningSuppressor Warnings { get; } = new WarningSuppressor();

            public List<ElementId> Moved { get; } = new List<ElementId>();

            public List<ElementId> Stale { get; } = new List<ElementId>();

            public Dictionary<string, List<ElementId>> Held { get; } =
                new Dictionary<string, List<ElementId>>(StringComparer.CurrentCultureIgnoreCase);

            /// <summary>Failures grouped by their reason: a reason repeated for a hundred ducts is one line, not a hundred.</summary>
            public Dictionary<string, List<ElementId>> Failures { get; } = new Dictionary<string, List<ElementId>>();

            public List<string> Notes { get; } = new List<string>();

            public int Already { get; set; }

            public int Gone { get; set; }

            /// <summary>The commit status when Revit rolled the whole batch back; <c>null</c> otherwise.</summary>
            public TransactionStatus? RolledBack { get; set; }

            public void Hold(string owner, ElementId id)
            {
                List<ElementId> list;
                if (!Held.TryGetValue(owner, out list))
                    Held[owner] = list = new List<ElementId>();

                list.Add(id);
            }

            public void Fail(ElementId id, string reason)
            {
                List<ElementId> list;
                if (!Failures.TryGetValue(reason, out list))
                    Failures[reason] = list = new List<ElementId>();

                list.Add(id);
            }

            /// <summary>"Ducts from "Параметры стадий" — 85; Duct Fittings from … — 30": a list of ids, in the words of the table.</summary>
            public IEnumerable<string> ByRow(IEnumerable<ElementId> ids)
            {
                return ids
                    .GroupBy(id =>
                    {
                        WorksetCategoryInfo info;
                        return _origin.TryGetValue(id, out info) ? info : null;
                    })
                    .Where(group => group.Key != null)
                    .OrderBy(group => group.Key.IsUserWorkset)
                    .ThenBy(group => group.Key.WorksetName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(group => group.Key.Category, StringComparer.CurrentCultureIgnoreCase)
                    .Select(group => group.Key.Category + " from \"" + group.Key.WorksetName + "\" — " + group.Count());
            }

            /// <summary>"Ducts 60, Duct Fittings 25" — a compact form for a line that already says the rest.</summary>
            public string Categories(IEnumerable<ElementId> ids)
            {
                return string.Join(", ", ids
                    .Select(id =>
                    {
                        WorksetCategoryInfo info;
                        return _origin.TryGetValue(id, out info) ? info.Category : "?";
                    })
                    .GroupBy(category => category)
                    .OrderByDescending(group => group.Count())
                    .Select(group => group.Key + " " + group.Count()));
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(string target, Outcome outcome)
        {
            var text = outcome.Moved.Count > 0
                ? "Moved to workset \"" + target + "\": " + outcome.Moved.Count + "\n\n• " + string.Join("\n• ", outcome.ByRow(outcome.Moved))
                : "Nothing was moved.";

            if (outcome.RolledBack.HasValue)
            {
                text += "\n\nRevit rolled the whole change back (" + outcome.RolledBack.Value + ") — not a single element " +
                        "changed workset. The reason is usually in the Revit messages below; moving the rows one at a " +
                        "time shows which of them Revit objects to.";
            }

            var lines = new List<string>();

            if (outcome.Already > 0)
                lines.Add("Already in \"" + target + "\": " + outcome.Already + " — nothing to do.");

            if (outcome.Gone > 0)
                lines.Add("No longer in the model: " + outcome.Gone + ".");

            if (outcome.Stale.Count > 0)
                lines.Add(outcome.Stale.Count + " elements (" + outcome.Categories(outcome.Stale) + ") were changed in the " +
                          "central model after your last reload and cannot be edited yet — Collaborate → Reload Latest, " +
                          "then press the button again.");

            foreach (var held in outcome.Held.OrderByDescending(pair => pair.Value.Count))
                lines.Add(held.Value.Count + " elements (" + outcome.Categories(held.Value) + ") are held by " + held.Key +
                          " — they have to synchronise with central and relinquish them first.");

            lines.AddRange(outcome.Notes);

            if (lines.Count > 0)
                text += "\n\n" + string.Join("\n", lines);

            var failures = outcome.Failures
                .OrderByDescending(pair => pair.Value.Count)
                .Select(pair => pair.Value.Count + " (" + outcome.Categories(pair.Value) + ") — " + pair.Key)
                .ToList();

            if (failures.Count > 0)
            {
                const int limit = 15;
                text += "\n\nCould not be moved:\n• " + string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… and " + (failures.Count - limit) + " more";
            }

            var warnings = outcome.Warnings.Messages;
            if (warnings.Count > 0)
            {
                const int limit = 5;
                text += "\n\nRevit warnings:\n• " + string.Join("\n• ", warnings.Take(limit));

                if (warnings.Count > limit)
                    text += "\n… and " + (warnings.Count - limit) + " more";
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
