using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// The "Excel" button: a schedule out to an Excel workbook, and values edited in Excel back into
    /// the schedule — the pair of commands ModPlus calls "Export to Excel" and "Excel to Schedule",
    /// behind one button that asks which way.
    ///
    /// **Export** writes every value exactly as the schedule shows it, as text, and — invisibly —
    /// which elements each line stands for (see <see cref="ScheduleWorkbook"/>).
    ///
    /// **Import** pairs the sheet's lines with the schedule's rows (by those elements, by a key column
    /// or by order), shows every value that would change before anything is written, and writes the
    /// checked ones into the elements' parameters in one transaction. Like typing into the schedule in
    /// Revit: an instance parameter goes to every element of the row, a type parameter to their type.
    ///
    /// Revit has no "elements of a schedule row" — how the rows are asked is in <see cref="ScheduleTable"/>.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExcelScheduleCommand : IExternalCommand
    {
        private const string DialogTitle = "Excel";
        private const int ListLimit = 15;

        private sealed class Job
        {
            public Job(PlannedWrite write, ElementId target)
            {
                Write = write;
                Target = target;
            }

            public PlannedWrite Write { get; }
            public ElementId Target { get; }
        }

        private sealed class Outcome
        {
            public List<string> Failures { get; } = new List<string>();
            public List<string> Notes { get; } = new List<string>();
            public Dictionary<string, List<ElementId>> Held { get; } = new Dictionary<string, List<ElementId>>();
            public List<ElementId> Stale { get; } = new List<ElementId>();
        }

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
                    "The command works only in a project or a project template (.rvt, .rte).\n" +
                    "Schedules live in a project, not in a family.");
                return Result.Cancelled;
            }

            try
            {
                var schedules = ScheduleLibrary.Copyable(doc)
                    .GroupBy(schedule => schedule.Name, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();

                if (schedules.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "The project has no schedules — there is nothing to export or to import into.");
                    return Result.Cancelled;
                }

                switch (AskDirection())
                {
                    case TaskDialogResult.CommandLink1:
                        return Export(commandData, uidoc, schedules);
                    case TaskDialogResult.CommandLink2:
                        return Import(commandData, uidoc, schedules);
                    default:
                        return Result.Cancelled;
                }
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        private static TaskDialogResult AskDirection()
        {
            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = "Schedules and Excel",
                MainContent = "Which way?",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                AllowCancellation = true
            };

            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Export to Excel",
                "Pick one or more schedules. Each becomes a sheet of one workbook, every value exactly as the schedule shows it.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Import from Excel",
                "Pick a workbook and the schedule it goes into. Every value that would change is shown highlighted before anything is written.");

            return dialog.Show();
        }

        // ───────────────────────────── export ─────────────────────────────

        private static Result Export(ExternalCommandData commandData, UIDocument uidoc, IReadOnlyList<ViewSchedule> schedules)
        {
            var doc = uidoc.Document;
            var preferences = ExcelPreferences.Load();

            var window = new ExcelExportWindow(
                schedules.Select(schedule => ScheduleLibrary.Describe(doc, schedule)).ToList(),
                (uidoc.ActiveView as ViewSchedule)?.Name,
                doc.Title,
                preferences);
            new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

            if (window.ShowDialog() != true)
                return Result.Cancelled;

            var byName = schedules.ToDictionary(schedule => schedule.Name, StringComparer.Ordinal);
            var meta = ScheduleWorkbook.NewMetaSheet();
            var taken = new List<string> { ScheduleWorkbook.MetaSheetName };
            var sheets = new List<XlsxSheet>();
            var written = new List<string>();
            var notes = new List<string>();
            var failures = new List<string>();

            foreach (var name in window.Selected)
            {
                if (!byName.TryGetValue(name, out var schedule))
                    continue;

                try
                {
                    var table = ScheduleTable.Read(doc, schedule, window.Layout == ScheduleExportLayout.AsInRevit);
                    sheets.Add(ScheduleWorkbook.Build(doc, table, window.Layout, Xlsx.SheetName(name, taken), meta));
                    written.Add(name + " — " + Rows(table.Rows.Count));

                    if (table.TieProblem != null)
                        notes.Add("\"" + name + "\": " + table.TieProblem + ". The sheet can be imported back by a key column or by row order only.");
                    else if (table.TiedRows < table.Rows.Count)
                        notes.Add("\"" + name + "\": " + (table.Rows.Count - table.TiedRows) + " of its rows could not be tied to their elements " +
                                  "(elements of a linked model, say); an import will show changes on them but cannot write them.");

                    if (window.Layout == ScheduleExportLayout.AsInRevit && table.LayoutProblem != null)
                        notes.Add("\"" + name + "\" is written as a plain table: " + table.LayoutProblem + ".");
                }
                catch (Exception exception)
                {
                    failures.Add(name + ": " + LinkCatalog.Short(exception.Message));
                }
            }

            if (sheets.Count == 0)
            {
                TaskDialog.Show(DialogTitle, "Nothing was exported.\n\n• " + string.Join("\n• ", failures.Take(ListLimit)));
                return Result.Succeeded;
            }

            sheets.Add(meta);

            try
            {
                XlsxWriter.Write(window.FilePath, sheets);
            }
            catch (Exception exception)
            {
                TaskDialog.Show(DialogTitle,
                    "The workbook could not be written:\n" + exception.Message + "\n\n" + window.FilePath + "\n\n" +
                    "If the file is open in Excel, close it there and export again.");
                return Result.Succeeded;
            }

            var opened = window.OpenAfter && Open(window.FilePath, notes);

            if (!opened || notes.Count > 0 || failures.Count > 0)
            {
                var text = "Exported " + written.Count + (written.Count == 1 ? " schedule" : " schedules") + " to\n" + window.FilePath +
                           "\n\n• " + string.Join("\n• ", written);

                foreach (var note in notes)
                    text += "\n\n" + note;

                if (failures.Count > 0)
                    text += "\n\nCould not be exported (" + failures.Count + "):\n• " + string.Join("\n• ", failures.Take(ListLimit));

                TaskDialog.Show(DialogTitle, text);
            }

            return Result.Succeeded;
        }

        private static string Rows(int count)
        {
            return count == 1 ? "1 row" : count + " rows";
        }

        private static bool Open(string path, List<string> notes)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch (Exception exception)
            {
                notes.Add("The file could not be opened (" + LinkCatalog.Short(exception.Message) + ") — open it by hand.");
                return false;
            }
        }

        // ───────────────────────────── import ─────────────────────────────

        private static Result Import(ExternalCommandData commandData, UIDocument uidoc, IReadOnlyList<ViewSchedule> schedules)
        {
            var doc = uidoc.Document;
            var preferences = ExcelPreferences.Load();

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import from Excel",
                Filter = "Excel workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
                CheckFileExists = true
            };

            var folder = preferences.ExistingFolder();
            if (folder != null)
                dialog.InitialDirectory = folder;

            if (dialog.ShowDialog() != true)
                return Result.Cancelled;

            var path = dialog.FileName;
            preferences.Folder = Path.GetDirectoryName(path) ?? string.Empty;
            preferences.Save();

            IReadOnlyList<ExcelSheetSource> sources;
            try
            {
                sources = ExcelSheetSource.Read(XlsxReader.Read(path));
            }
            catch (Exception exception)
            {
                TaskDialog.Show(DialogTitle, "The workbook could not be read:\n" + exception.Message + "\n\n" + path);
                return Result.Cancelled;
            }

            if (sources.Count == 0)
            {
                TaskDialog.Show(DialogTitle, "The workbook has no sheet with anything on it.\n\n" + path);
                return Result.Cancelled;
            }

            var byName = schedules.ToDictionary(schedule => schedule.Name, StringComparer.Ordinal);
            var bySheet = sources.GroupBy(source => source.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var active = (uidoc.ActiveView as ViewSchedule)?.Name;
            var guesses = sources.ToDictionary(source => source.Name, source => Guess(doc, source, byName, active), StringComparer.Ordinal);

            // A schedule is read once per window: asking its rows for their elements is the slow part,
            // and nothing in the document changes while the window is open.
            var tables = new Dictionary<string, ScheduleTable>(StringComparer.Ordinal);
            var plans = new Dictionary<ExcelImportPreview, ScheduleImportPlan>();

            ExcelImportPreview Plan(ExcelImportRequest request)
            {
                if (!bySheet.TryGetValue(request.SheetName, out var source))
                    return ExcelImportPreview.Failed(request, "The workbook has no sheet \"" + request.SheetName + "\".");

                if (!byName.TryGetValue(request.ScheduleName, out var schedule))
                    return ExcelImportPreview.Failed(request, "The project has no schedule \"" + request.ScheduleName + "\".");

                if (!tables.TryGetValue(request.ScheduleName, out var table))
                {
                    try
                    {
                        table = ScheduleTable.Read(doc, schedule, false);
                    }
                    catch (Exception exception)
                    {
                        return ExcelImportPreview.Failed(request, "The schedule could not be read: " + exception.Message);
                    }

                    tables[request.ScheduleName] = table;
                }

                var plan = ScheduleImportPlanner.Plan(doc, table, source, request);
                plans[plan.Preview] = plan;
                return plan.Preview;
            }

            var window = new ExcelImportWindow(
                Path.GetFileName(path),
                sources.Select(source => source.Name).ToList(),
                guesses,
                schedules.Select(schedule => ScheduleLibrary.Describe(doc, schedule)).ToList(),
                preferences,
                Plan);
            new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

            if (window.ShowDialog() != true || window.Result == null || !plans.TryGetValue(window.Result, out var chosen))
                return Result.Cancelled;

            Apply(uidoc, chosen, window.SelectAfter);
            return Result.Succeeded;
        }

        /// <summary>
        /// The schedule a sheet most likely belongs to: the one it was exported from (by UniqueId, then
        /// by name), one named like the sheet — Excel cuts sheet names at 31 characters — or, failing
        /// all of that, the schedule open on screen.
        /// </summary>
        private static string Guess(Document doc, ExcelSheetSource source, IReadOnlyDictionary<string, ViewSchedule> schedules, string active)
        {
            if (source.ScheduleUniqueId.Length > 0)
            {
                try
                {
                    if (doc.GetElement(source.ScheduleUniqueId) is ViewSchedule exported && schedules.ContainsKey(exported.Name))
                        return exported.Name;
                }
                catch (Exception)
                {
                }
            }

            if (source.ScheduleName.Length > 0 && schedules.ContainsKey(source.ScheduleName))
                return source.ScheduleName;

            var sheet = source.Name.Trim();
            var named = schedules.Keys.FirstOrDefault(name => string.Equals(name, sheet, StringComparison.CurrentCultureIgnoreCase))
                        ?? (sheet.Length >= 25
                            ? schedules.Keys.FirstOrDefault(name => name.StartsWith(sheet, StringComparison.CurrentCultureIgnoreCase))
                            : null);

            return named ?? active ?? string.Empty;
        }

        // ───────────────────────────── writing ─────────────────────────────

        private static void Apply(UIDocument uidoc, ScheduleImportPlan plan, bool selectAfter)
        {
            var doc = uidoc.Document;
            var outcome = new Outcome();
            var writes = plan.Writes.Where(write => write.Row.IsSelected).ToList();

            var targets = writes.SelectMany(write => write.Targets).Distinct().ToList();
            var free = new HashSet<ElementId>(doc.IsWorkshared ? Borrow(doc, targets, outcome) : targets);

            var jobs = writes.SelectMany(write => write.Targets.Where(free.Contains).Select(target => new Job(write, target))).ToList();
            var done = new List<Job>();
            var warnings = new WarningSuppressor();

            if (jobs.Count > 0)
            {
                using (var transaction = new Transaction(doc, "Import from Excel"))
                {
                    transaction.Start();

                    // A batch of parameter changes drags Revit warnings along (a duplicate mark, say); a
                    // modal dialog per warning would wreck the run, so they go into the report instead.
                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(warnings);
                    transaction.SetFailureHandlingOptions(options);

                    WriteInPasses(doc, jobs, done, outcome.Failures);

                    if (done.Count == 0)
                    {
                        transaction.RollBack();
                    }
                    else
                    {
                        // Commit returns a status, and Revit can roll the whole batch back there without
                        // throwing — a group refusing an edit, say. A rolled-back run must not report a
                        // single value as written.
                        var status = transaction.Commit();
                        if (status != TransactionStatus.Committed)
                        {
                            outcome.Failures.Insert(0, "Revit rolled the whole import back (" + status + ") — nothing was changed.");
                            done.Clear();
                        }
                    }
                }
            }

            // "Set returned true" is not "the value is there": every write is read back after the commit.
            var verified = new List<Job>();
            foreach (var job in done)
            {
                var parameter = ScheduleValues.On(doc, doc.GetElement(job.Target), job.Write.Column.ParameterId);
                if (parameter != null && ScheduleValues.Holds(parameter, job.Write.Value))
                    verified.Add(job);
                else
                    outcome.Failures.Add(Line(doc, job, "Revit took the value without an error, but it reads " +
                                                        Quote(parameter == null ? string.Empty : ScheduleValues.Show(parameter))));
            }

            var selected = selectAfter && verified.Count > 0 && SelectChanged(uidoc, plan.Table, verified, outcome);
            Report(doc, plan, verified, outcome, warnings, selected);
        }

        /// <summary>
        /// Writes in passes, the way "Rename Nested" renames: a value refused because another one is in
        /// the way — a sheet number still held by the sheet that is about to give it up — goes through
        /// in the next pass, once the other has moved. A refusal only counts as a failure in a pass
        /// where nothing at all went through.
        /// </summary>
        private static void WriteInPasses(Document doc, List<Job> jobs, List<Job> done, List<string> failures)
        {
            var pending = jobs;
            var lastError = new Dictionary<Job, string>();

            while (pending.Count > 0)
            {
                var retry = new List<Job>();

                foreach (var job in pending)
                {
                    var element = doc.GetElement(job.Target);
                    if (element == null)
                    {
                        failures.Add(Line(doc, job, "the element is no longer in the project"));
                        continue;
                    }

                    var parameter = ScheduleValues.On(doc, element, job.Write.Column.ParameterId);
                    if (parameter == null || parameter.IsReadOnly)
                    {
                        failures.Add(Line(doc, job, parameter == null ? "it has no such parameter any more" : "the parameter is read-only"));
                        continue;
                    }

                    try
                    {
                        if (Set(parameter, job.Write.Value))
                            done.Add(job);
                        else
                            failures.Add(Line(doc, job, "Revit refused the value"));
                    }
                    catch (Exception exception)
                    {
                        retry.Add(job);
                        lastError[job] = LinkCatalog.Short(exception.Message);
                    }
                }

                if (retry.Count == pending.Count)
                {
                    failures.AddRange(retry.Select(job => Line(doc, job, lastError[job])));
                    break;
                }

                pending = retry;
            }
        }

        private static bool Set(Parameter parameter, object value)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.Set(value as string ?? string.Empty);
                case StorageType.Integer:
                    return parameter.Set(Convert.ToInt32(value));
                case StorageType.Double:
                    return parameter.Set(Convert.ToDouble(value));
                default:
                    return false;
            }
        }

        /// <summary>
        /// Checks the elements out of the central model before writing — the same recipe as "Move to
        /// Workset": an element somebody else holds, or one changed in the central since the last
        /// reload, would fail the whole commit rather than itself, so both are taken out here and named.
        /// </summary>
        private static List<ElementId> Borrow(Document doc, List<ElementId> wanted, Outcome outcome)
        {
            if (wanted.Count == 0)
                return wanted;

            HashSet<ElementId> owned = null;

            try
            {
                owned = new HashSet<ElementId>(WorksharingUtils.CheckoutElements(doc, wanted));
            }
            catch (Exception exception)
            {
                outcome.Notes.Add("The elements could not be checked out in one go (" + LinkCatalog.Short(exception.Message) +
                                  "). Whatever Revit lets through is still written.");
            }

            var free = new List<ElementId>();

            foreach (var id in wanted)
            {
                var updates = ModelUpdatesStatus.CurrentWithCentral;
                try
                {
                    updates = WorksharingUtils.GetModelUpdatesStatus(doc, id);
                }
                catch (Exception)
                {
                }

                if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral)
                {
                    outcome.Stale.Add(id);
                    continue;
                }

                if (owned != null && owned.Contains(id))
                {
                    free.Add(id);
                    continue;
                }

                var owner = string.Empty;
                var status = CheckoutStatus.NotOwned;
                try
                {
                    status = WorksharingUtils.GetCheckoutStatus(doc, id, out owner);
                }
                catch (Exception)
                {
                }

                if (status == CheckoutStatus.OwnedByOtherUser)
                {
                    var key = string.IsNullOrEmpty(owner) ? "another user" : owner;
                    if (!outcome.Held.TryGetValue(key, out var list))
                    {
                        list = new List<ElementId>();
                        outcome.Held[key] = list;
                    }
                    list.Add(id);
                }
                else
                {
                    free.Add(id);
                }
            }

            return free;
        }

        /// <summary>
        /// Opens the schedule and selects the elements whose rows changed — Revit highlights a selected
        /// element's row in a schedule, which is the most direct way to show the user what moved.
        /// </summary>
        private static bool SelectChanged(UIDocument uidoc, ScheduleTable table, IReadOnlyList<Job> verified, Outcome outcome)
        {
            var doc = uidoc.Document;
            var ids = verified
                .SelectMany(job => job.Write.Data.ElementIds)
                .Distinct()
                .Where(id => doc.GetElement(id) != null)
                .ToList();

            try
            {
                if (doc.GetElement(table.Id) is ViewSchedule schedule && uidoc.ActiveView?.Id != schedule.Id)
                    uidoc.ActiveView = schedule;
            }
            catch (Exception exception)
            {
                outcome.Notes.Add("The schedule could not be opened (" + LinkCatalog.Short(exception.Message) + ") — the changed elements are selected anyway.");
            }

            try
            {
                uidoc.Selection.SetElementIds(ids);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(Document doc, ScheduleImportPlan plan, IReadOnlyList<Job> verified, Outcome outcome, WarningSuppressor warnings, bool selected)
        {
            string text;

            if (verified.Count > 0)
            {
                var values = verified.Select(job => job.Write).Distinct().ToList();
                var rows = values.Select(write => write.Row).Distinct().Count();
                var elements = verified.Select(job => job.Target).Distinct().Count();

                text = "Written into \"" + plan.Table.Name + "\": " + values.Count + (values.Count == 1 ? " value" : " values") +
                       " from " + rows + (rows == 1 ? " row" : " rows") + " — " + elements + (elements == 1 ? " element or type" : " elements and types") +
                       " changed.\n\n• " +
                       string.Join("\n• ", values
                           .GroupBy(write => write.Column.Heading)
                           .OrderByDescending(group => group.Count())
                           .Select(group => group.Key + " — " + group.Count()));

                if (selected)
                    text += "\n\nThe changed rows are selected in the schedule.";
            }
            else
            {
                text = "Nothing was written.";
            }

            foreach (var note in outcome.Notes)
                text += "\n\n" + note;

            if (outcome.Held.Count > 0)
            {
                text += "\n\nHeld by another user, so left as they were — ask them to synchronise and relinquish, then import again:";
                foreach (var held in outcome.Held)
                    text += "\n• " + held.Key + " — " + held.Value.Count + (held.Value.Count == 1 ? " element" : " elements");
            }

            if (outcome.Stale.Count > 0)
                text += "\n\nChanged in the central model since your last reload, so left as they were (" + outcome.Stale.Count +
                        "): Collaborate → Reload Latest, then import again.";

            if (outcome.Failures.Count > 0)
            {
                text += "\n\nCould not be done (" + outcome.Failures.Count + "):\n• " + string.Join("\n• ", outcome.Failures.Take(ListLimit));
                if (outcome.Failures.Count > ListLimit)
                    text += "\n… and " + (outcome.Failures.Count - ListLimit) + " more";
            }

            const int warningLimit = 5;

            foreach (var warning in warnings.Messages.Take(warningLimit))
                text += "\n\nRevit warning: " + warning;

            if (warnings.Messages.Count > warningLimit)
                text += "\n… and " + (warnings.Messages.Count - warningLimit) + " more Revit warnings";

            TaskDialog.Show(DialogTitle, text);
        }

        private static string Line(Document doc, Job job, string reason)
        {
            return "Row " + job.Write.Row.ScheduleRowText + ", " + Describe(doc.GetElement(job.Target)) + " — \"" + job.Write.Column.Heading +
                   "\" → " + Quote(job.Write.NewText) + ": " + reason;
        }

        private static string Describe(Element element)
        {
            if (element == null)
                return "a deleted element";

            try
            {
                var kind = element is ElementType ? "type" : element.Category?.Name ?? "element";
                return kind + " \"" + element.Name + "\"";
            }
            catch (Exception)
            {
                return "element " + element.Id;
            }
        }

        private static string Quote(string text)
        {
            return string.IsNullOrEmpty(text) ? "empty" : "«" + text + "»";
        }
    }
}
