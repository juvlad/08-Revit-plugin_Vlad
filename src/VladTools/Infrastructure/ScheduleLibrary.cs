using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The schedule cache: sets of schedules taken out of a base model once and inserted into every
    /// model after it.
    ///
    /// **A set is a small .rvt of its own**, not a text description of the schedule. Revit does allow
    /// a schedule to be built from scratch (<c>ViewSchedule.CreateSchedule</c> plus fields), but
    /// reproducing one exactly — calculated values, cell formatting, merged headings, an embedded
    /// schedule — is a large and fragile job, and the result would still differ from what the user
    /// drew. A .rvt carrier costs nothing to keep, and copying out of it is the same
    /// <c>ElementTransformUtils.CopyElements</c> that Revit's own "Insert Views from File" uses, so
    /// the schedule arrives whole, with its fields, filters, sorting and formatting.
    ///
    /// The price is that a set has to be refreshed by hand when the base model's schedule changes —
    /// the "take it again" button. The gain is that inserting costs the opening of a file a couple of
    /// megabytes big: the base model is not needed at all, and a set can be handed to a colleague.
    ///
    /// Files: `%AppData%\VladTools\schedules\&lt;Revit year&gt;\&lt;set name&gt;.rvt`.
    /// The year in the path is not tidiness: a .rvt only ever opens in its own Revit version or a
    /// newer one, so a set captured in 2025 would simply not open in 2022, and that failure would
    /// look like a broken button rather than what it is.
    /// </summary>
    internal static class ScheduleLibrary
    {
        /// <summary>
        /// The Revit year the sets are kept under. Set by <c>App.OnStartup</c> from
        /// <c>ControlledApplication.VersionNumber</c> — the same build runs in 2022, 2024 and 2025,
        /// and each needs a cache of its own. The constant here is only a fallback, for the case
        /// where startup did not run.
        /// </summary>
        public static string Year { get; set; } = "2022";

        /// <summary>
        /// The name a set gets when there is nothing to remember yet. A first run must not open with
        /// a refusal: without this, the window comes up with an empty name box, and the very first
        /// press of "Open project" answers "type in a name for a set first" instead of doing the one
        /// thing the user came for. The name can be changed straight away — it is only a starting point.
        /// </summary>
        public const string DefaultSetName = "Schedules";

        /// <summary>%AppData%\VladTools\schedules — the settings live here, the sets one level down.</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "schedules");
            }
        }

        /// <summary>%AppData%\VladTools\schedules\&lt;year&gt; — the sets themselves.</summary>
        public static string SetsFolderPath => Path.Combine(FolderPath, Year);

        /// <summary>The names of the saved sets in alphabetical order. An empty list if nothing was ever captured.</summary>
        public static IReadOnlyList<string> Names()
        {
            try
            {
                if (!Directory.Exists(SetsFolderPath))
                    return new List<string>();

                return Directory.GetFiles(SetsFolderPath, "*.rvt")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrEmpty(name) && name[0] != '_')
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>The path to the set file; null if the name is empty or made only of forbidden characters.</summary>
        public static string FilePathFor(string setName)
        {
            var name = (setName ?? string.Empty).Trim();
            if (name.Length == 0)
                return null;

            foreach (var forbidden in Path.GetInvalidFileNameChars())
                name = name.Replace(forbidden, '_');

            name = name.Trim('_', ' ');

            return name.Length == 0 ? null : Path.Combine(SetsFolderPath, name + ".rvt");
        }

        /// <summary>
        /// Deletes a set. Revit leaves backups next to a saved file (".0001.rvt"), and those go too:
        /// otherwise "the set is deleted" would leave its contents lying on disk.
        /// </summary>
        public static void Delete(string setName)
        {
            var file = FilePathFor(setName);
            if (file == null)
                return;

            if (File.Exists(file))
                File.Delete(file);

            var backups = Path.GetFileNameWithoutExtension(file) + ".*.rvt";

            foreach (var backup in Directory.GetFiles(SetsFolderPath, backups))
                File.Delete(backup);
        }

        // ───────────────────────────── reading a document ─────────────────────────────

        /// <summary>
        /// The schedules of a document that are fit to be copied.
        ///
        /// Left out are view templates and the two internal schedules — the revision schedule inside
        /// a titleblock and the keynote schedule (<c>IsTitleblockRevisionSchedule</c> /
        /// <c>IsInternalKeynoteSchedule</c>): those live not in the browser but inside a titleblock,
        /// and there is nowhere to insert them. The same rule as in "Cleanup".
        /// </summary>
        public static IReadOnlyList<ViewSchedule> Copyable(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(IsCopyable)
                .OrderBy(schedule => schedule.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static bool IsCopyable(ViewSchedule schedule)
        {
            try
            {
                if (schedule.IsTemplate || schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule)
                    return false;

                var definition = schedule.Definition;

                // An embedded schedule is part of its host, not a view of its own: it arrives with the host.
                return definition != null && !definition.IsEmbedded;
            }
            catch (Exception)
            {
                // A schedule that will not say what it is cannot be copied either.
                return false;
            }
        }

        /// <summary>A snapshot of the document's schedules for the window — plain data, no Revit types.</summary>
        public static IReadOnlyList<ScheduleInfo> Snapshot(Document doc)
        {
            return Copyable(doc).Select(schedule => Describe(doc, schedule)).ToList();
        }

        /// <summary>One schedule as the window sees it.</summary>
        public static ScheduleInfo Describe(Document doc, ViewSchedule schedule)
        {
            var name = schedule.Name;

            try
            {
                var definition = schedule.Definition;

                return new ScheduleInfo(
                    name,
                    CategoryName(doc, definition.CategoryId),
                    definition.GetFieldCount(),
                    definition.IsKeySchedule,
                    definition.IsMaterialTakeoff);
            }
            catch (Exception)
            {
                // The definition would not open — the schedule is still shown: the name is enough to copy it.
                return new ScheduleInfo(name, string.Empty, 0, false, false);
            }
        }

        /// <summary>The category in words. A multi-category schedule has none, and that is not a failure.</summary>
        private static string CategoryName(Document doc, ElementId categoryId)
        {
            if (categoryId == null || categoryId == ElementId.InvalidElementId)
                return "Multi-category";

            try
            {
                var category = Category.GetCategory(doc, categoryId);
                return category == null ? string.Empty : category.Name;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        // ───────────────────────────── the set ─────────────────────────────

        /// <summary>
        /// What a set holds. The cache file is opened and closed again right away: the window only
        /// shows a list, and keeping a document open for the whole life of a window is how documents
        /// end up hanging in memory for the rest of the session.
        /// </summary>
        public static ScheduleSetScan Read(Application app, string setName)
        {
            var failures = new List<string>();
            var file = FilePathFor(setName);

            if (file == null || !File.Exists(file))
                return new ScheduleSetScan(setName, file, null, failures, null);

            Document cache = null;

            try
            {
                cache = app.OpenDocumentFile(file);
                return new ScheduleSetScan(setName, file, Snapshot(cache), failures, null);
            }
            catch (Exception exception)
            {
                failures.Add("Could not open the set file: " + LinkCatalog.Short(exception.Message));
                return new ScheduleSetScan(setName, file, null, failures, null);
            }
            finally
            {
                Release(cache);
            }
        }

        /// <summary>
        /// Puts schedules out of a model into the set.
        ///
        /// <paramref name="openProject"/> is the shortest path and the one worth taking: standing in
        /// the base model, the schedules are right here and nothing has to be opened at all. Otherwise
        /// the model named by <paramref name="source"/> is opened in the background — detached, so
        /// that neither a local copy nor a lock on somebody's central model comes of it.
        ///
        /// Returns null when the user backs out of the choice: then there is nothing to report either.
        /// </summary>
        public static ScheduleSetScan Capture(
            Application app,
            string setName,
            Document openProject,
            LinkEntry source,
            ScheduleChooser chooser)
        {
            var failures = new List<string>();
            var added = new List<string>();
            var file = FilePathFor(setName);

            if (file == null)
            {
                failures.Add("The set name is empty or consists only of characters a file name cannot hold.");
                return new ScheduleSetScan(setName, null, null, failures, null);
            }

            Document model = null;
            Document cache = null;

            try
            {
                model = openProject ?? Open(app, source, failures);
                if (model == null)
                    return new ScheduleSetScan(setName, file, null, failures, null);

                var found = Snapshot(model);
                if (found.Count == 0)
                {
                    failures.Add("The model has no schedule that can be copied.");
                    return new ScheduleSetScan(setName, file, null, failures, null);
                }

                var chosen = chooser(found);
                if (chosen == null || chosen.Count == 0)
                    return null;

                var wanted = new HashSet<string>(
                    chosen.Select(info => info.Name),
                    StringComparer.CurrentCultureIgnoreCase);

                var schedules = Copyable(model).Where(schedule => wanted.Contains(schedule.Name)).ToList();

                bool created;
                cache = OpenOrCreate(app, file, out created);

                using (var transaction = new Transaction(cache, "Add schedules to the set"))
                {
                    transaction.Start();

                    // A set is a cache, not an archive: taking a schedule again means refreshing it,
                    // so a namesake already in the set makes way rather than turning into "Doors (2)".
                    Drop(cache, wanted);

                    foreach (var schedule in schedules)
                    {
                        if (CopyOne(model, cache, schedule.Id, schedule.Name, failures) != null)
                            added.Add(schedule.Name);
                    }

                    if (added.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                if (added.Count > 0)
                    Store(cache, file, created, failures);

                return new ScheduleSetScan(setName, file, Snapshot(cache), failures, added);
            }
            catch (Exception exception)
            {
                failures.Add(LinkCatalog.Short(exception.Message));
                return new ScheduleSetScan(setName, file, null, failures, added);
            }
            finally
            {
                // The open project is the user's, not ours — it is borrowed, never closed.
                if (!ReferenceEquals(model, openProject))
                    Release(model);

                Release(cache);
            }
        }

        /// <summary>Throws the named schedules out of the set and returns what is left in it.</summary>
        public static ScheduleSetScan Remove(Application app, string setName, IReadOnlyList<string> names)
        {
            var failures = new List<string>();
            var file = FilePathFor(setName);

            if (file == null || !File.Exists(file))
                return new ScheduleSetScan(setName, file, null, failures, null);

            Document cache = null;

            try
            {
                cache = app.OpenDocumentFile(file);

                var wanted = new HashSet<string>(names ?? new List<string>(), StringComparer.CurrentCultureIgnoreCase);
                int removed;

                using (var transaction = new Transaction(cache, "Remove schedules from the set"))
                {
                    transaction.Start();
                    removed = Drop(cache, wanted);

                    if (removed == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                if (removed > 0)
                    Store(cache, file, false, failures);

                return new ScheduleSetScan(setName, file, Snapshot(cache), failures, null);
            }
            catch (Exception exception)
            {
                failures.Add("Could not change the set: " + LinkCatalog.Short(exception.Message));
                return new ScheduleSetScan(setName, file, null, failures, null);
            }
            finally
            {
                Release(cache);
            }
        }

        /// <summary>Deletes the named schedules from an open cache document. A transaction must already be running.</summary>
        private static int Drop(Document cache, HashSet<string> names)
        {
            var ids = Copyable(cache)
                .Where(schedule => names.Contains(schedule.Name))
                .Select(schedule => schedule.Id)
                .ToList();

            if (ids.Count == 0)
                return 0;

            cache.Delete(ids);
            return ids.Count;
        }

        // ───────────────────────────── documents ─────────────────────────────

        /// <summary>
        /// Opens a model in the background.
        ///
        /// Detached on purpose: a workshared model opened as it is would either make a local copy or
        /// take a lock on somebody's central file, and the button only ever reads it. Detaching is not
        /// allowed on a model that is not workshared, and there Revit's refusal is the signal to open
        /// it plainly — that is what the second attempt is for, rather than deciding in advance.
        ///
        /// The worksets are left open, unlike everywhere else in this add-in: a key schedule's rows
        /// are elements, and closing the worksets would fetch the schedule without its keys. Opening
        /// is the slow half of the work, and it is paid for once — inserting goes through the cache.
        /// </summary>
        private static Document Open(Application app, LinkEntry entry, List<string> failures)
        {
            if (entry == null)
            {
                failures.Add("No model to take the schedules from was chosen.");
                return null;
            }

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

            var options = new OpenOptions
            {
                DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets
            };

            try
            {
                return app.OpenDocumentFile(path, options);
            }
            catch (Exception)
            {
                try
                {
                    return app.OpenDocumentFile(path, new OpenOptions());
                }
                catch (Exception exception)
                {
                    failures.Add(entry.Name + " — the model would not open: " + LinkCatalog.Short(exception.Message));
                    return null;
                }
            }
        }

        /// <summary>
        /// The set's own document: the existing file, or an empty project to put the first schedules
        /// into. The empty one is made without a template (<c>UnitSystem.Metric</c>) on purpose — the
        /// carrier holds nothing but schedules, and leaning on whatever template this machine has set
        /// up would mean a set built differently on somebody else's computer.
        /// </summary>
        private static Document OpenOrCreate(Application app, string file, out bool created)
        {
            if (File.Exists(file))
            {
                created = false;
                return app.OpenDocumentFile(file);
            }

            created = true;
            return app.NewProjectDocument(UnitSystem.Metric);
        }

        /// <summary>Writes the set out. A newly made document has no path of its own yet — hence the two ways.</summary>
        private static void Store(Document cache, string file, bool created, List<string> failures)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));

                if (created)
                    cache.SaveAs(file, new SaveAsOptions { OverwriteExistingFile = true });
                else
                    cache.Save();
            }
            catch (Exception exception)
            {
                failures.Add("The set could not be saved: " + LinkCatalog.Short(exception.Message));
            }
        }

        /// <summary>
        /// Closes a document opened in the background, without saving. Never skipped: a document left
        /// open hangs in memory for the rest of the Revit session — the same trap as with
        /// <c>EditFamily</c> in "Delete Shared Parameters".
        /// </summary>
        public static void Release(Document doc)
        {
            if (doc == null)
                return;

            try
            {
                doc.Close(false);
            }
            catch (Exception)
            {
                // Revit will not always let go of a document, and there is nothing to tell the user:
                // they did not ask for this document to be opened in the first place.
            }
        }

        // ───────────────────────────── copying ─────────────────────────────

        /// <summary>
        /// The options for copying between documents. A duplicate type name is answered with "the
        /// destination's own type": the only alternative Revit offers is <c>Abort</c>, which cancels
        /// the whole copy — and a schedule arriving with the project's own text types is a far better
        /// outcome than one not arriving at all.
        /// </summary>
        public static CopyPasteOptions Options()
        {
            var options = new CopyPasteOptions();
            options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());

            return options;
        }

        /// <summary>
        /// Copies one schedule between documents and hands back what it became on the other side.
        ///
        /// One at a time rather than the whole batch in a single call: <c>CopyElements</c> is
        /// all-or-nothing, and one schedule Revit will not take would cancel every other. A failure on
        /// one goes into the list and the rest carry on — the same rule as everywhere in this add-in.
        /// </summary>
        public static ViewSchedule CopyOne(
            Document source,
            Document target,
            ElementId id,
            string title,
            List<string> failures)
        {
            try
            {
                var copied = ElementTransformUtils.CopyElements(
                    source,
                    new List<ElementId> { id },
                    target,
                    Transform.Identity,
                    Options());

                var schedule = copied
                    .Select(target.GetElement)
                    .OfType<ViewSchedule>()
                    .FirstOrDefault();

                if (schedule == null)
                    failures.Add(title + " — Revit copied the schedule but did not hand back the view");

                return schedule;
            }
            catch (Exception exception)
            {
                failures.Add(title + " — " + LinkCatalog.Short(exception.Message));
                return null;
            }
        }

        private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            {
                return DuplicateTypeAction.UseDestinationTypes;
            }
        }
    }
}
