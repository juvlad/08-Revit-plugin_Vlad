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
    /// Removes from the open project whatever is checked in the window: sheets, views, legends,
    /// schedules, filters, model groups and unused families.
    ///
    /// A routine job: a model arrived from outside and is needed only as geometry — someone else's
    /// presentation is stripped off in one go rather than one browser node at a time.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CleanupCommand : IExternalCommand
    {
        private const string DialogTitle = "Model Cleanup";

        /// <summary>
        /// The execution order is not the order the items stand in inside the window.
        /// The presentation goes first (sheets, views, legends, schedules, filters), then the groups,
        /// and only at the end the unused families: by that point titleblocks, tags and detail
        /// components have become unneeded as well.
        /// Every <see cref="CleanupTarget"/> value is listed: an item missing from here would be
        /// offered by the window and never carried out by the command.
        /// </summary>
        private static readonly CleanupTarget[] Order =
        {
            CleanupTarget.Sheets,
            CleanupTarget.Views,
            CleanupTarget.Legends,
            CleanupTarget.Schedules,
            CleanupTarget.Filters,
            CleanupTarget.ModelGroups,
            CleanupTarget.UnusedGroups,
            CleanupTarget.UnusedFamilies
        };

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
                    "There is nothing to clean in the family editor: no sheets, no filters, no groups there.");
                return Result.Cancelled;
            }

            try
            {
                // The active view is left alone everywhere: Revit will not delete the view the user is
                // standing on, and the session rests on it too.
                var activeViewId = uidoc.ActiveView?.Id ?? ElementId.InvalidElementId;

                var options = Survey(doc, activeViewId);

                var window = new CleanupWindow(options);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var chosen = new HashSet<CleanupTarget>(window.Selected.Select(option => option.Target));

                var done = new List<string>();
                var notes = new List<string>();
                var failures = new List<string>();
                var warnings = new WarningSuppressor();

                using (var transaction = new Transaction(doc, "Model cleanup"))
                {
                    transaction.Start();

                    // Deleting views, filters and families in bulk drags a pile of Revit warnings along.
                    // A modal dialog for each would wreck the cleanup, so they are suppressed and go
                    // into the final report instead.
                    var failureOptions = transaction.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(warnings);
                    transaction.SetFailureHandlingOptions(failureOptions);

                    foreach (var target in Order)
                    {
                        if (chosen.Contains(target))
                            Run(doc, target, activeViewId, done, notes, failures);
                    }

                    if (done.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                Report(done, notes, failures, warnings.Messages);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── what the model holds ─────────────────────────────

        /// <summary>
        /// Counts how much of everything the model holds and builds the window items out of that.
        /// The slowest part is counting the unused families: it walks every element of the document,
        /// because there is no other way to tell whether a type is in use.
        /// </summary>
        private static IReadOnlyList<CleanupOption> Survey(Document doc, ElementId activeViewId)
        {
            var counts = new Dictionary<CleanupTarget, int>
            {
                { CleanupTarget.UnusedFamilies, UnusedSymbols(doc).Count },
                { CleanupTarget.Sheets, ViewsOf(doc, CleanupTarget.Sheets, activeViewId).Count },
                { CleanupTarget.Filters, Filters(doc).Count },
                { CleanupTarget.Views, ViewsOf(doc, CleanupTarget.Views, activeViewId).Count },
                { CleanupTarget.Legends, ViewsOf(doc, CleanupTarget.Legends, activeViewId).Count },
                { CleanupTarget.Schedules, ViewsOf(doc, CleanupTarget.Schedules, activeViewId).Count },
                { CleanupTarget.ModelGroups, ModelGroups(doc).Count },
                { CleanupTarget.UnusedGroups, UnusedGroupTypes(doc).Count }
            };

            return Enum.GetValues(typeof(CleanupTarget))
                .Cast<CleanupTarget>()
                .Select(target => CleanupOption.For(target, counts[target]))
                .ToList();
        }

        /// <summary>
        /// Carries out one item. The lists are gathered afresh rather than taken from the window: the
        /// previous items have already changed the document, and half of what was found may be gone.
        /// </summary>
        private static void Run(
            Document doc,
            CleanupTarget target,
            ElementId activeViewId,
            List<string> done,
            List<string> notes,
            List<string> failures)
        {
            switch (target)
            {
                case CleanupTarget.Sheets:
                    Removed(doc, ViewsOf(doc, target, activeViewId), "Sheet", "Sheets deleted: ", done, failures);
                    break;

                case CleanupTarget.Views:
                    Removed(doc, ViewsOf(doc, target, activeViewId), "View", "Views deleted: ", done, failures);
                    break;

                case CleanupTarget.Legends:
                    Removed(doc, ViewsOf(doc, target, activeViewId), "Legend", "Legends deleted: ", done, failures);
                    break;

                case CleanupTarget.Schedules:
                    Removed(doc, ViewsOf(doc, target, activeViewId), "Schedule", "Schedules deleted: ", done, failures);
                    break;

                case CleanupTarget.Filters:
                    Removed(doc, Filters(doc), "Filter", "Filters deleted: ", done, failures);
                    break;

                case CleanupTarget.ModelGroups:
                    Ungroup(doc, done, failures);
                    break;

                case CleanupTarget.UnusedGroups:
                    Removed(doc, UnusedGroupTypes(doc), "Group type", "Unused groups deleted: ", done, failures);
                    break;

                case CleanupTarget.UnusedFamilies:
                    PurgeFamilies(doc, done, notes, failures);
                    break;
            }
        }

        // ───────────────────────────── views, sheets, filters ─────────────────────────────

        /// <summary>
        /// The views of the required kind. Templates and the active view are never returned: a template
        /// is not a browser view, and Revit will not let the active one be deleted.
        /// </summary>
        private static List<ElementId> ViewsOf(Document doc, CleanupTarget target, ElementId activeViewId)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate && view.Id != activeViewId && Kind(view) == target)
                .Select(view => view.Id)
                .ToList();
        }

        /// <summary>
        /// Which cleanup item a view belongs to. Internal views (browsers, system views, analysis
        /// reports) belong to none and are never deleted.
        /// </summary>
        private static CleanupTarget? Kind(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.DrawingSheet:
                    return CleanupTarget.Sheets;

                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                case ViewType.Section:
                case ViewType.Elevation:
                case ViewType.Detail:
                case ViewType.ThreeD:
                case ViewType.Rendering:
                case ViewType.Walkthrough:
                case ViewType.DraftingView:
                    return CleanupTarget.Views;

                case ViewType.Legend:
                    return CleanupTarget.Legends;

                case ViewType.Schedule:
                case ViewType.ColumnSchedule:
                case ViewType.PanelSchedule:
                    return IsInternalSchedule(view) ? (CleanupTarget?)null : CleanupTarget.Schedules;

                default:
                    return null;
            }
        }

        /// <summary>
        /// A schedule that lives not in the browser but inside a titleblock or the file itself.
        /// Such a schedule must not be deleted: a titleblock without its revision schedule breaks.
        /// </summary>
        private static bool IsInternalSchedule(View view)
        {
            var schedule = view as ViewSchedule;
            if (schedule == null)
                return false;

            try
            {
                return schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule;
            }
            catch (Exception)
            {
                // We could not ask — we treat it as internal: leaving it alone is the safer error.
                return true;
            }
        }

        /// <summary>
        /// View filters and selection filters: both live under "View → Filters".
        /// The classes are listed separately rather than by their common ancestor <c>FilterElement</c>:
        /// the collector does not support every abstract class, and here there is nothing to choose from.
        /// </summary>
        private static List<ElementId> Filters(Document doc)
        {
            var classes = new List<Type> { typeof(ParameterFilterElement), typeof(SelectionFilterElement) };

            return new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticlassFilter(classes))
                .ToElementIds()
                .ToList();
        }

        // ───────────────────────────── groups ─────────────────────────────

        /// <summary>Placed model groups; detail and attached detail groups are a different category.</summary>
        private static List<Group> ModelGroups(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_IOSModelGroups)
                .WhereElementIsNotElementType()
                .OfClass(typeof(Group))
                .Cast<Group>()
                .ToList();
        }

        /// <summary>Group types that are placed nowhere in the model.</summary>
        private static List<ElementId> UnusedGroupTypes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(GroupType))
                .Cast<GroupType>()
                .Where(type => IsUnplaced(type))
                .Select(type => type.Id)
                .ToList();
        }

        private static bool IsUnplaced(GroupType type)
        {
            try
            {
                var groups = type.Groups;
                return groups == null || groups.IsEmpty;
            }
            catch (Exception)
            {
                // We could not ask — we treat it as placed and leave it alone.
                return false;
            }
        }

        /// <summary>
        /// Ungroups every model group: the elements stay in place, only the groups disappear.
        ///
        /// There are several passes, as with deleting parameters: Revit will not release a nested group
        /// while it sits inside another — it gets ungrouped on the next pass. A pass without a single
        /// success means nothing more will move, and its failures go into the report.
        /// </summary>
        private static void Ungroup(Document doc, List<string> done, List<string> failures)
        {
            var total = 0;

            while (true)
            {
                var groups = ModelGroups(doc);
                if (groups.Count == 0)
                    break;

                var before = total;
                var stuck = new List<string>();

                foreach (var group in groups)
                {
                    var name = SafeName(group);

                    try
                    {
                        // Revit will not ungroup a pinned group, and "ungroup everything" without
                        // unpinning would turn into a list of refusals.
                        if (group.Pinned)
                            group.Pinned = false;

                        group.UngroupMembers();
                        total++;
                    }
                    catch (Exception exception)
                    {
                        stuck.Add("Group \"" + name + "\" — " + exception.Message);
                    }
                }

                if (total == before)
                {
                    failures.AddRange(stuck);
                    break;
                }
            }

            if (total > 0)
                done.Add("Model groups ungrouped: " + total + ".");
        }

        // ───────────────────────────── unused families ─────────────────────────────

        /// <summary>
        /// Deletes the loaded families and types nothing in the model refers to.
        /// A family whose types are all free is deleted whole — otherwise it would be left hanging in
        /// the browser as an empty branch.
        /// </summary>
        private static void PurgeFamilies(Document doc, List<string> done, List<string> notes, List<string> failures)
        {
            var families = new List<ElementId>();
            var symbols = new List<ElementId>();
            var busy = 0;

            foreach (var group in UnusedSymbols(doc).GroupBy(symbol => FamilyIdOf(symbol)))
            {
                var free = new List<ElementId>();

                foreach (var symbol in group)
                {
                    if (HasInstances(symbol))
                        busy++;
                    else
                        free.Add(symbol.Id);
                }

                if (free.Count == 0)
                    continue;

                // The key is invalid when a type would not report its family: then we delete it on its
                // own rather than a whole branch of the browser.
                var family = group.Key == ElementId.InvalidElementId
                    ? null
                    : doc.GetElement(group.Key) as Family;

                if (family != null && AllSymbolsFree(family, free))
                    families.Add(family.Id);
                else
                    symbols.AddRange(free);
            }

            var removedFamilies = Delete(doc, families, "Family", failures);
            var removedSymbols = Delete(doc, symbols, "Type", failures);

            if (removedFamilies > 0 || removedSymbols > 0)
            {
                done.Add("Unused families deleted: " + removedFamilies +
                         ", individual types: " + removedSymbols + ".");
            }

            if (busy > 0)
            {
                notes.Add("Types kept: " + busy +
                          " — Revit reported that model elements would go with them.");
            }
        }

        /// <summary>Every type of the family is free — so the family itself is needed by nobody.</summary>
        private static bool AllSymbolsFree(Family family, IReadOnlyList<ElementId> free)
        {
            try
            {
                var known = new HashSet<ElementId>(free);
                return family.GetFamilySymbolIds().All(id => known.Contains(id));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The types of loaded families that nothing in the model refers to.
        /// System types (walls, floors, pipes) do not appear here — they have no FamilySymbol.
        /// </summary>
        private static List<FamilySymbol> UnusedSymbols(Document doc)
        {
            var used = UsedTypeIds(doc);

            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(symbol => !used.Contains(symbol.Id))
                .ToList();
        }

        /// <summary>
        /// The ids of the types something in the document uses.
        ///
        /// The pass walks every placed element rather than FamilyInstances alone: a family type is held
        /// by tags, titleblocks and detail components too — their classes differ, but every one of them
        /// has a GetTypeId. Types are collected separately (a curtain panel and a nested type sit in a
        /// parameter rather than in GetTypeId), and so are legend components.
        ///
        /// Walking the whole model is not free, but there is no way around it: Revit 2022 does not
        /// expose the "Purge Unused" command through the API.
        /// </summary>
        private static HashSet<ElementId> UsedTypeIds(Document doc)
        {
            var used = new HashSet<ElementId>();

            foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                    used.Add(typeId);
            }

            foreach (var type in new FilteredElementCollector(doc).WhereElementIsElementType())
                AddReferences(type, used);

            foreach (var component in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_LegendComponents)
                         .WhereElementIsNotElementType())
                AddReferences(component, used);

            return used;
        }

        /// <summary>
        /// Adds to the set every element this element's parameters point at.
        /// References to itself and to its own family are skipped: every type has the built-in "Family"
        /// and "Type" parameters, and without this check every single one of them would count as in use.
        /// </summary>
        private static void AddReferences(Element element, HashSet<ElementId> used)
        {
            try
            {
                var symbol = element as FamilySymbol;
                var ownFamily = symbol == null ? ElementId.InvalidElementId : FamilyIdOf(symbol);

                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter.StorageType != StorageType.ElementId)
                        continue;

                    var id = parameter.AsElementId();

                    if (id == null || id == ElementId.InvalidElementId || id == element.Id || id == ownFamily)
                        continue;

                    used.Add(id);
                }
            }
            catch (Exception)
            {
                // The element would not let its parameters be read — we simply take nothing from it.
            }
        }

        /// <summary>
        /// Whether the model holds elements Revit would take away together with this type.
        ///
        /// The GetTypeId pass catches almost everything, but a reference can also be indirect, and the
        /// price of a mistake is erased geometry — so right before deleting we ask Revit itself. The
        /// "not types" filter leaves only placed elements in the answer: the type itself does not appear
        /// in it.
        /// </summary>
        private static bool HasInstances(FamilySymbol symbol)
        {
            try
            {
                var dependents = symbol.GetDependentElements(new ElementIsElementTypeFilter(true));
                return dependents != null && dependents.Count > 0;
            }
            catch (Exception)
            {
                // We could not ask — we treat it as in use: under-cleaning is safer than erasing too much.
                return true;
            }
        }

        private static ElementId FamilyIdOf(FamilySymbol symbol)
        {
            try
            {
                var family = symbol.Family;
                return family == null ? ElementId.InvalidElementId : family.Id;
            }
            catch (Exception)
            {
                return ElementId.InvalidElementId;
            }
        }

        // ───────────────────────────── deletion ─────────────────────────────

        private static void Removed(
            Document doc,
            IReadOnlyList<ElementId> ids,
            string what,
            string report,
            List<string> done,
            List<string> failures)
        {
            var count = Delete(doc, ids, what, failures);

            if (count > 0)
                done.Add(report + count + ".");
        }

        /// <summary>
        /// Deletes the elements one by one: a failure on one must not wreck the whole batch.
        /// Revit takes related things along in one go — a dependent view with its parent, the types with
        /// their family — so before every deletion we check whether the element is still alive.
        /// </summary>
        private static int Delete(Document doc, IReadOnlyList<ElementId> ids, string what, List<string> failures)
        {
            var count = 0;

            foreach (var id in ids)
            {
                var name = SafeName(doc, id);

                try
                {
                    if (doc.GetElement(id) == null)
                    {
                        count++;
                        continue;
                    }

                    var removed = doc.Delete(id);

                    if (removed != null && removed.Count > 0)
                        count++;
                    else
                        failures.Add(what + " \"" + name + "\" — Revit would not release the element for deletion");
                }
                catch (Exception exception)
                {
                    failures.Add(what + " «" + name + "» — " + exception.Message);
                }
            }

            return count;
        }

        private static string SafeName(Document doc, ElementId id)
        {
            try
            {
                var element = doc.GetElement(id);
                return element == null ? "id " + id.IntegerValue : SafeName(element);
            }
            catch (Exception)
            {
                return "id " + id.IntegerValue;
            }
        }

        private static string SafeName(Element element)
        {
            try
            {
                var name = element.Name;
                return string.IsNullOrEmpty(name) ? "id " + element.Id.IntegerValue : name;
            }
            catch (Exception)
            {
                return "id " + element.Id.IntegerValue;
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(
            IReadOnlyList<string> done,
            IReadOnlyList<string> notes,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
        {
            var text = done.Count > 0
                ? "Cleanup complete.\n\n• " + string.Join("\n• ", done)
                : "Nothing was removed from the model.";

            if (notes.Count > 0)
                text += "\n\n" + string.Join("\n", notes);

            if (failures.Count > 0)
            {
                const int limit = 15;
                text += "\n\nCould not be removed (" + failures.Count + "):\n• " +
                        string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… and " + (failures.Count - limit) + " more";
            }

            if (warnings.Count > 0)
            {
                const int limit = 5;
                text += "\n\nRevit warnings (" + warnings.Count + "):\n• " +
                        string.Join("\n• ", warnings.Take(limit));

                if (warnings.Count > limit)
                    text += "\n… and " + (warnings.Count - limit) + " more";
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
