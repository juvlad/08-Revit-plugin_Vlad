using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>What one "Insert Links" run puts into every model of its list, and how.</summary>
    internal sealed class LinkInsertionRequest
    {
        public LinkInsertionRequest(
            IReadOnlyList<LinkEntry> links,
            LinkPlacement placement,
            bool isAttachment,
            LinkWorksetRule inside,
            bool createWorksets)
        {
            Links = links ?? new List<LinkEntry>();
            Placement = placement;
            IsAttachment = isAttachment;
            Inside = inside;
            CreateWorksets = createWorksets;
        }

        /// <summary>
        /// The models to link, every one on Revit Server. <see cref="LinkEntry.Workset"/> is the project
        /// workset each goes into — one name for every model of the run, as the office names them.
        /// </summary>
        public IReadOnlyList<LinkEntry> Links { get; }

        public LinkPlacement Placement { get; }

        public bool IsAttachment { get; }

        /// <summary>Which worksets inside the links to close — "Link Manager"'s setup, one for the whole run.</summary>
        public LinkWorksetRule Inside { get; }

        /// <summary>Make a link's project workset in a model that lacks it, rather than leave the link out there.</summary>
        public bool CreateWorksets { get; }
    }

    /// <summary>One model of an "Insert Links" run: what became of every link in it.</summary>
    internal sealed class LinkInsertionResult
    {
        public LinkInsertionResult(LinkEntry entry)
        {
            Entry = entry;
        }

        /// <summary>The model the links go into — on Revit Server.</summary>
        public LinkEntry Entry { get; }

        public string Name => Entry.Name;

        /// <summary>Why nothing could be done in the model, or why what was done could not be saved; empty otherwise.</summary>
        public string Failure { get; set; } = string.Empty;

        /// <summary>The model was synchronised with central — what <see cref="Inserted"/> lists is there for everyone.</summary>
        public bool Saved { get; set; }

        /// <summary>"AR.rvt → 01_Link_AR" — the workset read back after the commit, not the one asked for.</summary>
        public List<string> Inserted { get; } = new List<string>();

        /// <summary>Links the model had already — counted, never touched.</summary>
        public List<string> Already { get; } = new List<string>();

        /// <summary>Links left out of this model, each with the reason: no workset, held by somebody, a namesake…</summary>
        public List<string> Skipped { get; } = new List<string>();

        /// <summary>The project worksets made in this model for its links.</summary>
        public List<string> WorksetsMade { get; } = new List<string>();

        /// <summary>Links whose worksets inside took hold only on a second load.</summary>
        public List<string> Healed { get; } = new List<string>();

        /// <summary>Links whose worksets inside are not as asked, even after a second load.</summary>
        public List<string> Mismatched { get; } = new List<string>();

        /// <summary>Links whose worksets inside could not be checked — never counted as checked.</summary>
        public List<string> Unverified { get; } = new List<string>();

        /// <summary>What went wrong with a single link — the rest of the model carried on.</summary>
        public List<string> Failures { get; } = new List<string>();

        /// <summary>What is worth knowing that is not a failure.</summary>
        public List<string> Notes { get; } = new List<string>();

        /// <summary>Revit's warnings dismissed while the model was worked on (<see cref="WarningSilencer"/>).</summary>
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// Putting a set of links into a model on Revit Server that nobody has open — what "Insert Links" does
    /// to every model of its list.
    ///
    /// The model is opened the way every batch here edits one (<see cref="BatchCoordination.OpenLocal"/>): a
    /// local copy, never the central in place, with only the project worksets the links go into open —
    /// a new element is put into an open workset, and nothing else of the model needs to load. Each link
    /// is created in a transaction of its own: a link Revit refuses at commit takes only itself back, not
    /// the links before it. Every link is read back after the commit — the workset it stands in, and the
    /// worksets inside it, checked by "Link Manager"'s rule (<see cref="LinkWorksetRule"/>) and loaded once
    /// more where Revit did not carry the request out, which Revit Server links are known to need. Then
    /// the model is synchronised with everything handed back; one where nothing was inserted is
    /// relinquished and closed.
    ///
    /// A link the model has already is only counted: inserting a second copy of a model is never what a
    /// batch means, and reloading it would be "Link Manager"'s job, with the model open.
    /// </summary>
    internal static class LinkInsertion
    {
        /// <param name="seen">The worksets found inside the links so far — for "checked, but in no link"; added to here.</param>
        public static void Run(Application app, LinkInsertionResult result, LinkInsertionRequest request, ISet<string> seen)
        {
            var entry = result.Entry;
            var links = new List<LinkEntry>();

            foreach (var link in request.Links)
            {
                if (link.Key == entry.Key)
                    result.Skipped.Add(link.Name + " — this is the model itself");
                else
                    links.Add(link);
            }

            if (links.Count == 0)
                return;

            var worksets = new List<string>();
            foreach (var name in links.Select(link => link.Workset))
            {
                if (name.Length > 0 && !worksets.Any(known => LinkPreferences.SameWorkset(known, name)))
                    worksets.Add(name);
            }

            string reason;
            var model = BatchCoordination.OpenLocal(app, entry, worksets, out reason);
            if (model == null)
            {
                result.Failure = reason;
                return;
            }

            if (model.Note.Length > 0)
                result.Notes.Add(model.Note);

            try
            {
                var doc = model.Document;

                foreach (var item in Insert(app, doc, links, request, result, seen))
                    Verify(app, doc, item, request.Inside, seen, result);

                var failures = new List<string>();

                if (result.Inserted.Count > 0 || result.WorksetsMade.Count > 0)
                {
                    // No comment, the way "Upload to Server" synchronises its links: the server's history
                    // says who and when without one.
                    result.Saved = BatchCoordination.Commit(model, string.Empty, failures);
                    if (!result.Saved)
                        result.Failure = ServerUpload.Strip(failures, model.Entry.Name);
                }
                else
                {
                    BatchCoordination.Relinquish(model, failures);
                    if (failures.Count > 0)
                        result.Notes.Add(ServerUpload.Strip(failures, model.Entry.Name));
                }
            }
            catch (Exception exception)
            {
                result.Failure = "the run broke off: " + LinkCatalog.Explain(exception);
            }
            finally
            {
                BatchCoordination.Release(model);
            }
        }

        // ───────────────────────────── inserting ─────────────────────────────

        private static List<Pending> Insert(
            Application app,
            Document doc,
            IReadOnlyList<LinkEntry> links,
            LinkInsertionRequest request,
            LinkInsertionResult result,
            ISet<string> seen)
        {
            var pending = new List<Pending>();
            var existing = Existing(doc);
            var todo = new List<LinkEntry>();

            foreach (var link in links)
            {
                var same = existing.FirstOrDefault(known => known.Key == link.Key);
                if (same != null)
                {
                    result.Already.Add(link.Name + (same.Workset.Length > 0 ? " (in \"" + same.Workset + "\")" : string.Empty));
                    continue;
                }

                // Two link types of one name in a model are one model linked twice, as a rule — this one
                // still from BIM360, say. Adding the server copy next to it would leave both, and every
                // view would show the building twice.
                var named = existing.FirstOrDefault(known => SameName(known.Name, link.Name));
                if (named != null)
                {
                    result.Skipped.Add(link.Name + " — a link of this name is in the model already, pointing at " + named.Where +
                                       ": left as it is (\"Link Updater\" repoints it, if it should be this one)");
                    continue;
                }

                todo.Add(link);
            }

            if (todo.Count == 0)
                return pending;

            var obstacles = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
            var ids = Worksets(app, doc, todo, request.CreateWorksets, result, obstacles);
            var placement = LinkCatalog.Placement(request.Placement);

            foreach (var link in todo)
            {
                var key = LinkPreferences.NormalizeWorkset(link.Workset);

                WorksetId workset;
                if (!ids.TryGetValue(key, out workset))
                {
                    string obstacle;
                    result.Skipped.Add(link.Name + " — " + (obstacles.TryGetValue(key, out obstacle)
                        ? obstacle
                        : "no project workset was chosen for it"));
                    continue;
                }

                try
                {
                    var item = Create(app, doc, link, workset, placement, request, result, seen);
                    if (item != null)
                        pending.Add(item);
                }
                catch (Exception exception)
                {
                    result.Failures.Add(link.Name + " — " + LinkCatalog.Explain(exception));
                }
            }

            return pending;
        }

        /// <summary>
        /// The project worksets the links go into, by normalised name → id: those the model has and nobody
        /// else holds, plus those made here when the run may make them. Every one left out gets its reason
        /// in <paramref name="obstacles"/>.
        /// </summary>
        private static Dictionary<string, WorksetId> Worksets(
            Application app,
            Document doc,
            IReadOnlyList<LinkEntry> links,
            bool create,
            LinkInsertionResult result,
            Dictionary<string, string> obstacles)
        {
            var usable = new Dictionary<string, WorksetId>(StringComparer.CurrentCultureIgnoreCase);
            var names = links
                .Select(link => LinkPreferences.NormalizeWorkset(link.Workset))
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var table = doc.GetWorksetTable();
            var missing = new List<string>();

            foreach (var name in names)
            {
                var id = Find(doc, name);
                if (id == WorksetId.InvalidWorksetId)
                {
                    missing.Add(name);
                    continue;
                }

                // Left to Revit, a new element in a workset somebody else made editable may be refused at the
                // commit — and an error there is a dialog that stops the whole batch until somebody clicks it.
                var owner = Owner(table, id);
                if (owner.Length > 0 && !string.Equals(owner, app.Username, StringComparison.OrdinalIgnoreCase))
                {
                    obstacles[name] = "its workset \"" + name + "\" is checked out to " + owner +
                                      " — left out rather than risk Revit's refusal stopping the run; ask them to relinquish it";
                    continue;
                }

                usable[name] = id;
            }

            if (missing.Count == 0)
                return usable;

            if (!create)
            {
                foreach (var name in missing)
                    obstacles[name] = "the model has no workset \"" + name + "\" (the window can be told to make it)";

                return usable;
            }

            var made = new List<string>();

            using (var transaction = new Transaction(doc, "Link worksets"))
            {
                transaction.Start();

                foreach (var name in missing)
                {
                    try
                    {
                        if (!WorksetTable.IsWorksetNameUnique(doc, name))
                        {
                            obstacles[name] = "the workset \"" + name + "\" could not be made: the name is taken by a workset of another kind";
                            continue;
                        }

                        Workset.Create(doc, name);
                        made.Add(name);
                    }
                    catch (Exception exception)
                    {
                        obstacles[name] = "the workset \"" + name + "\" could not be made: " + LinkCatalog.Short(exception.Message);
                    }
                }

                if (made.Count == 0)
                {
                    transaction.RollBack();
                    return usable;
                }

                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    foreach (var name in made)
                        obstacles[name] = "the workset \"" + name + "\" could not be made: Revit rolled it back";

                    return usable;
                }
            }

            // Read back: a workset Revit took without an error but does not list is not a workset to put a link in.
            foreach (var name in made)
            {
                var id = Find(doc, name);
                if (id == WorksetId.InvalidWorksetId)
                {
                    obstacles[name] = "the workset \"" + name + "\" was made, but the model does not list it afterwards";
                    continue;
                }

                usable[name] = id;
                result.WorksetsMade.Add(name);
            }

            return usable;
        }

        /// <summary>The user workset of this name, by the add-in's one workset-name rule; invalid when there is none.</summary>
        private static WorksetId Find(Document doc, string name)
        {
            var workset = new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .ToWorksets()
                .FirstOrDefault(candidate => LinkPreferences.SameWorkset(candidate.Name, name));

            return workset == null ? WorksetId.InvalidWorksetId : workset.Id;
        }

        private static string Owner(WorksetTable table, WorksetId id)
        {
            try
            {
                return table.GetWorkset(id)?.Owner ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// One link: the type with the worksets inside it set up, the instance placed, pinned and put into
        /// its project workset — in a transaction of its own. Null when it did not work out, with the reason
        /// in the result.
        /// </summary>
        private static Pending Create(
            Application app,
            Document doc,
            LinkEntry link,
            WorksetId workset,
            ImportPlacement placement,
            LinkInsertionRequest request,
            LinkInsertionResult result,
            ISet<string> seen)
        {
            var notes = new List<string>();
            var placing = new List<string>();

            using (var configuration = Configuration(app, link, request.Inside, notes, seen))
            using (var options = new RevitLinkOptions(false, configuration))
            using (var transaction = new Transaction(doc, "Insert link"))
            {
                transaction.Start();

                var made = RevitLinkType.Create(doc, LinkCatalog.ToModelPath(link), options);

                if (!LinkLoadResult.IsCodeSuccess(made.LoadResult))
                {
                    transaction.RollBack();

                    if (made.LoadResult == LinkLoadResultType.LinkExists)
                        result.Already.Add(link.Name + " (Revit says it is linked already)");
                    else
                        result.Failures.Add(link.Name + " — " + LinkCatalog.Describe(made.LoadResult));

                    return null;
                }

                var type = doc.GetElement(made.ElementId) as RevitLinkType;
                if (type != null && request.IsAttachment)
                    type.AttachmentType = AttachmentType.Attachment;

                var instance = RevitLinkInstance.Create(doc, made.ElementId, placement);

                // Pinned at once, as "Link Manager" pins: a consultant's link is placed by coordinates, and a
                // drag nobody noticed is later hunted down by the whole team.
                try
                {
                    instance.Pinned = true;
                }
                catch (Exception exception)
                {
                    result.Failures.Add(link.Name + " — could not pin the link: " + LinkCatalog.Explain(exception));
                }

                // On the elements themselves, never through the active workset: the local copy was opened by
                // the run, and whatever Revit made active there is nobody's choice.
                LinkCatalog.Place(instance, workset, placing, link.Name);
                LinkCatalog.Place(type, workset, placing, link.Name);

                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                {
                    result.Failures.Add(link.Name + " — Revit rolled the link back when it was committed (" + status + ")");
                    return null;
                }

                return new Pending(link, made.ElementId, instance.Id, workset, notes, placing);
            }
        }

        /// <summary>
        /// The setup of the worksets inside one link, out of its own worksets read just now — never out of the
        /// window's read, which may be hours old by the time the run reaches this model. Read failures go into
        /// <paramref name="notes"/>, and the link then comes in with the base mode.
        /// </summary>
        private static WorksetConfiguration Configuration(
            Application app,
            LinkEntry link,
            LinkWorksetRule rule,
            List<string> notes,
            ISet<string> seen)
        {
            if (!rule.Requested)
                return rule.Base();

            List<ModelWorkset> worksets;

            try
            {
                worksets = LinkCatalog.ModelWorksets(app, link, false);
            }
            catch (Exception exception)
            {
                notes.Add(link.Name + " — the worksets inside the link could not be read (" + LinkCatalog.Explain(exception) +
                          "), it was linked without the closing");
                return rule.Base();
            }

            // A workshared model always has at least the default workset: an empty list is a read that
            // failed without throwing, and taking it for "no name matched" would lose the only clue.
            if (worksets.Count == 0)
            {
                notes.Add(link.Name + " — the server returned an empty workset list for the link " +
                          "(a read failure rather than an absence), it was linked without the closing");
                return rule.Base();
            }

            foreach (var workset in worksets)
                seen.Add(LinkPreferences.NormalizeWorkset(workset.Name));

            return rule.Configuration(worksets);
        }

        // ───────────────────────────── checking ─────────────────────────────

        /// <summary>A committed link read back: the workset it stands in, then the worksets inside it.</summary>
        private static void Verify(
            Application app,
            Document doc,
            Pending item,
            LinkWorksetRule rule,
            ISet<string> seen,
            LinkInsertionResult result)
        {
            var name = item.Link.Name;
            var instance = doc.GetElement(item.InstanceId);
            var type = doc.GetElement(item.TypeId);

            if (instance == null || type == null)
            {
                result.Failures.Add(name + " — the link is not in the model after it was committed");
                return;
            }

            var standing = LinkCatalog.WorksetName(doc, instance);
            result.Inserted.Add(name + " → " + (standing.Length > 0 ? standing : "(not a user workset)"));

            if (instance.WorksetId != item.Workset || type.WorksetId != item.Workset)
            {
                result.Failures.Add(name + " — inserted, but it stands in \"" + standing + "\" rather than \"" +
                                    item.Link.Workset + "\"" +
                                    (item.Placing.Count > 0 ? ": " + string.Join("; ", item.Placing) : string.Empty));
            }

            try
            {
                bool retried;
                var check = Ensure(app, doc, item, rule, seen, out retried);
                Account(name, item.Notes, check, retried, result);
            }
            catch (Exception exception)
            {
                result.Failures.AddRange(item.Notes);
                result.Failures.Add(name + " — the worksets inside the link could not be checked: " + LinkCatalog.Explain(exception));
            }
        }

        /// <summary>
        /// Checks the worksets inside a committed link and, where Revit did not carry the request out, loads
        /// the link once more with the setup built afresh — "Link Manager"'s cure for Revit Server links,
        /// whose first workset request Revit sometimes ignores while reporting success. Only on a mismatch
        /// known for certain: a second load on "could not be checked" would be firing blind. Outside any
        /// transaction — <c>LoadFrom</c> refuses to run inside one.
        /// </summary>
        private static LinkWorksetCheck Ensure(
            Application app,
            Document doc,
            Pending item,
            LinkWorksetRule rule,
            ISet<string> seen,
            out bool retried)
        {
            retried = false;

            var check = rule.Check(LinkDocument(doc, item.TypeId));
            if (check.Verdict != LinkWorksetVerdict.Mismatched)
                return check;

            var type = doc.GetElement(item.TypeId) as RevitLinkType;
            if (type == null)
                return check;

            var notes = new List<string>();

            try
            {
                using (var configuration = Configuration(app, item.Link, rule, notes, seen))
                {
                    // Without the link's list there is only the base mode, and loading with it again would change nothing.
                    if (notes.Count > 0)
                        return check;

                    retried = true;

                    var reload = type.LoadFrom(LinkCatalog.ToModelPath(item.Link), configuration);
                    if (!LinkLoadResult.IsCodeSuccess(reload.LoadResult))
                        return check.Because(LinkCatalog.Describe(reload.LoadResult));
                }
            }
            catch (Exception exception)
            {
                return check.Because(LinkCatalog.Explain(exception));
            }

            return rule.Check(LinkDocument(doc, item.TypeId));
        }

        /// <summary>
        /// Sorts the check into the result — the same reading "Link Manager"'s report gives it. The notes from
        /// creating the link (its worksets could not be read, so it came in with the base mode) are said only
        /// for a link that is not as asked; one the check found as asked needs no excuse.
        /// </summary>
        private static void Account(string name, IReadOnlyList<string> notes, LinkWorksetCheck check, bool retried, LinkInsertionResult result)
        {
            var unread = notes.Count > 0;

            if (unread && check.Verdict != LinkWorksetVerdict.Satisfied)
                result.Failures.AddRange(notes);

            switch (check.Verdict)
            {
                case LinkWorksetVerdict.Unknown:
                    result.Unverified.Add(name + " — " + check.Reason);
                    break;

                case LinkWorksetVerdict.Mismatched:
                    if (retried)
                        result.Mismatched.Add(name + " — even after a second load: " + string.Join(", ", check.Names) +
                                              (check.Reason.Length > 0 ? " (the second load failed: " + check.Reason + ")" : string.Empty));
                    else if (!unread)
                        result.Mismatched.Add(name + " — Revit did not apply them: " + string.Join(", ", check.Names));
                    break;

                case LinkWorksetVerdict.Satisfied:
                    if (retried)
                        result.Healed.Add(name + (unread
                                              ? " — its worksets could not be read before linking, only after it"
                                              : " — Revit ignored the first request"));
                    break;
            }
        }

        private static Document LinkDocument(Document doc, ElementId typeId)
        {
            return LinkCatalog.Instances(doc, typeId).FirstOrDefault()?.GetLinkDocument();
        }

        // ───────────────────────────── what the model has already ─────────────────────────────

        /// <summary>
        /// The model's top-level links. A link type is in the document even while its workset is closed —
        /// which the local copy's mostly are — so nothing already linked is missed here.
        /// </summary>
        private static List<Present> Existing(Document doc)
        {
            var links = new List<Present>();

            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .Where(type => !type.IsNestedLink);

            foreach (var type in types)
            {
                // A BIM360 link Revit keeps as a cloud resource gives no path here; it still has its name.
                var entry = LinkCatalog.Describe(doc, type);
                var where = entry == null
                    ? "BIM360"
                    : entry.Origin == LinkOrigin.Cloud ? "BIM360" : entry.Path;

                links.Add(new Present(
                    entry == null ? string.Empty : entry.Key,
                    type.Name,
                    where,
                    LinkCatalog.WorksetName(doc, LinkCatalog.Instances(doc, type.Id).FirstOrDefault() ?? (Element)type)));
            }

            return links;
        }

        private static bool SameName(string first, string second)
        {
            return string.Equals(ServerModelIndex.Bare(first), ServerModelIndex.Bare(second), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A link the model had before the run.</summary>
        private sealed class Present
        {
            public Present(string key, string name, string where, string workset)
            {
                Key = key;
                Name = name ?? string.Empty;
                Where = where ?? string.Empty;
                Workset = workset ?? string.Empty;
            }

            /// <summary>The <see cref="LinkEntry.Key"/> of where it points; empty when Revit does not give the path.</summary>
            public string Key { get; }

            public string Name { get; }

            public string Where { get; }

            public string Workset { get; }
        }

        /// <summary>A link committed in this model, waiting to be read back.</summary>
        private sealed class Pending
        {
            public Pending(LinkEntry link, ElementId typeId, ElementId instanceId, WorksetId workset,
                IReadOnlyList<string> notes, IReadOnlyList<string> placing)
            {
                Link = link;
                TypeId = typeId;
                InstanceId = instanceId;
                Workset = workset;
                Notes = notes;
                Placing = placing;
            }

            public LinkEntry Link { get; }

            public ElementId TypeId { get; }

            public ElementId InstanceId { get; }

            /// <summary>The project workset it was put into.</summary>
            public WorksetId Workset { get; }

            /// <summary>What <see cref="Configuration"/> said about the worksets inside it when it was created.</summary>
            public IReadOnlyList<string> Notes { get; }

            /// <summary>Why it could not be put into its project workset — said only if the read-back finds it elsewhere.</summary>
            public IReadOnlyList<string> Placing { get; }
        }
    }

    /// <summary>
    /// The run's full report, written as it goes into <see cref="InsertLinksPreferences.ReportsFolder"/> — the
    /// "Upload to Server" report's reason: a run over a project's models takes hours, and if Revit falls over
    /// at model thirty, the dialog at the end is never shown and this file says which twenty-nine are done.
    /// A failure to write it is kept and said in that dialog, never allowed to stop the run.
    /// </summary>
    internal sealed class LinkInsertionLog
    {
        private LinkInsertionLog(string filePath)
        {
            FilePath = filePath;
        }

        public string FilePath { get; }

        /// <summary>Why the file could not be written; empty while it could.</summary>
        public string Error { get; private set; } = string.Empty;

        public static LinkInsertionLog Start(LinkInsertionRequest request, int models)
        {
            var now = DateTime.Now;
            var log = new LinkInsertionLog(Path.Combine(InsertLinksPreferences.ReportsFolder,
                "Insert " + now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".txt"));

            var lines = new List<string>
            {
                "VladTools — Insert Links, " + now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                "Links (" + request.Links.Count + "), each into its project workset:"
            };

            lines.AddRange(request.Links.Select(link => "    " + link.Name + " → " + link.Workset + "    (" + link.Path + ")"));

            lines.Add("Placement: " + Describe(request.Placement) + ", " + (request.IsAttachment ? "attachment" : "overlay") + ", pinned");
            lines.Add("Worksets inside the links: " + request.Inside.Describe());
            lines.Add("A model without a link's project workset: " +
                      (request.CreateWorksets ? "the workset is made there" : "that link is left out there"));
            lines.Add("Models: " + models);
            lines.Add(string.Empty);
            lines.Add("MODELS");

            log.Write(true, lines);
            return log;
        }

        public void Model(LinkInsertionResult result)
        {
            var lines = new List<string>();
            var lost = !result.Saved && result.Inserted.Count + result.WorksetsMade.Count > 0;

            if (lost)
                lines.Add(Stamp() + result.Name + " — the links were made in the local copy but NOT saved: " + result.Failure);
            else if (result.Failure.Length > 0)
                lines.Add(Stamp() + result.Name + " — NOT done: " + result.Failure);
            else
                lines.Add(Stamp() + result.Name + " — " + Summary(result) + (result.Saved ? ", synchronised" : string.Empty));

            var made = lost ? "made, but lost with the local copy: " : "inserted: ";

            lines.AddRange(result.Inserted.Select(text => "    " + made + text));
            lines.AddRange(result.WorksetsMade.Select(text => "    " + (lost ? "workset made, but lost with the local copy: " : "workset made: ") + text));
            lines.AddRange(result.Already.Select(text => "    already in the model: " + text));
            lines.AddRange(result.Skipped.Select(text => "    left out: " + text));
            lines.AddRange(result.Healed.Select(text => "    worksets inside the link took hold only on a second load: " + text));
            lines.AddRange(result.Mismatched.Select(text => "    worksets inside the link are NOT as asked: " + text));
            lines.AddRange(result.Unverified.Select(text => "    worksets inside the link could not be checked: " + text));
            lines.AddRange(result.Failures.Select(text => "    FAILED: " + text));
            lines.AddRange(result.Notes.Select(text => "    note: " + text));
            lines.AddRange(result.Warnings.Select(text => "    Revit warning, dismissed: " + text));

            Write(false, lines);
        }

        public void Finish(IEnumerable<string> lines)
        {
            Write(false, new[] { string.Empty, Stamp() + "Done." }.Concat(lines));
        }

        /// <summary>"3 inserted, 1 already there, 1 left out, 1 failed".</summary>
        public static string Summary(LinkInsertionResult result)
        {
            var parts = new List<string> { result.Inserted.Count + " inserted" };

            if (result.Already.Count > 0)
                parts.Add(result.Already.Count + " already there");

            if (result.Skipped.Count > 0)
                parts.Add(result.Skipped.Count + " left out");

            if (result.Failures.Count > 0)
                parts.Add(result.Failures.Count + " failed");

            return string.Join(", ", parts);
        }

        public static string Describe(LinkPlacement placement)
        {
            switch (placement)
            {
                case LinkPlacement.Origin:
                    return "origin to origin";
                case LinkPlacement.Centered:
                    return "centre to centre";
                case LinkPlacement.Site:
                    return "by project site location";
                default:
                    return "by shared coordinates";
            }
        }

        private static string Stamp()
        {
            return "[" + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "] ";
        }

        private void Write(bool create, IEnumerable<string> lines)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? InsertLinksPreferences.ReportsFolder);

                var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;

                // The BOM once, at the top — Notepad then reads Cyrillic model names right.
                if (create)
                    File.WriteAllText(FilePath, text, new UTF8Encoding(true));
                else
                    File.AppendAllText(FilePath, text, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                if (Error.Length == 0)
                    Error = exception.Message;
            }
        }
    }
}
