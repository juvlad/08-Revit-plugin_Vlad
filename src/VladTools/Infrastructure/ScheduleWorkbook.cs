using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>How an exported schedule is laid out on its sheet.</summary>
    internal enum ScheduleExportLayout
    {
        /// <summary>As Revit draws it: the title, the headings, group lines, subtotals and the grand total.</summary>
        AsInRevit,

        /// <summary>One heading line and one line per schedule row, with filter buttons — the form for editing.</summary>
        PlainTable
    }

    /// <summary>
    /// How a schedule sits in an Excel workbook, both ways: what the export writes, and what the import
    /// reads back from a sheet — one written here or any other.
    ///
    /// An exported sheet carries three things a person does not see:
    /// • row 1, hidden — what each column shows (<see cref="ScheduleColumn.Key"/>), so a column is
    ///   found again even after its heading was edited or the columns were moved about;
    /// • column A, hidden — a short key per schedule row;
    /// • the hidden "VladTools" sheet — which elements each key stands for, by UniqueId.
    /// The key, not the element list, travels in the row because a grouped row can stand for thousands
    /// of elements, more than an Excel cell holds; and it travels <em>in the row</em> so that sorting or
    /// filtering the sheet in Excel cannot separate a line from its elements.
    /// </summary>
    internal static class ScheduleWorkbook
    {
        public const string MetaSheetName = "VladTools";

        private const string Marker = "VladTools schedule export";
        private const string ExportRow = "EXPORT";
        private const string ElementsRow = "ROW";
        private const int ChunkLength = 30000;

        /// <summary>Adds one schedule's sheet to the book, and its bookkeeping to <paramref name="meta"/>.</summary>
        public static XlsxSheet Build(
            Document doc,
            ScheduleTable table,
            ScheduleExportLayout layout,
            string sheetName,
            XlsxSheet meta)
        {
            var exportKey = Guid.NewGuid().ToString("N");
            var sheet = new XlsxSheet(sheetName);
            var lastColumn = table.Columns.Count;

            sheet.Set(0, 0, Marker + "|" + exportKey, XlsxStyle.Plain);
            foreach (var column in table.Columns)
                sheet.Set(0, column.Index + 1, column.Key, XlsxStyle.Plain);
            sheet.HiddenRows.Add(0);

            sheet.HiddenColumns.Add(0);
            sheet.ColumnWidths[0] = 8;
            foreach (var column in table.Columns)
                sheet.ColumnWidths[column.Index + 1] = Width(column);

            var row = 1;
            var display = layout == ScheduleExportLayout.AsInRevit ? table.DisplayRows : null;

            var titles = display != null ? table.TitleLines : new[] { table.Name };
            foreach (var title in titles)
            {
                sheet.Set(row, 1, title, XlsxStyle.Title);
                if (lastColumn > 1)
                    sheet.Merges.Add(new XlsxRange(row, 1, row, lastColumn));
                row++;
            }

            var rowKeys = new Dictionary<int, string>();
            string KeyOf(int dataRow)
            {
                if (!rowKeys.TryGetValue(dataRow, out var key))
                {
                    key = "R" + (dataRow + 1).ToString(CultureInfo.InvariantCulture);
                    rowKeys[dataRow] = key;
                }

                return key;
            }

            if (display != null)
            {
                var lastHeading = -1;

                foreach (var line in display)
                {
                    var style = line.IsHeading ? XlsxStyle.Heading : line.DataRow >= 0 ? XlsxStyle.Cell : XlsxStyle.Group;
                    for (var column = 0; column < line.Cells.Count; column++)
                        sheet.Set(row, column + 1, ScheduleTable.Clean(line.Cells[column]), style);

                    if (line.DataRow >= 0 && table.Rows[line.DataRow].IsTied)
                        sheet.Set(row, 0, KeyOf(line.DataRow), XlsxStyle.Plain);

                    if (line.IsHeading)
                        lastHeading = row;

                    row++;
                }

                sheet.FrozenRows = lastHeading >= 0 ? lastHeading + 1 : 1 + titles.Count;
            }
            else
            {
                var headingRow = row;
                foreach (var column in table.Columns)
                    sheet.Set(row, column.Index + 1, column.Heading, XlsxStyle.Heading);
                row++;

                foreach (var data in table.Rows)
                {
                    for (var column = 0; column < data.Cells.Count; column++)
                        sheet.Set(row, column + 1, ScheduleTable.Clean(data.Cells[column]), XlsxStyle.Cell);

                    if (data.IsTied)
                        sheet.Set(row, 0, KeyOf(data.Index), XlsxStyle.Plain);

                    row++;
                }

                sheet.FrozenRows = headingRow + 1;

                // The filter starts at the hidden key column: Excel sorts within the filter's range,
                // and a key left outside it would stay put while its line moved.
                if (table.Rows.Count > 0)
                    sheet.AutoFilter = new XlsxRange(headingRow, 0, row - 1, lastColumn);
            }

            WriteMeta(doc, table, exportKey, rowKeys, meta);
            return sheet;
        }

        /// <summary>The bookkeeping sheet, with a line on top saying what it is for.</summary>
        public static XlsxSheet NewMetaSheet()
        {
            var meta = new XlsxSheet(MetaSheetName) { IsHidden = true };
            meta.Set(0, 0, "Written by the VladTools \"Excel\" button. Which elements each exported row stands for — the import " +
                           "reads it from here. Edit nothing on this sheet.", XlsxStyle.Plain);
            return meta;
        }

        private static void WriteMeta(Document doc, ScheduleTable table, string exportKey, IReadOnlyDictionary<int, string> rowKeys, XlsxSheet meta)
        {
            var row = meta.LastRow + 1;

            meta.Set(row, 0, ExportRow, XlsxStyle.Plain);
            meta.Set(row, 1, exportKey, XlsxStyle.Plain);
            meta.Set(row, 2, table.Name, XlsxStyle.Plain);
            meta.Set(row, 3, table.UniqueId, XlsxStyle.Plain);
            meta.Set(row, 4, doc.Title, XlsxStyle.Plain);
            meta.Set(row, 5, DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), XlsxStyle.Plain);
            row++;

            foreach (var pair in rowKeys.OrderBy(pair => pair.Key))
            {
                var ids = string.Join(";", table.Rows[pair.Key].ElementIds
                    .Select(id => doc.GetElement(id)?.UniqueId)
                    .Where(uniqueId => !string.IsNullOrEmpty(uniqueId)));

                meta.Set(row, 0, ElementsRow, XlsxStyle.Plain);
                meta.Set(row, 1, exportKey, XlsxStyle.Plain);
                meta.Set(row, 2, pair.Value, XlsxStyle.Plain);

                var column = 3;
                for (var start = 0; start < ids.Length; start += ChunkLength)
                    meta.Set(row, column++, ids.Substring(start, Math.Min(ChunkLength, ids.Length - start)), XlsxStyle.Plain);

                row++;
            }
        }

        /// <summary>A Revit column width on paper, in Excel's unit. Excel's default font is larger than a schedule's text, so it is widened.</summary>
        private static double Width(ScheduleColumn column)
        {
            var millimetres = column.Width * 304.8;
            return millimetres <= 0 ? 16 : Math.Max(8, Math.Min(80, millimetres * 0.75));
        }
    }

    /// <summary>
    /// One worksheet as the import sees it: the values, and — for a sheet this add-in exported — which
    /// schedule it came from, what each column shows and which elements each line stands for.
    /// A sheet from anywhere else is simply a grid; the import then matches it by headings and by order.
    /// </summary>
    internal sealed class ExcelSheetSource
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _elements;

        private ExcelSheetSource(XlsxReadSheet sheet)
        {
            Sheet = sheet;
            _elements = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            ColumnKeys = new Dictionary<int, string>();
        }

        public XlsxReadSheet Sheet { get; }

        public string Name => Sheet.Name;

        /// <summary>Written by this add-in's export.</summary>
        public bool IsExport { get; private set; }

        /// <summary>The schedule the sheet was exported from, by name and by UniqueId; empty for any other sheet.</summary>
        public string ScheduleName { get; private set; } = string.Empty;

        public string ScheduleUniqueId { get; private set; } = string.Empty;

        /// <summary>Sheet column → what it showed in the schedule (<see cref="ScheduleColumn.Key"/>).</summary>
        public Dictionary<int, string> ColumnKeys { get; }

        /// <summary>The hidden column holding row keys; -1 for a sheet from anywhere else.</summary>
        public int IdColumn => IsExport ? 0 : -1;

        /// <summary>The sheet holds lines tied to elements — the import can match by element.</summary>
        public bool HasIds => _elements.Count > 0;

        public bool HasRowKey(int row)
        {
            return IdColumn >= 0 && _elements.ContainsKey(Sheet.Text(row, IdColumn).Trim());
        }

        /// <summary>The UniqueIds of the elements a line stands for; empty for a line with none.</summary>
        public IReadOnlyList<string> ElementsOf(int row)
        {
            return IdColumn >= 0 && _elements.TryGetValue(Sheet.Text(row, IdColumn).Trim(), out var ids) ? ids : new string[0];
        }

        /// <summary>Every visible sheet of the book with something on it, ready for the import.</summary>
        public static IReadOnlyList<ExcelSheetSource> Read(IReadOnlyList<XlsxReadSheet> book)
        {
            var meta = book.FirstOrDefault(sheet => string.Equals(sheet.Name, ScheduleWorkbook.MetaSheetName, StringComparison.OrdinalIgnoreCase) && sheet.IsHidden)
                       ?? book.FirstOrDefault(sheet => string.Equals(sheet.Name, ScheduleWorkbook.MetaSheetName, StringComparison.OrdinalIgnoreCase));

            var sources = new List<ExcelSheetSource>();
            foreach (var sheet in book.Where(sheet => !sheet.IsHidden && !sheet.IsEmpty && sheet != meta))
            {
                var source = new ExcelSheetSource(sheet);
                source.ReadExport(meta);
                sources.Add(source);
            }

            return sources;
        }

        private void ReadExport(XlsxReadSheet meta)
        {
            var marker = Sheet.Text(0, 0);
            const string prefix = "VladTools schedule export|";
            if (!marker.StartsWith(prefix, StringComparison.Ordinal))
                return;

            IsExport = true;
            var exportKey = marker.Substring(prefix.Length).Trim();

            for (var column = 1; column < Sheet.ColumnCount; column++)
            {
                var key = Sheet.Text(0, column).Trim();
                if (key.Length > 0)
                    ColumnKeys[column] = key;
            }

            if (meta == null)
                return;

            for (var row = 0; row < meta.RowCount; row++)
            {
                if (meta.Text(row, 1).Trim() != exportKey)
                    continue;

                var kind = meta.Text(row, 0).Trim();
                if (kind == "EXPORT")
                {
                    ScheduleName = meta.Text(row, 2);
                    ScheduleUniqueId = meta.Text(row, 3).Trim();
                }
                else if (kind == "ROW")
                {
                    var joined = string.Concat(Enumerable.Range(3, Math.Max(0, meta.ColumnCount - 3)).Select(column => meta.Text(row, column)));
                    var ids = joined.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(id => id.Trim()).Where(id => id.Length > 0).ToList();
                    if (ids.Count > 0)
                        _elements[meta.Text(row, 2).Trim()] = ids;
                }
            }
        }
    }
}
