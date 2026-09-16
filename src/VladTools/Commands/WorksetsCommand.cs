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
    /// The "Worksets" button: shows every user workset of the open project and removes the checked
    /// ones in a batch, either carrying their contents into another workset or deleting the contents
    /// along with them.
    ///
    /// In Revit itself this is "Collaborate → Worksets", one workset at a time: select, press
    /// "Delete", answer the question about the elements, confirm — and the same again for the next
    /// one. After a model arrives from a consultant there are dozens of them.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class WorksetsCommand : IExternalCommand
    {
        private const string DialogTitle = "Worksets";

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
                    "The project is not workshared, so it has no worksets.\n" +
                    "They appear once \"Collaborate → Worksets\" turns worksharing on.");
                return Result.Cancelled;
            }

            try
            {
                var worksets = Survey(doc);
                if (worksets.Count == 0)
                {
                    TaskDialog.Show(DialogTitle,
                        "The project has no user worksets — only Revit's own (\"Shared Levels and Grids\", " +
                        "the family and view worksets), and those cannot be removed.");
                    return Result.Cancelled;
                }

                var window = new WorksetsWindow(worksets, doc.Title);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                Remove(doc, window);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── what the project holds ─────────────────────────────

        /// <summary>
        /// The project's user worksets, with what each of them holds. Revit's own worksets (any
        /// <c>WorksetKind</c> other than <c>UserWorkset</c>) are left out: <c>CanDeleteWorkset</c>
        /// refuses them by definition, and offering them would only fill the table with rows that say no.
        /// </summary>
        private static IReadOnlyList<WorksetInfo> Survey(Document doc)
        {
            var active = doc.GetWorksetTable().GetActiveWorksetId();

            return new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .ToWorksets()
                .OrderBy(workset => workset.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(workset =>
                {
                    var count = Count(doc, workset);

                    return new WorksetInfo(
                        workset.UniqueId,
                        workset.Name,
                        count ?? 0,
                        count.HasValue,
                        workset.IsOpen,
                        workset.Id == active,
                        workset.IsEditable,
                        workset.Owner);
                })
                .ToList();
        }

        /// <summary>
        /// How many elements stand in the workset, or <c>null</c> when that could not be established.
        ///
        /// **A closed workset is never counted, and the zero it would otherwise report is a lie.** A
        /// <c>FilteredElementCollector</c> does not see elements in a closed workset, and the API
        /// offers no way to open one in an already-open document — <c>WorksetConfiguration</c> only
        /// applies to opening a document or a link. On a button that offers to delete the contents,
        /// "0 elements" for a workset full of walls is the worst kind of silent failure, so "empty"
        /// and "not counted" are kept apart all the way to the confirmation dialog.
        /// </summary>
        private static int? Count(Document doc, Workset workset)
        {
            if (!workset.IsOpen)
                return null;

            try
            {
                return new FilteredElementCollector(doc)
                    .WherePasses(new ElementWorksetFilter(workset.Id))
                    .GetElementCount();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ───────────────────────────── removal ─────────────────────────────

        /// <summary>
        /// Carries out what the window agreed to. The order of the three phases is not a matter of
        /// taste — see <see cref="Checkout"/> and <see cref="Delete"/>.
        /// </summary>
        private static void Remove(Document doc, WorksetsWindow window)
        {
            var done = new List<string>();
            var notes = new List<string>();
            var failures = new List<string>();

            // The ids are resolved afresh rather than carried in from the window: by Autodesk's own
            // documentation a WorksetId changes on synchronising with the central model and only the
            // GUID is stable — the same rule "Link Manager" keeps before loading links.
            var table = doc.GetWorksetTable();

            var targets = new List<KeyValuePair<string, WorksetId>>();
            foreach (var row in window.Selected)
            {
                var workset = table.GetWorkset(row.Info.UniqueId);
                if (workset == null)
                    failures.Add(row.Name + " — the workset is no longer in the project");
                else
                    targets.Add(new KeyValuePair<string, WorksetId>(row.Name, workset.Id));
            }

            var moving = window.ElementAction == WorksetElementAction.Move;
            var destination = WorksetId.InvalidWorksetId;

            if (moving)
            {
                var refuge = table.GetWorkset(window.DestinationId);
                if (refuge == null)
                {
                    TaskDialog.Show(DialogTitle,
                        "The workset \"" + window.Destination + "\" the elements were to move into is no longer " +
                        "in the project. Nothing was removed.");
                    return;
                }

                destination = refuge.Id;
            }

            if (targets.Count > 0)
            {
                var ids = targets.Select(target => target.Value).ToList();

                Checkout(doc, ids, destination, notes);
                Vacate(doc, ids, destination, notes, failures);
                Delete(doc, targets, moving, destination, window.Destination, done, notes, failures);
            }

            Report(done, notes, failures);
        }

        /// <summary>
        /// Takes ownership of the worksets about to go, and of the one the elements move into.
        ///
        /// **This must happen before any transaction opens, and that order is forced by the API
        /// itself:** <c>CheckoutWorksets</c> throws when a transaction, sub-transaction or
        /// transaction group is open ("Operation is not permitted when there is any open …"), while
        /// <c>DeleteWorkset</c> throws when there is none. The same shape as "Link Manager", where
        /// <c>LoadFrom</c> has to run outside a transaction and <c>Create</c> inside one.
        ///
        /// Without the checkout every deletion would simply refuse: <c>CanDeleteWorkset</c> returns
        /// false for a workset the current user does not own, and in a freshly opened local model
        /// nobody owns anything — the button would look broken on the very models it is for. A
        /// failure here is not fatal: it becomes a note, and <c>CanDeleteWorkset</c> gets the last word.
        /// </summary>
        private static void Checkout(Document doc, IReadOnlyList<WorksetId> targets, WorksetId destination, List<string> notes)
        {
            var wanted = new List<WorksetId>(targets);

            // The destination is checked out too: elements arriving in it are an edit to that workset
            // as much as to the one being deleted.
            if (destination != WorksetId.InvalidWorksetId)
                wanted.Add(destination);

            try
            {
                var owned = WorksharingUtils.CheckoutWorksets(doc, wanted);

                if (owned != null && owned.Count < wanted.Count)
                    notes.Add("Worksets that could not be checked out: " + (wanted.Count - owned.Count) +
                              " — somebody else owns them, and those will not be removed.");
            }
            catch (Exception exception)
            {
                // A workshared model with no central yet, or the central out of reach. Say so and
                // carry on: CanDeleteWorkset below decides whether it actually mattered.
                notes.Add("The worksets could not be checked out (" + LinkCatalog.Short(exception.Message) +
                          "). Whatever is owned already is still removed.");
            }
        }

        /// <summary>
        /// Steps off the active workset when it is one of those going. Revit always has an active
        /// workset — new elements need somewhere to land — and it is not something that can be
        /// deleted from under itself. This is session state, so it is set outside the transaction and
        /// deliberately not undone by Ctrl+Z, exactly as the workset switch in "Base File".
        /// </summary>
        private static void Vacate(
            Document doc,
            IReadOnlyList<WorksetId> targets,
            WorksetId destination,
            List<string> notes,
            List<string> failures)
        {
            var table = doc.GetWorksetTable();
            var active = table.GetActiveWorksetId();

            if (!targets.Contains(active))
                return;

            var doomed = new HashSet<WorksetId>(targets);

            var refuge = destination != WorksetId.InvalidWorksetId
                ? destination
                : new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToWorksets()
                    .Where(workset => !doomed.Contains(workset.Id))
                    .Select(workset => workset.Id)
                    .FirstOrDefault() ?? WorksetId.InvalidWorksetId;

            if (refuge == WorksetId.InvalidWorksetId)
            {
                failures.Add("There is no workset left to make active — the active one cannot be removed.");
                return;
            }

            try
            {
                table.SetActiveWorksetId(refuge);

                var name = table.GetWorkset(refuge)?.Name ?? string.Empty;
                notes.Add("The active workset was one of those being removed — it is now \"" + name + "\".");
            }
            catch (Exception exception)
            {
                failures.Add("Could not step off the active workset: " + LinkCatalog.Short(exception.Message));
            }
        }

        /// <summary>
        /// Deletes the worksets — one transaction each, all of them inside a transaction group that
        /// is assimilated at the end.
        ///
        /// **This is the one place in the add-in that does not put the whole batch in a single
        /// transaction, and the reason is in Autodesk's own documentation for <c>DeleteWorkset</c>:**
        /// it names a *transaction* failure as a possible outcome ("Deleting all open views in a
        /// project is not allowed") — a failure that rolls the transaction back rather than throwing
        /// on one item. Sharing one transaction, a single such workset would take every other
        /// deletion down with it, after the user had already confirmed. A transaction group
        /// assimilated at the end buys back what the batch rule was protecting: the whole run still
        /// undoes with one Ctrl+Z.
        ///
        /// The commit status is checked rather than assumed — <c>Transaction.Commit()</c> returns one,
        /// and Revit can roll a transaction back there without throwing (the same trap as in
        /// "Accept Changes").
        /// </summary>
        private static void Delete(
            Document doc,
            IReadOnlyList<KeyValuePair<string, WorksetId>> targets,
            bool moving,
            WorksetId destination,
            string destinationName,
            List<string> done,
            List<string> notes,
            List<string> failures)
        {
            var warnings = new WarningSuppressor();
            var removed = 0;

            using (var group = new TransactionGroup(doc, "Remove worksets"))
            {
                group.Start();

                foreach (var target in targets)
                {
                    if (DeleteOne(doc, target.Key, target.Value, moving, destination, warnings, failures))
                        removed++;
                }

                if (removed == 0)
                    group.RollBack();
                else
                    group.Assimilate();
            }

            if (removed > 0)
            {
                done.Add("Worksets removed: " + removed + " of " + targets.Count);
                done.Add(moving
                    ? "Their contents moved into the workset \"" + destinationName + "\""
                    : "Their contents were deleted together with them");
            }

            const int limit = 5;

            foreach (var warning in warnings.Messages.Take(limit))
                notes.Add("Revit warning: " + warning);

            if (warnings.Messages.Count > limit)
                notes.Add("… and " + (warnings.Messages.Count - limit) + " more Revit warnings");
        }

        /// <summary>One workset, in a transaction of its own. A refusal lands in the list and the rest carry on.</summary>
        private static bool DeleteOne(
            Document doc,
            string name,
            WorksetId id,
            bool moving,
            WorksetId destination,
            WarningSuppressor warnings,
            List<string> failures)
        {
            try
            {
                using (var settings = moving
                    ? new DeleteWorksetSettings(DeleteWorksetOption.MoveElementsToWorkset, destination)
                    : new DeleteWorksetSettings())
                {
                    // Asked with the very settings that will be used: whether a workset can go depends
                    // on the answer about its contents too.
                    if (!WorksetTable.CanDeleteWorkset(doc, id, settings))
                    {
                        failures.Add(name + " — Revit will not delete this workset. It is owned by another user, " +
                                     "or it is the last user workset left in the project.");
                        return false;
                    }

                    using (var transaction = new Transaction(doc, "Remove workset \"" + name + "\""))
                    {
                        transaction.Start();

                        // Removing a workset with its contents drags a pile of Revit warnings along
                        // (a wall losing its host, a view losing its elements). A modal dialog per
                        // warning would wreck the batch, so they are gathered into the report instead.
                        var options = transaction.GetFailureHandlingOptions();
                        options.SetFailuresPreprocessor(warnings);
                        transaction.SetFailureHandlingOptions(options);

                        WorksetTable.DeleteWorkset(doc, id, settings);

                        var status = transaction.Commit();
                        if (status == TransactionStatus.Committed)
                            return true;

                        failures.Add(name + " — Revit rolled the deletion back (" + status + "). That happens " +
                                     "when the workset holds every open view of the project.");
                        return false;
                    }
                }
            }
            catch (Exception exception)
            {
                failures.Add(name + " — " + LinkCatalog.Short(exception.Message));
                return false;
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(IReadOnlyList<string> done, IReadOnlyList<string> notes, IReadOnlyList<string> failures)
        {
            var text = done.Count > 0
                ? "Done.\n\n• " + string.Join("\n• ", done)
                : "No workset was removed.";

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

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
