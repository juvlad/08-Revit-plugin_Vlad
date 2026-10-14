using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    /// Nothing is written into the model, so there is no transaction at all.
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
            public Placed(int rank, string text)
            {
                Rank = rank;
                Text = text;
            }

            public int Rank { get; }
            public string Text { get; }
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
                    "Sheets live in a project, not in a family.");
                return Result.Cancelled;
            }

            try
            {
                var sheets = Collect(doc, out var organization);
                if (sheets.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "The project has no sheets.");
                    return Result.Cancelled;
                }

                var preferences = SheetPreferences.Load();
                var window = new SheetsWindow(sheets, organization, (uidoc.ActiveView as ViewSheet)?.UniqueId, doc.Title, preferences);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

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

        /// <summary>Every sheet of the project, in the order the Project Browser lists them.</summary>
        private static List<SheetInfo> Collect(Document doc, out string organizationName)
        {
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(sheet => !sheet.IsTemplate)
                .ToList();

            var placed = Placements(doc, new HashSet<ElementId>(sheets.Select(sheet => sheet.Id)));

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

                var contents = placed.TryGetValue(sheet.Id, out var list)
                    ? list.OrderBy(item => item.Rank).ThenBy(item => item.Text, ProjectBrowser.NaturalOrder).Select(item => item.Text).ToList()
                    : new List<string>();

                var info = new SheetInfo(sheet.UniqueId, sheet.SheetNumber, sheet.Name, folders, contents, !sheet.IsPlaceholder);
                entries.Add(new Entry(info, group, group == 0 ? ProjectBrowser.SortValue(sheet, sortingId, sheet.SheetNumber) : sheet.SheetNumber));
            }

            entries.Sort((a, b) => Compare(a, b, descending));
            return entries.Select(entry => entry.Info).ToList();
        }

        /// <summary>
        /// What stands on every sheet, read in one pass per kind over the whole project rather than one
        /// collector per sheet: viewports, schedule instances, images and CAD each point at the sheet
        /// that holds them.
        /// </summary>
        private static Dictionary<ElementId, List<Placed>> Placements(Document doc, HashSet<ElementId> sheets)
        {
            var raw = new Dictionary<ElementId, List<Placed>>();

            void Add(ElementId sheet, int rank, string text)
            {
                if (sheet == null || !sheets.Contains(sheet))
                    return;

                if (!raw.TryGetValue(sheet, out var list))
                {
                    list = new List<Placed>();
                    raw[sheet] = list;
                }

                list.Add(new Placed(rank, text));
            }

            foreach (var viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                if (doc.GetElement(viewport.ViewId) is View view)
                    Add(viewport.SheetId, view.ViewType == ViewType.Legend ? LegendRank : ViewRank, TextSiteCollector.ViewKind(view) + ": " + view.Name);
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
                    Add(group.Key.Sheet, ScheduleRank, "Schedule: " + schedule.Name + Segments(group.Count()));
            }

            var panelSchedules = new FilteredElementCollector(doc)
                .OfClass(typeof(PanelScheduleSheetInstance))
                .Cast<PanelScheduleSheetInstance>()
                .GroupBy(instance => new { Sheet = instance.OwnerViewId, Schedule = instance.ScheduleId });

            foreach (var group in panelSchedules)
            {
                var schedule = doc.GetElement(group.Key.Schedule);
                if (schedule != null)
                    Add(group.Key.Sheet, PanelScheduleRank, "Panel schedule: " + schedule.Name + Segments(group.Count()));
            }

            foreach (var image in new FilteredElementCollector(doc).OfClass(typeof(ImageInstance)).Cast<ImageInstance>())
                Add(image.OwnerViewId, ImageRank, "Image: " + TypeName(doc, image));

            foreach (var import in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Where(import => import.ViewSpecific))
                Add(import.OwnerViewId, CadRank, (import.IsLinked ? "Linked CAD: " : "Imported CAD: ") + TypeName(doc, import));

            // The same legend or logo placed twice on one sheet is one line with a count, not two equal lines.
            return raw.ToDictionary(
                entry => entry.Key,
                entry => entry.Value
                    .GroupBy(item => new { item.Rank, item.Text })
                    .Select(group => new Placed(group.Key.Rank, group.Key.Text + (group.Count() > 1 ? " (×" + group.Count() + ")" : string.Empty)))
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
        /// The table as one Excel sheet: a heading line, then the window's lines one for one. A folder
        /// is a heading line of its own, and everything under it is an Excel row group, so the folders
        /// fold in Excel the way they fold in the Project Browser. Filter buttons go on only where
        /// there are no folder lines: sorting through them would scatter the sheets out of their folders.
        /// </summary>
        private static XlsxSheet Workbook(IReadOnlyList<SheetListRow> rows)
        {
            var sheet = new XlsxSheet("Sheets") { FrozenRows = 1 };
            sheet.Set(0, 0, "Sheet", XlsxStyle.Heading);
            sheet.Set(0, 1, "On the sheet", XlsxStyle.Heading);

            var line = 1;
            foreach (var row in rows)
            {
                if (row.IsFolder)
                {
                    sheet.Set(line, 0, new string(' ', 4 * row.Level) + row.FolderName, XlsxStyle.Group);
                    sheet.Set(line, 1, SheetListRow.Count(row.ShownSheets), XlsxStyle.Group);
                }
                else
                {
                    sheet.Set(line, 0, row.Sheet.Title);
                    sheet.Set(line, 1, row.ContentsText);
                }

                if (row.Level > 0)
                    sheet.OutlineLevels[line] = row.Level;

                line++;
            }

            var titles = rows.Select(row => row.IsFolder ? 4 * row.Level + row.FolderName.Length : row.Sheet.Title.Length).DefaultIfEmpty(0).Max();
            var contents = rows.Where(row => !row.IsFolder).SelectMany(row => row.ContentsText.Split('\n')).Select(text => text.Length).DefaultIfEmpty(0).Max();
            sheet.ColumnWidths[0] = Math.Max(30, Math.Min(70, titles + 4));
            sheet.ColumnWidths[1] = Math.Max(30, Math.Min(100, contents + 4));

            if (!rows.Any(row => row.IsFolder))
                sheet.AutoFilter = new XlsxRange(0, 0, line - 1, 1);

            return sheet;
        }
    }
}
