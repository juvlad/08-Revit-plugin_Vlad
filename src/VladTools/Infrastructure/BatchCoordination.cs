using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// One model opened in the background for a batch run: the document itself plus everything the
    /// command needs to put it back where it came from.
    /// </summary>
    internal sealed class BatchModel
    {
        public BatchModel(LinkEntry entry, Document document, IReadOnlyList<string> worksets)
        {
            Entry = entry;
            Document = document;
            Worksets = worksets ?? new List<string>();
        }

        /// <summary>The model as the user chose it — a path or a pair of GUIDs.</summary>
        public LinkEntry Entry { get; }

        public Document Document { get; }

        /// <summary>The worksets that were opened. Empty means the model is not workshared at all.</summary>
        public IReadOnlyList<string> Worksets { get; }

        /// <summary>
        /// Changes go back through "Synchronize with Central" rather than a plain save: true for a
        /// local copy made here and for a cloud model (there Revit keeps the local itself).
        /// </summary>
        public bool NeedsSync { get; set; }

        /// <summary>The folder holding the local copy made for this run; empty — nothing to sweep away.</summary>
        public string LocalFolder { get; set; } = string.Empty;
    }

    /// <summary>
    /// Opening other people's models, editing them and putting them back: what the "Accept Changes"
    /// button needs to work on a batch of models rather than the open one.
    ///
    /// **A workshared model is never edited where it stands.** A central model opened directly
    /// through <c>OpenDocumentFile</c> is editable and saveable, and that is exactly why it must not
    /// be done here: a batch would be writing straight into the file the whole team synchronises
    /// with, with no local copy to fall back on and no "Synchronize" for Revit to merge through. So
    /// the path is the ordinary one a person would take — <c>WorksharingUtils.CreateNewLocal</c>,
    /// edit the local, <c>SynchronizeWithCentral</c>, relinquish everything, throw the local away.
    /// Where a local cannot be made, the model is skipped with a reason rather than opened anyway.
    ///
    /// A cloud model is the exception in form only: <c>CreateNewLocal</c> does not apply to a cloud
    /// path, Revit makes and keeps the local itself when the model is opened, and synchronising
    /// works the same way from there.
    ///
    /// **Only the named worksets are opened** (<see cref="DefaultWorksets"/>): the grids live in
    /// "00_Shared levels and grids" and the coordination file is linked into "00_Link_BM", and
    /// nothing else in the model has anything to do with this job. That is not only about opening
    /// faster: what a closed workset holds is not in the document at all, so a batch run cannot
    /// touch a single element it was not asked about. The price is that the names have to match —
    /// hence a prefix rather than an exact name ("00_Link_BM_K3" is the same workset), and hence the
    /// opened names being reported per model: a model where nothing matched is a model where
    /// nothing could have been found.
    /// </summary>
    internal static class BatchCoordination
    {
        /// <summary>
        /// The worksets opened by default. Prefixes, not names: the coordination file's workset
        /// carries a building suffix often enough ("00_Link_BM_K3") that an exact name would quietly
        /// open a model with no base file in it. "01_Link_BM" is here because that is what this
        /// add-in's own "Base File" button creates — see <see cref="BaseFilePreferences.BaseLinkWorkset"/>.
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultWorksets = new List<string>
        {
            BaseFilePreferences.SharedLevelsWorkset,
            "00_Link_BM",
            BaseFilePreferences.BaseLinkWorkset
        };

        /// <summary>
        /// Whether a workset is one of those asked for. A prefix comparison, through the add-in's
        /// single workset-name rule (<see cref="LinkPreferences.SameWorkset"/>'s normalisation): case
        /// is ignored, and leading and trailing spaces are trimmed off both sides — a consultant's
        /// "00_Link_BM " with a trailing space is the same workset, and a comparison that misses it
        /// would leave the base file out of the opened model without a word.
        /// </summary>
        public static bool Matches(string workset, IReadOnlyList<string> prefixes)
        {
            var name = LinkPreferences.NormalizeWorkset(workset);
            if (name.Length == 0 || prefixes == null)
                return false;

            return prefixes
                .Select(LinkPreferences.NormalizeWorkset)
                .Where(prefix => prefix.Length > 0)
                .Any(prefix => name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase));
        }

        // ───────────────────────────── opening ─────────────────────────────

        /// <summary>
        /// Opens one model with only the asked-for worksets, making a local copy where the model is
        /// workshared.
        ///
        /// Returns null on any refusal, with the reason in <paramref name="failures"/>: a batch run
        /// goes on to the next model rather than stopping, exactly like a loop over elements inside
        /// a transaction.
        /// </summary>
        public static BatchModel Open(
            Application app,
            LinkEntry entry,
            IReadOnlyList<string> prefixes,
            List<string> failures)
        {
            if (entry == null)
                return null;

            ModelPath path;

            try
            {
                path = LinkCatalog.ToModelPath(entry);
            }
            catch (Exception exception)
            {
                failures.Add(entry.Name + " — " + LinkCatalog.Short(exception.Message));
                return null;
            }

            // A model already open in this Revit session cannot be opened a second time, and a
            // silent Revit exception would say so far less clearly than this does.
            var busy = Busy(app, path);
            if (busy != null)
            {
                failures.Add(entry.Name + " — " + busy);
                return null;
            }

            List<WorksetPreview> previews;

            try
            {
                previews = WorksharingUtils.GetUserWorksetInfo(path).ToList();
            }
            catch (Exception exception)
            {
                failures.Add(entry.Name + " — the worksets could not be read: " + LinkCatalog.Short(exception.Message));
                return null;
            }

            var wanted = previews.Where(preview => Matches(preview.Name, prefixes)).ToList();

            // Opening a workshared model with nothing but closed worksets would fetch an empty
            // document over the network and find nothing in it — and "nothing found" would read as
            // "the model is fine". The names are the thing at fault here, and the message says so.
            if (previews.Count > 0 && wanted.Count == 0)
            {
                failures.Add(entry.Name + " — none of its worksets start with " + Quote(prefixes) +
                             ": there would be neither grids nor the base file in the opened model. " +
                             "Its worksets: " + Quote(previews.Select(preview => preview.Name).ToList()) + ".");
                return null;
            }

            var options = new OpenOptions();

            if (wanted.Count > 0)
            {
                // "Close all, then open the listed ones" — the same phrasing as for link worksets,
                // and for the same reason: it is the one Revit carries out reliably.
                var configuration = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);
                configuration.Open(wanted.Select(preview => preview.Id).ToList());
                options.SetOpenWorksetsConfiguration(configuration);
            }

            var names = wanted.Select(preview => LinkPreferences.NormalizeWorkset(preview.Name)).ToList();
            var local = string.Empty;
            var target = path;

            if (previews.Count > 0 && entry.Origin != LinkOrigin.Cloud)
            {
                string reason;
                target = Localise(path, entry, out local, out reason);

                if (target == null)
                {
                    failures.Add(entry.Name + " — a local copy could not be made: " + reason +
                                 " The model was left untouched: editing somebody's central file " +
                                 "directly is not something this button does.");
                    return null;
                }
            }

            try
            {
                var doc = app.OpenDocumentFile(target, options);

                return new BatchModel(entry, doc, names)
                {
                    NeedsSync = doc.IsWorkshared && (local.Length > 0 || entry.Origin == LinkOrigin.Cloud),
                    LocalFolder = local
                };
            }
            catch (Exception exception)
            {
                Sweep(local);
                failures.Add(entry.Name + " — the model would not open: " + LinkCatalog.Short(exception.Message));
                return null;
            }
        }

        /// <summary>
        /// Makes a local copy of a workshared model in a temporary folder of its own.
        ///
        /// Each copy gets its own folder rather than a unique file name: Revit keeps a "&lt;name&gt;_backup"
        /// folder next to a local model, and sweeping one folder away afterwards is the only way to
        /// leave nothing behind.
        /// </summary>
        private static ModelPath Localise(ModelPath central, LinkEntry entry, out string folder, out string reason)
        {
            folder = string.Empty;
            reason = string.Empty;

            var root = Path.Combine(Path.GetTempPath(), "VladTools", "coordination",
                Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(root);

                var name = Path.GetFileNameWithoutExtension(entry.Name);
                if (string.IsNullOrEmpty(name))
                    name = "model";

                foreach (var bad in Path.GetInvalidFileNameChars())
                    name = name.Replace(bad, '_');

                var file = Path.Combine(root, name + ".rvt");

                WorksharingUtils.CreateNewLocal(central, ModelPathUtils.ConvertUserVisiblePathToModelPath(file));

                folder = root;
                return ModelPathUtils.ConvertUserVisiblePathToModelPath(file);
            }
            catch (Exception exception)
            {
                // The folder is swept away here and not in Release: there is no BatchModel to sweep
                // it from, and an empty temporary folder per failed model is still litter.
                Sweep(root);
                folder = string.Empty;
                reason = LinkCatalog.Short(exception.Message);
                return null;
            }
        }

        /// <summary>
        /// Whether this model is already open in the session — as the project itself or as another
        /// document. Comparison is by the user-visible path, the only form every store has in common.
        /// </summary>
        private static string Busy(Application app, ModelPath path)
        {
            var wanted = Visible(path);
            if (wanted.Length == 0)
                return null;

            foreach (var doc in app.Documents.Cast<Document>())
            {
                if (doc.IsFamilyDocument)
                    continue;

                if (Same(doc.PathName, wanted) || Same(Central(doc), wanted))
                    return "this model is already open in Revit. Close it, or accept the changes in it " +
                           "through the window itself.";
            }

            return null;
        }

        private static string Central(Document doc)
        {
            try
            {
                return doc.IsWorkshared ? Visible(doc.GetWorksharingCentralModelPath()) : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string Visible(ModelPath path)
        {
            try
            {
                return path == null ? string.Empty : ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool Same(string first, string second)
        {
            return first != null && first.Length > 0 &&
                   string.Equals(first, second, StringComparison.CurrentCultureIgnoreCase);
        }

        // ───────────────────────────── giving the model back ─────────────────────────────

        /// <summary>
        /// Writes the changes back: "Synchronize with Central" for a local copy and for the cloud,
        /// an ordinary save for a model that is not workshared.
        ///
        /// Everything is relinquished on the way out. A batch run leaves no one to hand the borrowed
        /// elements back later: an element still checked out to this machine is one nobody else can
        /// edit, and the local copy it was borrowed into is deleted a moment afterwards.
        /// </summary>
        public static bool Commit(BatchModel model, string comment, List<string> failures)
        {
            try
            {
                if (model.NeedsSync)
                {
                    var synchronise = new SynchronizeWithCentralOptions
                    {
                        Comment = comment,
                        SaveLocalBefore = false,
                        SaveLocalAfter = false
                    };

                    synchronise.SetRelinquishOptions(new RelinquishOptions(true));

                    model.Document.SynchronizeWithCentral(new TransactWithCentralOptions(), synchronise);
                }
                else
                {
                    model.Document.Save();
                }

                return true;
            }
            catch (Exception exception)
            {
                failures.Add(model.Entry.Name + " — the changes were made but could not be saved: " +
                             LinkCatalog.Short(exception.Message) +
                             " The model is left as it was.");
                return false;
            }
        }

        /// <summary>
        /// Hands back what was borrowed without writing anything — for a model nothing was changed in.
        ///
        /// Deleting the local copy does not do this by itself: the central goes on believing the
        /// elements are checked out to this machine, and nobody else can edit them until somebody
        /// relinquishes them by hand. So it is done here, while there is still a document to do it on.
        /// </summary>
        public static void Relinquish(BatchModel model, List<string> failures)
        {
            if (!model.NeedsSync || !model.Document.IsWorkshared)
                return;

            try
            {
                WorksharingUtils.RelinquishOwnership(model.Document, new RelinquishOptions(true),
                    new TransactWithCentralOptions());
            }
            catch (Exception exception)
            {
                failures.Add(model.Entry.Name + " — what was checked out could not be handed back: " +
                             LinkCatalog.Short(exception.Message) +
                             " Relinquish it in Revit (\"Collaborate → Relinquish All Mine\").");
            }
        }

        /// <summary>
        /// Closes the document and sweeps away the local copy. Never skipped: a document left open
        /// hangs in memory for the rest of the Revit session, and a local copy nobody deletes stays
        /// in the Windows temp folder at the size of a whole model.
        /// </summary>
        public static void Release(BatchModel model)
        {
            if (model == null)
                return;

            try
            {
                model.Document.Close(false);
            }
            catch (Exception)
            {
                // Revit does not always let go of a document, and there is nothing to be done about
                // it from here — the same as when closing the schedule cache.
            }

            Sweep(model.LocalFolder);
        }

        /// <summary>Deletes the temporary folder of a local copy, backups and all.</summary>
        private static void Sweep(string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return;

            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            catch (Exception)
            {
                // A file still held by Revit is left in the Windows temp folder — not worth a word
                // in the report.
            }
        }

        private static string Quote(IReadOnlyList<string> values)
        {
            const int limit = 8;

            if (values == null || values.Count == 0)
                return "none";

            var text = string.Join(", ", values.Take(limit).Select(value => "\"" + value + "\""));

            return values.Count > limit ? text + " and " + (values.Count - limit) + " more" : text;
        }
    }
}
