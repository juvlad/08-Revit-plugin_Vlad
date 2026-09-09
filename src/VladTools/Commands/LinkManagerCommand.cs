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
        private readonly Dictionary<string, List<WorksetInfo>> _worksets =
            new Dictionary<string, List<WorksetInfo>>(StringComparer.Ordinal);

        /// <summary>
        /// The workset names actually encountered in links during this load run. This exists for
        /// exactly one purpose: a checked name that was not found in any link is the most common
        /// reason behind "the workset did not close". There is simply nothing to close in that
        /// case, <see cref="Matches"/> never finds such a workset, and until now nobody found out
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
                    rows => ReadWorksets(rows),
                    LinkCatalog.Host(doc));
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var preferences = window.Preferences;
                var chosen = window.Selected;

                // The workset ids are read afresh. By Autodesk's documentation, WorksetPreview.Id
                // changes on synchronising with the central model, and any amount of time can pass
                // between "Read Worksets" and "Load". A stale WorksetId is silently ignored by both
                // Open and Close — a failure that is impossible to find afterwards.
                _worksets.Clear();
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
                Reload(doc, chosen.Where(row => row.IsExisting).ToList(), preferences,
                    reloaded, healed, unverified, failures);

                Apply(doc, chosen, preferences, created, moved, healed, unverified, failures, warnings);

                Report(created, reloaded, moved, healed, Missing(preferences), unverified, failures,
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
        /// Reads the workset names of the checked models without opening them. It goes over the
        /// network to every file, so in the window it sits behind a button rather than running on
        /// its own. Along the way it fills <see cref="_worksets"/> — the same data will be needed
        /// on load, to turn the checked names into every model's own ids.
        /// </summary>
        private LinkWorksetScan ReadWorksets(IReadOnlyList<LinkRow> rows)
        {
            var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var failures = new List<string>();
            var scanned = 0;

            foreach (var row in rows)
            {
                try
                {
                    var worksets = Worksets(row, true);
                    row.WorksetNames = worksets.Select(workset => workset.Name).ToList();
                    row.Note = string.Empty;

                    foreach (var workset in worksets)
                        names.Add(workset.Name);

                    scanned++;
                }
                catch (Exception exception)
                {
                    row.Note = "Worksets not read";
                    failures.Add(row.Name + " — " + LinkCatalog.Short(exception.Message));
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
        private List<WorksetInfo> Worksets(LinkRow row, bool refresh)
        {
            List<WorksetInfo> cached;
            if (!refresh && _worksets.TryGetValue(row.Key, out cached))
                return cached;

            var worksets = WorksharingUtils.GetUserWorksetInfo(LinkCatalog.ToModelPath(row.Entry))
                .Select(preview => new WorksetInfo(preview.Name, preview.Id))
                .ToList();

            _worksets[row.Key] = worksets;

            foreach (var workset in worksets)
                _seenNames.Add(LinkPreferences.NormalizeWorkset(workset.Name));

            return worksets;
        }

        /// <summary>
        /// Builds the workset setup for one link: the base mode plus the names checked in the
        /// window plus a name rule. The rule is applied here rather than in the window, exactly
        /// because every model's worksets are its own: "00_" catches both "00_Shared Levels and
        /// Grids" and its Russian-named equivalent, though neither appears in the window's own list.
        ///
        /// The request is always phrased as "close all, open the listed ones" rather than "open
        /// all, close the listed ones". By the documentation both phrasings are equivalent, but
        /// CloseAllWorksets + Open is the only one Autodesk's own example uses specifically for
        /// links (Developers Guide → Linked Files → Revit Links), and the only one with no reports
        /// of silently doing nothing. OpenAllWorksets + Close on links leaves the worksets open
        /// while reporting success — exactly what made the button "fail at closing". So "close the
        /// checked ones" is computed as a complement: open every link workset except the checked
        /// ones. The workset list needed for that has already been read anyway.
        ///
        /// The worksets could not be read — we hand back a plain base mode: the link will still
        /// load, just without the selective closing.
        /// </summary>
        private WorksetConfiguration Configuration(LinkRow row, LinkPreferences preferences, List<string> notes)
        {
            var hasNames = preferences.Worksets.Count > 0;
            var hasPattern = !string.IsNullOrEmpty(preferences.WorksetPattern);

            // Nothing is checked — no reason to go to the file for worksets.
            if (!hasNames && !hasPattern)
                return new WorksetConfiguration(BaseOption(preferences.WorksetMode));

            List<WorksetInfo> worksets;
            try
            {
                worksets = Worksets(row, false);
            }
            catch (Exception exception)
            {
                notes.Add(row.Name + " — could not read the worksets (" + LinkCatalog.Short(exception.Message) +
                          "), the link was loaded as is");
                return new WorksetConfiguration(BaseOption(preferences.WorksetMode));
            }

            // An empty list after a successful read is suspicious: a workshared model always has
            // at least the default workset. GetUserWorksetInfo most likely could not reach the
            // file and simply did not throw. Failing to tell this apart from "no name matched"
            // would mean losing the only clue when Revit silently did not close what was asked.
            if (worksets.Count == 0)
            {
                notes.Add(row.Name + " — the file returned an empty workset list " +
                          "(this looks like a read failure rather than an absence), the link was loaded with the worksets unchanged");
                return new WorksetConfiguration(BaseOption(preferences.WorksetMode));
            }

            var marked = worksets.Where(workset => Matches(workset.Name, preferences))
                .Select(workset => workset.Id)
                .ToList();

            var rest = worksets.Where(workset => !Matches(workset.Name, preferences))
                .Select(workset => workset.Id)
                .ToList();

            // Nothing checked matches this link — nothing to close, hand back a plain base mode.
            // This also sidesteps an ambiguity in the Open method's XML doc: "if all worksets are
            // set to open, the configuration will be unchanged". Finding out on a live project
            // whether that means CloseAll + Open(all) would leave the link entirely closed is not
            // a price worth paying for the answer.
            if (marked.Count == 0)
                return new WorksetConfiguration(BaseOption(preferences.WorksetMode));

            // "As last opened" cannot be expressed through Open: only Revit knows what was open
            // last time, and there is no complement to compute here. Close is what is left — with
            // the very unreliability described above; if Revit does not carry it out, Check will
            // catch that and say so in the report rather than swallow it.
            if (preferences.WorksetMode == LinkWorksetMode.LastViewed)
            {
                var lastViewed = new WorksetConfiguration(WorksetConfigurationOption.OpenLastViewed);
                lastViewed.Close(marked);

                return lastViewed;
            }

            // Under "close all", a check mark means the opposite: open only the checked ones.
            var open = preferences.WorksetMode == LinkWorksetMode.CloseAll ? marked : rest;

            var configuration = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);

            if (open.Count > 0)
                configuration.Open(open);

            return configuration;
        }

        /// <summary>
        /// The base mode when there is nothing to list by name: nothing is checked, or the link's
        /// workset list could not be read.
        ///
        /// It matters here that "open all" is specifically <c>OpenAllWorksets</c>: substituting
        /// <c>CloseAllWorksets</c> here, as in the main path, would mean loading the link empty on
        /// any read failure, silently.
        /// </summary>
        private static WorksetConfigurationOption BaseOption(LinkWorksetMode mode)
        {
            switch (mode)
            {
                case LinkWorksetMode.CloseAll:
                    return WorksetConfigurationOption.CloseAllWorksets;
                case LinkWorksetMode.LastViewed:
                    return WorksetConfigurationOption.OpenLastViewed;
                default:
                    return WorksetConfigurationOption.OpenAllWorksets;
            }
        }

        /// <summary>
        /// What a workset with this name should become after loading: <c>true</c> — open,
        /// <c>false</c> — closed, <c>null</c> — nothing was promised about it, and it cannot be
        /// checked. A mirror of <see cref="Configuration"/>: edit one, edit the other.
        /// </summary>
        private static bool? Expected(string name, LinkPreferences preferences)
        {
            var marked = Matches(name, preferences);

            switch (preferences.WorksetMode)
            {
                case LinkWorksetMode.CloseAll:
                    return marked;

                case LinkWorksetMode.OpenAll:
                    return !marked;

                default:
                    // "As last opened": only Revit knows what happened to the unchecked ones.
                    return marked ? (bool?)false : null;
            }
        }

        /// <summary>
        /// Re-reads an already-loaded link document and checks what Revit actually did with the
        /// worksets against what was asked. Without this, finding out whether it obeyed could only
        /// be done by hand — through Revit's own "Manage Worksets". A WorksetConfiguration only
        /// conveys a wish; the check here is the only way to catch a case where Revit did not
        /// carry it out, instead of guessing blindly.
        ///
        /// There are three outcomes, not two, and that is the whole point. Previously "could not
        /// be checked" was returned as "everything as asked": the link would not hand over its
        /// document, the collector would fail — and the report read "Loaded" with not a word about
        /// worksets. That is precisely how a closing failure stayed unnoticed, so
        /// <see cref="WorksetVerdict.Unknown"/> now reaches the report as a line of its own.
        /// </summary>
        private static WorksetCheck Check(Document linkDocument, LinkPreferences preferences)
        {
            var hasNames = preferences.Worksets.Count > 0;
            var hasPattern = !string.IsNullOrEmpty(preferences.WorksetPattern);
            if (!hasNames && !hasPattern)
                return WorksetCheck.NotRequested();

            if (linkDocument == null)
                return WorksetCheck.Unknown("the link did not hand over its document — the worksets could not be checked");

            try
            {
                // A non-workshared link has no worksets at all: nothing to close, and that is not a failure.
                if (!linkDocument.IsWorkshared)
                    return WorksetCheck.NotRequested();

                var worksets = new FilteredWorksetCollector(linkDocument)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToWorksets()
                    .ToList();

                if (worksets.Count == 0)
                    return WorksetCheck.Unknown("the workshared link has no worksets at all — nothing to check against");

                var wrong = new List<string>();

                foreach (var workset in worksets)
                {
                    var expected = Expected(workset.Name, preferences);

                    if (expected.HasValue && workset.IsOpen != expected.Value)
                        wrong.Add(workset.Name);
                }

                return WorksetCheck.Verified(wrong);
            }
            catch (Exception exception)
            {
                return WorksetCheck.Unknown("could not check the worksets: " + LinkCatalog.Short(exception.Message));
            }
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
        /// This can only be called outside a transaction: `LoadFrom` refuses to work inside an open one.
        /// </summary>
        /// <param name="retried">Whether a second attempt was needed — if not, the report has nothing to say.</param>
        private WorksetCheck EnsureWorksets(Document doc, LinkRow row, ElementId typeId, LinkPreferences preferences, out bool retried)
        {
            retried = false;

            var check = Check(LinkDocument(doc, typeId), preferences);

            // We only retry when we know for certain that Revit did not comply. On Unknown, a
            // second LoadFrom would be firing blind, and it wipes the document's undo history —
            // too high a price for a guess. The report will say "not checked".
            if (check.Verdict != WorksetVerdict.Mismatched)
                return check;

            var type = doc.GetElement(typeId) as RevitLinkType;
            if (type == null)
                return check;

            var notes = new List<string>();

            try
            {
                using (var configuration = Configuration(row, preferences, notes))
                {
                    // Retrying only makes sense with a full configuration. If Configuration could
                    // not read the link's worksets, it handed back the base mode: such a LoadFrom
                    // would change nothing, yet it would still wipe the undo history. The reason
                    // already went into the report from Create/Reload — we report the mismatch as it is.
                    if (notes.Count > 0)
                        return check;

                    retried = true;

                    var result = type.LoadFrom(LinkCatalog.ToModelPath(row.Entry), configuration);
                    if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                        return check;
                }
            }
            catch (Exception)
            {
                return check;
            }

            return Check(LinkDocument(doc, typeId), preferences);
        }

        /// <summary>
        /// Sorts the check's outcome into the report's lists. "Could not be checked" also lands
        /// here as a line of its own: staying quiet about it would mean going back to how
        /// unclosed worksets went unnoticed in the first place.
        /// </summary>
        private static void Account(
            LinkRow row,
            WorksetCheck check,
            bool retried,
            List<string> healed,
            List<string> unverified,
            List<string> failures)
        {
            switch (check.Verdict)
            {
                case WorksetVerdict.Unknown:
                    unverified.Add(row.Name + " — " + check.Reason);
                    break;

                case WorksetVerdict.Mismatched:
                    failures.Add(row.Name + (retried
                                     ? " — the worksets did not apply even after reloading: "
                                     : " — Revit did not apply the worksets: ") +
                                 string.Join(", ", check.Names));
                    break;

                case WorksetVerdict.Satisfied:
                    if (retried)
                        healed.Add(row.Name);
                    break;
            }
        }

        private static bool Matches(string name, LinkPreferences preferences)
        {
            // Leading/trailing spaces are trimmed both here and in the rule: a consultant's
            // workset name with a trailing space would otherwise never close at all —
            // see LinkPreferences.NormalizeWorkset.
            var trimmed = LinkPreferences.NormalizeWorkset(name);

            if (preferences.Worksets.Any(chosen => LinkPreferences.SameWorkset(chosen, trimmed)))
                return true;

            var pattern = preferences.WorksetPattern;
            if (string.IsNullOrEmpty(pattern))
                return false;

            return preferences.WorksetPatternContains
                ? trimmed.IndexOf(pattern, StringComparison.CurrentCultureIgnoreCase) >= 0
                : trimmed.StartsWith(pattern, StringComparison.CurrentCultureIgnoreCase);
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
            var toVerify = new List<KeyValuePair<LinkRow, ElementId>>();

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
                        var typeId = Create(doc, row, preferences, placement, worksets, created, failures);
                        if (typeId != ElementId.InvalidElementId)
                            toVerify.Add(new KeyValuePair<LinkRow, ElementId>(row, typeId));
                    }
                    catch (Exception exception)
                    {
                        failures.Add(row.Name + " — " + LinkCatalog.Short(exception.Message));
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
                        failures.Add(row.Name + " — " + LinkCatalog.Short(exception.Message));
                    }
                }

                if (created.Count == 0 && moved.Count == 0)
                    transaction.RollBack();
                else
                    transaction.Commit();
            }

            foreach (var pair in toVerify)
            {
                try
                {
                    bool retried;
                    var check = EnsureWorksets(doc, pair.Key, pair.Value, preferences, out retried);
                    Account(pair.Key, check, retried, healed, unverified, failures);
                }
                catch (Exception exception)
                {
                    failures.Add(pair.Key.Name + " — " + LinkCatalog.Short(exception.Message));
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

        /// <summary>Returns the id of a new link — or <c>InvalidElementId</c> if it did not work out.</summary>
        private ElementId Create(
            Document doc,
            LinkRow row,
            LinkPreferences preferences,
            ImportPlacement placement,
            Dictionary<string, WorksetId> worksets,
            List<string> created,
            List<string> failures)
        {
            var notes = new List<string>();

            using (var configuration = Configuration(row, preferences, notes))
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
                        failures.Add(row.Name + " — could not pin the link: " + LinkCatalog.Short(exception.Message));
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
                    failures.AddRange(notes);

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
            LinkPreferences preferences,
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

                    using (var configuration = Configuration(row, preferences, notes))
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
                    failures.AddRange(notes);

                    // Reload already runs outside a transaction, so healing (EnsureWorksets →
                    // LoadFrom) can happen right away, without waiting for a separate pass as new links need.
                    bool retried;
                    var check = EnsureWorksets(doc, row, row.ExistingId, preferences, out retried);
                    Account(row, check, retried, healed, unverified, failures);
                }
                catch (Exception exception)
                {
                    failures.Add(row.Name + " — " + LinkCatalog.Short(exception.Message));
                }
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        /// <summary>
        /// The checked workset names that were not found in any link this run.
        ///
        /// An empty <see cref="_seenNames"/> means the worksets could not be read for any link at
        /// all — then "not found" has nothing to say: the reason is a different one, and it is
        /// already in the report, on a line of its own.
        /// </summary>
        private List<string> Missing(LinkPreferences preferences)
        {
            if (_seenNames.Count == 0)
                return new List<string>();

            return preferences.Worksets
                .Where(name => !_seenNames.Contains(LinkPreferences.NormalizeWorkset(name)))
                .ToList();
        }

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
                text += "\n\nRevit did not close the worksets as asked on the first try, a reload fixed it " +
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

        /// <summary>What was found out about the worksets of an already-loaded link.</summary>
        private enum WorksetVerdict
        {
            /// <summary>Nothing was requested — nothing to check.</summary>
            NotRequested,

            /// <summary>Everything is as asked.</summary>
            Satisfied,

            /// <summary>Some worksets are not in the right state — Revit did not carry out the request.</summary>
            Mismatched,

            /// <summary>Could not be checked. This is not the same thing as "all is well".</summary>
            Unknown
        }

        /// <summary>
        /// The result of checking a link's worksets against what was requested. It exists so that
        /// <see cref="WorksetVerdict.Unknown"/> can never be mistaken for a success: the previous
        /// check returned an empty list both when it checked and everything matched, and when it
        /// could not check at all.
        /// </summary>
        private sealed class WorksetCheck
        {
            private static readonly string[] None = new string[0];

            private WorksetCheck(WorksetVerdict verdict, IReadOnlyList<string> names, string reason)
            {
                Verdict = verdict;
                Names = names ?? None;
                Reason = reason ?? string.Empty;
            }

            public WorksetVerdict Verdict { get; }

            /// <summary>The names of the worksets whose state did not match what was requested.</summary>
            public IReadOnlyList<string> Names { get; }

            /// <summary>Why it could not be checked; empty for the other outcomes.</summary>
            public string Reason { get; }

            public static WorksetCheck NotRequested()
            {
                return new WorksetCheck(WorksetVerdict.NotRequested, None, null);
            }

            public static WorksetCheck Unknown(string reason)
            {
                return new WorksetCheck(WorksetVerdict.Unknown, None, reason);
            }

            public static WorksetCheck Verified(IReadOnlyList<string> wrong)
            {
                return wrong.Count == 0
                    ? new WorksetCheck(WorksetVerdict.Satisfied, None, null)
                    : new WorksetCheck(WorksetVerdict.Mismatched, wrong, null);
            }
        }

        /// <summary>A link's workset: the name checked in the window, and its id in this model.</summary>
        private sealed class WorksetInfo
        {
            public WorksetInfo(string name, WorksetId id)
            {
                Name = name ?? string.Empty;
                Id = id;
            }

            public string Name { get; }
            public WorksetId Id { get; }
        }
    }
}
