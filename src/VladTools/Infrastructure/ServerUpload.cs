using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>One model of an "Upload to Server" run: what became of its upload and of its links.</summary>
    internal sealed class ServerUploadResult
    {
        public ServerUploadResult(LinkEntry entry)
        {
            Entry = entry;
        }

        /// <summary>The model as it stood in the list: a file to upload, or a server model whose links only are updated.</summary>
        public LinkEntry Entry { get; }

        public string Name => Entry.Name;

        /// <summary>A server model in the list: nothing is uploaded, only its links are updated.</summary>
        public bool LinksOnly => Entry.Origin == LinkOrigin.Server;

        /// <summary>RSN://server/folder/model.rvt — where the file went, or where the server model lies.</summary>
        public string ServerPath { get; set; } = string.Empty;

        /// <summary>The file is on the server now — saved there, and the server folder lists it.</summary>
        public bool Uploaded { get; set; }

        /// <summary>A model of this name was in the folder already, and the window said to leave it.</summary>
        public bool Skipped { get; set; }

        /// <summary>A model of this name was in the folder and has been written over.</summary>
        public bool Replaced { get; set; }

        /// <summary>Why the model did not go up (or, for a BIM360 one, why it was never tried); empty otherwise.</summary>
        public string Failure { get; set; } = string.Empty;

        /// <summary>What is worth knowing about a model that went up: an upgraded version, worksets still checked out…</summary>
        public List<string> Notes { get; } = new List<string>();

        /// <summary>How many top-level links the model has; -1 — not read.</summary>
        public int LinkCount { get; set; } = -1;

        /// <summary>The user worksets its link types stand in — what has to be open to reload them; null — not read.</summary>
        public List<string> LinkWorksets { get; set; }

        /// <summary>The second pass took this model up.</summary>
        public bool LinksTried { get; set; }

        /// <summary>What became of the links that were pointed somewhere; null when none were.</summary>
        public LinkRepointOutcome Links { get; set; }

        /// <summary>Links left where they point, each with the reason: not on the server, several of one name, a file link…</summary>
        public List<string> LinksLeft { get; } = new List<string>();

        /// <summary>Links that already pointed at the server model they would have been given.</summary>
        public int LinksAlready { get; set; }

        /// <summary>Why the links could not be updated at all — the model would not open, or would not synchronise.</summary>
        public string LinksFailure { get; set; } = string.Empty;

        /// <summary>What is worth knowing about the second pass that is not a failure of it.</summary>
        public List<string> LinksNotes { get; } = new List<string>();

        /// <summary>Revit's warnings dismissed while the model went up (<see cref="WarningSilencer"/>) — said here, never shown as dialogs.</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>The same for the second pass — "needs Coordination Review" after a link was repointed, above all.</summary>
        public List<string> LinksWarnings { get; } = new List<string>();
    }

    /// <summary>
    /// Putting models on Revit Server and pointing their links there — what "Upload to Server" does to
    /// every model of its list, in two passes.
    ///
    /// **The server takes a model only through Revit.** Revit Server keeps a model as a folder of its own
    /// data, not as an .rvt file, and its REST service creates and lists folders but accepts no model;
    /// <c>RevitServerTool</c> only goes the other way. So every model is opened in Revit and saved on the
    /// server as a central model (<c>Document.SaveAs</c> to an <c>RSN://</c> path with
    /// <c>WorksharingSaveAsOptions.SaveAsCentral</c>) — the "Save As" a person does by hand, without the
    /// clicks.
    ///
    /// **Pass 1 opens a file detached with every workset closed.** Detached, so the file on disk is never
    /// written to; every workset closed, so neither the geometry nor a single link is loaded — a link in a
    /// closed workset does not load — and opening costs seconds, not minutes. A closed workset's elements
    /// are still in the file Revit saves: closing a workset only keeps them out of memory. Saving as
    /// central makes the one who saved the owner of every workset, so everything is handed back at once.
    ///
    /// **Pass 2 comes only after every upload**, because a link can be pointed only at a model already on
    /// the server, and the models of one project link one another both ways. Each uploaded model is
    /// opened again — a local copy, with only the worksets its link types stand in (read in pass 1) —
    /// every link with a certain namesake on the server is reloaded from there through
    /// <see cref="LinkRepointing"/>, the very run of "Link Updater", and the model is synchronised back.
    /// Pass 2 on its own, over models already on a server, is the "Link Updater" batch.
    /// </summary>
    internal static class ServerUpload
    {
        /// <summary>The models in a server folder, by name, case aside. Throws with ready-to-show text.</summary>
        public static HashSet<string> ModelNames(ModelFolder folder)
        {
            return new HashSet<string>(
                RevitServerClient.Contents(folder.Server, folder.Path).Where(entry => !entry.IsFolder).Select(entry => entry.Name),
                StringComparer.OrdinalIgnoreCase);
        }

        // ───────────────────────────── where each file goes ─────────────────────────────

        /// <summary>
        /// The deepest folder every one of these files lies under — what "Recreate the subfolders" repeats
        /// the structure below. Null when they share none worth the name (different drives or servers).
        /// Compared folder by folder, case aside, so "D:\SLS\PS" is never taken for "D:\SLS\PS2".
        /// </summary>
        public static string CommonFolder(IEnumerable<string> files)
        {
            List<string> common = null;

            foreach (var file in files)
            {
                var parts = (Path.GetDirectoryName(file ?? string.Empty) ?? string.Empty).Split('\\', '/');

                if (common == null)
                {
                    common = parts.ToList();
                    continue;
                }

                var same = 0;
                while (same < common.Count && same < parts.Length &&
                       string.Equals(common[same], parts[same], StringComparison.OrdinalIgnoreCase))
                    same++;

                common.RemoveRange(same, common.Count - same);
            }

            if (common == null)
                return null;

            var root = string.Join("\\", common);
            return root.TrimEnd('\\').Length == 0 ? null : root;
        }

        /// <summary>
        /// The server folder a file goes into, in service form: the destination, plus the path the file's
        /// folder has below <paramref name="root"/> — or the destination itself with no root. Null when a
        /// folder on the way has a name Revit Server will not take, with the reason in <paramref name="problem"/>.
        /// </summary>
        public static string TargetPath(string destination, string root, string file, out string problem)
        {
            problem = string.Empty;

            var folder = Path.GetDirectoryName(file ?? string.Empty) ?? string.Empty;
            if (string.IsNullOrEmpty(root) || folder.Length < root.Length)
                return destination;

            var path = destination;

            foreach (var segment in folder.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var bad = RevitServerClient.NameProblem(segment);
                if (bad.Length > 0)
                {
                    problem = "its folder \"" + segment + "\" cannot be a Revit Server folder: " + bad;
                    return null;
                }

                path = path == RevitServerClient.RootFolder ? RevitServerClient.RootFolder + segment : path + "|" + segment;
            }

            return path;
        }

        // ───────────────────────────── pass 1: the upload ─────────────────────────────

        /// <summary>
        /// Pass 1 for one file: open it detached with every workset closed, read where its links stand,
        /// save it on the server as a central model, hand back the worksets that made this user own, and
        /// confirm the server folder lists it. Every refusal is a reason in the result, never an exception.
        /// </summary>
        /// <param name="taken">The models in the destination folder before the run, by name.</param>
        /// <param name="uploaded">The models this run has put on the server so far, by name without the extension — added to here.</param>
        public static void Upload(
            Application app,
            ServerUploadResult result,
            ModelFolder destination,
            bool replace,
            ISet<string> taken,
            IDictionary<string, ServerUploadResult> uploaded)
        {
            var entry = result.Entry;
            var name = entry.Name;
            result.ServerPath = RevitServerClient.RsnPath(destination.Server, destination.Path, name);

            var problem = RevitServerClient.NameProblem(ServerModelIndex.Bare(name));
            if (problem.Length > 0)
            {
                result.Failure = "Revit Server will not take this name: " + problem;
                return;
            }

            var exists = taken.Contains(name);
            if (exists && !replace)
            {
                result.Skipped = true;
                return;
            }

            if (!Ready(app, result))
                return;

            var options = new OpenOptions { DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets };
            options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));

            Document doc;

            try
            {
                doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(entry.Path), options);
            }
            catch (Exception exception)
            {
                result.Failure = "the model would not open: " + LinkCatalog.Explain(exception);
                return;
            }

            try
            {
                try
                {
                    ReadLinks(doc, result);
                }
                catch (Exception)
                {
                    // Not read here, read in the second pass: it opens the server model to look first.
                    result.LinkCount = -1;
                    result.LinkWorksets = null;
                }

                // The worksets a new local opens with by default: all of them. Saved with every workset
                // closed, "last viewed" would hand the first colleague to open it an empty model.
                var worksharing = new WorksharingSaveAsOptions
                {
                    SaveAsCentral = true,
                    OpenWorksetsDefault = SimpleWorksetConfiguration.AllWorksets
                };

                var save = new SaveAsOptions { OverwriteExistingFile = exists };
                save.SetWorksharingOptions(worksharing);

                doc.SaveAs(ModelPathUtils.ConvertUserVisiblePathToModelPath(result.ServerPath), save);

                HandBack(app, doc, result);
            }
            catch (Exception exception)
            {
                result.Failure = "it could not be saved on the server: " + LinkCatalog.Explain(exception);
                return;
            }
            finally
            {
                Close(doc);
            }

            Confirm(result, destination, exists);

            // One name uploaded into two subfolders is the batch's own copy of neither: kept as null, so
            // pass 2 says so instead of picking one.
            if (result.Uploaded)
            {
                var key = ServerModelIndex.Bare(name);
                uploaded[key] = uploaded.ContainsKey(key) ? null : result;
            }
        }

        /// <summary>
        /// What the file's own header rules out before it is opened — opening a large model only to find
        /// it cannot go up costs a minute per model. Notes an upgrade on the way.
        /// </summary>
        private static bool Ready(Application app, ServerUploadResult result)
        {
            var path = result.Entry.Path;

            if (!File.Exists(path))
            {
                result.Failure = "there is no such file — the path may have come from a saved list and gone stale, " +
                                 "or the network folder may not be reachable from this machine";
                return false;
            }

            var header = BatchCoordination.Info(path);
            if (header == null)
            {
                result.Failure = "Revit will not read even the file's header: it may be open by somebody, damaged, " +
                                 "or not a Revit model at all";
                return false;
            }

            if (header.IsLater)
            {
                result.Failure = "the model was saved in a newer Revit (" + header.Format + ") than this one (" +
                                 app.VersionNumber + "): it will not open here, and this server takes only " +
                                 app.VersionNumber + " models";
                return false;
            }

            if (!header.IsWorkshared)
            {
                result.Failure = "the model is not workshared, and Revit Server keeps only workshared models — " +
                                 "enable worksharing in it (Collaborate → Worksets) and add it again";
                return false;
            }

            if (header.Format.Length > 0 && header.Format != app.VersionNumber)
                result.Notes.Add("saved in Revit " + header.Format + " — upgraded to " + app.VersionNumber + " on the way");

            foreach (var doc in app.Documents.Cast<Document>())
            {
                if (string.Equals(doc.PathName, path, StringComparison.OrdinalIgnoreCase))
                {
                    result.Failure = "the file is open in this Revit session — close it first";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The model's top-level links and the user worksets their types stand in. Read while every
        /// workset is closed: a link type is in the document even then — that is why Revit has a "closed
        /// workset" status for links at all — and the second pass opens exactly these worksets. A link in
        /// a closed workset cannot be reloaded, and opening every workset would load the whole model and
        /// every link in it.
        /// </summary>
        public static void ReadLinks(Document doc, ServerUploadResult result)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .Where(type => !type.IsNestedLink)
                .ToList();

            var table = doc.GetWorksetTable();
            var names = new List<string>();

            foreach (var type in types)
            {
                try
                {
                    var workset = table.GetWorkset(type.WorksetId);
                    if (workset != null && workset.Kind == WorksetKind.UserWorkset &&
                        !names.Any(known => LinkPreferences.SameWorkset(known, workset.Name)))
                        names.Add(workset.Name);
                }
                catch (Exception)
                {
                    // A workset Revit will not name is one the second pass cannot open by name either;
                    // its link then reads "closed workset" there, and the report says so.
                }
            }

            result.LinkCount = types.Count;
            result.LinkWorksets = names;
        }

        /// <summary>
        /// Saving as central makes the one who saved it the owner of every workset in it — which is why
        /// Revit's own dialog asks about relinquishing when a new central is closed. Left owned, nobody else
        /// could edit a thing in the model; so everything is handed back, and read back.
        /// </summary>
        private static void HandBack(Application app, Document doc, ServerUploadResult result)
        {
            try
            {
                WorksharingUtils.RelinquishOwnership(doc, new RelinquishOptions(true), new TransactWithCentralOptions());
            }
            catch (Exception exception)
            {
                result.Notes.Add("its worksets could not be handed back (" + LinkCatalog.Short(exception.Message) +
                                 ") — they are checked out to you: open it from the server and use Collaborate → Relinquish All Mine");
                return;
            }

            try
            {
                var still = new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToWorksets()
                    .Where(workset => !string.IsNullOrEmpty(workset.Owner) &&
                                      string.Equals(workset.Owner, app.Username, StringComparison.OrdinalIgnoreCase))
                    .Select(workset => workset.Name)
                    .ToList();

                if (still.Count > 0)
                    result.Notes.Add("still checked out to you after handing back: " + Quote(still) +
                                     " — open it from the server and use Collaborate → Relinquish All Mine");
            }
            catch (Exception)
            {
                // The hand-back raised no error; there is nothing more to be found out.
            }
        }

        /// <summary>
        /// "Saved" is what Revit said; the server folder listing the model is what happened. A listing
        /// that cannot be read leaves the save standing, with a note — it raised no error.
        /// </summary>
        private static void Confirm(ServerUploadResult result, ModelFolder destination, bool existed)
        {
            try
            {
                if (!ModelNames(destination).Contains(result.Name))
                {
                    result.Failure = "Revit reported it saved, but the server folder does not list it";
                    return;
                }
            }
            catch (Exception exception)
            {
                result.Notes.Add("the server folder could not be read to confirm it (" + LinkCatalog.Short(exception.Message) + ")");
            }

            result.Uploaded = true;
            result.Replaced = existed;
        }

        private static void Close(Document doc)
        {
            try
            {
                doc.Close(false);
            }
            catch (Exception)
            {
                // Revit does not always let go of a document — the same as in BatchCoordination.Release.
            }
        }

        // ───────────────────────────── pass 2: the links ─────────────────────────────

        /// <summary>
        /// Pass 2 for one model on the server: a local copy with the worksets its links stand in, every
        /// link with a certain target pointed there, synchronised back with everything handed back. A
        /// model nothing was pointed in is relinquished and closed, never synchronised.
        /// </summary>
        public static void UpdateLinks(Application app, ServerUploadResult result, ServerLinkTargets targets)
        {
            result.LinksTried = true;

            var entry = LinkEntry.ForServer(result.ServerPath);
            string reason;

            if (result.LinkWorksets == null)
            {
                // A server model from the list: where its links stand has not been read — read it the
                // way the upload does, detached with every workset closed, and close it again.
                var probe = BatchCoordination.OpenToRead(app, entry, out reason);
                if (probe == null)
                {
                    result.LinksFailure = reason;
                    return;
                }

                try
                {
                    ReadLinks(probe.Document, result);
                }
                catch (Exception exception)
                {
                    result.LinksFailure = "its links could not be read: " + LinkCatalog.Explain(exception);
                    return;
                }
                finally
                {
                    BatchCoordination.Release(probe);
                }
            }

            if (result.LinkCount == 0)
                return;

            var model = BatchCoordination.OpenLocal(app, entry, result.LinkWorksets, out reason);
            if (model == null)
            {
                result.LinksFailure = reason;
                return;
            }

            if (model.Note.Length > 0)
                result.LinksNotes.Add(model.Note);

            try
            {
                var doc = model.Document;

                int nested;
                var links = LinkRepointing.Collect(doc, out nested);

                targets.Search(Folder(result.ServerPath), links);
                var plan = targets.Plan(links, result);

                var outcome = new LinkRepointOutcome();
                var paths = plan.ToDictionary(pair => pair.Key.TypeId, pair => pair.Value);

                LinkRepointing.Run(doc, plan.Select(pair => pair.Key).ToList(), link => paths[link.TypeId], outcome);
                result.Links = outcome;

                var failures = new List<string>();

                if (outcome.Count > 0)
                {
                    // No comment, as the office asked (October 2026): the model's history on the server
                    // says who synchronised and when without one.
                    if (!BatchCoordination.Commit(model, string.Empty, failures))
                        result.LinksFailure = Strip(failures, model.Entry.Name);
                }
                else
                {
                    BatchCoordination.Relinquish(model, failures);
                    if (failures.Count > 0)
                        result.LinksNotes.Add(Strip(failures, model.Entry.Name));
                }
            }
            catch (Exception exception)
            {
                result.LinksFailure = "the links broke off: " + LinkCatalog.Explain(exception);
            }
            finally
            {
                BatchCoordination.Release(model);
            }
        }

        /// <summary>
        /// Pass 2 over a whole list: what "Upload to Server" runs after its uploads, and the whole of the
        /// "Link Updater" batch. Each model is the unit of work — one that fails is a line, and the next is
        /// taken — and each goes into the report the moment it is done. Revit's warnings are taken after
        /// every model, so each model's lines say its own.
        /// </summary>
        public static void UpdateLinks(
            Application app,
            IReadOnlyList<ServerUploadResult> queue,
            ServerLinkTargets targets,
            ServerUploadLog log,
            WarningSilencer warnings)
        {
            foreach (var result in queue)
            {
                try
                {
                    UpdateLinks(app, result, targets);
                }
                catch (Exception exception)
                {
                    // Nothing in UpdateLinks is meant to throw; a model that does anyway is a line, not the
                    // end of a run hours long.
                    result.LinksFailure = "the links broke off: " + LinkCatalog.Explain(exception);
                }

                result.LinksWarnings.AddRange(warnings.Take());
                log.Links(result);
            }

            if (targets.LimitReached)
                log.Lines(new[] { "The server search stopped after " + targets.Limit + " folders: a link left \"not on the server\" may still be there." });

            if (targets.Failures.Count > 0)
                log.Lines(new[] { "Server folders that could not be read:" }.Concat(targets.Failures.Select(line => "    " + line)));
        }

        /// <summary>The folder a server model lies in.</summary>
        private static ModelFolder Folder(string rsnPath)
        {
            string server;
            string folderPath;
            string name;

            return RevitServerClient.TryParse(rsnPath, out server, out folderPath, out name)
                ? ModelFolder.ForServer(server, folderPath)
                : null;
        }

        /// <summary>BatchCoordination's lines start with the model's name; in a line about that model it is said already.</summary>
        private static string Strip(IEnumerable<string> lines, string name)
        {
            return string.Join("; ", lines.Select(line => line.StartsWith(name + " — ", StringComparison.Ordinal)
                ? line.Substring(name.Length + 3)
                : line));
        }

        public static string Quote(IReadOnlyList<string> values)
        {
            const int limit = 8;

            var text = string.Join(", ", values.Take(limit).Select(value => "\"" + value + "\""));

            return values.Count > limit ? text + " and " + (values.Count - limit) + " more" : text;
        }
    }

    /// <summary>
    /// The destination folder and the folders below it, as an "Upload to Server" run (or its window) sees
    /// them: what each holds, read once, and the missing ones made. A folder that does not exist yet holds
    /// no models — so "already there" is answered for a subfolder before it is made. The destination
    /// itself is taken as existing (it was picked in the tree); below it, a folder exists when its parent
    /// lists it, so nothing above the destination is ever read.
    /// </summary>
    internal sealed class ServerFolderCache
    {
        private readonly string _server;
        private readonly string _root;
        private readonly Dictionary<string, Listing> _read = new Dictionary<string, Listing>(StringComparer.OrdinalIgnoreCase);

        private sealed class Listing
        {
            public HashSet<string> Models;
            public HashSet<string> Folders;
        }

        /// <param name="root">The destination folder, in service form.</param>
        public ServerFolderCache(string server, string root)
        {
            _server = server;
            _root = root;
        }

        public string Server => _server;

        /// <summary>The models in a folder by name; empty for one not made yet. Throws with ready-to-show text.</summary>
        public ISet<string> Models(string path)
        {
            return Read(path)?.Models ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The folders in a folder by name; empty for one not made yet. Throws with ready-to-show text.</summary>
        public ISet<string> Folders(string path)
        {
            return Read(path)?.Folders ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Whether the folder is on the server. Throws with ready-to-show text.</summary>
        public bool Exists(string path)
        {
            return Read(path) != null;
        }

        /// <summary>
        /// Makes the folder and every missing one above it, top down — the service makes one level per
        /// request — and reads each back: a folder its parent does not list afterwards was not made, whatever
        /// the request answered. The paths made go into <paramref name="made"/>. Throws with ready-to-show text.
        /// </summary>
        public bool Ensure(string path, List<string> made, out string failure)
        {
            failure = string.Empty;

            if (Read(path) != null)
                return true;

            var parent = RevitServerClient.ParentFolder(path);
            if (parent == null)
            {
                failure = "the server root cannot be made";
                return false;
            }

            if (!Ensure(parent, made, out failure))
                return false;

            RevitServerClient.CreateFolder(_server, path);

            _read.Remove(parent);
            _read.Remove(path);

            if (Read(path) == null)
            {
                failure = "the server answered, but " + RevitServerClient.RsnPath(_server, path, string.Empty).TrimEnd('/') + " is not there";
                return false;
            }

            made.Add(path);
            return true;
        }

        /// <summary>A folder's listing; null when it does not exist.</summary>
        private Listing Read(string path)
        {
            Listing listing;
            if (_read.TryGetValue(path, out listing))
                return listing;

            if (!string.Equals(path, _root, StringComparison.OrdinalIgnoreCase))
            {
                var parent = RevitServerClient.ParentFolder(path);
                var above = parent == null ? null : Read(parent);

                if (above == null || !above.Folders.Contains(RevitServerClient.FolderName(path)))
                {
                    _read[path] = null;
                    return null;
                }
            }

            var entries = RevitServerClient.Contents(_server, path);

            listing = new Listing
            {
                Models = new HashSet<string>(entries.Where(entry => !entry.IsFolder).Select(entry => entry.Name), StringComparer.OrdinalIgnoreCase),
                Folders = new HashSet<string>(entries.Where(entry => entry.IsFolder).Select(entry => entry.Name), StringComparer.OrdinalIgnoreCase)
            };

            _read[path] = listing;
            return listing;
        }
    }

    /// <summary>
    /// Where the second pass of "Upload to Server" points each link. A model of the link's name uploaded
    /// in this run comes first — the batch's own copy is the certain one, whatever namesakes the server
    /// holds elsewhere. Failing that, exactly one model of exactly the name found round the model's own
    /// folder, searched the way "Link Updater" searches: the folder with everything under it, then a level
    /// up while some link is still missing, never onto the server root. Anything less certain — several of
    /// one name, a name that only resembles — is left for "Link Updater", where a person chooses: nobody is
    /// watching a batch.
    ///
    /// One index serves the whole pass (<see cref="ServerModelIndex"/> only grows), so the models of one
    /// project read their shared folders once. It is bounded by the batch's limit, not the window's: a run
    /// over a whole server reads every project's folders. And because it holds every project read so far,
    /// a link is matched only with the models under the folder its own model's search reached — never with
    /// a namesake in another project that an earlier model of the run happened to read.
    /// </summary>
    internal sealed class ServerLinkTargets
    {
        private readonly ServerModelIndex _index = new ServerModelIndex(ServerModelIndex.BatchFolderLimit);
        private readonly IDictionary<string, ServerUploadResult> _uploaded;

        /// <summary>The highest folder the last <see cref="Search"/> read; null — the model's folder is not known.</summary>
        private ModelFolder _reach;

        /// <param name="uploaded">The models this run uploaded, by name without the extension; null for a name it uploaded twice.</param>
        public ServerLinkTargets(IDictionary<string, ServerUploadResult> uploaded)
        {
            _uploaded = uploaded ?? new Dictionary<string, ServerUploadResult>();
        }

        /// <summary>Folders the search could not read — named in the report, not fatal.</summary>
        public IReadOnlyList<string> Failures => _index.Failures;

        public bool LimitReached => _index.LimitReached;

        /// <summary>The most folders the run reads in all.</summary>
        public int Limit => _index.Limit;

        /// <summary>Reads the server round a model's folder, as far as its links need; <see cref="Plan"/> then looks only there.</summary>
        public void Search(ModelFolder folder, IReadOnlyList<LinkPathInfo> links)
        {
            _reach = folder;

            if (folder == null)
                return;

            Walk(folder);

            while (!_index.LimitReached && links.Any(link => link.Obstacle.Length == 0 && Missing(link)))
            {
                var above = RevitServerClient.ParentFolder(_reach.Path);
                if (above == null || above == RevitServerClient.RootFolder)
                    return;

                _reach = ModelFolder.ForServer(_reach.Server, above);
                Walk(_reach);
            }
        }

        private void Walk(ModelFolder folder)
        {
            if (!_index.Covers(folder))
                _index.Walk(folder);
        }

        private bool Missing(LinkPathInfo link)
        {
            return !_uploaded.ContainsKey(ServerModelIndex.Bare(link.Name)) && _index.Named(link.Name, _reach).Count == 0;
        }

        /// <summary>
        /// The links to point and where; every link left alone goes into <see cref="ServerUploadResult.LinksLeft"/>
        /// with its reason, and one already pointing at its target is only counted. The window's rules of
        /// "Link Updater" hold: a server model already linked here as another link is not chosen, and two
        /// links given one model get neither.
        /// </summary>
        public List<KeyValuePair<LinkPathInfo, string>> Plan(IReadOnlyList<LinkPathInfo> links, ServerUploadResult result)
        {
            var linkedAs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var link in links)
            {
                if (!linkedAs.ContainsKey(link.Current.Key))
                    linkedAs[link.Current.Key] = link.Name;
            }

            var chosen = new List<KeyValuePair<LinkPathInfo, string>>();

            foreach (var link in links)
            {
                if (link.Obstacle.Length > 0)
                {
                    result.LinksLeft.Add(link.Name + " — " + link.Obstacle);
                    continue;
                }

                string reason;
                var target = Target(link, out reason);
                if (target == null)
                {
                    result.LinksLeft.Add(link.Name + " — " + reason);
                    continue;
                }

                var key = LinkEntry.ForServer(target).Key;
                if (key == link.Current.Key)
                {
                    result.LinksAlready++;
                    continue;
                }

                string other;
                if (linkedAs.TryGetValue(key, out other))
                {
                    result.LinksLeft.Add(link.Name + " — its server model is linked here already as \"" + other +
                                         "\": remove one of the two in Manage Links");
                    continue;
                }

                chosen.Add(new KeyValuePair<LinkPathInfo, string>(link, target));
            }

            var clashing = chosen
                .GroupBy(pair => LinkEntry.ForServer(pair.Value).Key)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group)
                .ToList();

            foreach (var pair in clashing)
            {
                result.LinksLeft.Add(pair.Key.Name + " — another link of this model would get the same server model " +
                                     pair.Value + ": choose in \"Link Updater\"");
                chosen.Remove(pair);
            }

            return chosen;
        }

        private string Target(LinkPathInfo link, out string reason)
        {
            reason = string.Empty;
            var plainFile = link.Current.Origin == LinkOrigin.File && !link.IsCloud;

            ServerUploadResult own;
            if (_uploaded.TryGetValue(ServerModelIndex.Bare(link.Name), out own))
            {
                if (own == null)
                {
                    reason = "this run uploaded a model of this name into more than one folder — choose one in \"Link Updater\"";
                    return null;
                }

                // A file link moves with the batch only when it points at the very file this run
                // uploaded — then the server copy is that file. Another file of the same name may be a
                // local model on purpose.
                if (plainFile && !SameFile(link.Current.Path, own.Entry.Path))
                {
                    reason = "a file link to " + link.Current.Path + ", not to the file uploaded under this name — left as it is";
                    return null;
                }

                return own.ServerPath;
            }

            if (plainFile)
            {
                reason = "a file link — left as it is (\"Link Updater\" points it at the server if it should be)";
                return null;
            }

            if (_reach == null)
            {
                reason = "where the model itself lies on the server is not known, so nothing was searched round it";
                return null;
            }

            var exact = _index.Named(link.Name, _reach);
            if (exact.Count == 1)
                return exact[0].RsnPath;

            if (exact.Count > 1)
            {
                reason = exact.Count + " models of this name under " + _reach.Display + " — choose one in \"Link Updater\"";
                return null;
            }

            var loose = _index.Resembling(link.Name, _reach);
            reason = "no model of this name under " + _reach.Display +
                     (loose.Count > 0 ? " — a similar one: " + loose[0].Name + "; check it in \"Link Updater\"" : string.Empty) +
                     (_index.LimitReached ? " (the search stopped after " + _index.Limit + " folders)" : string.Empty);
            return null;
        }

        private static bool SameFile(string first, string second)
        {
            return string.Equals(
                (first ?? string.Empty).Replace('/', '\\').Trim(),
                (second ?? string.Empty).Replace('/', '\\').Trim(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The run's full report, written as it goes into <see cref="ServerUploadPreferences.ReportsFolder"/>.
    /// A run over a hundred models takes hours; if Revit falls over at model sixty, the dialog at the end
    /// is never shown, and this file says which fifty-nine are done. A failure to write it is kept and
    /// said in that dialog, never allowed to stop the run.
    /// </summary>
    internal sealed class ServerUploadLog
    {
        private ServerUploadLog(string filePath)
        {
            FilePath = filePath;
        }

        public string FilePath { get; }

        /// <summary>Why the file could not be written; empty while it could.</summary>
        public string Error { get; private set; } = string.Empty;

        /// <param name="subfolders">
        /// Null — every file straight into the destination; otherwise the folder whose subfolders are
        /// recreated below it, empty when the files share none.
        /// </param>
        public static ServerUploadLog Start(ModelFolder destination, int files, int linksOnly, bool links, bool replace, string subfolders)
        {
            var now = DateTime.Now;
            var log = new ServerUploadLog(Path.Combine(ServerUploadPreferences.ReportsFolder,
                "Upload " + now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".txt"));

            log.Write(true, new[]
            {
                "VladTools — Upload to Server, " + now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                "Into: " + (destination == null ? "(nothing to upload — links only)" : destination.Display),
                "Subfolders: " + (subfolders == null
                    ? "not recreated — every file straight into the folder"
                    : subfolders.Length == 0
                        ? "asked for, but the files share no folder (different drives) — every file straight into the folder"
                        : "recreated below it as they are below " + subfolders),
                "Files to upload: " + files + (replace ? " (a model of the same name already there is replaced)" : " (a model of the same name already there is left as it is)"),
                "Server models for links only: " + linksOnly,
                "Links updated afterwards: " + (links ? "yes" : "no"),
                string.Empty,
                "UPLOAD"
            });

            return log;
        }

        /// <summary>The "Link Updater" batch's report: nothing goes up, only the links of models already on a server.</summary>
        public static ServerUploadLog StartLinks(int models)
        {
            var now = DateTime.Now;
            var log = new ServerUploadLog(Path.Combine(LinkUpdaterPreferences.ReportsFolder,
                "Links " + now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".txt"));

            log.Write(true, new[]
            {
                "VladTools — Link Updater, several models, " + now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                "Revit Server models: " + models,
                string.Empty,
                "LINKS"
            });

            return log;
        }

        public void Section(string title)
        {
            Write(false, new[] { string.Empty, title });
        }

        public void Upload(ServerUploadResult result)
        {
            string line;

            if (result.Uploaded)
                line = result.Name + " — " + (result.Replaced ? "REPLACED " : "uploaded ") + "→ " + result.ServerPath +
                       " (" + Count(result.LinkCount, "link") + ")";
            else if (result.Skipped)
                line = result.Name + " — already there, left as it is: " + result.ServerPath;
            else
                line = result.Name + " — NOT uploaded: " + result.Failure +
                       (result.Entry.Path.Length > 0 ? " (" + result.Entry.Path + ")" : string.Empty);

            Write(false, new[] { Stamp() + line }
                .Concat(result.Notes.Select(note => "    note: " + note))
                .Concat(result.Warnings.Select(warning => "    Revit warning, dismissed: " + warning)));
        }

        public void Links(ServerUploadResult result)
        {
            var lines = new List<string>();

            if (result.LinksFailure.Length > 0)
                lines.Add(Stamp() + result.Name + " — links NOT updated: " + result.LinksFailure);
            else if (result.LinkCount == 0)
                lines.Add(Stamp() + result.Name + " — no links (none were found while it was " + (result.Uploaded ? "uploaded" : "read") + ")");
            else
                lines.Add(Stamp() + result.Name + " — " + LinkSummary(result));

            if (result.LinkCount > 0 && result.LinkWorksets != null)
                lines.Add("    worksets asked for, the ones its links stand in: " +
                          (result.LinkWorksets.Count == 0 ? "none (no link stands in a user workset)" : string.Join(", ", result.LinkWorksets)));

            var outcome = result.Links;
            if (outcome != null)
            {
                lines.AddRange(outcome.Repointed.Select(text => "    " + text));
                lines.AddRange(outcome.NotLoaded.Select(text => "    repointed, but Revit did not load it from there: " + text));
                lines.AddRange(outcome.Healed.Select(text => "    worksets inside the link came back only on a second load: " + text));
                lines.AddRange(outcome.Mismatched.Select(text => "    worksets inside the link differ from before: " + text));
                lines.AddRange(outcome.Absent.Select(text => "    open before, but the server model has no such workset: " + text));
                lines.AddRange(outcome.Unverified.Select(text => "    worksets inside the link could not be compared: " + text));
                lines.AddRange(outcome.Remembered.Select(text => "    was not loaded, worksets as Revit remembered: " + text));
                lines.AddRange(outcome.Failures.Select(text => "    FAILED: " + text));
                lines.AddRange(outcome.Notes.Select(text => "    note: " + text));
            }

            lines.AddRange(result.LinksLeft.Select(text => "    left as it is: " + text));
            lines.AddRange(result.LinksNotes.Select(note => "    note: " + note));
            lines.AddRange(result.LinksWarnings.Select(warning => "    Revit warning, dismissed: " + warning));

            Write(false, lines);
        }

        public void Lines(IEnumerable<string> lines)
        {
            Write(false, lines);
        }

        public void Finish(IEnumerable<string> lines)
        {
            Write(false, new[] { string.Empty, Stamp() + "Done." }.Concat(lines));
        }

        /// <summary>The second pass in one line, for the report's totals.</summary>
        public static string LinkTotals(IReadOnlyList<ServerUploadResult> results)
        {
            var tried = results.Where(result => result.LinksTried).ToList();

            return "Links: models where links were pointed at the server: " +
                   tried.Count(result => result.Links != null && result.Links.Count > 0 && result.LinksFailure.Length == 0) +
                   " of " + tried.Count + "; models whose links could not be updated: " +
                   tried.Count(result => result.LinksFailure.Length > 0) + ".";
        }

        /// <summary>
        /// The second pass in the dialog at the end, as titled lists: "Upload to Server" and the "Link
        /// Updater" batch say the same about the same work, each in its own dialog.
        /// </summary>
        public static List<KeyValuePair<string, List<string>>> LinkSections(IReadOnlyList<ServerUploadResult> results)
        {
            var tried = results.Where(result => result.LinksTried).ToList();

            return new List<KeyValuePair<string, List<string>>>
            {
                Titled("Links", tried.Where(result => result.LinksFailure.Length == 0)
                    .Select(result => result.Name + " — " + (result.LinkCount == 0 ? "no links" : LinkSummary(result)))),

                Titled("Links left as they are — finish them in \"Link Updater\" with the model open", tried
                    .SelectMany(result => result.LinksLeft.Select(line => result.Name + ": " + line))),

                Titled("Links that failed", tried
                    .Where(result => result.Links != null)
                    .SelectMany(result => result.Links.Failures.Select(line => result.Name + ": " + line))),

                Titled("Links could not be updated", tried.Where(result => result.LinksFailure.Length > 0)
                    .Select(result => result.Name + " — " + result.LinksFailure))
            };
        }

        /// <summary>Every warning dismissed in the run, each with its model — for the dialog at the end.</summary>
        public static List<string> WarningLines(IReadOnlyList<ServerUploadResult> results)
        {
            return results
                .SelectMany(result => result.Warnings.Concat(result.LinksWarnings).Select(warning => result.Name + " — " + warning))
                .ToList();
        }

        private static KeyValuePair<string, List<string>> Titled(string title, IEnumerable<string> lines)
        {
            return new KeyValuePair<string, List<string>>(title, lines.ToList());
        }

        /// <summary>"5 pointed at the server, 1 already there, 2 left as they are, 1 failed".</summary>
        public static string LinkSummary(ServerUploadResult result)
        {
            var outcome = result.Links;
            var parts = new List<string>
            {
                (outcome == null ? 0 : outcome.Count) + " of " + Count(result.LinkCount, "link") + " pointed at the server"
            };

            if (result.LinksAlready > 0)
                parts.Add(result.LinksAlready + " pointed there already");

            if (result.LinksLeft.Count > 0)
                parts.Add(result.LinksLeft.Count + " left as they are");

            if (outcome != null && outcome.Failures.Count > 0)
                parts.Add(outcome.Failures.Count + " failed");

            return string.Join(", ", parts);
        }

        private static string Count(int count, string word)
        {
            if (count < 0)
                return "links not read";

            return count + " " + word + (count == 1 ? string.Empty : "s");
        }

        private static string Stamp()
        {
            return "[" + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "] ";
        }

        private void Write(bool create, IEnumerable<string> lines)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ServerUploadPreferences.ReportsFolder);

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
