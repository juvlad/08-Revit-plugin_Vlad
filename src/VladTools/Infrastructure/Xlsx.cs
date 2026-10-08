using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>The handful of cell looks an exported schedule uses — one entry each in styles.xml.</summary>
    internal enum XlsxStyle
    {
        Plain = 0,
        Title = 1,
        Heading = 2,
        Cell = 3,
        Group = 4
    }

    /// <summary>A rectangle of cells, zero-based and inclusive.</summary>
    internal struct XlsxRange
    {
        public XlsxRange(int firstRow, int firstColumn, int lastRow, int lastColumn)
        {
            FirstRow = firstRow;
            FirstColumn = firstColumn;
            LastRow = lastRow;
            LastColumn = lastColumn;
        }

        public int FirstRow { get; }
        public int FirstColumn { get; }
        public int LastRow { get; }
        public int LastColumn { get; }

        public string Reference => Xlsx.CellName(FirstRow, FirstColumn) + ":" + Xlsx.CellName(LastRow, LastColumn);
    }

    /// <summary>
    /// One worksheet to be written: text cells with a look each, plus the few sheet-level settings an
    /// exported schedule needs. Every cell is text — see <see cref="XlsxWriter"/> for why.
    /// </summary>
    internal sealed class XlsxSheet
    {
        private readonly SortedDictionary<int, SortedDictionary<int, KeyValuePair<string, XlsxStyle>>> _rows =
            new SortedDictionary<int, SortedDictionary<int, KeyValuePair<string, XlsxStyle>>>();

        public XlsxSheet(string name)
        {
            Name = name;
        }

        public string Name { get; set; }

        /// <summary>Hidden from the tabs — the export's bookkeeping sheet.</summary>
        public bool IsHidden { get; set; }

        /// <summary>Rows kept on screen while scrolling (the title and the headings).</summary>
        public int FrozenRows { get; set; }

        /// <summary>Column widths in Excel's own unit — roughly the width of one digit.</summary>
        public Dictionary<int, double> ColumnWidths { get; } = new Dictionary<int, double>();

        public HashSet<int> HiddenColumns { get; } = new HashSet<int>();

        public HashSet<int> HiddenRows { get; } = new HashSet<int>();

        public List<XlsxRange> Merges { get; } = new List<XlsxRange>();

        /// <summary>The range the filter buttons sit on, headings row first; null for none.</summary>
        public XlsxRange? AutoFilter { get; set; }

        public IEnumerable<KeyValuePair<int, SortedDictionary<int, KeyValuePair<string, XlsxStyle>>>> Rows => _rows;

        public int LastRow => _rows.Count == 0 ? -1 : _rows.Keys.Max();

        public void Set(int row, int column, string text, XlsxStyle style = XlsxStyle.Cell)
        {
            if (!_rows.TryGetValue(row, out var cells))
            {
                cells = new SortedDictionary<int, KeyValuePair<string, XlsxStyle>>();
                _rows[row] = cells;
            }

            cells[column] = new KeyValuePair<string, XlsxStyle>(text ?? string.Empty, style);
        }
    }

    /// <summary>Small shared pieces of the format: cell names and the names Excel accepts for a sheet.</summary>
    internal static class Xlsx
    {
        /// <summary>The most text one Excel cell holds.</summary>
        public const int MaxCellText = 32767;

        public static string ColumnName(int column)
        {
            var name = string.Empty;
            var value = column + 1;

            while (value > 0)
            {
                var remainder = (value - 1) % 26;
                name = (char)('A' + remainder) + name;
                value = (value - 1) / 26;
            }

            return name;
        }

        public static string CellName(int row, int column)
        {
            return ColumnName(column) + (row + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A sheet name Excel will open: at most 31 characters, none of <c>[ ] : * ? / \</c>, not empty,
        /// and not already taken in the book (compared ignoring case, as Excel does).
        /// </summary>
        public static string SheetName(string wanted, ICollection<string> taken)
        {
            var invalid = new[] { '[', ']', ':', '*', '?', '/', '\\' };
            var clean = new string((wanted ?? string.Empty).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray())
                .Trim().Trim('\'');

            if (clean.Length == 0)
                clean = "Schedule";

            var name = Cut(clean, 31);
            for (var number = 2; taken.Any(other => string.Equals(other, name, StringComparison.OrdinalIgnoreCase)); number++)
            {
                var suffix = " (" + number + ")";
                name = Cut(clean, 31 - suffix.Length) + suffix;
            }

            taken.Add(name);
            return name;
        }

        private static string Cut(string text, int length)
        {
            return text.Length <= length ? text : text.Substring(0, length).TrimEnd();
        }
    }

    /// <summary>
    /// Writes an .xlsx — the Open XML spreadsheet format, which is a zip of XML parts — with nothing
    /// but the framework's own zip and XML classes.
    ///
    /// A third-party Excel library was the obvious alternative and is deliberately not used: the
    /// add-in lives inside Revit's process, where every extra assembly is one more way of failing to
    /// load or of clashing with another add-in's copy of the same library — the same reasoning as the
    /// add-in's own JSON parser. What an exported schedule needs is small: text cells, five looks,
    /// column widths, a frozen heading, merged title cells, a filter, a hidden column and a hidden sheet.
    ///
    /// Every value is written as text, in a text-formatted cell, exactly as the schedule shows it.
    /// Turning "1 200" or "001" into numbers would depend on the reader's locale and lose leading
    /// zeros, and the import compares what comes back against what the schedule shows — text is the
    /// one form that survives the round trip unchanged.
    /// </summary>
    internal static class XlsxWriter
    {
        private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelationshipNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string PackageRelationshipNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        private const string ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
        private const string XmlNs = "http://www.w3.org/XML/1998/namespace";

        public static void Write(string path, IReadOnlyList<XlsxSheet> sheets)
        {
            var firstVisible = -1;
            for (var i = 0; i < sheets.Count && firstVisible < 0; i++)
            {
                if (!sheets[i].IsHidden)
                    firstVisible = i;
            }

            if (firstVisible < 0)
                throw new ArgumentException("A workbook needs at least one visible sheet.");

            byte[] bytes;

            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    Entry(zip, "[Content_Types].xml", writer => ContentTypes(writer, sheets.Count));
                    Entry(zip, "_rels/.rels", RootRelationships);
                    Entry(zip, "xl/workbook.xml", writer => Workbook(writer, sheets, firstVisible));
                    Entry(zip, "xl/_rels/workbook.xml.rels", writer => WorkbookRelationships(writer, sheets.Count));
                    TextEntry(zip, "xl/styles.xml", Styles());

                    for (var i = 0; i < sheets.Count; i++)
                    {
                        var sheet = sheets[i];
                        var selected = i == firstVisible;
                        Entry(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", writer => Worksheet(writer, sheet, selected));
                    }
                }

                bytes = memory.ToArray();
            }

            // Built in memory first: a file open in Excel refuses the write at the very start, and the
            // copy already on disk is left whole rather than half-overwritten.
            File.WriteAllBytes(path, bytes);
        }

        private static void Entry(ZipArchive zip, string name, Action<XmlWriter> write)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);

            using (var stream = entry.Open())
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            {
                writer.WriteStartDocument(true);
                write(writer);
                writer.WriteEndDocument();
            }
        }

        /// <summary>A part written as ready text — the fixed style sheet, which has nothing to escape.</summary>
        private static void TextEntry(ZipArchive zip, string name, string xml)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);

            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                writer.Write(xml);
            }
        }

        private static void ContentTypes(XmlWriter writer, int sheetCount)
        {
            writer.WriteStartElement("Types", ContentTypesNs);

            Default(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Default(writer, "xml", "application/xml");
            Override(writer, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Override(writer, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");

            for (var i = 1; i <= sheetCount; i++)
                Override(writer, "/xl/worksheets/sheet" + i + ".xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");

            writer.WriteEndElement();
        }

        private static void Default(XmlWriter writer, string extension, string type)
        {
            writer.WriteStartElement("Default", ContentTypesNs);
            writer.WriteAttributeString("Extension", extension);
            writer.WriteAttributeString("ContentType", type);
            writer.WriteEndElement();
        }

        private static void Override(XmlWriter writer, string part, string type)
        {
            writer.WriteStartElement("Override", ContentTypesNs);
            writer.WriteAttributeString("PartName", part);
            writer.WriteAttributeString("ContentType", type);
            writer.WriteEndElement();
        }

        private static void RootRelationships(XmlWriter writer)
        {
            writer.WriteStartElement("Relationships", PackageRelationshipNs);
            Relationship(writer, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
            writer.WriteEndElement();
        }

        private static void WorkbookRelationships(XmlWriter writer, int sheetCount)
        {
            writer.WriteStartElement("Relationships", PackageRelationshipNs);

            for (var i = 1; i <= sheetCount; i++)
                Relationship(writer, "rId" + i, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet" + i + ".xml");

            Relationship(writer, "rId" + (sheetCount + 1), "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
            writer.WriteEndElement();
        }

        private static void Relationship(XmlWriter writer, string id, string type, string target)
        {
            writer.WriteStartElement("Relationship", PackageRelationshipNs);
            writer.WriteAttributeString("Id", id);
            writer.WriteAttributeString("Type", type);
            writer.WriteAttributeString("Target", target);
            writer.WriteEndElement();
        }

        private static void Workbook(XmlWriter writer, IReadOnlyList<XlsxSheet> sheets, int firstVisible)
        {
            writer.WriteStartElement("workbook", MainNs);
            writer.WriteAttributeString("xmlns", "r", null, RelationshipNs);

            writer.WriteStartElement("bookViews", MainNs);
            writer.WriteStartElement("workbookView", MainNs);
            writer.WriteAttributeString("activeTab", firstVisible.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("sheets", MainNs);
            for (var i = 0; i < sheets.Count; i++)
            {
                writer.WriteStartElement("sheet", MainNs);
                writer.WriteAttributeString("name", sheets[i].Name);
                writer.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                if (sheets[i].IsHidden)
                    writer.WriteAttributeString("state", "hidden");
                writer.WriteAttributeString("id", RelationshipNs, "rId" + (i + 1));
                writer.WriteEndElement();
            }
            writer.WriteEndElement();

            // Excel keeps the filter range under this reserved name; without it some versions drop the
            // filter buttons when the file is saved again.
            var filtered = sheets.Select((sheet, index) => new { sheet, index }).Where(item => item.sheet.AutoFilter.HasValue).ToList();
            if (filtered.Count > 0)
            {
                writer.WriteStartElement("definedNames", MainNs);
                foreach (var item in filtered)
                {
                    var range = item.sheet.AutoFilter.Value;
                    writer.WriteStartElement("definedName", MainNs);
                    writer.WriteAttributeString("name", "_xlnm._FilterDatabase");
                    writer.WriteAttributeString("localSheetId", item.index.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("hidden", "1");
                    writer.WriteString("'" + item.sheet.Name.Replace("'", "''") + "'!" + Absolute(range));
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        private static string Absolute(XlsxRange range)
        {
            return "$" + Xlsx.ColumnName(range.FirstColumn) + "$" + (range.FirstRow + 1) + ":$" +
                   Xlsx.ColumnName(range.LastColumn) + "$" + (range.LastRow + 1);
        }

        /// <summary>
        /// Five looks, in the order of <see cref="XlsxStyle"/>. Every look but the first carries the
        /// text number format ("@"), so a value typed over in Excel stays text too — "001" is not
        /// quietly turned into 1 on the way back.
        /// </summary>
        private static string Styles()
        {
            return
                "<styleSheet xmlns=\"" + MainNs + "\">" +
                "<fonts count=\"3\">" +
                "<font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
                "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
                "<font><b/><sz val=\"13\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
                "</fonts>" +
                "<fills count=\"3\">" +
                "<fill><patternFill patternType=\"none\"/></fill>" +
                "<fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDEBF7\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "</fills>" +
                "<borders count=\"2\">" +
                "<border><left/><right/><top/><bottom/><diagonal/></border>" +
                "<border>" + Side("left") + Side("right") + Side("top") + Side("bottom") + "<diagonal/></border>" +
                "</borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"5\">" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"49\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyAlignment=\"1\">" +
                "<alignment vertical=\"center\"/></xf>" +
                "<xf numFmtId=\"49\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\">" +
                "<alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
                "<xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\" applyAlignment=\"1\">" +
                "<alignment vertical=\"top\" wrapText=\"1\"/></xf>" +
                "<xf numFmtId=\"49\" fontId=\"1\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\">" +
                "<alignment vertical=\"top\" wrapText=\"1\"/></xf>" +
                "</cellXfs>" +
                "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                "</styleSheet>";
        }

        private static string Side(string name)
        {
            return "<" + name + " style=\"thin\"><color rgb=\"FFA6A6A6\"/></" + name + ">";
        }

        private static void Worksheet(XmlWriter writer, XlsxSheet sheet, bool selected)
        {
            writer.WriteStartElement("worksheet", MainNs);
            writer.WriteAttributeString("xmlns", "r", null, RelationshipNs);

            writer.WriteStartElement("sheetViews", MainNs);
            writer.WriteStartElement("sheetView", MainNs);
            if (selected)
                writer.WriteAttributeString("tabSelected", "1");
            writer.WriteAttributeString("workbookViewId", "0");

            if (sheet.FrozenRows > 0)
            {
                writer.WriteStartElement("pane", MainNs);
                writer.WriteAttributeString("ySplit", sheet.FrozenRows.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("topLeftCell", "A" + (sheet.FrozenRows + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("activePane", "bottomLeft");
                writer.WriteAttributeString("state", "frozen");
                writer.WriteEndElement();

                writer.WriteStartElement("selection", MainNs);
                writer.WriteAttributeString("pane", "bottomLeft");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("sheetFormatPr", MainNs);
            writer.WriteAttributeString("defaultRowHeight", "15");
            writer.WriteEndElement();

            var columns = sheet.ColumnWidths.Keys.Union(sheet.HiddenColumns).OrderBy(column => column).ToList();
            if (columns.Count > 0)
            {
                writer.WriteStartElement("cols", MainNs);
                foreach (var column in columns)
                {
                    var number = (column + 1).ToString(CultureInfo.InvariantCulture);
                    writer.WriteStartElement("col", MainNs);
                    writer.WriteAttributeString("min", number);
                    writer.WriteAttributeString("max", number);
                    writer.WriteAttributeString("width", (sheet.ColumnWidths.TryGetValue(column, out var width) ? width : 10).ToString("0.##", CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("customWidth", "1");
                    if (sheet.HiddenColumns.Contains(column))
                        writer.WriteAttributeString("hidden", "1");
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            writer.WriteStartElement("sheetData", MainNs);

            var rowNumbers = sheet.Rows.Select(row => row.Key).Union(sheet.HiddenRows).OrderBy(row => row).ToList();
            var cellsByRow = sheet.Rows.ToDictionary(row => row.Key, row => row.Value);

            foreach (var row in rowNumbers)
            {
                writer.WriteStartElement("row", MainNs);
                writer.WriteAttributeString("r", (row + 1).ToString(CultureInfo.InvariantCulture));
                if (sheet.HiddenRows.Contains(row))
                    writer.WriteAttributeString("hidden", "1");

                if (cellsByRow.TryGetValue(row, out var cells))
                {
                    foreach (var cell in cells)
                        Cell(writer, row, cell.Key, cell.Value.Key, cell.Value.Value);
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();

            if (sheet.AutoFilter.HasValue)
            {
                writer.WriteStartElement("autoFilter", MainNs);
                writer.WriteAttributeString("ref", sheet.AutoFilter.Value.Reference);
                writer.WriteEndElement();
            }

            if (sheet.Merges.Count > 0)
            {
                writer.WriteStartElement("mergeCells", MainNs);
                writer.WriteAttributeString("count", sheet.Merges.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var merge in sheet.Merges)
                {
                    writer.WriteStartElement("mergeCell", MainNs);
                    writer.WriteAttributeString("ref", merge.Reference);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            writer.WriteStartElement("pageMargins", MainNs);
            writer.WriteAttributeString("left", "0.5");
            writer.WriteAttributeString("right", "0.5");
            writer.WriteAttributeString("top", "0.75");
            writer.WriteAttributeString("bottom", "0.75");
            writer.WriteAttributeString("header", "0.3");
            writer.WriteAttributeString("footer", "0.3");
            writer.WriteEndElement();

            writer.WriteEndElement();
        }

        private static void Cell(XmlWriter writer, int row, int column, string text, XlsxStyle style)
        {
            writer.WriteStartElement("c", MainNs);
            writer.WriteAttributeString("r", Xlsx.CellName(row, column));
            if (style != XlsxStyle.Plain)
                writer.WriteAttributeString("s", ((int)style).ToString(CultureInfo.InvariantCulture));

            var clean = Clean(text);
            if (clean.Length > 0)
            {
                // An inline string rather than the shared-string table: one part fewer to write, and
                // Excel converts it to its own form the first time the file is saved anyway.
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is", MainNs);
                writer.WriteStartElement("t", MainNs);
                writer.WriteAttributeString("xml", "space", XmlNs, "preserve");
                writer.WriteString(clean);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        /// <summary>Drops the characters XML cannot carry at all, and keeps within Excel's cell limit.</summary>
        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var builder = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var character = text[i];

                if (char.IsHighSurrogate(character) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(character).Append(text[i + 1]);
                    i++;
                    continue;
                }

                if (char.IsSurrogate(character))
                    continue;

                if (character == '\r')
                    continue;

                if (character < 0x20 && character != '\t' && character != '\n')
                    continue;

                if (character == '￾' || character == '￿')
                    continue;

                builder.Append(character);
            }

            return builder.Length > Xlsx.MaxCellText ? builder.ToString(0, Xlsx.MaxCellText) : builder.ToString();
        }
    }

    /// <summary>One cell as read back: its text as Excel shows it, and the number itself where it is one.</summary>
    internal sealed class XlsxValue
    {
        public static readonly XlsxValue Empty = new XlsxValue(string.Empty, null);

        public XlsxValue(string text, double? number)
        {
            Text = text ?? string.Empty;
            Number = number;
        }

        public string Text { get; }

        /// <summary>
        /// The value of a numeric cell — what was typed, before any display rounding. Null for text,
        /// dates and everything else.
        /// </summary>
        public double? Number { get; }

        public bool IsEmpty => Text.Trim().Length == 0;
    }

    /// <summary>One worksheet as read back: a sparse grid of values, zero-based.</summary>
    internal sealed class XlsxReadSheet
    {
        private readonly Dictionary<int, Dictionary<int, XlsxValue>> _rows = new Dictionary<int, Dictionary<int, XlsxValue>>();

        public XlsxReadSheet(string name, bool isHidden)
        {
            Name = name ?? string.Empty;
            IsHidden = isHidden;
        }

        public string Name { get; }

        public bool IsHidden { get; }

        /// <summary>One past the last row holding anything.</summary>
        public int RowCount { get; private set; }

        /// <summary>One past the last column holding anything.</summary>
        public int ColumnCount { get; private set; }

        public bool IsEmpty => _rows.Count == 0;

        public XlsxValue Get(int row, int column)
        {
            return _rows.TryGetValue(row, out var cells) && cells.TryGetValue(column, out var value) ? value : XlsxValue.Empty;
        }

        public string Text(int row, int column)
        {
            return Get(row, column).Text;
        }

        public bool IsRowEmpty(int row)
        {
            return !_rows.TryGetValue(row, out var cells) || cells.Values.All(value => value.IsEmpty);
        }

        internal void Put(int row, int column, XlsxValue value)
        {
            if (value.Text.Length == 0)
                return;

            if (!_rows.TryGetValue(row, out var cells))
            {
                cells = new Dictionary<int, XlsxValue>();
                _rows[row] = cells;
            }

            cells[column] = value;
            RowCount = Math.Max(RowCount, row + 1);
            ColumnCount = Math.Max(ColumnCount, column + 1);
        }
    }

    /// <summary>
    /// Reads an .xlsx or .xlsm back into plain values — the counterpart of <see cref="XlsxWriter"/>,
    /// and on the same terms: framework classes only.
    ///
    /// It reads what Excel and other spreadsheet programs actually save, not only what the writer
    /// produced: shared strings and inline strings, numbers, booleans, formula results, dates (told
    /// apart from numbers by the cell's number format), and the "strict" flavour of the format, whose
    /// namespaces differ — elements are matched by local name for that reason.
    /// </summary>
    internal static class XlsxReader
    {
        private static readonly int[] BuiltInDateFormats = { 14, 15, 16, 17, 18, 19, 20, 21, 22, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 45, 46, 47, 50, 51, 52, 53, 54, 55, 56, 57, 58 };

        public static IReadOnlyList<XlsxReadSheet> Read(string path)
        {
            // Excel keeps an open workbook locked for writing but lets others read it, so the file can
            // be imported without closing it first.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var signature = new byte[4];
                var read = stream.Read(signature, 0, 4);
                if (read == 4 && signature[0] == 0xD0 && signature[1] == 0xCF && signature[2] == 0x11 && signature[3] == 0xE0)
                    throw new InvalidDataException("This is an old-format Excel file (.xls). Open it in Excel and save it as " +
                                                   "\"Excel Workbook (*.xlsx)\", then import that.");

                stream.Position = 0;

                ZipArchive zip;
                try
                {
                    zip = new ZipArchive(stream, ZipArchiveMode.Read);
                }
                catch (InvalidDataException)
                {
                    throw new InvalidDataException("This is not an Excel workbook (.xlsx). Save it from Excel as " +
                                                   "\"Excel Workbook (*.xlsx)\" and import that.");
                }

                using (zip)
                {
                    return ReadBook(zip);
                }
            }
        }

        private static IReadOnlyList<XlsxReadSheet> ReadBook(ZipArchive zip)
        {
            var workbookPath = "xl/workbook.xml";

            var rootRelationships = Load(zip, "_rels/.rels");
            if (rootRelationships != null)
            {
                var office = Children(rootRelationships.Root, "Relationship")
                    .FirstOrDefault(element => ((string)element.Attribute("Type") ?? string.Empty).EndsWith("/officeDocument", StringComparison.Ordinal));
                if (office != null)
                    workbookPath = Resolve(string.Empty, (string)office.Attribute("Target"));
            }

            var workbook = Load(zip, workbookPath) ?? throw new InvalidDataException("The workbook has no list of sheets — the file is damaged.");
            var folder = workbookPath.Contains("/") ? workbookPath.Substring(0, workbookPath.LastIndexOf('/')) : string.Empty;
            var fileName = workbookPath.Substring(workbookPath.LastIndexOf('/') + 1);

            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            string sharedStringsPath = null;
            string stylesPath = null;

            var relationships = Load(zip, (folder.Length > 0 ? folder + "/" : string.Empty) + "_rels/" + fileName + ".rels");
            if (relationships != null)
            {
                foreach (var relationship in Children(relationships.Root, "Relationship"))
                {
                    var id = (string)relationship.Attribute("Id") ?? string.Empty;
                    var type = (string)relationship.Attribute("Type") ?? string.Empty;
                    var target = Resolve(folder, (string)relationship.Attribute("Target"));

                    targets[id] = target;
                    if (type.EndsWith("/sharedStrings", StringComparison.Ordinal))
                        sharedStringsPath = target;
                    else if (type.EndsWith("/styles", StringComparison.Ordinal))
                        stylesPath = target;
                }
            }

            var sharedStrings = ReadSharedStrings(zip, sharedStringsPath);
            var dateStyles = ReadDateStyles(zip, stylesPath);

            var sheets = new List<XlsxReadSheet>();
            var sheetList = Children(workbook.Root, "sheets").FirstOrDefault();
            if (sheetList == null)
                return sheets;

            foreach (var element in Children(sheetList, "sheet"))
            {
                var name = (string)element.Attribute("name") ?? string.Empty;
                var state = (string)element.Attribute("state") ?? string.Empty;
                var relationshipId = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id" && attribute.Name.Namespace != XNamespace.None);

                if (relationshipId == null || !targets.TryGetValue(relationshipId.Value, out var target))
                    continue;

                var entry = Find(zip, target);
                if (entry == null)
                    continue;

                var sheet = new XlsxReadSheet(name, state == "hidden" || state == "veryHidden");
                using (var stream = entry.Open())
                    ReadSheet(stream, sheet, sharedStrings, dateStyles);

                sheets.Add(sheet);
            }

            return sheets;
        }

        private static List<string> ReadSharedStrings(ZipArchive zip, string path)
        {
            var result = new List<string>();
            var document = path == null ? null : Load(zip, path);
            if (document == null)
                return result;

            foreach (var item in Children(document.Root, "si"))
                result.Add(RichText(item));

            return result;
        }

        /// <summary>The text of a string item: plain or in runs; the phonetic hints of East Asian text are left out.</summary>
        private static string RichText(XElement item)
        {
            var builder = new StringBuilder();
            foreach (var text in item.Descendants().Where(element => element.Name.LocalName == "t"))
            {
                if (text.Ancestors().Any(ancestor => ancestor.Name.LocalName == "rPh"))
                    continue;
                builder.Append(text.Value);
            }

            return builder.ToString();
        }

        /// <summary>The style indexes whose number format shows a date or a time.</summary>
        private static HashSet<int> ReadDateStyles(ZipArchive zip, string path)
        {
            var result = new HashSet<int>();
            var document = path == null ? null : Load(zip, path);
            if (document == null)
                return result;

            var custom = new Dictionary<int, string>();
            var formats = Children(document.Root, "numFmts").FirstOrDefault();
            if (formats != null)
            {
                foreach (var format in Children(formats, "numFmt"))
                {
                    if (int.TryParse((string)format.Attribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                        custom[id] = (string)format.Attribute("formatCode") ?? string.Empty;
                }
            }

            var cellFormats = Children(document.Root, "cellXfs").FirstOrDefault();
            if (cellFormats == null)
                return result;

            var index = 0;
            foreach (var format in Children(cellFormats, "xf"))
            {
                if (int.TryParse((string)format.Attribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                    (BuiltInDateFormats.Contains(id) || (custom.TryGetValue(id, out var code) && IsDateCode(code))))
                {
                    result.Add(index);
                }

                index++;
            }

            return result;
        }

        /// <summary>A custom number format that shows a date: a year, a day or an hour:second, outside quotes and brackets.</summary>
        private static bool IsDateCode(string code)
        {
            var builder = new StringBuilder();
            var quoted = false;
            var bracketed = false;

            for (var i = 0; i < code.Length; i++)
            {
                var character = code[i];
                if (character == '"')
                {
                    quoted = !quoted;
                    continue;
                }
                if (quoted)
                    continue;
                if (character == '\\' || character == '_' || character == '*')
                {
                    i++;
                    continue;
                }
                if (character == '[')
                {
                    bracketed = true;
                    continue;
                }
                if (character == ']')
                {
                    bracketed = false;
                    continue;
                }
                if (!bracketed)
                    builder.Append(char.ToLowerInvariant(character));
            }

            var text = builder.ToString();
            return text.Contains("y") || text.Contains("d") || text.Contains("h:") || text.Contains(":s");
        }

        private static void ReadSheet(Stream stream, XlsxReadSheet sheet, IReadOnlyList<string> sharedStrings, HashSet<int> dateStyles)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true };

            using (var reader = XmlReader.Create(stream, settings))
            {
                var row = -1;
                var nextColumn = 0;

                reader.MoveToContent();
                while (!reader.EOF)
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
                    {
                        var number = reader.GetAttribute("r");
                        row = int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed - 1 : row + 1;
                        nextColumn = 0;
                        reader.Read();
                        continue;
                    }

                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
                    {
                        var cell = (XElement)XNode.ReadFrom(reader);
                        var reference = (string)cell.Attribute("r");

                        int cellRow = row, column = nextColumn;
                        if (!string.IsNullOrEmpty(reference))
                            ParseReference(reference, ref cellRow, ref column);

                        sheet.Put(Math.Max(cellRow, 0), column, Value(cell, sharedStrings, dateStyles));
                        nextColumn = column + 1;
                        continue;
                    }

                    reader.Read();
                }
            }
        }

        private static XlsxValue Value(XElement cell, IReadOnlyList<string> sharedStrings, HashSet<int> dateStyles)
        {
            var type = (string)cell.Attribute("t") ?? "n";
            var raw = Children(cell, "v").FirstOrDefault()?.Value ?? string.Empty;

            switch (type)
            {
                case "s":
                    return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count
                        ? new XlsxValue(sharedStrings[index], null)
                        : XlsxValue.Empty;

                case "inlineStr":
                    var inline = Children(cell, "is").FirstOrDefault();
                    return new XlsxValue(inline == null ? string.Empty : RichText(inline), null);

                case "b":
                    return new XlsxValue(raw == "1" ? "TRUE" : "FALSE", null);

                case "str":
                case "e":
                    return new XlsxValue(raw, null);

                case "d":
                    return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp)
                        ? new XlsxValue(DateText(stamp), null)
                        : new XlsxValue(raw, null);

                default:
                    if (raw.Length == 0 || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        return new XlsxValue(raw, null);

                    var style = int.TryParse((string)cell.Attribute("s"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
                    if (dateStyles.Contains(style) && number > -657435 && number < 2958466)
                        return new XlsxValue(DateText(DateTime.FromOADate(number)), null);

                    // "G15" rather than the shortest round trip: 0.1 + 0.2 typed in Excel comes back
                    // as 0.30000000000000004 otherwise. The culture is the user's, the same one Excel showed it in.
                    return new XlsxValue(number.ToString("G15", CultureInfo.CurrentCulture), number);
            }
        }

        private static string DateText(DateTime stamp)
        {
            return stamp.TimeOfDay == TimeSpan.Zero
                ? stamp.ToString("d", CultureInfo.CurrentCulture)
                : stamp.ToString("g", CultureInfo.CurrentCulture);
        }

        private static void ParseReference(string reference, ref int row, ref int column)
        {
            var letters = 0;
            var columnValue = 0;

            while (letters < reference.Length && char.IsLetter(reference[letters]))
            {
                columnValue = columnValue * 26 + (char.ToUpperInvariant(reference[letters]) - 'A' + 1);
                letters++;
            }

            if (letters > 0)
                column = columnValue - 1;

            if (int.TryParse(reference.Substring(letters), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowValue))
                row = rowValue - 1;
        }

        private static IEnumerable<XElement> Children(XElement parent, string localName)
        {
            return parent == null ? Enumerable.Empty<XElement>() : parent.Elements().Where(element => element.Name.LocalName == localName);
        }

        private static XDocument Load(ZipArchive zip, string path)
        {
            var entry = Find(zip, path);
            if (entry == null)
                return null;

            using (var stream = entry.Open())
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                return XDocument.Load(reader);
        }

        private static ZipArchiveEntry Find(ZipArchive zip, string path)
        {
            return zip.Entries.FirstOrDefault(entry => string.Equals(entry.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A relationship target made absolute within the package: "/xl/…" as is, "worksheets/…" against its folder, "../" walked up.</summary>
        private static string Resolve(string folder, string target)
        {
            if (string.IsNullOrEmpty(target))
                return string.Empty;

            target = target.Replace('\\', '/');
            if (target.StartsWith("/", StringComparison.Ordinal))
                return target.TrimStart('/');

            var parts = new List<string>(folder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
            foreach (var part in target.Split('/'))
            {
                if (part == "..")
                {
                    if (parts.Count > 0)
                        parts.RemoveAt(parts.Count - 1);
                }
                else if (part != "." && part.Length > 0)
                {
                    parts.Add(part);
                }
            }

            return string.Join("/", parts);
        }
    }
}
