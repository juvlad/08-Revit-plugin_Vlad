using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VladTools.Infrastructure;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// Carries schedules from one model into another: they are taken out of a base model once, kept
    /// in the Windows profile, and inserted into every model after that with one button.
    ///
    /// In Revit this is "Insert → Insert Views from File", and every time it means walking the
    /// dialog again, finding the base model on the network again and picking the same schedules out
    /// of its whole list again. Here the list is assembled once and named.
    ///
    /// **The cache is a small .rvt of its own**, not a text description of the schedule — the reasons
    /// are in <see cref="ScheduleLibrary"/>. Inserting is therefore
    /// <c>ElementTransformUtils.CopyElements</c> out of that file, the same call Revit's own command
    /// makes, so a schedule arrives whole: fields, filters, sorting, formatting.
    ///
    /// The one thing Revit will not do is hold two views of the same name, so a name already taken in
    /// the project has to be settled before copying, not after — see <see cref="Prepare"/>.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ScheduleLibraryCommand : IExternalCommand
    {
        private const string DialogTitle = "Schedule Library";

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
                    "Schedules live in a project, not in a family.");
                return Result.Cancelled;
            }

            try
            {
                var app = commandData.Application.Application;

                var window = new ScheduleLibraryWindow(
                    Existing(doc),
                    doc.Title,
                    setName => ScheduleLibrary.Read(app, setName),
                    (setName, entry, chooser) =>
                        ScheduleLibrary.Capture(app, setName, entry == null ? doc : null, entry, chooser),
                    (setName, names) => ScheduleLibrary.Remove(app, setName, names));

                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                Insert(app, doc, window.SetName, window.Selected);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── the snapshot ─────────────────────────────

        /// <summary>
        /// The schedules already in the project and how many sheets each of them sits on.
        ///
        /// The sheets matter because of one action only: replacing deletes the project's own
        /// schedule, and its placements go with it — so they are counted before the user decides,
        /// not reported afterwards. Sheets are counted distinctly: a schedule split into segments
        /// puts several instances on one sheet, and "placed on 3 sheets" must not mean one.
        /// </summary>
        private static Dictionary<string, int> Existing(Document doc)
        {
            var sheets = new Dictionary<string, HashSet<ElementId>>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var schedule in ScheduleLibrary.Copyable(doc))
                sheets[schedule.Name] = new HashSet<ElementId>();

            foreach (var instance in new FilteredElementCollector(doc)
                         .OfClass(typeof(ScheduleSheetInstance))
                         .Cast<ScheduleSheetInstance>())
            {
                var schedule = doc.GetElement(instance.ScheduleId) as ViewSchedule;
                if (schedule == null)
                    continue;

                HashSet<ElementId> placed;
                if (sheets.TryGetValue(schedule.Name, out placed))
                    placed.Add(instance.OwnerViewId);
            }

            return sheets.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.CurrentCultureIgnoreCase);
        }

        // ───────────────────────────── inserting ─────────────────────────────

        private static void Insert(Application app, Document doc, string setName, IReadOnlyList<ScheduleRow> rows)
        {
            var failures = new List<string>();
            var done = new List<string>();
            var warnings = new WarningSuppressor();

            var file = ScheduleLibrary.FilePathFor(setName);
            if (file == null || !System.IO.File.Exists(file))
            {
                TaskDialog.Show(DialogTitle, "The set \"" + setName + "\" is no longer on disk — there is nothing to insert from.");
                return;
            }

            Document cache = null;

            try
            {
                cache = app.OpenDocumentFile(file);

                var jobs = Prepare(cache, rows, failures);

                using (var transaction = new Transaction(doc, "Insert schedules"))
                {
                    transaction.Start();

                    // A schedule arrives with its fields, and Revit will warn about a good deal along
                    // the way — a duplicate parameter, a type it substituted. A modal dialog for each
                    // would turn one button into clicking through dialogs.
                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(warnings);
                    transaction.SetFailureHandlingOptions(options);

                    foreach (var job in jobs)
                        Insert(doc, cache, job, done, failures);

                    if (done.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }
            }
            catch (Exception exception)
            {
                failures.Add(LinkCatalog.Short(exception.Message));
            }
            finally
            {
                // Closed without saving: the names given to the copies below live for this run only.
                ScheduleLibrary.Release(cache);
            }

            Report(setName, done, failures, warnings.Messages);
        }

        /// <summary>
        /// Settles the name clashes **inside the cache document**, before a single thing is copied.
        ///
        /// Revit will not hold two views of one name, and what it does when asked to is its own
        /// business — it may rename the arrival, it may refuse. Neither is something to build on, so
        /// no clash is left to reach the copy: what is to be replaced is deleted from the project
        /// first (see <see cref="Clear"/>), and what is to arrive alongside is renamed here, in our
        /// own file. The cache is closed without saving, so the rename lasts exactly one run.
        /// </summary>
        private static List<Job> Prepare(Document cache, IReadOnlyList<ScheduleRow> rows, List<string> failures)
        {
            var byName = new Dictionary<string, ViewSchedule>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var schedule in ScheduleLibrary.Copyable(cache))
            {
                if (!byName.ContainsKey(schedule.Name))
                    byName[schedule.Name] = schedule;
            }

            var jobs = new List<Job>();
            var renames = new List<Job>();

            foreach (var row in rows)
            {
                ViewSchedule schedule;
                if (!byName.TryGetValue(row.Name, out schedule))
                {
                    failures.Add(row.Name + " — not in the set any more: the set file changed while the window was open");
                    continue;
                }

                var job = new Job(row, schedule.Id);
                jobs.Add(job);

                if (!string.Equals(row.ResultName, row.Name, StringComparison.CurrentCultureIgnoreCase))
                    renames.Add(job);
            }

            if (renames.Count == 0)
                return jobs;

            using (var transaction = new Transaction(cache, "Name the copies"))
            {
                transaction.Start();

                foreach (var job in renames)
                {
                    try
                    {
                        var schedule = (ViewSchedule)cache.GetElement(job.SourceId);
                        schedule.Name = job.Row.ResultName;
                    }
                    catch (Exception exception)
                    {
                        // The copy keeps its old name: it will clash in the project, Revit will do
                        // whatever it does, and the report will say what the schedule ended up called.
                        failures.Add(job.Row.Name + " — could not be renamed to \"" + job.Row.ResultName + "\": " +
                                     LinkCatalog.Short(exception.Message));
                    }
                }

                transaction.Commit();
            }

            return jobs;
        }

        /// <summary>One schedule out of the set into the project. A transaction is already running.</summary>
        private static void Insert(Document doc, Document cache, Job job, List<string> done, List<string> failures)
        {
            var placements = new List<Placement>();

            if (job.Row.HasConflict && job.Row.Action == ScheduleAction.Replace && !Clear(doc, job.Row.Name, placements, failures))
                return;

            var inserted = ScheduleLibrary.CopyOne(cache, doc, job.SourceId, job.Row.Name, failures);
            if (inserted == null)
                return;

            var restored = Restore(doc, inserted.Id, placements, job.Row.Name, failures);

            done.Add(Describe(job.Row, inserted.Name, restored));
        }

        /// <summary>
        /// Clears the way for a replacement: remembers where the project's own schedule is placed and
        /// deletes it. Deleting takes its sheet placements with it — that is exactly why they are
        /// written down first, to be put back once the new one is in.
        /// </summary>
        private static bool Clear(Document doc, string name, List<Placement> placements, List<string> failures)
        {
            var existing = ScheduleLibrary.Copyable(doc)
                .FirstOrDefault(schedule => string.Equals(schedule.Name, name, StringComparison.CurrentCultureIgnoreCase));

            // Somebody removed it while the window was open — there is nothing to replace, and the
            // new one simply arrives under a free name.
            if (existing == null)
                return true;

            try
            {
                placements.AddRange(Placements(doc, existing.Id));
                doc.Delete(existing.Id);
                return true;
            }
            catch (Exception exception)
            {
                failures.Add(name + " — the project's own schedule could not be deleted: " +
                             LinkCatalog.Short(exception.Message));
                return false;
            }
        }

        private static IEnumerable<Placement> Placements(Document doc, ElementId scheduleId)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .Where(instance => instance.ScheduleId == scheduleId)
                .Select(instance => new Placement(
                    instance.OwnerViewId,
                    SheetName(doc, instance.OwnerViewId),
                    instance.Point,
                    instance.SegmentIndex,
                    instance.Rotation))
                .ToList();
        }

        private static string SheetName(Document doc, ElementId sheetId)
        {
            var sheet = doc.GetElement(sheetId) as ViewSheet;
            return sheet == null ? "?" : sheet.SheetNumber + " — " + sheet.Name;
        }

        /// <summary>
        /// Puts the new schedule back on the sheets the old one stood on, in the same spots. A
        /// placement that will not come back does not derail the rest: the schedule itself is already
        /// in the project, it is simply missing from a sheet — and that has to be said, not passed over.
        /// </summary>
        private static int Restore(
            Document doc,
            ElementId scheduleId,
            IReadOnlyList<Placement> placements,
            string title,
            List<string> failures)
        {
            var restored = 0;

            foreach (var placement in placements)
            {
                try
                {
                    var instance = placement.Segment > 0
                        ? ScheduleSheetInstance.Create(doc, placement.SheetId, scheduleId, placement.Point, placement.Segment)
                        : ScheduleSheetInstance.Create(doc, placement.SheetId, scheduleId, placement.Point);

                    if (instance != null && placement.Rotation != ViewportRotation.None)
                        instance.Rotation = placement.Rotation;

                    restored++;
                }
                catch (Exception exception)
                {
                    failures.Add(title + " — could not be put back on the sheet " + placement.SheetName + ": " +
                                 LinkCatalog.Short(exception.Message));
                }
            }

            return restored;
        }

        /// <summary>
        /// What happened to one schedule, in words. The name is the one Revit really gave it, not the
        /// one we asked for: if a clash was settled some other way than planned, the report must show
        /// that rather than repeat the plan.
        /// </summary>
        private static string Describe(ScheduleRow row, string actualName, int restored)
        {
            var replaced = row.HasConflict && row.Action == ScheduleAction.Replace;

            var text = string.Equals(actualName, row.Name, StringComparison.CurrentCultureIgnoreCase)
                ? row.Name
                : row.Name + " → inserted as \"" + actualName + "\"";

            if (replaced)
            {
                text += " — replaced the project's own";
                if (restored > 0)
                    text += ", put back onto " + restored + " sheet placement(s)";
            }

            return text;
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(
            string setName,
            IReadOnlyList<string> done,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
        {
            const int limit = 15;

            var text = "Set: " + setName + ".\n\n";

            text += done.Count == 0
                ? "Nothing was inserted."
                : "Inserted (" + done.Count + "):\n• " + string.Join("\n• ", done.Take(limit)) +
                  (done.Count > limit ? "\n… and " + (done.Count - limit) + " more" : string.Empty);

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

        // ───────────────────────────── odds and ends ─────────────────────────────

        /// <summary>One schedule to insert: the window's row plus where it lies in the cache document.</summary>
        private sealed class Job
        {
            public Job(ScheduleRow row, ElementId sourceId)
            {
                Row = row;
                SourceId = sourceId;
            }

            public ScheduleRow Row { get; }

            /// <summary>The schedule's id **in the cache document**, not in the project.</summary>
            public ElementId SourceId { get; }
        }

        /// <summary>Where a schedule stood on a sheet — enough to put the replacement back in its place.</summary>
        private sealed class Placement
        {
            public Placement(ElementId sheetId, string sheetName, XYZ point, int segment, ViewportRotation rotation)
            {
                SheetId = sheetId;
                SheetName = sheetName;
                Point = point;
                Segment = segment;
                Rotation = rotation;
            }

            public ElementId SheetId { get; }
            public string SheetName { get; }
            public XYZ Point { get; }

            /// <summary>The segment of a split schedule; 0 or less means it is not split.</summary>
            public int Segment { get; }

            public ViewportRotation Rotation { get; }
        }
    }
}
