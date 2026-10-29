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
    /// Batch-loading Revit links: files, Revit Server and BIM360 — as one list, with one
    /// placement and one workset setup for the whole batch.
    ///
    /// A routine job: a new project needs a couple of dozen consultants' links set up, all "by
    /// shared coordinates" and all with "00_Shared levels and grids" closed. In Revit itself that
    /// is twenty dialogs in a row, each one choosing the same thing over again.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LinkManagerCommand : IExternalCommand
    {
        private const string DialogTitle = "Link Manager";

        /// <summary>
        /// The link worksets that were read: keyed by a row's key, a list of "workset name → its
        /// Id". Every model has its own ids, so the names checked in the window are turned into
        /// ids separately for each link. Filled in when reading via the button, and topped up on load.
        /// </summary>
        private readonly Dictionary<string, List<ModelWorkset>> _worksets =
            new Dictionary<string, List<ModelWorkset>>(StringComparer.Ordinal);

        /// <summary>
        /// Why a row's worksets could not be read this run, keyed like <see cref="_worksets"/>. A
        /// failure is remembered as firmly as a success, so a model out of reach is not asked again
        /// for every step of one load. It is also how <see cref="EnsureWorksets"/> knows a link came
        /// in without its list — a BIM360 model, whose list is read only once it is linked.
        /// </summary>
        private readonly Dictionary<string, string> _unreadable =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// The workset names actually encountered in links during this load run. This exists for
        /// exactly one purpose: a checked name that was not found in any link is the most common
        /// reason behind "the workset did not close". There is simply nothing to close in that
        /// case, <see cref="LinkWorksetRule.Matches"/> never finds such a workset, and until now nobody found out
        /// about it: the "Found in" column in the window read "from last time" both when a
        /// workset was not found and when nobody had looked for it at all.
        /// </summary>
        private readonly HashSet<string> _seenNames =
            new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

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
                var window = new LinkManagerWindow(
                    LinkCatalog.Existing(doc),
                    LinkCatalog.HostWorksets(doc),
                    rows => ReadWorksets(doc, rows),
                    LinkCatalog.Host(doc));
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var preferences = window.Preferences;
                var rule = LinkWorksetRule.From(preferences);
                var chosen = window.Selected;

                // The workset ids are read afresh. By Autodesk's documentation, WorksetPreview.Id
                // changes on synchronising with the central model, and any amount of time can pass
                // between "Read Worksets" and "Load". A stale WorksetId is silently ignored by both
                // Open and Close — a failure that is impossible to find afterwards.
                _worksets.Clear();
                _unreadable.Clear();
                _seenNames.Clear();

                var created = new List<string>();
                var reloaded = new List<string>();
                var moved = new List<string>();
                var healed = new List<string>();
                var unverified = new List<string>();
                var failures = new List<string>();
                var warnings = new WarningSuppressor();

                // The reload runs first and outside a transaction. LoadFrom requires every
                // transaction to be closed, and on top of that **it wipes the document's undo
                // history**. Do it after the creation instead, and Ctrl+Z would no longer bring
                // back the links just set up; this order keeps undo working at least for the new ones.
                Reload(doc, chosen.Where(row => row.IsExisting).ToList(), rule,
                    reloaded, healed, unverified, failures);

                Apply(doc, chosen, preferences, rule, created, moved, healed, unverified, failures, warnings);

                Report(created, reloaded, moved, healed, rule.Missing(_seenNames), unverified, failures,
                    warnings.Messages);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── worksets ─────────────────────────────

        /// <summary>
        /// Reads the workset names of the checked models without loading them. A BIM360 model is read
        /// only if it is already linked: it is never opened here (see
        /// <see cref="LinkCatalog.ModelWorksets"/>). It goes over the network to every model, so in
        /// the window it sits behind a button rather than running on its own. Along the way it fills
        /// <see cref="_worksets"/> — the same data will be needed on load, to turn the checked names
        /// into every model's own ids.
        /// </summary>
        private LinkWorksetScan ReadWorksets(Document doc, IReadOnlyList<LinkRow> rows)
        {
            var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var failures = new List<string>();
            var scanned = 0;

            foreach (var row in rows)
            {
                try
                {
                    var worksets = Worksets(doc, row, true);
                    row.WorksetNames = worksets.Select(workset => workset.Name).ToList();
                    row.Note = string.Empty;

                    foreach (var workset in worksets)
                        names.Add(workset.Name);

                    scanned++;
                }
                catch (Exception exception)
                {
                    var later = exception.Message == LinkCatalog.NotLinkedYet;

                    row.Note = later ? "Worksets read once linked" : "Worksets not read";
                    failures.Add(row.Name + " — " + (later
                                     ? LinkCatalog.NotLinkedYet + ": on \"Load\" they are read from the link " +
                                       "and applied by a second load"
                                     : LinkCatalog.Explain(exception)));
                }
            }

            return new LinkWorksetScan(names.ToList(), scanned, failures);
        }

        /// <summary>
        /// The worksets of one model. A non-workshared file has no worksets — that is an empty
        /// list, not a failure. What is read is remembered so that a single "Load" click does not
        /// visit the same file twice (creation, verification, a possible reload), but the cache
        /// lives only for this run: <see cref="Execute"/> clears it before loading, because
        /// workset ids can change between the read and the load.
        /// </summary>
        private List<ModelWorkset> Worksets(Document doc, LinkRow row, bool refresh)
        {
            if (!refresh)
            {
                List<ModelWorkset> cached;
                if (_worksets.TryGetValue(row.Key, out cached))
                    return cached;

                string failure;
                if (_unreadable.TryGetValue(row.Key, out failure))
                    throw new InvalidOperationException(failure);
            }

            List<ModelWorkset> worksets;
            try
            {
                worksets = LinkCatalog.ModelWorksets(doc.Application, row.Entry, false);
            }
            catch (Exception exception)
            {
                _worksets.Remove(row.Key);
                _unreadable[row.Key] = exception.Message;
                throw;
            }

            _worksets[row.Key] = worksets;
            _unreadable.Remove(row.Key);

            foreach (var workset in worksets)
                _seenNames.Add(LinkPreferences.NormalizeWorkset(workset.Name));

            return worksets;
        }

        /// <summary>
        /// The workset setup for one link (<see cref="LinkWorksetRule.Configuration"/>) out of the
        /// link's own worksets, read for this run. The worksets could not be read — a plain base mode:
        /// the link will still load, just without the selective closing, and the note says why.
        /// </summary>
        private WorksetConfiguration Configuration(Document doc, LinkRow row, LinkWorksetRule rule, List<string> notes)
        {
            // Nothing is checked — no reason to go to the file for worksets.
            if (!rule.Requested)
                return rule.Base();

            List<ModelWorkset> worksets;
            try
            {
                worksets = Worksets(doc, row, false);
            }
            catch (Exception exception)
            {
                notes.Add(row.Name + " — could not read the worksets (" + LinkCatalog.Explain(exception) +
                          "), the link was loaded as is");
                return rule.Base();
            }

            // An empty list after a successful read is suspicious: a workshared model always has
            // at least the default workset. The read most likely could not reach the model and
            // simply did not throw. Failing to tell this apart from "no name matched"
            // would mean losing the only clue when Revit silently did not close what was asked.
            if (worksets.Count == 0)
            {
                notes.Add(row.Name + " — the file returned an empty workset list " +
                          "(this looks like a read failure rather than an absence), the link was loaded with the worksets unchanged");
                return rule.Base();
            }

            return rule.Configuration(worksets);
        }

        /// <summary>The link's document — or <c>null</c> if the link does not hand it over.</summary>
        private static Document LinkDocument(Document doc, ElementId typeId)
        {
            return LinkCatalog.Instances(doc, typeId).FirstOrDefault()?.GetLinkDocument();
        }

        /// <summary>
        /// Checks the worksets of an already-loaded link and, if Revit did not carry out what was
        /// asked, tries to force the setup through once more via <c>LoadFrom</c> — the same path
        /// the command uses to change worksets on existing links.
        ///
        /// In practice, for Revit Server links the very first application of a workset
        /// configuration (whether from <c>RevitLinkType.Create</c> or the first <c>LoadFrom</c>)
        /// sometimes silently does nothing: Revit reports success but leaves the worksets as they
        /// were. Calling it again with the same configuration usually fixes it — this looks like a
        /// quirk of Revit's own first contact with a server model rather than a fault in the data
        /// sent: the mismatch shows up in what actually loaded, not in what was requested. Files
        /// and the cloud were never seen to behave this way.
        ///
        /// The other way to arrive here is a link loaded without a workset list at all — its worksets
        /// could not be read beforehand, so it came in with the base mode. That is every new BIM360
        /// link: a cloud model is not opened just to read its list (see
        /// <see cref="LinkCatalog.ModelWorksets"/>). Once loaded, its own document is where the list
        /// is read from, and the same reload applies the real setup.
        ///
        /// This can only be called outside a transaction: `LoadFrom` refuses to work inside an open one.
        /// </summary>
        /// <param name="retried">Whether a second attempt was needed — if not, the report has nothing to say.</param>
        private LinkWorksetCheck EnsureWorksets(Document doc, LinkRow row, ElementId typeId, LinkWorksetRule rule, out bool retried)
        {
            retried = false;

            // Read now, from the link itself, even if no reload turns out to be needed: its names
            // count for "not found in any link", which would otherwise accuse a name it never saw.
            var reread = rule.Requested && _unreadable.ContainsKey(row.Key);
            if (reread)
                Reread(doc, row);

            var check = rule.Check(LinkDocument(doc, typeId));

            // We only retry when we know for certain that Revit did not comply. On Unknown, a
            // second LoadFrom would be firing blind, and it wipes the document's undo history —
            // too high a price for a guess. The report will say "not checked".
            if (check.Verdict != LinkWorksetVerdict.Mismatched)
                return check;

            var type = doc.GetElement(typeId) as RevitLinkType;
            if (type == null)
                return check;

            if (!reread)
                Reread(doc, row);

            var notes = new List<string>();

            try
            {
                using (var configuration = Configuration(doc, row, rule, notes))
                {
                    // Retrying only makes sense with a full configuration. If Configuration could
                    // not read the link's worksets even now, it handed back the base mode: such a
                    // LoadFrom would change nothing, yet it would still wipe the undo history.
                    // Account reports why the link came in as it is.
                    if (notes.Count > 0)
                        return check;

                    retried = true;

                    var result = type.LoadFrom(LinkCatalog.ToModelPath(row.Entry), configuration);
                    if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                        return check.Because(LinkCatalog.Describe(result.LoadResult));
                }
            }
            catch (Exception exception)
            {
                return check.Because(LinkCatalog.Explain(exception));
            }

            return rule.Check(LinkDocument(doc, typeId));
        }

        /// <summary>
        /// Reads a row's worksets again once its link has loaded: for a cloud model whose list could
        /// not be read beforehand this is the moment it can be, and for any model the ids are then
        /// those of the version just loaded. A failure here keeps the earlier list — it is still a
        /// better basis for the second attempt than none.
        /// </summary>
        private void Reread(Document doc, LinkRow row)
        {
            List<ModelWorkset> earlier;
            _worksets.TryGetValue(row.Key, out earlier);

            try
            {
                Worksets(doc, row, true);
            }
            catch (Exception)
            {
                if (earlier != null)
                {
                    _worksets[row.Key] = earlier;
                    _unreadable.Remove(row.Key);
                }
            }
        }

        /// <summary>
        /// Sorts the check's outcome into the report's lists. "Could not be checked" also lands
        /// here as a line of its own: staying quiet about it would mean going back to how
        /// unclosed worksets went unnoticed in the first place.
        ///
        /// <paramref name="notes"/> are what <see cref="Configuration"/> said on the first load:
        /// the worksets could not be read, so the link came in with the base mode. That is the
        /// explanation of a link that is not as asked, and it goes into the report only for such a
        /// link — one the check found as asked, at once or after a reload, needs none. Every new
        /// BIM360 link starts that way, and a note on each one would read as a failure. A mismatch
        /// on top of it is not reported as Revit failing to apply the worksets: no workset setup
        /// was sent to Revit in the first place.
        /// </summary>
        private static void Account(
            LinkRow row,
            IReadOnlyList<string> notes,
            LinkWorksetCheck check,
            bool retried,
            List<string> healed,
            List<string> unverified,
            List<string> failures)
        {
            var unread = notes.Count > 0;

            if (unread && check.Verdict != LinkWorksetVerdict.Satisfied)
                failures.AddRange(notes);

            switch (check.Verdict)
            {
                case LinkWorksetVerdict.Unknown:
                    unverified.Add(row.Name + " — " + check.Reason);
                    break;

                case LinkWorksetVerdict.Mismatched:
                    if (retried)
                        failures.Add(row.Name + " — the worksets did not apply even after reloading: " +
                                     string.Join(", ", check.Names) +
                                     (check.Reason.Length > 0 ? " (the reload failed: " + check.Reason + ")" : string.Empty));
                    else if (!unread)
                        failures.Add(row.Name + " — Revit did not apply the worksets: " +
                                     string.Join(", ", check.Names));
                    break;

                case LinkWorksetVerdict.Satisfied:
                    if (retried)
                        healed.Add(row.Name + (unread
                                       ? " — the worksets could not be read before loading, only after it"
                                       : " — Revit ignored the first request"));
                    break;
            }
        }

        // ───────────────────────────── loading ─────────────────────────────

        /// <summary>
        /// Creates new links and moves the ones already in the project into another workset.
        /// Everything in one transaction: the user rolls back the whole batch with one Ctrl+Z.
        /// Each link in its own try — a failure on one does not cancel the rest.
        /// </summary>
        private void Apply(
            Document doc,
            IReadOnlyList<LinkRow> rows,
            LinkPreferences preferences,
            LinkWorksetRule rule,
            List<string> created,
            List<string> moved,
            List<string> healed,
            List<string> unverified,
            List<string> failures,
            WarningSuppressor warnings)
        {
            var fresh = rows.Where(row => !row.IsExisting).ToList();
            var existing = rows.Where(row => row.IsExisting).ToList();

            if (fresh.Count == 0 && existing.Count == 0)
                return;

            var placement = LinkCatalog.Placement(preferences.Placement);
            var worksets = LinkCatalog.WorksetIds(doc);

            // Who to check and, if needed, heal through LoadFrom — that must happen outside the
            // transaction, so the list is gathered inside and processed outside.
            var toVerify = new List<PendingCheck>();

            using (var transaction = new Transaction(doc, "Loading links"))
            {
                transaction.Start();

                // A link almost always arrives with warnings — about coordinates, duplicate
                // names, the file version. A modal dialog for each would wreck the batch load.
                var options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(warnings);
                transaction.SetFailureHandlingOptions(options);

                foreach (var row in fresh)
                {
                    try
                    {
                        var notes = new List<string>();
                        var typeId = Create(doc, row, preferences, rule, placement, worksets, created, notes, failures);
                        if (typeId != ElementId.InvalidElementId)
                            toVerify.Add(new PendingCheck(row, typeId, notes));
                    }
                    catch (Exception exception)
                    {
                        failures.Add(row.Name + " — " + LinkCatalog.Explain(exception));
                    }
                }

                foreach (var row in existing)
                {
                    try
                    {
                        Move(doc, row, worksets, moved, failures);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(row.Name + " — " + LinkCatalog.Explain(exception));
                    }
                }

                if (created.Count == 0 && moved.Count == 0)
                    transaction.RollBack();
                else
                    transaction.Commit();
            }

            foreach (var pending in toVerify)
            {
                try
                {
                    bool retried;
                    var check = EnsureWorksets(doc, pending.Row, pending.TypeId, rule, out retried);
                    Account(pending.Row, pending.Notes, check, retried, healed, unverified, failures);
                }
                catch (Exception exception)
                {
                    failures.AddRange(pending.Notes);
                    failures.Add(pending.Row.Name + " — " + LinkCatalog.Explain(exception));
                }
            }
        }

        /// <summary>
        /// Moves a link already in the project into the chosen workset — along with all of its
        /// instances. No workset chosen, or the same one — left alone: in the window that is the
        /// ordinary state, not a failure.
        /// </summary>
        private static void Move(
            Document doc,
            LinkRow row,
            Dictionary<string, WorksetId> worksets,
            List<string> moved,
            List<string> failures)
        {
            WorksetId workset;
            if (row.Entry.Workset.Length == 0 || !worksets.TryGetValue(row.Entry.Workset, out workset))
                return;

            var type = doc.GetElement(row.ExistingId) as RevitLinkType;
            if (type == null)
                return;

            var changed = LinkCatalog.Instances(doc, row.ExistingId)
                .Count(instance => LinkCatalog.Place(instance, workset, failures, row.Name));

            LinkCatalog.Place(type, workset, failures, row.Name);

            if (changed > 0)
            {
                row.Note = "Moved to \"" + row.Entry.Workset + "\"";
                moved.Add(row.Name + " → " + row.Entry.Workset);
            }
        }

        /// <summary>
        /// Returns the id of a new link — or <c>InvalidElementId</c> if it did not work out.
        /// <paramref name="notes"/> gets what <see cref="Configuration"/> said about the worksets;
        /// they are reported once the link has been checked (<see cref="Account"/>), not here.
        /// </summary>
        private ElementId Create(
            Document doc,
            LinkRow row,
            LinkPreferences preferences,
            LinkWorksetRule rule,
            ImportPlacement placement,
            Dictionary<string, WorksetId> worksets,
            List<string> created,
            List<string> notes,
            List<string> failures)
        {
            if (row.Entry.Origin == LinkOrigin.Cloud && LinkCatalog.HasUnreadCloudLink(doc, row.Entry.Name))
            {
                row.Note = "Already in the project, not loaded";
                failures.Add(row.Name + " — a BIM360 link of this name is already in the project, not loaded: " +
                             "load it in Manage Links, and it will be listed here as an existing link");
                return ElementId.InvalidElementId;
            }

            using (var configuration = Configuration(doc, row, rule, notes))
            {
                // A relative path exists only for a file: for Revit Server and the cloud the path
                // is always absolute, and Revit simply will not accept a relative one there.
                var relative = preferences.IsRelativePath && row.Entry.Origin == LinkOrigin.File;

                using (var options = new RevitLinkOptions(relative, configuration))
                {
                    var result = RevitLinkType.Create(doc, LinkCatalog.ToModelPath(row.Entry), options);

                    if (result.LoadResult == LinkLoadResultType.LinkExists)
                    {
                        row.Note = "Already in the project";
                        failures.Add(row.Name + " — such a link is already in the project");
                        return ElementId.InvalidElementId;
                    }

                    if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                    {
                        row.Note = LinkCatalog.Describe(result.LoadResult);
                        failures.Add(row.Name + " — " + LinkCatalog.Describe(result.LoadResult));
                        return ElementId.InvalidElementId;
                    }

                    var type = doc.GetElement(result.ElementId) as RevitLinkType;
                    if (type != null && preferences.IsAttachment)
                        type.AttachmentType = AttachmentType.Attachment;

                    var instance = RevitLinkInstance.Create(doc, result.ElementId, placement);

                    // The new link is pinned right away: a consultant's link is inserted by
                    // coordinates, and an accidental drag with the mouse is later hunted down by
                    // the whole team. Removing a pin by hand is one button, putting a link that
                    // drifted back in place is not.
                    try
                    {
                        instance.Pinned = true;
                    }
                    catch (Exception exception)
                    {
                        failures.Add(row.Name + " — could not pin the link: " + LinkCatalog.Explain(exception));
                    }

                    // The workset is set on the elements that were just created, rather than
                    // through the document's active workset: that way the link lands wherever it
                    // was asked to, regardless of where the user is standing.
                    WorksetId hostWorkset;
                    if (row.Entry.Workset.Length > 0 && worksets.TryGetValue(row.Entry.Workset, out hostWorkset))
                    {
                        LinkCatalog.Place(instance, hostWorkset, failures, row.Name);
                        LinkCatalog.Place(type, hostWorkset, failures, row.Name);
                    }

                    row.Note = "Loaded";
                    created.Add(row.Name);

                    // The link's worksets are checked and, if needed, healed later, outside the
                    // transaction (Apply.toVerify) — LoadFrom refuses to work inside one.
                    return result.ElementId;
                }
            }
        }

        /// <summary>
        /// Reloads links already in the project — for the sake of a new workset setup. The
        /// placement is left unchanged: Revit will not let it be redefined on an existing link.
        ///
        /// Called outside a transaction and before creating new links: <c>LoadFrom</c> requires
        /// every transaction to be closed, and it wipes the document's undo history. Because of
        /// that the reload itself is never undoable, but the creation of new links that follows it is.
        /// </summary>
        private void Reload(
            Document doc,
            IReadOnlyList<LinkRow> rows,
            LinkWorksetRule rule,
            List<string> reloaded,
            List<string> healed,
            List<string> unverified,
            List<string> failures)
        {
            foreach (var row in rows)
            {
                try
                {
                    var type = doc.GetElement(row.ExistingId) as RevitLinkType;
                    if (type == null)
                    {
                        failures.Add(row.Name + " — the link is no longer in the project");
                        continue;
                    }

                    var notes = new List<string>();

                    using (var configuration = Configuration(doc, row, rule, notes))
                    {
                        var result = type.LoadFrom(LinkCatalog.ToModelPath(row.Entry), configuration);

                        if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                        {
                            row.Note = LinkCatalog.Describe(result.LoadResult);
                            failures.Add(row.Name + " — " + LinkCatalog.Describe(result.LoadResult));
                            continue;
                        }
                    }

                    row.Note = "Reloaded";
                    reloaded.Add(row.Name);

                    // Reload already runs outside a transaction, so healing (EnsureWorksets →
                    // LoadFrom) can happen right away, without waiting for a separate pass as new links need.
                    bool retried;
                    var check = EnsureWorksets(doc, row, row.ExistingId, rule, out retried);
                    Account(row, notes, check, retried, healed, unverified, failures);
                }
                catch (Exception exception)
                {
                    failures.Add(row.Name + " — " + LinkCatalog.Explain(exception));
                }
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(
            IReadOnlyList<string> created,
            IReadOnlyList<string> reloaded,
            IReadOnlyList<string> moved,
            IReadOnlyList<string> healed,
            IReadOnlyList<string> missing,
            IReadOnlyList<string> unverified,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
        {
            const int limit = 15;

            var text = created.Count == 0 && reloaded.Count == 0 && moved.Count == 0
                ? "Not a single link could be loaded."
                : "Links loaded: " + created.Count +
                  (reloaded.Count > 0 ? ", reloaded: " + reloaded.Count : string.Empty) +
                  (moved.Count > 0 ? ", moved to another workset: " + moved.Count : string.Empty) + ".";

            if (created.Count > 0)
                text += "\n\n• " + string.Join("\n• ", created.Take(limit)) +
                        (created.Count > limit ? "\n… and " + (created.Count - limit) + " more" : string.Empty);

            if (healed.Count > 0)
                text += "\n\nThe worksets took hold only on a second load " +
                        "(that also wipes the document's undo history):\n• " +
                        string.Join("\n• ", healed.Take(limit)) +
                        (healed.Count > limit ? "\n… and " + (healed.Count - limit) + " more" : string.Empty);

            if (moved.Count > 0)
                text += "\n\nMoved to another workset:\n• " + string.Join("\n• ", moved.Take(limit)) +
                        (moved.Count > limit ? "\n… and " + (moved.Count - limit) + " more" : string.Empty);

            // A name found in no link is not a Revit refusal but a typo or a different workset
            // name on the consultant's side. Telling the two apart is left to the user, so they
            // have to be worded differently too.
            if (missing.Count > 0)
                text += "\n\nThese checked worksets were not found in any link (" + missing.Count +
                        ") — check the name: in the window, check the links and press \"Read Worksets\", " +
                        "the \"Found in\" column will show where each workset exists:\n• " +
                        string.Join("\n• ", missing.Take(limit)) +
                        (missing.Count > limit ? "\n… and " + (missing.Count - limit) + " more" : string.Empty);

            // A section of its own, not silence: the link is loaded, but whether the worksets took
            // hold, the command does not know. Staying quiet here would pass "not checked" off as "done".
            if (unverified.Count > 0)
                text += "\n\nWorksets were requested but could not be checked (" + unverified.Count +
                        ") — look in the link's \"Manage Worksets\":\n• " +
                        string.Join("\n• ", unverified.Take(limit)) +
                        (unverified.Count > limit ? "\n… and " + (unverified.Count - limit) + " more" : string.Empty);

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

        /// <summary>A link just created, waiting to be checked once its transaction has committed.</summary>
        private sealed class PendingCheck
        {
            public PendingCheck(LinkRow row, ElementId typeId, IReadOnlyList<string> notes)
            {
                Row = row;
                TypeId = typeId;
                Notes = notes;
            }

            public LinkRow Row { get; }

            public ElementId TypeId { get; }

            /// <summary>What <see cref="Configuration"/> said about the worksets when the link was created.</summary>
            public IReadOnlyList<string> Notes { get; }
        }
    }
}
