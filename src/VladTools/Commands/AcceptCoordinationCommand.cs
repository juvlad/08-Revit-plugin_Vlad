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
    /// it computes the differences itself (<see cref="CoordinationCatalog"/>) and **moves the
    /// elements itself** — that is, it does exactly what the "Modify Grid" / "Move" action in the
    /// dialog would do, not what "Accept Difference" does (which leaves the grid where it is and
    /// merely stops reporting it). Once an element is back in place, Revit stops counting it as a
    /// difference, and "Coordination Review" empties itself.
    ///
    /// **That the move went through is checked, never assumed** — see <see cref="Verify"/> and the
    /// commit status below. An edit that Revit accepted without an error may still leave the
    /// element where it was, and a report claiming a shift that never happened is worse than no
    /// button at all.
    ///
    /// A grid or a level that has vanished from the coordination file can be deleted too — but only
    /// at the user's say-so: a level takes everything standing on it with it, so such a row is never
    /// checked by default, becomes checkable only once the window's "Delete…" box is on, and is
    /// named in the confirmation together with the count of what is hosted on it.
    ///
    /// What the command still does not do: it does not set up monitoring on new link elements —
    /// creating monitoring links is not in the API at all. Those are shown in the window as a line,
    /// to be copied in by hand through "Copy/Monitor".
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AcceptCoordinationCommand : IExternalCommand
    {
        private const string DialogTitle = "Accept Coordination Changes";

        /// <summary>The comment a batch run synchronises somebody else's model with.</summary>
        private const string SyncComment = "Grids and levels put in line with the coordination file (Vlad Tools)";

        /// <summary>
        /// An edit Revit accepted, together with the link it was measured against.
        ///
        /// The link travels with the row because checking the result means reading that same link
        /// once more, and in a batch run a single model may hold several: the coordination file of
        /// the building and the overall site placement are two different links, and a grid checked
        /// against the wrong one would be declared "still off" for no reason.
        /// </summary>
        private sealed class Change
        {
            public Change(CoordinationChangeRow row, CoordinationScan scan)
            {
                Row = row;
                Scan = scan;
            }

            public CoordinationChangeRow Row { get; }

            public CoordinationScan Scan { get; }
        }

        /// <summary>
        /// Everything one run produced, kept apart by outcome rather than poured into a single
        /// "done" list. The separation is the point: "asked for" and "carried out" are different
        /// things here, and the report must not blur them.
        /// </summary>
        private sealed class Outcome
        {
            /// <summary>Rows Revit accepted the edit for — the ones to check against the file afterwards.</summary>
            public readonly List<Change> Applied = new List<Change>();

            /// <summary>Checked afterwards and really standing where the file has them.</summary>
            public readonly List<string> Moved = new List<string>();

            /// <summary>Renamed to follow the link.</summary>
            public readonly List<string> Renamed = new List<string>();

            /// <summary>Deleted as gone from the coordination file.</summary>
            public readonly List<string> Removed = new List<string>();

            /// <summary>The edit raised no error, yet the element is still off.</summary>
            public readonly List<string> Stuck = new List<string>();

            /// <summary>There was nothing to check the result against.</summary>
            public readonly List<string> Unverified = new List<string>();

            public readonly List<string> Failures = new List<string>();

            /// <summary>Lines that belong at the end of the report — what was done besides editing.</summary>
            public readonly List<string> Notes = new List<string>();

            /// <summary>Something reached the document and the transaction has to be committed.</summary>
            public bool Any => Applied.Count > 0 || Renamed.Count > 0 || Removed.Count > 0;

            /// <summary>Everything above is undone — Revit rolled the batch back.</summary>
            public void Forget()
            {
                Applied.Clear();
                Renamed.Clear();
                Removed.Clear();
            }
        }

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
                var outcome = new Outcome();
                var scans = CoordinationCatalog.Scan(doc, outcome.Failures);

                if (scans.Count == 0)
                {
                    // Nothing monitors a link **here** — which says nothing about the other models,
                    // and this command is often started from an empty coordination model of one's
                    // own. So the refusal offers the batch instead of being a dead end.
                    return Offer(commandData.Application,
                        "The project has no grid or level monitoring a link.\n\n" +
                        "There is nothing to compare against: the button works off monitoring links, and " +
                        "those are only ever set up by hand — \"Collaborate → Copy/Monitor → Select Link\" " +
                        "(the \"Base File\" button also opens this mode as its last step)." +
                        Note(outcome.Failures));
                }

                var window = new AcceptCoordinationWindow(scans);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                var answer = window.ShowDialog();

                // "Other models…" closes this window rather than opening a second one on top of it:
                // the two decide different things, and what is chosen here means nothing there.
                if (window.WantsBatch)
                    return Batch(commandData.Application);

                if (answer != true)
                    return Result.Cancelled;

                var rows = window.Selected;
                var chosen = window.Chosen;

                var warnings = new WarningSuppressor();
                var status = TransactionStatus.RolledBack;

                using (var transaction = new Transaction(doc, "Accept coordination changes"))
                {
                    transaction.Start();

                    // Shifting a level drags everything standing on it along, and Revit will almost
                    // certainly warn about something: broken joins, elements left behind. A modal
                    // dialog for each would turn one button into clicking through dialogs.
                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(warnings);
                    transaction.SetFailureHandlingOptions(options);

                    // Deletions come first, and the order matters twice over: a name held by an
                    // element that is about to go frees up for the rename pass right away, and
                    // nothing is moved a moment before being deleted.
                    foreach (var row in rows.Where(item => item.Kind == CoordinationChangeKind.Missing))
                        Remove(doc, row, outcome);

                    foreach (var row in rows.Where(item => item.Kind == CoordinationChangeKind.Position))
                        Move(doc, row, chosen, outcome);

                    Rename(doc, rows.Where(item => item.Kind == CoordinationChangeKind.Name).ToList(), outcome);

                    if (!outcome.Any)
                    {
                        transaction.RollBack();
                    }
                    else
                    {
                        // Commit returns a status, and it is not decoration: an error resolved at
                        // commit time can roll the whole batch back, and everything gathered above
                        // then describes a document that no longer exists. Reporting that list
                        // without looking at the status is how a button comes to claim it moved a
                        // grid that never moved.
                        status = transaction.Commit();
                    }
                }

                if (outcome.Any && status != TransactionStatus.Committed)
                {
                    outcome.Failures.Add(
                        "Revit rolled the whole batch back when committing (" + status + ") — the project is " +
                        "unchanged. That happens when an edit cannot be carried out: a datum held by a " +
                        "constraint or a lock, an element inside a group, or something that could not follow " +
                        "the level being moved. Try the changes a few at a time to find which one it is.");

                    outcome.Forget();
                }
                else
                {
                    Verify(doc, outcome);
                }

                if (window.OpenReview)
                    OpenReview(commandData.Application, outcome);

                Report(chosen, outcome, warnings.Messages);
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
        ///
        /// A row lands in <see cref="Outcome.Applied"/> rather than straight in the report: all
        /// that is known at this point is that Revit raised no error. Whether the element actually
        /// moved is settled after the commit, in <see cref="Verify"/>.
        /// </summary>
        private static void Move(Document doc, CoordinationChangeRow row, CoordinationScan scan, Outcome outcome)
        {
            var element = doc.GetElement(row.Update.HostId);
            if (element == null)
            {
                outcome.Failures.Add(row.Title + " — this element is no longer in the project");
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

                outcome.Applied.Add(new Change(row, scan));
            }
            catch (Exception exception)
            {
                outcome.Failures.Add(row.Title + " — could not be repositioned: " + LinkCatalog.Short(exception.Message));
            }
            finally
            {
                Repin(element, pinned, outcome.Failures, row.Title);
            }
        }

        /// <summary>
        /// Deletes a grid or a level that is gone from the coordination file.
        ///
        /// This is the one thing here that takes something out of the project rather than moving
        /// it, and the whole path to it is deliberate: the row is never checked by default, it only
        /// becomes checkable once the window's own "Delete…" box is on, and the confirmation names
        /// every element and the count of what is standing on it. By that point the decision has
        /// been made and the command's job is to carry it out honestly — including saying how much
        /// really went, which is what <c>doc.Delete</c> returns and the estimate in the window
        /// could only guess at.
        /// </summary>
        private static void Remove(Document doc, CoordinationChangeRow row, Outcome outcome)
        {
            var element = doc.GetElement(row.Update.HostId);
            if (element == null)
            {
                // Not a failure: a level deleted a moment ago may well have taken this one along.
                outcome.Removed.Add(row.Title + " — was already gone by the time its turn came");
                return;
            }

            var pinned = false;

            try
            {
                // The same pin as in Move: a base file's grids and levels are almost always pinned,
                // and Revit will not delete a pinned element either.
                pinned = element.Pinned;
                if (pinned)
                    element.Pinned = false;

                var deleted = doc.Delete(element.Id);

                // The returned set holds the element itself as well; what is worth reporting is
                // what went **with** it.
                var count = deleted == null ? 0 : Math.Max(0, deleted.Count - 1);

                outcome.Removed.Add(row.Title + (count > 0 ? " — and " + count + " element(s) with it" : string.Empty));
                return;
            }
            catch (Exception exception)
            {
                outcome.Failures.Add(row.Title + " — could not be deleted: " + LinkCatalog.Short(exception.Message));
            }

            // Only reached when the deletion did not go through: the element is still in the
            // project and must be left exactly as it was found, pin included.
            Repin(element, pinned, outcome.Failures, row.Title);
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
        private static void Rename(Document doc, IReadOnlyList<CoordinationChangeRow> rows, Outcome outcome)
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
                        outcome.Failures.Add(row.Title + " — this element is no longer in the project");
                        continue;
                    }

                    try
                    {
                        element.Name = row.Update.NewName;
                        outcome.Renamed.Add(row.Title + " — renamed to \"" + row.Update.NewName + "\"");
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
                        outcome.Failures.Add(row.Title + " — could not be renamed: " + reasons[row]);

                    return;
                }

                left = stuck;
            }
        }

        // ───────────────────────────── checking the result ─────────────────────────────

        /// <summary>
        /// Reads the elements back after the commit and compares them against the coordination file
        /// once more.
        ///
        /// **A move that raised no error is not a move that happened.**
        /// <c>ElementTransformUtils.MoveElement</c> reports that Revit took the request, and that is
        /// all: a constraint, a lock, a group or a failure resolved at commit time can leave the
        /// element exactly where it was. Without this pass the button would print the shift it had
        /// asked for and the user would find the grid unmoved — the button looking as though it
        /// merely "accepted the difference" instead of applying it. Exactly the same lesson as
        /// checking link worksets after loading, and it is answered the same way: three outcomes,
        /// with "could not be checked" kept apart from "worked" rather than merged into it.
        ///
        /// Renaming is not checked this way: <c>Element.Name</c> throws when Revit refuses, so there
        /// is nothing a second read could add.
        /// </summary>
        private static void Verify(Document doc, Outcome outcome)
        {
            if (outcome.Applied.Count == 0)
                return;

            // One verifier per link rather than per row: reading a link's grids and levels is the
            // expensive half of this, and several rows almost always share a link.
            var verifiers = new Dictionary<ElementId, CoordinationCatalog.Verifier>();

            foreach (var change in outcome.Applied)
            {
                var row = change.Row;
                var asked = row.Title + " — " + row.Detail;

                var verifier = change.Scan == null ? null : Verifier(doc, change.Scan, verifiers);

                if (verifier == null)
                {
                    outcome.Unverified.Add(asked);
                    continue;
                }

                var element = doc.GetElement(row.Update.HostId);
                if (element == null)
                {
                    outcome.Unverified.Add(row.Title + " — the element is no longer in the project");
                    continue;
                }

                string detail;
                switch (verifier.Check(element, out detail))
                {
                    case DatumVerdict.Aligned:
                        outcome.Moved.Add(asked);
                        break;

                    case DatumVerdict.Off:
                        outcome.Stuck.Add(row.Title + " — " + detail);
                        break;

                    default:
                        outcome.Unverified.Add(asked);
                        break;
                }
            }
        }

        /// <summary>The link's verifier, read once and kept for the rest of the run. Null stays null: an unloaded link is not read again on every row.</summary>
        private static CoordinationCatalog.Verifier Verifier(
            Document doc,
            CoordinationScan scan,
            Dictionary<ElementId, CoordinationCatalog.Verifier> cache)
        {
            CoordinationCatalog.Verifier verifier;
            if (cache.TryGetValue(scan.LinkId, out verifier))
                return verifier;

            verifier = CoordinationCatalog.CreateVerifier(doc, scan.LinkId);
            cache[scan.LinkId] = verifier;

            return verifier;
        }

        // ───────────────────────────── other models ─────────────────────────────

        /// <summary>
        /// The refusal that offers the batch instead of ending the conversation. A project where
        /// nothing monitors a link is the usual place to start this work from — somebody's own
        /// coordination model, or a fresh one — and the answer "there is nothing here" is true and
        /// useless at the same time.
        /// </summary>
        private static Result Offer(UIApplication application, string text)
        {
            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = "Nothing to compare in this project",
                MainContent = text,
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close
            };

            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Other models…",
                "Open a batch of models in turn and put the grids in each of them in line with the " +
                "coordination file linked into it.");

            return dialog.Show() == TaskDialogResult.CommandLink1
                ? Batch(application)
                : Result.Cancelled;
        }

        /// <summary>
        /// The batch: every checked model is opened in turn, its grids are put in line with the
        /// coordination file linked into it, and the model is synchronised back.
        ///
        /// **Nothing is deleted here, ever.** In the main window a grid gone from the coordination
        /// file can be deleted — behind a switch of its own, unchecked, with the count of what is
        /// standing on it in the confirmation. None of that can be done for a model nobody has
        /// opened: the count would be read from a model the user is not looking at, and the answer
        /// would be given for a dozen models at once. Such rows are counted in the report and left
        /// where they are; deleting one is a decision to take with that model open.
        ///
        /// The work runs straight through, model after model, with Revit frozen for the duration —
        /// the same as with the family scan in "Delete Shared Parameters", and for the same reason:
        /// a progress bar would mean doing the work on another thread, and the Revit API is single-threaded.
        /// </summary>
        private static Result Batch(UIApplication application)
        {
            var preferences = CoordinationPreferences.Load();

            var window = new BatchCoordinationWindow(preferences, LinkPreferences.Load());
            new WindowInteropHelper(window).Owner = application.MainWindowHandle;

            if (window.ShowDialog() != true)
                return Result.Cancelled;

            var models = window.Selected;
            var worksets = window.Worksets;

            var lines = new List<string>();
            var moved = new List<string>();
            var stuck = new List<string>();
            var unverified = new List<string>();
            var failures = new List<string>();
            var warnings = new List<string>();

            foreach (var model in models)
            {
                RunOne(application.Application, model, worksets, window.WithLevels, window.WithRename,
                    lines, moved, stuck, unverified, failures, warnings);
            }

            ReportBatch(models.Count, lines, moved, stuck, unverified, failures, warnings);
            return Result.Succeeded;
        }

        /// <summary>
        /// One model of the batch: open, compare, apply, check, synchronise, close.
        ///
        /// Everything that goes wrong lands in a list and the next model is taken — the same rule as
        /// for a loop over elements inside a transaction. A model is the unit of work here: its own
        /// transaction, its own check afterwards, its own synchronisation.
        /// </summary>
        private static void RunOne(
            Autodesk.Revit.ApplicationServices.Application app,
            BatchModelRow model,
            IReadOnlyList<string> worksets,
            bool levels,
            bool rename,
            List<string> lines,
            List<string> moved,
            List<string> stuck,
            List<string> unverified,
            List<string> failures,
            List<string> warnings)
        {
            var opened = BatchCoordination.Open(app, model.Entry, worksets, failures);

            if (opened == null)
            {
                lines.Add(model.Name + " — not opened, the reason is below");
                return;
            }

            try
            {
                var doc = opened.Document;
                var outcome = new Outcome();
                var messages = new List<string>();

                var scans = CoordinationCatalog.Scan(doc, outcome.Failures);
                var wanted = Wanted(scans, levels, rename);
                var pending = Borrow(doc, wanted, outcome);

                if (pending.Count == 0)
                {
                    lines.Add(model.Name + " — " + Idle(scans, opened, wanted.Count) + Left(scans, levels, rename));
                    BatchCoordination.Relinquish(opened, failures);
                }
                else
                {
                    var suppressor = new WarningSuppressor();
                    var status = TransactionStatus.RolledBack;

                    using (var transaction = new Transaction(doc, "Accept coordination changes"))
                    {
                        transaction.Start();

                        var options = transaction.GetFailureHandlingOptions();
                        options.SetFailuresPreprocessor(suppressor);
                        transaction.SetFailureHandlingOptions(options);

                        foreach (var change in pending.Where(item => item.Row.Kind == CoordinationChangeKind.Position))
                            Move(doc, change.Row, change.Scan, outcome);

                        Rename(doc,
                            pending.Where(item => item.Row.Kind == CoordinationChangeKind.Name)
                                .Select(item => item.Row).ToList(),
                            outcome);

                        if (!outcome.Any)
                            transaction.RollBack();
                        else
                            status = transaction.Commit();
                    }

                    messages.AddRange(suppressor.Messages);

                    if (!outcome.Any)
                    {
                        lines.Add(model.Name + " — nothing reached the model, the reason is below");
                        BatchCoordination.Relinquish(opened, failures);
                    }
                    else if (status != TransactionStatus.Committed)
                    {
                        // The same trap as in the open project: Revit can roll the batch back at
                        // commit time, and everything gathered above then describes a model that
                        // does not exist. Here it matters twice over — the model is about to be
                        // synchronised, and a report claiming moves that never happened would send
                        // the user looking for them in someone else's file.
                        outcome.Failures.Add("Revit rolled the whole batch back when committing (" + status +
                                             ") — the model is unchanged.");
                        outcome.Forget();

                        lines.Add(model.Name + " — Revit rolled the changes back, the model is unchanged");
                        BatchCoordination.Relinquish(opened, failures);
                    }
                    else
                    {
                        Verify(doc, outcome);

                        var saved = BatchCoordination.Commit(opened, SyncComment, failures);

                        lines.Add(model.Name + " — " + Summary(outcome, saved) + Left(scans, levels, rename));

                        foreach (var line in outcome.Moved.Concat(outcome.Renamed))
                            moved.Add(model.Name + ": " + line);
                    }
                }

                foreach (var line in outcome.Stuck)
                    stuck.Add(model.Name + ": " + line);

                foreach (var line in outcome.Unverified)
                    unverified.Add(model.Name + ": " + line);

                foreach (var line in outcome.Failures)
                    failures.Add(model.Name + ": " + line);

                foreach (var line in messages)
                    warnings.Add(model.Name + ": " + line);
            }
            catch (Exception exception)
            {
                // One model that went wrong in a way nothing above expected must not take the rest
                // of the batch with it — the model is closed in "finally" either way.
                failures.Add(model.Name + " — " + LinkCatalog.Short(exception.Message));
                lines.Add(model.Name + " — the work broke off, the reason is below");
            }
            finally
            {
                BatchCoordination.Release(opened);
            }
        }

        /// <summary>
        /// What of the differences found the batch is allowed to apply: positions always, names on
        /// request, levels on request — and never a deletion.
        ///
        /// A single model may monitor several links, so the same element can turn up in two scans at
        /// once; it is taken once, with the link it was first measured against. Moving it twice
        /// would mean the second edit undoing the first.
        /// </summary>
        private static List<Change> Wanted(IReadOnlyList<CoordinationScan> scans, bool levels, bool rename)
        {
            var pending = new List<Change>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var scan in scans.Where(item => item.IsLoaded))
            {
                foreach (var row in scan.Rows)
                {
                    if (row.Kind != CoordinationChangeKind.Position &&
                        (row.Kind != CoordinationChangeKind.Name || !rename))
                        continue;

                    if (row.IsLevel && !levels)
                        continue;

                    if (row.Update == null || !seen.Add(row.Update.HostId + "|" + row.Kind))
                        continue;

                    pending.Add(new Change(row, scan));
                }
            }

            return pending;
        }

        /// <summary>
        /// Checks the elements out of the central model before editing them.
        ///
        /// In a local copy this is not a formality: an element somebody else has borrowed cannot be
        /// edited at all, and the only honest thing to do is to say who is holding it rather than
        /// report a Revit exception per grid. What nobody owns is borrowed here and handed straight
        /// back when the model is synchronised.
        /// </summary>
        private static List<Change> Borrow(Document doc, List<Change> pending, Outcome outcome)
        {
            if (pending.Count == 0 || !doc.IsWorkshared)
                return pending;

            try
            {
                WorksharingUtils.CheckoutElements(doc,
                    pending.Select(change => change.Row.Update.HostId).Distinct().ToList());
            }
            catch (Exception exception)
            {
                // Not fatal by itself: the ones that were not checked out are found below, one by one.
                outcome.Failures.Add("The elements could not be checked out in one go: " +
                                     LinkCatalog.Short(exception.Message));
            }

            var free = new List<Change>();

            foreach (var change in pending)
            {
                var owner = string.Empty;
                var status = CheckoutStatus.NotOwned;

                try
                {
                    status = WorksharingUtils.GetCheckoutStatus(doc, change.Row.Update.HostId, out owner);
                }
                catch (Exception)
                {
                    // Nothing could be found out about the element — let the edit itself decide.
                }

                if (status == CheckoutStatus.OwnedByOtherUser)
                {
                    outcome.Failures.Add(change.Row.Title + " — checked out by " +
                                         (string.IsNullOrEmpty(owner) ? "another user" : owner) +
                                         ", it cannot be edited until they relinquish it");
                    continue;
                }

                free.Add(change);
            }

            return free;
        }

        /// <summary>
        /// Why a model was left alone. Four different things hide behind "nothing was done", and
        /// they are not interchangeable: nothing to compare, nothing loaded to compare against,
        /// nothing this run was allowed to touch, and everything already in place. Only the last of
        /// them means the model is fine.
        /// </summary>
        private static string Idle(IReadOnlyList<CoordinationScan> scans, BatchModel model, int wanted)
        {
            if (scans.Count == 0)
            {
                return "nothing in it monitors a link (worksets opened: " +
                       (model.Worksets.Count == 0 ? "the model is not workshared" : string.Join(", ", model.Worksets)) + ")";
            }

            if (scans.All(scan => !scan.IsLoaded))
                return "the link it monitors is not loaded in it — there is nothing to compare against";

            // Everything found was dropped along the way — checked out by somebody else, and said so
            // one by one below. "Already in line with the file" here would be the opposite of true.
            if (wanted > 0)
                return "none of the " + wanted + " change(s) found could be made, the reason is below";

            var differences = scans
                .Where(scan => scan.IsLoaded)
                .SelectMany(scan => scan.Rows)
                .Count(row => row.Kind == CoordinationChangeKind.Position ||
                              row.Kind == CoordinationChangeKind.Name);

            return differences > 0
                ? "there are differences, but none this run was allowed to make"
                : "already in line with the coordination file";
        }

        /// <summary>What was found and deliberately not touched. Staying silent about this would make "done" mean "everything is in order".</summary>
        private static string Left(IReadOnlyList<CoordinationScan> scans, bool levels, bool rename)
        {
            var rows = scans.Where(scan => scan.IsLoaded).SelectMany(scan => scan.Rows).ToList();

            var parts = new List<string>();

            Count(parts, "gone from the file", rows.Count(row => row.Kind == CoordinationChangeKind.Missing));
            Count(parts, "new in the file", rows.Count(row => row.Kind == CoordinationChangeKind.New));
            Count(parts, "cannot be applied", rows.Count(row => row.Kind == CoordinationChangeKind.Unsupported));

            if (!levels)
                Count(parts, "levels", rows.Count(row => row.IsLevel && row.Kind == CoordinationChangeKind.Position));

            if (!rename)
                Count(parts, "names", rows.Count(row => row.Kind == CoordinationChangeKind.Name));

            return parts.Count == 0 ? string.Empty : "; left alone: " + string.Join(", ", parts);
        }

        private static void Count(List<string> parts, string what, int count)
        {
            if (count > 0)
                parts.Add(what + " — " + count);
        }

        /// <summary>The model's own line in the report: what really went into it.</summary>
        private static string Summary(Outcome outcome, bool saved)
        {
            var parts = new List<string>();

            Count(parts, "put in line with the file", outcome.Moved.Count);
            Count(parts, "renamed", outcome.Renamed.Count);
            Count(parts, "Revit did not carry out", outcome.Stuck.Count);
            Count(parts, "could not be checked", outcome.Unverified.Count);

            var text = parts.Count == 0 ? "nothing changed" : string.Join(", ", parts);

            // The model was edited and the edit did not reach the central file — the most important
            // thing that can be said about this model, so it goes first, not into a footnote.
            return saved ? text : "NOT SYNCHRONISED, the changes are lost — " + text;
        }

        private static void ReportBatch(
            int count,
            IReadOnlyList<string> lines,
            IReadOnlyList<string> moved,
            IReadOnlyList<string> stuck,
            IReadOnlyList<string> unverified,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
        {
            var text = "Models worked on: " + count + ".";

            text += Section("Models", lines);
            text += Section("Put in line with the coordination file", moved);

            if (stuck.Count > 0)
            {
                text += Section("Revit did not carry these out — the elements are still where they were", stuck);
                text += "\nSuch an element is usually held in place: a constraint or a lock on the datum, " +
                        "a group it sits in, or a pin that could not be removed. Open that model and move " +
                        "it by hand.\n";
            }

            text += Section("Could not be checked against the file — look at them by eye", unverified);
            text += Section("Could not be done", failures);
            text += Section("Revit warned", warnings);

            text += "\n\nNothing was deleted: grids and levels gone from the coordination file are only " +
                    "counted here. Deleting one is a decision to take with that model open — the main " +
                    "window of this button does it.";

            TaskDialog.Show(DialogTitle, text.Trim());
        }

        // ───────────────────────────── coordination review ─────────────────────────────

        /// <summary>
        /// Opens "Coordination Review → Select Link".
        ///
        /// Not a continuation of the work but a check on it: nothing in this dialog can be pressed
        /// through the API, but once the elements are back in line with the file, its list should
        /// be empty. The command is queued with Revit and fires after the report closes.
        /// </summary>
        private static void OpenReview(UIApplication application, Outcome outcome)
        {
            try
            {
                var command = RevitCommandId.LookupPostableCommandId(PostableCommand.CoordinationSelectLink);
                if (command == null || !application.CanPostCommand(command))
                {
                    outcome.Failures.Add("\"Coordination Review\" is not available right now — open it by hand " +
                                         "(\"Collaborate → Coordination Review\").");
                    return;
                }

                application.PostCommand(command);
                outcome.Notes.Add("Opening \"Coordination Review → Select Link\" — check that the list is empty.");
            }
            catch (Exception exception)
            {
                outcome.Failures.Add("Could not open \"Coordination Review\": " + LinkCatalog.Short(exception.Message));
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(CoordinationScan scan, Outcome outcome, IReadOnlyList<string> warnings)
        {
            // Every section brings its own blank line in front of it, so the text is trimmed at the
            // end rather than each piece guessing what came before it.
            var text = scan != null ? "Coordination file: " + scan.LinkName + "." : string.Empty;

            var accepted = outcome.Moved.Concat(outcome.Renamed).ToList();

            if (accepted.Count == 0 && outcome.Removed.Count == 0 && outcome.Stuck.Count == 0)
                text += "\n\nNothing was done.";
            else
                text += Section("Accepted", accepted);

            // Deletions get a section of their own rather than a line among the moves: it is the
            // one part of the run that cannot be checked by looking at the model afterwards.
            text += Section("Deleted — gone from the coordination file", outcome.Removed);

            // The section that must never be folded into "Accepted": Revit took the edit without
            // an error and the element still did not end up where the file has it.
            if (outcome.Stuck.Count > 0)
            {
                text += Section("Revit did not carry these out — the elements are still where they were",
                    outcome.Stuck);

                text += "\nSuch an element is usually held in place: a constraint or a lock on the datum, " +
                        "a group it sits in, or a pin that could not be removed. Move it by hand and " +
                        "run the button again.\n";
            }

            text += Section("Could not be checked against the file — apply by eye", outcome.Unverified);
            text += Section("Could not be done", outcome.Failures);
            text += Section("Revit warned", warnings);

            if (outcome.Notes.Count > 0)
                text += "\n\n" + string.Join("\n", outcome.Notes);

            TaskDialog.Show(DialogTitle, text.Trim());
        }

        /// <summary>One report section, or nothing at all when the list is empty.</summary>
        private static string Section(string caption, IReadOnlyList<string> lines)
        {
            const int limit = 15;

            if (lines.Count == 0)
                return string.Empty;

            var text = "\n\n" + caption + " (" + lines.Count + "):\n• " + string.Join("\n• ", lines.Take(limit));

            return lines.Count > limit ? text + "\n… and " + (lines.Count - limit) + " more" : text;
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
