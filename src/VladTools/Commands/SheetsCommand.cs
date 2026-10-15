using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using VladTools.Infrastructure;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// The "Sheets" button: every sheet of the project with what is placed on it — views, legends,
    /// schedules, images, CAD — in the folders and the order of the Project Browser, and the same
    /// table out to Excel.
    ///
    /// The folders are the browser's own, not a guess: Revit hands out the sheet organization in use
    /// (<see cref="BrowserOrganization.GetCurrentBrowserOrganizationForSheets"/>), the folders each
    /// sheet sits in (<see cref="BrowserOrganization.GetFolderItems"/>) and the parameter the browser
    /// sorts sheets by. Only the order of the folders themselves is not exposed — they are sorted
    /// ascending, which is how the browser lists them.
    ///
    /// Every opening is also a check of what changed since the previous one: Revit keeps no date of a
    /// change, so the button keeps its own (<see cref="SheetHistory"/>, <see cref="ViewFingerprint"/>).
    ///
    /// "Several models…" does the same for a batch: every model is read in turn
    /// (<see cref="BatchCoordination.OpenToRead"/>), its table is written into a report of its own,
    /// and one summary covers them all (<see cref="SheetReport"/>). With no document open, or in a
    /// family, the batch is all the button can do, and it is offered straight away.
    ///
    /// Nothing is written into any model, so there is no transaction at all.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SheetsCommand : IExternalCommand
    {
        private const string DialogTitle = "Sheets";

        private const string PlaceholderFolder = "(Placeholder sheets — in sheet lists only)";

        private const int ViewRank = 0;
        private const int LegendRank = 1;
        private const int ScheduleRank = 2;
        private const int PanelScheduleRank = 3;
        private const int ImageRank = 4;
        private const int CadRank = 5;

        private sealed class Placed
        {
            public Placed(int rank, string text, View view)
            {
                Rank = rank;
                Text = text;
                View = view;
            }

            public int Rank { get; }
            public string Text { get; }

            /// <summary>The view placed — a view, a legend, a schedule; null for an image or a CAD file, which belong to the sheet itself.</summary>
            public View View { get; }
        }

        /// <summary>A sheet on its way to the window, with what it is sorted by.</summary>
        private sealed class Entry
        {
            public Entry(SheetInfo info, int group, string sortValue)
            {
                Info = info;
                Group = group;
                SortValue = sortValue ?? string.Empty;
            }

            public SheetInfo Info { get; }

            /// <summary>0 — in the browser; 1 — hidden by its filter; 2 — a placeholder. The last two follow the first.</summary>
            public int Group { get; }

            public string SortValue { get; }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var application = commandData?.Application;
            if (application == null)
                return Result.Cancelled;

            try
            {
                // The button stays pressable with nothing open (see AlwaysAvailable): a batch opens its
                // models itself, and that is the one thing there is to do then.
                var uidoc = application.ActiveUIDocument;
                if (uidoc == null)
                    return Batch(application);

                var doc = uidoc.Document;
                if (doc.IsFamilyDocument)
                {
                    return Offer(application,
                        "The command works only in a project or a project template (.rvt, .rte).\n" +
                        "Sheets live in a project, not in a family.");
                }

                var sheets = Collect(doc, LinkCatalog.VisiblePath(doc), true, out var organization, out var tracking);
                if (sheets.Count == 0)
                    return Offer(application, "The project has no sheets.");

                var preferences = SheetPreferences.Load();
                var window = new SheetsWindow(sheets, organization, tracking, (uidoc.ActiveView as ViewSheet)?.UniqueId, doc.Title, preferences);
                new WindowInteropHelper(window).Owner = application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                switch (window.Action)
                {
                    case SheetsWindowAction.Open:
                        Open(uidoc, window.SheetToOpen);
                        return Result.Succeeded;
                    case SheetsWindowAction.Export:
                        Export(window.ExportRows, window.FilePath, window.OpenAfter);
                        return Result.Succeeded;
                    case SheetsWindowAction.Batch:
                        return Batch(application);
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

        // ───────────────────────────── reading ─────────────────────────────

        /// <summary>
        /// Every sheet of the project, in the order the Project Browser lists them, each with the dates
        /// of the changes found in it — and the check that found them saved for the next one.
        /// </summary>
        /// <param name="historyKey">
        /// The model the change history is kept under — its central model's path. Given rather than read
        /// off the document: a model a batch opened detached has no path at all (RevitAPI.xml: a detached
        /// document's PathName is empty), and its history is still the one its own window keeps.
        /// </param>
        /// <param name="check">
        /// False — read the dates the history holds, but neither compare nor save: a model a batch opened
        /// detached. RevitAPI.xml promises equal versions only "in two instances of the saved file", and if
        /// detaching gave the elements new ones, every batch run would date every view "changed" and the
        /// model's own window would date them all again the next time — each check undoing the last.
        /// </param>
        private static List<SheetInfo> Collect(Document doc, string historyKey, bool check, out string organizationName, out SheetTracking tracking)
        {
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(sheet => !sheet.IsTemplate)
                .ToList();

            var placed = Placements(doc, new HashSet<ElementId>(sheets.Select(sheet => sheet.Id)));

            // No fingerprint means SheetHistory.Check keeps the old record as it is — exactly a read.
            var history = SheetHistory.Load(historyKey, DateTime.Today);
            var fingerprints = check && history != null && history.IsReadable
                ? ViewFingerprint.Of(doc, sheets.Cast<View>().Concat(placed.Values.SelectMany(list => list).Select(item => item.View).Where(view => view != null)))
                : new Dictionary<ElementId, string>();

            BrowserOrganization organization = null;
            var sortingId = ElementId.InvalidElementId;
            var descending = false;

            try
            {
                organization = BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc);
                if (organization != null)
                {
                    sortingId = organization.SortingParameterId;
                    descending = organization.SortingOrder == SortingOrder.Descending;
                }
            }
            catch (Exception)
            {
                // No organization means one flat list in sheet-number order — still the whole answer.
            }

            organizationName = ProjectBrowser.Name(organization);

            var entries = new List<Entry>();

            foreach (var sheet in sheets)
            {
                int group;
                IReadOnlyList<string> folders;

                // A placeholder never shows in the browser, and a sheet the organization filters out
                // does not either; both are still sheets of the project, so they get folders of their
                // own at the end rather than being dropped.
                if (sheet.IsPlaceholder)
                {
                    group = 2;
                    folders = new[] { PlaceholderFolder };
                }
                else if (organization != null && !ProjectBrowser.Passes(organization, sheet.Id))
                {
                    group = 1;
                    folders = new[] { ProjectBrowser.FilteredFolder };
                }
                else
                {
                    group = 0;
                    folders = ProjectBrowser.Folders(organization, sheet.Id);
                }

                var items = placed.TryGetValue(sheet.Id, out var list)
                    ? list.OrderBy(item => item.Rank).ThenBy(item => item.Text, ProjectBrowser.NaturalOrder).ToList()
                    : new List<Placed>();

                var sheetEntry = Check(history, fingerprints, sheet);
                var found = new List<KeyValuePair<string, SheetHistoryEntry>> { new KeyValuePair<string, SheetHistoryEntry>(null, sheetEntry) };
                var contents = new List<string>();

                foreach (var item in items)
                {
                    var entry = Check(history, fingerprints, item.View);
                    found.Add(new KeyValuePair<string, SheetHistoryEntry>(item.Text, entry));
                    contents.Add(item.Text + Suffix(entry));
                }

                var latest = found.Max(pair => pair.Value?.FoundOn);
                var info = new SheetInfo(sheet.UniqueId, sheet.SheetNumber, sheet.Name, folders, contents, !sheet.IsPlaceholder,
                    latest, Note(history, found, latest), found.Any(pair => pair.Value?.FoundNow == true), group == 1, Count(items));
                entries.Add(new Entry(info, group, group == 0 ? ProjectBrowser.SortValue(sheet, sortingId, sheet.SheetNumber) : sheet.SheetNumber));
            }

            // Saved before the window opens: looking is the check, whatever the window is closed with.
            if (check)
                history?.Save();

            tracking = Tracking(history);

            entries.Sort((a, b) => Compare(a, b, descending));
            return entries.Select(entry => entry.Info).ToList();
        }

        /// <summary>What stands on a sheet, counted by kind — a batch's summary adds these up.</summary>
        private static SheetCounts Count(IReadOnlyList<Placed> items)
        {
            return new SheetCounts(
                items.Count(item => item.Rank == ViewRank),
                items.Count(item => item.Rank == LegendRank),
                items.Count(item => item.Rank == ScheduleRank || item.Rank == PanelScheduleRank),
                items.Count(item => item.Rank == ImageRank || item.Rank == CadRank));
        }

        private static SheetHistoryEntry Check(SheetHistory history, Dictionary<ElementId, string> fingerprints, View view)
        {
            if (history == null || view == null)
                return null;

            return history.Check(view.UniqueId, fingerprints.TryGetValue(view.Id, out var fingerprint) ? fingerprint : null);
        }

        /// <summary>" — changed 2026-10-07" after a line of "On the sheet"; nothing for a view with no change found.</summary>
        private static string Suffix(SheetHistoryEntry entry)
        {
            if (entry?.FoundOn == null)
                return string.Empty;

            return (entry.Change == SheetChange.Added ? " — placed " : " — changed ") + SheetInfo.FormatDate(entry.FoundOn);
        }

        /// <summary>
        /// The tooltip of a sheet's date: between which checks the latest change was made and what it
        /// was in — the sheet itself or which of its views. A null label in <paramref name="found"/> is
        /// the sheet itself.
        /// </summary>
        private static string Note(SheetHistory history, List<KeyValuePair<string, SheetHistoryEntry>> found, DateTime? latest)
        {
            if (history == null || !history.IsReadable)
                return string.Empty;

            if (latest == null)
            {
                return history.Since == null
                    ? "Change tracking starts with this check: from the next one on, a change gets the date of the check that finds it."
                    : "No change found since the first check, " + SheetInfo.FormatDate(history.Since) + ".";
            }

            var latestEntries = found.Where(pair => pair.Value?.FoundOn == latest).ToList();
            var after = latestEntries.Select(pair => pair.Value.After).Min();

            var text = new StringBuilder(after == null || after == latest
                ? "Changed on " + SheetInfo.FormatDate(latest) + "."
                : "Changed between the checks of " + SheetInfo.FormatDate(after) + " and " + SheetInfo.FormatDate(latest) + ".");

            foreach (var pair in latestEntries)
            {
                text.Append("\n•  ");
                if (pair.Key == null)
                    text.Append(pair.Value.Change == SheetChange.Added ? "a new sheet" : "the sheet itself — its viewports, titleblock, text or parameters");
                else
                    text.Append(pair.Key).Append(pair.Value.Change == SheetChange.Added ? " — placed on the sheet" : string.Empty);
            }

            return text.ToString();
        }

        private static SheetTracking Tracking(SheetHistory history)
        {
            if (history == null)
                return new SheetTracking(false, null, null, "Change dates are not tracked: the model has never been saved, so there is no name to keep its history under.");

            return new SheetTracking(history.IsReadable, history.Since ?? history.Today, history.PreviousCheck, history.Problem);
        }

        /// <summary>
        /// What stands on every sheet, read in one pass per kind over the whole project rather than one
        /// collector per sheet: viewports, schedule instances, images and CAD each point at the sheet
        /// that holds them.
        /// </summary>
        private static Dictionary<ElementId, List<Placed>> Placements(Document doc, HashSet<ElementId> sheets)
        {
            var raw = new Dictionary<ElementId, List<Placed>>();

            void Add(ElementId sheet, int rank, string text, View view = null)
            {
                if (sheet == null || !sheets.Contains(sheet))
                    return;

                if (!raw.TryGetValue(sheet, out var list))
                {
                    list = new List<Placed>();
                    raw[sheet] = list;
                }

                list.Add(new Placed(rank, text, view));
            }

            foreach (var viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                if (doc.GetElement(viewport.ViewId) is View view)
                    Add(viewport.SheetId, view.ViewType == ViewType.Legend ? LegendRank : ViewRank, TextSiteCollector.ViewKind(view) + ": " + view.Name, view);
            }

            // A split schedule stands on its sheet once per segment; the revision schedule inside a
            // titleblock stands on every sheet and belongs to the titleblock, not to what was placed.
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .Where(instance => !instance.IsTitleblockRevisionSchedule)
                .GroupBy(instance => new { Sheet = instance.OwnerViewId, Schedule = instance.ScheduleId });

            foreach (var group in schedules)
            {
                if (doc.GetElement(group.Key.Schedule) is ViewSchedule schedule)
                    Add(group.Key.Sheet, ScheduleRank, "Schedule: " + schedule.Name + Segments(group.Count()), schedule);
            }

            var panelSchedules = new FilteredElementCollector(doc)
                .OfClass(typeof(PanelScheduleSheetInstance))
                .Cast<PanelScheduleSheetInstance>()
                .GroupBy(instance => new { Sheet = instance.OwnerViewId, Schedule = instance.ScheduleId });

            foreach (var group in panelSchedules)
            {
                var schedule = doc.GetElement(group.Key.Schedule);
                if (schedule != null)
                    Add(group.Key.Sheet, PanelScheduleRank, "Panel schedule: " + schedule.Name + Segments(group.Count()), schedule as View);
            }

            foreach (var image in new FilteredElementCollector(doc).OfClass(typeof(ImageInstance)).Cast<ImageInstance>())
                Add(image.OwnerViewId, ImageRank, "Image: " + TypeName(doc, image));

            foreach (var import in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Where(import => import.ViewSpecific))
                Add(import.OwnerViewId, CadRank, (import.IsLinked ? "Linked CAD: " : "Imported CAD: ") + TypeName(doc, import));

            // The same legend or logo placed twice on one sheet is one line with a count, not two equal
            // lines. Grouped by the view's id, not the object: every GetElement hands out a wrapper of its own.
            return raw.ToDictionary(
                entry => entry.Key,
                entry => entry.Value
                    .GroupBy(item => new { item.Rank, item.Text, ViewId = item.View?.Id })
                    .Select(group => new Placed(group.Key.Rank, group.Key.Text + (group.Count() > 1 ? " (×" + group.Count() + ")" : string.Empty), group.First().View))
                    .ToList());
        }

        private static string Segments(int count)
        {
            return count > 1 ? " (" + count + " segments)" : string.Empty;
        }

        /// <summary>A CAD file's or an image's name: its type is named after the file.</summary>
        private static string TypeName(Document doc, Element element)
        {
            try
            {
                var name = doc.GetElement(element.GetTypeId())?.Name;
                return string.IsNullOrEmpty(name) ? element.Category?.Name ?? element.Name : name;
            }
            catch (Exception)
            {
                return element.Id.ToString();
            }
        }

        private static int Compare(Entry a, Entry b, bool descending)
        {
            var result = a.Group.CompareTo(b.Group);
            if (result != 0)
                return result;

            for (var level = 0; level < Math.Min(a.Info.Folders.Count, b.Info.Folders.Count); level++)
            {
                result = ProjectBrowser.Natural(a.Info.Folders[level], b.Info.Folders[level]);
                if (result != 0)
                    return result;
            }

            result = a.Info.Folders.Count.CompareTo(b.Info.Folders.Count);
            if (result != 0)
                return result;

            result = ProjectBrowser.Natural(a.SortValue, b.SortValue);
            if (result != 0)
                return descending ? -result : result;

            return ProjectBrowser.Natural(a.Info.Number, b.Info.Number);
        }

        // ───────────────────────────── opening ─────────────────────────────

        private static void Open(UIDocument uidoc, SheetInfo info)
        {
            if (info == null)
                return;

            if (!(uidoc.Document.GetElement(info.UniqueId) is ViewSheet sheet))
            {
                TaskDialog.Show(DialogTitle, "\"" + info.Title + "\" is no longer in the project.");
                return;
            }

            try
            {
                uidoc.ActiveView = sheet;
            }
            catch (Exception exception)
            {
                TaskDialog.Show(DialogTitle, "\"" + info.Title + "\" could not be opened:\n" + exception.Message);
            }
        }

        // ───────────────────────────── export ─────────────────────────────

        private static void Export(IReadOnlyList<SheetListRow> rows, string path, bool openAfter)
        {
            try
            {
                XlsxWriter.Write(path, new[] { Workbook(rows) });
            }
            catch (Exception exception)
            {
                TaskDialog.Show(DialogTitle,
                    "The workbook could not be written:\n" + exception.Message + "\n\n" + path + "\n\n" +
                    "If the file is open in Excel, close it there and export again.");
                return;
            }

            if (openAfter)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return;
                }
                catch (Exception exception)
                {
                    TaskDialog.Show(DialogTitle, "Written to\n" + path + "\n\nbut the file could not be opened (" + LinkCatalog.Short(exception.Message) + ") — open it by hand.");
                    return;
                }
            }

            var sheets = rows.Count(row => !row.IsFolder);
            TaskDialog.Show(DialogTitle, "Exported " + SheetListRow.Count(sheets) + " to\n" + path);
        }

        /// <summary>
        /// The table as one Excel sheet: a heading line, then the window's lines one for one. The
        /// number and the name are columns of their own — the number is what a week's file is matched
        /// against the next week's by. A folder is a heading line across the number, the name and the
        /// date, and everything under it is an Excel row group, so the folders fold in Excel the way
        /// they fold in the Project Browser. Filter buttons go on only where there are no folder lines:
        /// sorting through them would scatter the sheets out of their folders.
        /// </summary>
        private static XlsxSheet Workbook(IReadOnlyList<SheetListRow> rows)
        {
            const int numberColumn = 0;
            const int nameColumn = 1;
            const int changedColumn = 2;
            const int contentsColumn = 3;

            var sheet = new XlsxSheet("Sheets") { FrozenRows = 1 };
            sheet.Set(0, numberColumn, "Number", XlsxStyle.Heading);
            sheet.Set(0, nameColumn, "Sheet name", XlsxStyle.Heading);
            sheet.Set(0, changedColumn, "Changed", XlsxStyle.Heading);
            sheet.Set(0, contentsColumn, "On the sheet", XlsxStyle.Heading);

            var line = 1;
            foreach (var row in rows)
            {
                if (row.IsFolder)
                {
                    sheet.Set(line, numberColumn, new string(' ', 4 * row.Level) + row.FolderName, XlsxStyle.Group);
                    sheet.Set(line, nameColumn, string.Empty, XlsxStyle.Group);
                    sheet.Set(line, changedColumn, string.Empty, XlsxStyle.Group);
                    sheet.Set(line, contentsColumn, SheetListRow.Count(row.ShownSheets), XlsxStyle.Group);
                    sheet.Merges.Add(new XlsxRange(line, numberColumn, line, changedColumn));
                }
                else
                {
                    sheet.Set(line, numberColumn, row.Sheet.Number);
                    sheet.Set(line, nameColumn, row.Sheet.Name);
                    sheet.Set(line, changedColumn, row.Sheet.ChangedText);
                    sheet.Set(line, contentsColumn, row.ContentsText);
                }

                if (row.Level > 0)
                    sheet.OutlineLevels[line] = row.Level;

                line++;
            }

            var sheets = rows.Where(row => !row.IsFolder).ToList();
            var numbers = sheets.Select(row => row.Sheet.Number.Length).DefaultIfEmpty(0).Max();
            var names = sheets.Select(row => row.Sheet.Name.Length).DefaultIfEmpty(0).Max();
            var contents = sheets.SelectMany(row => row.ContentsText.Split('\n')).Select(text => text.Length).DefaultIfEmpty(0).Max();
            sheet.ColumnWidths[numberColumn] = Math.Max(10, Math.Min(24, numbers + 4));
            sheet.ColumnWidths[nameColumn] = Math.Max(30, Math.Min(70, names + 4));
            sheet.ColumnWidths[changedColumn] = 13;
            sheet.ColumnWidths[contentsColumn] = Math.Max(30, Math.Min(100, contents + 4));

            if (!rows.Any(row => row.IsFolder))
                sheet.AutoFilter = new XlsxRange(0, numberColumn, line - 1, contentsColumn);

            return sheet;
        }

        // ───────────────────────────── a batch of models ─────────────────────────────

        /// <summary>
        /// The refusal that offers the batch instead of ending the conversation: a family, or a project
        /// with no sheets, is a usual enough place to start a report over the project's models from.
        /// </summary>
        private static Result Offer(UIApplication application, string text)
        {
            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = "Nothing to list here",
                MainContent = text,
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close
            };

            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Several models…",
                "Read a batch of models — a folder of them, files, Revit Server, BIM360 or a saved set — and write " +
                "a report of each one's sheets, plus a summary of them all.");

            return dialog.Show() == TaskDialogResult.CommandLink1 ? Batch(application) : Result.Cancelled;
        }

        /// <summary>
        /// The batch: every checked model is read in turn, its table written into a report of its own,
        /// and one summary written over them all.
        ///
        /// A model is the unit of work: one that cannot be opened or read is a line in the summary and
        /// in the report, and the next one is taken — the same rule as for a loop over elements. The run
        /// goes straight through with Revit busy, as the "Accept Changes" batch does: a progress bar
        /// would mean another thread, and the Revit API has only the one.
        /// </summary>
        private static Result Batch(UIApplication application)
        {
            var window = new SheetsBatchWindow(SheetPreferences.Load(), LinkPreferences.Load());
            new WindowInteropHelper(window).Owner = application.MainWindowHandle;

            if (window.ShowDialog() != true)
                return Result.Cancelled;

            var summary = window.FilePath;
            var folder = SheetReport.ReportsFolder(summary);

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception exception)
            {
                TaskDialog.Show(DialogTitle, "The folder for the models' reports could not be made:\n" + folder + "\n\n" + exception.Message);
                return Result.Cancelled;
            }

            var results = new List<SheetModelResult>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in window.Selected)
                results.Add(ReadOne(application.Application, row.Entry, folder, taken));

            var summaryError = string.Empty;

            try
            {
                XlsxWriter.Write(summary, SheetReport.Summary(results, summary));
            }
            catch (Exception exception)
            {
                summaryError = exception.Message;
            }

            ReportBatch(results, summary, folder, summaryError);
            return Result.Succeeded;
        }

        /// <summary>
        /// One model of the batch: open, read, write its report, close. The read is the same as the
        /// window's; the change check is made only on a model opened as itself — one already open, a cloud
        /// model, a file that is not workshared — and a detached copy shows the dates its history holds.
        /// </summary>
        private static SheetModelResult ReadOne(
            Autodesk.Revit.ApplicationServices.Application app,
            LinkEntry entry,
            string folder,
            ISet<string> taken)
        {
            var result = new SheetModelResult(entry);

            var model = BatchCoordination.OpenToRead(app, entry, out var reason);
            if (model == null)
            {
                result.Failure = reason;
                return result;
            }

            try
            {
                var doc = model.Document;
                result.WasOpen = model.WasOpen;

                // A detached copy only reads the history: whether detaching keeps the central's element
                // versions is not confirmed, and a check against new ones would date every view.
                result.Checked = !model.IsDetached;
                result.Sheets = Collect(doc, HistoryKey(doc, entry), result.Checked, out var organization, out var tracking);
                result.Organization = organization;
                result.Tracking = tracking;

                if (result.Sheets.Count > 0)
                {
                    var file = Path.Combine(folder, SheetReport.ModelFileName(entry.Name, taken));

                    try
                    {
                        XlsxWriter.Write(file, new[] { Workbook(SheetListRow.Tree(result.Sheets)) });
                        result.ReportFile = file;
                    }
                    catch (Exception exception)
                    {
                        result.ReportError = LinkCatalog.Short(exception.Message);
                    }
                }
            }
            catch (Exception exception)
            {
                // A model that went wrong in a way nothing above expected must not take the rest of
                // the batch with it; it is closed in "finally" either way.
                result.Failure = "the read broke off: " + LinkCatalog.Short(exception.Message);
                result.Sheets = new List<SheetInfo>();
            }
            finally
            {
                BatchCoordination.Release(model);
            }

            return result;
        }

        /// <summary>
        /// The path a model's change history is kept under: its central model's, as the window keeps it.
        /// A model opened detached has none of its own, and then the path it was opened from stands in —
        /// the same central model, so a batch run and the model's own window share one history.
        /// </summary>
        private static string HistoryKey(Document doc, LinkEntry entry)
        {
            var key = LinkCatalog.VisiblePath(doc);
            if (key.Length > 0)
                return key;

            try
            {
                return ModelPathUtils.ConvertModelPathToUserVisiblePath(LinkCatalog.ToModelPath(entry));
            }
            catch (Exception)
            {
                return entry.Path;
            }
        }

        private static void ReportBatch(IReadOnlyList<SheetModelResult> results, string summary, string folder, string summaryError)
        {
            var read = results.Where(result => result.IsRead).ToList();

            var text = summaryError.Length > 0
                ? "THE SUMMARY COULD NOT BE WRITTEN: " + summaryError + "\n" + summary +
                  "\nIf the file is open in Excel, close it there and run the batch again."
                : "Summary: " + summary;

            if (read.Any(result => result.ReportFile.Length > 0))
                text += "\nThe models' own reports: " + folder;

            text += Section("Read", read.Select(Line).ToList());
            text += Section("Not read", results.Where(result => !result.IsRead).Select(result => result.Name + " — " + result.Failure).ToList());
            text += Section("Read, but the report could not be written — close it in Excel if it is open there",
                read.Where(result => result.ReportError.Length > 0).Select(result => result.Name + " — " + result.ReportError).ToList());
            text += Section("Change history",
                read.Where(result => result.Tracking != null && result.Tracking.Problem.Length > 0)
                    .Select(result => result.Name + " — " + result.Tracking.Problem).ToList());

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = "Read " + read.Count + " of " + results.Count + " models",
                MainContent = text.Trim(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close
            };

            if (summaryError.Length == 0)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the summary", Path.GetFileName(summary));

            if (read.Any(result => result.ReportFile.Length > 0))
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the folder with the models' reports", folder);

            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1:
                    Start(summary);
                    break;
                case TaskDialogResult.CommandLink2:
                    Start(folder);
                    break;
            }
        }

        /// <summary>"AR.rvt — 120 sheets, 3 with nothing placed, 5 changed since the previous check".</summary>
        private static string Line(SheetModelResult result)
        {
            var parts = new List<string> { SheetListRow.Count(result.Sheets.Count) };

            var empty = result.Sheets.Count(info => info.IsEmpty);
            if (empty > 0)
                parts.Add(empty + " with nothing placed");

            if (result.HasDates)
                parts.Add(result.Sheets.Count(info => info.ChangedAtThisCheck) + " changed since the previous check");
            else if (!result.Checked)
                parts.Add("change dates " + SheetReport.CheckNote(result));
            else if (result.Tracking != null && result.Tracking.IsTracked)
                parts.Add("the first check of its change history");

            return result.Name + " — " + string.Join(", ", parts) + (result.WasOpen ? " (read as it stands open in Revit)" : string.Empty);
        }

        private static string Section(string title, IReadOnlyList<string> lines)
        {
            const int limit = 15;

            if (lines.Count == 0)
                return string.Empty;

            var text = "\n\n" + title + ":\n" + string.Join("\n", lines.Take(limit).Select(line => "• " + line));

            return lines.Count > limit ? text + "\n… and " + (lines.Count - limit) + " more" : text;
        }

        /// <summary>Opens a file in its program, or a folder in Explorer; a failure is said, not swallowed.</summary>
        private static void Start(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                TaskDialog.Show(DialogTitle, path + "\n\ncould not be opened (" + LinkCatalog.Short(exception.Message) + ") — open it by hand.");
            }
        }
    }
}
