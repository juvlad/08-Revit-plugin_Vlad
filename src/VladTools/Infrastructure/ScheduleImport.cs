using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>One value the import will write: a cell of the file, onto the elements (or types) of its schedule row.</summary>
    internal sealed class PlannedWrite
    {
        public PlannedWrite(
            ExcelPreviewRow row,
            ScheduleColumn column,
            ScheduleDataRow data,
            IReadOnlyList<ElementId> targets,
            object value,
            string oldText,
            string newText)
        {
            Row = row;
            Column = column;
            Data = data;
            Targets = targets;
            Value = value;
            OldText = oldText;
            NewText = newText;
        }

        /// <summary>The preview line — its check box decides whether the write happens.</summary>
        public ExcelPreviewRow Row { get; }

        public ScheduleColumn Column { get; }

        public ScheduleDataRow Data { get; }

        /// <summary>The elements whose parameter is set: the row's elements, or their types for a type parameter.</summary>
        public IReadOnlyList<ElementId> Targets { get; }

        /// <summary>A string, an int or a double in Revit's internal units — whatever the parameter stores.</summary>
        public object Value { get; }

        public string OldText { get; }

        public string NewText { get; }
    }

    /// <summary>A preview and the writes behind it — the window sees the first, the command carries out the second.</summary>
    internal sealed class ScheduleImportPlan
    {
        public ScheduleImportPlan(ScheduleTable table, ExcelImportPreview preview, IReadOnlyList<PlannedWrite> writes)
        {
            Table = table;
            Preview = preview;
            Writes = writes;
        }

        public ScheduleTable Table { get; }

        public ExcelImportPreview Preview { get; }

        public IReadOnlyList<PlannedWrite> Writes { get; }
    }

    /// <summary>
    /// Compares a sheet of Excel with a schedule and works out, cell by cell, what an import would
    /// change — before anything is changed. The preview the window shows and the writes the command
    /// carries out come out of one pass here, so the table can never promise one thing while the
    /// button does another.
    ///
    /// A cell is a change only when it changes the <em>value</em>, not merely the text: "1200" typed
    /// over "1 200 mm" is the same length, and rewriting it would round off whatever the display hid.
    /// So a new number is parsed the way the schedule column formats it, formatted back the same way,
    /// and compared with what the schedule shows.
    ///
    /// What cannot be written is shown, not dropped: a calculated column, a read-only parameter, an
    /// element inside a model group, a total over a grouped row, two lines giving one type two values —
    /// each such cell is coloured and says why in its tooltip.
    /// </summary>
    internal static class ScheduleImportPlanner
    {
        private static readonly string[] KeyHints = { "mark", "марка", "number", "номер", "позиция", "position", "key", "ключ", "код", "code", "id" };

        private sealed class Pair
        {
            public ScheduleDataRow Data;
            public int Line = -1;
            public string Note;
            public string Problem;
        }

        private sealed class CellPlan
        {
            public ExcelCellState State;
            public string Text;
            public string Tooltip;
            public IReadOnlyList<ElementId> Targets;
            public object Value;
            public string OldText;
            public string NewText;
            public int Line;
        }

        private sealed class Resolution
        {
            public readonly List<ElementId> Targets = new List<ElementId>();
            public StorageType Storage = StorageType.None;
            public ForgeTypeId Spec;
            public string Problem;
            public string Note;
        }

        public static ScheduleImportPlan Plan(Document doc, ScheduleTable table, ExcelSheetSource source, ExcelImportRequest request)
        {
            var effective = request.Copy();
            var sheet = source.Sheet;
            var notes = new List<string>();

            int headingRow;
            if (effective.FirstDataRow > 0)
            {
                headingRow = effective.FirstDataRow - 2;
            }
            else
            {
                headingRow = DetectHeadingRow(source, table);
                effective.FirstDataRow = headingRow + 2;

                if (headingRow < 0 && !source.HasIds)
                    notes.Add("No line of the sheet reads like the schedule's column headings, so the data is taken from row 1 and the " +
                              "columns in their order. If the sheet has headings, set \"First data row\".");
            }

            var sourceColumns = SourceColumns(source, headingRow);
            var map = ColumnMap(source, table, headingRow, effective.ColumnMap);
            effective.ColumnMap = map;

            var lines = DataLines(source, effective.FirstDataRow - 1, out var skipped);
            if (skipped > 0)
                notes.Add(skipped + (skipped == 1 ? " line" : " lines") + " of the sheet carry no element and are skipped: headings, group lines " +
                          "and totals — or lines added in Excel (the import never creates elements).");

            var mode = effective.MatchMode ?? (source.HasIds ? ExcelMatchMode.ElementIds : ExcelMatchMode.RowOrder);
            if (mode == ExcelMatchMode.ElementIds && !source.HasIds)
            {
                notes.Add("The sheet carries no element IDs — it was not exported by this button — so the rows are paired by their order.");
                mode = ExcelMatchMode.RowOrder;
            }
            effective.MatchMode = mode;

            var keyCandidates = KeyCandidates(table);
            if (mode == ExcelMatchMode.KeyColumn && !keyCandidates.Contains(effective.KeyColumn))
                effective.KeyColumn = PickKey(table, keyCandidates, map);

            var keyColumn = mode == ExcelMatchMode.KeyColumn ? effective.KeyColumn : -1;

            List<Pair> pairs;
            switch (mode)
            {
                case ExcelMatchMode.ElementIds:
                    pairs = MatchByElements(doc, table, source, lines, notes);
                    break;
                case ExcelMatchMode.KeyColumn:
                    pairs = MatchByKey(table, sheet, lines, keyColumn, map, notes);
                    break;
                default:
                    pairs = MatchByOrder(table, lines);
                    break;
            }

            if (table.TieProblem != null)
                notes.Insert(0, "Nothing can be written into this schedule: " + table.TieProblem + ".");
            else if (table.TiedRows < table.Rows.Count)
                notes.Add((table.Rows.Count - table.TiedRows) + " of the schedule's " + table.Rows.Count + " rows could not be tied to their " +
                          "elements — a change on them is shown but cannot be written (the tooltip says why).");

            if (source.IsExport && source.ScheduleName.Length > 0 && !string.Equals(source.ScheduleName, table.Name, StringComparison.Ordinal))
                notes.Add("The sheet was exported from the schedule \"" + source.ScheduleName + "\". Its columns are found by what they show, " +
                          "not by their position.");

            var missing = table.Columns.Where(column => map[column.Index] < 0 && column.ReadOnlyReason == null).Select(column => "\"" + column.Heading + "\"").ToList();
            if (missing.Count > 0 && lines.Count > 0)
                notes.Add("Not found in the sheet, so left as they are: " + string.Join(", ", missing) +
                          ". A column can be picked by hand in the drop-down under its heading.");

            var context = new Context(doc, table, effective.EmptyClears);
            var plans = new Dictionary<Pair, CellPlan[]>();

            foreach (var pair in pairs.Where(pair => pair.Data != null && pair.Line >= 0))
            {
                var cells = new CellPlan[table.Columns.Count];
                foreach (var column in table.Columns)
                {
                    var from = map[column.Index];
                    var old = ScheduleTable.Clean(pair.Data.Cells[column.Index]);

                    if (column.Index == keyColumn)
                        cells[column.Index] = new CellPlan { State = ExcelCellState.Key, Text = old, Tooltip = "The rows are paired by this column" };
                    else if (from < 0)
                        cells[column.Index] = new CellPlan { State = ExcelCellState.Same, Text = old };
                    else
                        cells[column.Index] = context.Evaluate(column, pair.Data, sheet.Get(pair.Line, from), pair.Line);
                }

                plans[pair] = cells;
            }

            Conflicts(doc, table, plans.Values);

            var rows = new List<ExcelPreviewRow>();
            var writes = new List<PlannedWrite>();

            var ordered = pairs.Where(pair => pair.Data != null).OrderBy(pair => pair.Data.Index)
                .Concat(pairs.Where(pair => pair.Data == null).OrderBy(pair => pair.Line));

            foreach (var pair in ordered)
            {
                ExcelPreviewRow row;

                if (pair.Data == null)
                {
                    var cells = table.Columns
                        .Select(column => map[column.Index] < 0
                            ? new ExcelPreviewCell(string.Empty, ExcelCellState.Absent, null)
                            : new ExcelPreviewCell(ScheduleTable.Clean(sheet.Text(pair.Line, map[column.Index])), ExcelCellState.Absent, null))
                        .ToList();

                    row = new ExcelPreviewRow(ExcelRowKind.FileOnly, 0, pair.Line + 1, 0, cells, 0, 0, pair.Problem);
                }
                else if (pair.Line < 0)
                {
                    var cells = pair.Data.Cells.Select(text => new ExcelPreviewCell(ScheduleTable.Clean(text), ExcelCellState.Same, null)).ToList();
                    row = new ExcelPreviewRow(ExcelRowKind.ScheduleOnly, pair.Data.Index + 1, 0, Elements(pair.Data), cells, 0, 0,
                        pair.Problem ?? "No line of the sheet for this row");
                }
                else
                {
                    var plan = plans[pair];
                    var changes = plan.Count(cell => cell.State == ExcelCellState.Changed || cell.State == ExcelCellState.Cleared);
                    var blocked = plan.Count(cell => cell.State == ExcelCellState.Blocked);

                    var status = new List<string>();
                    if (changes > 0)
                        status.Add(changes + (changes == 1 ? " change" : " changes"));
                    if (blocked > 0)
                        status.Add(blocked + " cannot be written");
                    if (status.Count == 0)
                        status.Add("No changes");
                    if (pair.Note != null)
                        status.Add(pair.Note);

                    row = new ExcelPreviewRow(
                        ExcelRowKind.Matched,
                        pair.Data.Index + 1,
                        pair.Line + 1,
                        Elements(pair.Data),
                        plan.Select(cell => new ExcelPreviewCell(cell.Text, cell.State, cell.Tooltip)).ToList(),
                        changes,
                        blocked,
                        string.Join("; ", status));

                    foreach (var column in table.Columns)
                    {
                        var cell = plan[column.Index];
                        if (cell.State == ExcelCellState.Changed || cell.State == ExcelCellState.Cleared)
                            writes.Add(new PlannedWrite(row, column, pair.Data, cell.Targets, cell.Value, cell.OldText, cell.NewText));
                    }
                }

                rows.Add(row);
            }

            var columns = table.Columns
                .Select(column => new ExcelPreviewColumn(column.Heading, map[column.Index], column.ReadOnlyReason, column.Index == keyColumn))
                .ToList();

            var preview = new ExcelImportPreview(effective, columns, sourceColumns, keyCandidates, rows, notes, source.HasIds, null);
            return new ScheduleImportPlan(table, preview, writes);
        }

        private static int Elements(ScheduleDataRow data)
        {
            return data.IsTied ? data.ElementIds.Count : 0;
        }

        // ───────────────────────────── the sheet ─────────────────────────────

        /// <summary>
        /// The line that reads most like the schedule's column headings, among the first fifty. On a
        /// sheet this button exported without headings, the line above the first exported row.
        /// </summary>
        private static int DetectHeadingRow(ExcelSheetSource source, ScheduleTable table)
        {
            var headings = new HashSet<string>(table.Columns.Select(column => Loose(column.Heading).ToLowerInvariant()).Where(text => text.Length > 0));
            var sheet = source.Sheet;

            var best = -1;
            var bestScore = 0;

            for (var row = 0; row < Math.Min(sheet.RowCount, 50); row++)
            {
                var score = Enumerable.Range(0, sheet.ColumnCount)
                    .Where(column => column != source.IdColumn)
                    .Select(column => Loose(sheet.Text(row, column)).ToLowerInvariant())
                    .Where(headings.Contains)
                    .Distinct()
                    .Count();

                if (score > bestScore)
                {
                    best = row;
                    bestScore = score;
                }
            }

            if (bestScore > 0)
                return best;

            if (source.HasIds)
            {
                for (var row = 0; row < sheet.RowCount; row++)
                {
                    if (source.HasRowKey(row))
                        return row - 1;
                }
            }

            return -1;
        }

        private static IReadOnlyList<ExcelSourceColumn> SourceColumns(ExcelSheetSource source, int headingRow)
        {
            var columns = new List<ExcelSourceColumn> { new ExcelSourceColumn(-1, "(not imported)") };
            var sheet = source.Sheet;

            for (var column = 0; column < sheet.ColumnCount; column++)
            {
                if (column == source.IdColumn)
                    continue;

                var heading = headingRow >= 0 ? Loose(sheet.Text(headingRow, column)).Replace('\n', ' ') : string.Empty;
                if (heading.Length > 40)
                    heading = heading.Substring(0, 40) + "…";

                columns.Add(new ExcelSourceColumn(column, Xlsx.ColumnName(column) + (heading.Length > 0 ? " — " + heading : string.Empty)));
            }

            return columns;
        }

        /// <summary>
        /// Which column of the sheet feeds each schedule column: the window's own choice when it made
        /// one; otherwise what the column showed when exported, then the heading, and — on a sheet with
        /// no recognisable headings at all — simply the order.
        /// </summary>
        private static int[] ColumnMap(ExcelSheetSource source, ScheduleTable table, int headingRow, int[] requested)
        {
            var sheet = source.Sheet;
            var count = table.Columns.Count;

            if (requested != null && requested.Length == count)
                return requested.Select(column => column >= 0 && column < sheet.ColumnCount && column != source.IdColumn ? column : -1).ToArray();

            var map = Enumerable.Repeat(-1, count).ToArray();
            var used = new HashSet<int>();

            foreach (var column in table.Columns)
            {
                var hit = source.ColumnKeys
                    .Where(pair => pair.Value == column.Key && !used.Contains(pair.Key))
                    .Select(pair => pair.Key)
                    .DefaultIfEmpty(-1)
                    .Min();

                if (hit >= 0)
                {
                    map[column.Index] = hit;
                    used.Add(hit);
                }
            }

            if (headingRow >= 0)
            {
                foreach (var column in table.Columns.Where(column => map[column.Index] < 0))
                {
                    var heading = Loose(column.Heading).ToLowerInvariant();
                    if (heading.Length == 0)
                        continue;

                    for (var candidate = 0; candidate < sheet.ColumnCount; candidate++)
                    {
                        if (candidate == source.IdColumn || used.Contains(candidate))
                            continue;

                        if (Loose(sheet.Text(headingRow, candidate)).ToLowerInvariant() == heading)
                        {
                            map[column.Index] = candidate;
                            used.Add(candidate);
                            break;
                        }
                    }
                }
            }

            if (map.All(column => column < 0) && headingRow < 0)
            {
                var data = Enumerable.Range(0, sheet.ColumnCount).Where(column => column != source.IdColumn).ToList();
                for (var i = 0; i < count && i < data.Count; i++)
                    map[i] = data[i];
            }

            return map;
        }

        /// <summary>
        /// The lines of data, from <paramref name="first"/> down, empty lines left out. On a sheet this
        /// button exported, a line without an element is not data: it is a heading, a group line, a
        /// total — or a line typed in by hand, which has no element to write to.
        /// </summary>
        private static List<int> DataLines(ExcelSheetSource source, int first, out int skipped)
        {
            skipped = 0;
            var lines = new List<int>();
            var sheet = source.Sheet;

            for (var row = Math.Max(first, 0); row < sheet.RowCount; row++)
            {
                if (sheet.IsRowEmpty(row))
                    continue;

                if (source.HasIds && !source.HasRowKey(row))
                {
                    skipped++;
                    continue;
                }

                lines.Add(row);
            }

            return lines;
        }

        // ───────────────────────────── pairing ─────────────────────────────

        /// <summary>
        /// Pairs by element. A line goes to the schedule row standing for exactly its elements; failing
        /// that, to the one row holding any of them. Grouping can change between export and import —
        /// an element edited into another group — so a row that differs is still taken when there is
        /// only one, and says so; a line whose elements now stand in several rows is not guessed at.
        /// Rows standing for the very same elements (a material takeoff lists an element once per
        /// material) are taken in turn.
        /// </summary>
        private static List<Pair> MatchByElements(Document doc, ScheduleTable table, ExcelSheetSource source, List<int> lines, List<string> notes)
        {
            var pairs = table.Rows.Select(row => new Pair { Data = row }).ToList();
            var bySet = new Dictionary<string, Queue<Pair>>(StringComparer.Ordinal);
            var byElement = new Dictionary<ElementId, List<Pair>>();

            foreach (var pair in pairs.Where(pair => pair.Data.IsTied))
            {
                var key = SetKey(pair.Data.ElementIds);
                if (!bySet.TryGetValue(key, out var queue))
                {
                    queue = new Queue<Pair>();
                    bySet[key] = queue;
                }
                queue.Enqueue(pair);

                foreach (var id in pair.Data.ElementIds)
                {
                    if (!byElement.TryGetValue(id, out var list))
                    {
                        list = new List<Pair>();
                        byElement[id] = list;
                    }
                    list.Add(pair);
                }
            }

            var fileOnly = new List<Pair>();
            var resolvedAny = false;

            foreach (var line in lines)
            {
                var exported = source.ElementsOf(line);
                var ids = exported.Select(uniqueId => Resolve(doc, uniqueId)).Where(id => id != null).Distinct().ToList();
                resolvedAny |= ids.Count > 0;

                if (ids.Count == 0)
                {
                    fileOnly.Add(new Pair { Line = line, Problem = "Its elements are no longer in the project" });
                    continue;
                }

                if (bySet.TryGetValue(SetKey(ids), out var exact))
                {
                    while (exact.Count > 0 && exact.Peek().Line >= 0)
                        exact.Dequeue();

                    if (exact.Count > 0)
                    {
                        exact.Dequeue().Line = line;
                        continue;
                    }
                }

                var holders = ids.Where(byElement.ContainsKey).SelectMany(id => byElement[id]).Distinct().ToList();
                var free = holders.Where(pair => pair.Line < 0).ToList();

                if (holders.Count == 0)
                {
                    fileOnly.Add(new Pair { Line = line, Problem = "Its elements are not in this schedule — filtered out of it, or the sheet belongs to another schedule" });
                }
                else if (free.Count == 1 && holders.Count == 1)
                {
                    free[0].Line = line;
                    free[0].Note = "the row has changed since the export: " + Count(free[0].Data.ElementIds.Count) + " now, " + Count(exported.Count) + " in the file";
                }
                else if (holders.Count > 1)
                {
                    fileOnly.Add(new Pair { Line = line, Problem = "Its elements now stand in " + holders.Count + " different rows — the schedule's grouping has changed since the export; export it again" });
                }
                else
                {
                    fileOnly.Add(new Pair { Line = line, Problem = "Schedule row " + (holders[0].Data.Index + 1) + " is already paired with sheet line " + (holders[0].Line + 1) });
                }
            }

            if (!resolvedAny && lines.Count > 0)
                notes.Insert(0, "None of the sheet's elements are in this project — it was exported from another model. Pair the rows " +
                                "by a key column or by row order instead.");

            return pairs.Concat(fileOnly).ToList();
        }

        private static string Count(int elements)
        {
            return elements == 1 ? "1 element" : elements + " elements";
        }

        private static ElementId Resolve(Document doc, string uniqueId)
        {
            try
            {
                return doc.GetElement(uniqueId)?.Id;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SetKey(IEnumerable<ElementId> ids)
        {
            return string.Join(",", ids.Select(id => id.ToString()).OrderBy(value => value, StringComparer.Ordinal));
        }

        private static List<Pair> MatchByKey(ScheduleTable table, XlsxReadSheet sheet, List<int> lines, int keyColumn, int[] map, List<string> notes)
        {
            var pairs = table.Rows.Select(row => new Pair { Data = row }).ToList();
            var fileOnly = new List<Pair>();

            if (keyColumn < 0)
            {
                notes.Insert(0, "No column of this schedule tells every row apart (each value present and different), so the rows cannot be " +
                                "paired by a key. Pair them by row order instead.");
                return pairs.Concat(lines.Select(line => new Pair { Line = line, Problem = "No key column to pair it by" })).ToList();
            }

            var heading = table.Columns[keyColumn].Heading;
            var from = map[keyColumn];
            if (from < 0)
            {
                notes.Insert(0, "The key column \"" + heading + "\" is not fed by any column of the sheet — pick one in the drop-down under its heading.");
                return pairs.Concat(lines.Select(line => new Pair { Line = line, Problem = "No key value to pair it by" })).ToList();
            }

            var index = pairs.GroupBy(pair => Loose(pair.Data.Cells[keyColumn]), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

            foreach (var line in lines)
            {
                var value = Loose(sheet.Text(line, from));
                if (value.Length == 0)
                {
                    fileOnly.Add(new Pair { Line = line, Problem = "Its \"" + heading + "\" is empty" });
                    continue;
                }

                if (!index.TryGetValue(value, out var found))
                {
                    fileOnly.Add(new Pair { Line = line, Problem = "No schedule row reads \"" + value + "\" in \"" + heading + "\"" });
                    continue;
                }

                var target = found[0];
                if (target.Line >= 0)
                    fileOnly.Add(new Pair { Line = line, Problem = "Schedule row " + (target.Data.Index + 1) + " is already paired with sheet line " + (target.Line + 1) });
                else
                    target.Line = line;
            }

            return pairs.Concat(fileOnly).ToList();
        }

        private static List<Pair> MatchByOrder(ScheduleTable table, List<int> lines)
        {
            var pairs = table.Rows.Select(row => new Pair { Data = row }).ToList();
            var fileOnly = new List<Pair>();

            for (var i = 0; i < lines.Count; i++)
            {
                if (i < pairs.Count)
                    pairs[i].Line = lines[i];
                else
                    fileOnly.Add(new Pair { Line = lines[i], Problem = "The schedule has only " + pairs.Count + " rows" });
            }

            foreach (var pair in pairs.Where(pair => pair.Line < 0))
                pair.Problem = "The sheet has fewer lines of data than the schedule has rows";

            return pairs.Concat(fileOnly).ToList();
        }

        /// <summary>Columns whose values are present on every row and differ from row to row — usable to pair rows by.</summary>
        private static IReadOnlyList<int> KeyCandidates(ScheduleTable table)
        {
            var result = new List<int>();
            if (table.Rows.Count == 0)
                return result;

            foreach (var column in table.Columns)
            {
                var values = table.Rows.Select(row => Loose(row.Cells[column.Index])).ToList();
                if (values.All(value => value.Length > 0) && values.Distinct(StringComparer.Ordinal).Count() == values.Count)
                    result.Add(column.Index);
            }

            return result;
        }

        /// <summary>A key the sheet also has; one named like a mark or a number before any other.</summary>
        private static int PickKey(ScheduleTable table, IReadOnlyList<int> candidates, int[] map)
        {
            var fed = candidates.Where(column => map[column] >= 0).ToList();
            var pool = fed.Count > 0 ? fed : candidates.ToList();

            foreach (var column in pool)
            {
                var heading = table.Columns[column].Heading.ToLowerInvariant();
                if (KeyHints.Any(heading.Contains))
                    return column;
            }

            return pool.Count > 0 ? pool[0] : -1;
        }

        // ───────────────────────────── cells ─────────────────────────────

        private sealed class Context
        {
            private readonly Document _doc;
            private readonly ScheduleTable _table;
            private readonly bool _emptyClears;
            private Dictionary<ElementId, int> _rowsPerType;

            public Context(Document doc, ScheduleTable table, bool emptyClears)
            {
                _doc = doc;
                _table = table;
                _emptyClears = emptyClears;
            }

            public CellPlan Evaluate(ScheduleColumn column, ScheduleDataRow data, XlsxValue value, int line)
            {
                var old = ScheduleTable.Clean(data.Cells[column.Index]);
                var text = ScheduleTable.Clean(value.Text);
                var clearing = false;

                if (text.Length == 0)
                {
                    if (old.Length == 0)
                        return Same(old);

                    if (!_emptyClears)
                        return new CellPlan
                        {
                            State = ExcelCellState.Kept,
                            Text = old,
                            Tooltip = "Empty in the sheet — left as it is. Turn on \"Empty cells clear values\" to clear it."
                        };

                    clearing = true;
                }
                else if (Loose(text) == Loose(old))
                {
                    return Same(old);
                }

                var resolution = Resolve(column, data);

                if (!clearing && ScheduleValues.SameValue(_doc, resolution.Storage, resolution.Spec, column.Format, value, text, old))
                    return Same(old);

                var shown = clearing ? "(cleared)" : text;
                var compared = "In Revit: " + Quote(old) + "\nIn the sheet: " + (clearing ? "empty" : Quote(text));

                if (resolution.Problem != null)
                    return Blocked(text, old, compared, resolution.Problem);

                object parsed;
                if (clearing)
                {
                    if (resolution.Storage != StorageType.String)
                        return Blocked(text, old, compared, "A number cannot be emptied — type a value, or leave the cell as it was");
                    parsed = string.Empty;
                }
                else if (!ScheduleValues.TryParse(_doc, resolution.Storage, resolution.Spec, column.Format, value, text, out parsed, out var error))
                {
                    return Blocked(text, old, compared, error + (old.Length > 0 ? " — the schedule shows it as " + Quote(old) : string.Empty));
                }

                return new CellPlan
                {
                    State = clearing ? ExcelCellState.Cleared : ExcelCellState.Changed,
                    Text = shown,
                    Tooltip = compared + (resolution.Note != null ? "\n" + resolution.Note : string.Empty),
                    Targets = resolution.Targets,
                    Value = parsed,
                    OldText = old,
                    NewText = clearing ? string.Empty : text,
                    Line = line
                };
            }

            private static CellPlan Same(string old)
            {
                return new CellPlan { State = ExcelCellState.Same, Text = old };
            }

            private static CellPlan Blocked(string text, string old, string compared, string reason)
            {
                return new CellPlan
                {
                    State = ExcelCellState.Blocked,
                    Text = text.Length == 0 ? "(cleared)" : text,
                    Tooltip = "Cannot be written: " + reason + "\n" + compared,
                    OldText = old,
                    NewText = text
                };
            }

            /// <summary>
            /// What the cell would be written into, and whether it can be: the parameter on each of the
            /// row's elements (or on their types), its storage and spec — and the first reason it cannot,
            /// in the words the tooltip uses. The storage is worked out even for a row that cannot be
            /// written, so a number merely retyped is still recognised as the same.
            /// </summary>
            private Resolution Resolve(ScheduleColumn column, ScheduleDataRow data)
            {
                var resolution = new Resolution();

                var missing = 0;
                var readOnly = 0;
                var inGroups = 0;
                var gone = 0;
                var storages = new HashSet<StorageType>();
                var types = new List<ElementId>();

                if (column.ReadOnlyReason == null && data.IsTied)
                {
                    foreach (var id in data.ElementIds)
                    {
                        var element = _doc.GetElement(id);
                        if (element == null)
                        {
                            gone++;
                            continue;
                        }

                        var found = ScheduleValues.Find(_doc, element, column);
                        if (found.Parameter == null)
                        {
                            missing++;
                            continue;
                        }

                        if (resolution.Spec == null)
                        {
                            resolution.Storage = found.Parameter.StorageType;
                            resolution.Spec = SafeSpec(found.Parameter);
                        }

                        storages.Add(found.Parameter.StorageType);

                        if (found.Parameter.IsReadOnly)
                            readOnly++;

                        var isType = found.Owner.Id != element.Id;
                        if (isType)
                        {
                            if (!types.Contains(found.Owner.Id))
                                types.Add(found.Owner.Id);
                        }
                        else
                        {
                            if (element.GroupId != null && element.GroupId != ElementId.InvalidElementId && !VariesAcrossGroups(found.Parameter))
                                inGroups++;
                        }

                        if (!resolution.Targets.Contains(found.Owner.Id))
                            resolution.Targets.Add(found.Owner.Id);
                    }
                }

                var all = data.ElementIds.Count;

                if (column.ReadOnlyReason != null)
                    resolution.Problem = column.ReadOnlyReason;
                else if (!data.IsTied)
                    resolution.Problem = "This schedule row is not tied to its elements — " + data.UntiedReason;
                else if (column.ShowsTotals && all > 1)
                    resolution.Problem = "The cell shows a total over the row's " + all + " elements, which no single element holds — change them " +
                                         "in an itemized schedule";
                else if (gone > 0)
                    resolution.Problem = gone == all ? "The row's element is no longer in the project" : gone + " of the row's elements are no longer in the project";
                else if (resolution.Targets.Count == 0)
                    resolution.Problem = "The row's elements do not have this parameter";
                else if (missing > 0)
                    resolution.Problem = missing + " of the row's " + all + " elements do not have this parameter";
                else if (readOnly > 0)
                    resolution.Problem = "The parameter is read-only" + (readOnly < all ? " on " + readOnly + " of the row's elements" : string.Empty) +
                                         " — Revit computes or locks it";
                else if (inGroups > 0)
                    resolution.Problem = (all == 1 ? "The element stands" : inGroups + " of the row's elements stand") + " in a model group, and this parameter " +
                                         "does not vary by group instance — change it with \"Edit Group\"";
                else if (storages.Count > 1)
                    resolution.Problem = "The row's elements store this parameter differently";
                else if (resolution.Storage == StorageType.ElementId || resolution.Storage == StorageType.None)
                    resolution.Problem = "The value is a choice of another element (a type, a level, a material…) — pick it in Revit";

                if (resolution.Problem == null)
                {
                    if (types.Count > 0)
                        resolution.Note = TypeNote(types, data);
                    else if (resolution.Targets.Count > 1)
                        resolution.Note = "Written to all " + resolution.Targets.Count + " elements of this row";
                }

                return resolution;
            }

            private string TypeNote(IReadOnlyList<ElementId> types, ScheduleDataRow data)
            {
                var rowsPerType = RowsPerType();
                var others = types.Sum(type => rowsPerType.TryGetValue(type, out var count) ? count : 0) - 1;

                var name = types.Count == 1 ? "the type \"" + SafeName(_doc.GetElement(types[0])) + "\"" : types.Count + " types";
                return "A type parameter: " + name + " changes, and with it every element of that type" +
                       (others > 0 ? " — " + others + (others == 1 ? " more row" : " more rows") + " of this schedule show it too" : string.Empty);
            }

            /// <summary>How many rows of the schedule hold an element of each type — read once, for the type notes.</summary>
            private Dictionary<ElementId, int> RowsPerType()
            {
                if (_rowsPerType != null)
                    return _rowsPerType;

                _rowsPerType = new Dictionary<ElementId, int>();
                foreach (var row in _table.Rows.Where(row => row.IsTied))
                {
                    var rowTypes = row.ElementIds
                        .Select(id => _doc.GetElement(id)?.GetTypeId())
                        .Where(id => id != null && id != ElementId.InvalidElementId)
                        .Distinct();

                    foreach (var type in rowTypes)
                        _rowsPerType[type] = _rowsPerType.TryGetValue(type, out var count) ? count + 1 : 1;
                }

                return _rowsPerType;
            }

            private static bool VariesAcrossGroups(Parameter parameter)
            {
                try
                {
                    return (parameter.Definition as InternalDefinition)?.VariesAcrossGroups == true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            private static ForgeTypeId SafeSpec(Parameter parameter)
            {
                try
                {
                    return parameter.Definition.GetDataType();
                }
                catch (Exception)
                {
                    return new ForgeTypeId();
                }
            }

            private static string SafeName(Element element)
            {
                try
                {
                    return element?.Name ?? string.Empty;
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// Two lines giving one element or one type two different values: the sheet contradicts itself,
        /// and whichever went last would silently win. Neither is written; both say why.
        /// </summary>
        private static void Conflicts(Document doc, ScheduleTable table, IEnumerable<CellPlan[]> plans)
        {
            var claims = new Dictionary<Tuple<ElementId, int>, List<CellPlan>>();

            foreach (var cells in plans)
            {
                for (var column = 0; column < cells.Length; column++)
                {
                    var cell = cells[column];
                    if (cell.State != ExcelCellState.Changed && cell.State != ExcelCellState.Cleared)
                        continue;

                    foreach (var target in cell.Targets)
                    {
                        var key = Tuple.Create(target, column);
                        if (!claims.TryGetValue(key, out var list))
                        {
                            list = new List<CellPlan>();
                            claims[key] = list;
                        }
                        list.Add(cell);
                    }
                }
            }

            foreach (var claim in claims)
            {
                var cells = claim.Value;
                if (cells.Count < 2 || cells.All(cell => ScheduleValues.Equal(cell.Value, cells[0].Value)))
                    continue;

                var lines = string.Join(", ", cells.Select(cell => cell.Line + 1).Distinct().OrderBy(line => line));
                var what = doc.GetElement(claim.Key.Item1) is ElementType ? "the same type" : "the same element";

                foreach (var cell in cells.Where(cell => cell.State != ExcelCellState.Blocked))
                {
                    cell.State = ExcelCellState.Blocked;
                    cell.Tooltip = "Cannot be written: sheet lines " + lines + " give " + what + " different values in \"" +
                                   table.Columns[claim.Key.Item2].Heading + "\" — make them agree\n" + cell.Tooltip;
                    cell.Targets = null;
                }
            }
        }

        private static string Quote(string text)
        {
            return text.Length == 0 ? "empty" : "«" + text + "»";
        }

        /// <summary>The comparison form of a text: cleaned, with the non-breaking spaces Revit puts between digit groups made plain.</summary>
        internal static string Loose(string text)
        {
            return ScheduleTable.Clean(text).Replace(' ', ' ').Replace(' ', ' ');
        }
    }

    /// <summary>
    /// Finding, reading and parsing a schedule column's parameter on an element — shared by the
    /// planner, which decides, and the command, which writes and then checks what was written.
    /// </summary>
    internal static class ScheduleValues
    {
        private static readonly string[] Yes = { "yes", "да", "true", "истина", "1", "x", "х", "✓", "✔", "+", "y", "д" };
        private static readonly string[] No = { "no", "нет", "false", "ложь", "0", "-", "—", "n", "н" };

        /// <summary>
        /// The parameter a column shows, on an element or on its type. A shared parameter bound per type
        /// still comes to a schedule as an instance field (RevitAPI.xml, <c>ScheduleFieldType.Instance</c>),
        /// so an instance field missing on the element is looked for on its type as well.
        /// </summary>
        public static (Element Owner, Parameter Parameter) Find(Document doc, Element element, ScheduleColumn column)
        {
            if (column.FieldType == ScheduleFieldType.ElementType)
            {
                var type = TypeOf(doc, element);
                return (type, On(doc, type, column.ParameterId));
            }

            var own = On(doc, element, column.ParameterId);
            if (own != null)
                return (element, own);

            var owner = TypeOf(doc, element);
            return (owner, On(doc, owner, column.ParameterId));
        }

        /// <summary>The parameter with the given id on an element itself — a built-in by its enum value, a shared one by GUID, a project one by definition.</summary>
        public static Parameter On(Document doc, Element element, ElementId parameterId)
        {
            if (element == null || parameterId == null || parameterId == ElementId.InvalidElementId)
                return null;

            try
            {
                var value = parameterId.IntegerValue;
                if (value < 0)
                    return element.get_Parameter((BuiltInParameter)value);

                var parameterElement = doc.GetElement(parameterId);
                if (parameterElement is SharedParameterElement shared)
                    return element.get_Parameter(shared.GuidValue);

                return parameterElement is ParameterElement project ? element.get_Parameter(project.GetDefinition()) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Element TypeOf(Document doc, Element element)
        {
            try
            {
                var typeId = element?.GetTypeId();
                return typeId == null || typeId == ElementId.InvalidElementId ? null : doc.GetElement(typeId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The sheet's text is the schedule's value in another spelling: a number typed without the
        /// unit or the digit grouping, a check box written as "Да". Compared through the value, at the
        /// precision the schedule shows — a number that merely reads the same is not rewritten.
        /// </summary>
        public static bool SameValue(Document doc, StorageType storage, ForgeTypeId spec, FormatOptions format, XlsxValue cell, string text, string old)
        {
            switch (storage)
            {
                case StorageType.Double:
                    if (!TryDouble(doc, spec, format, cell, text, out var value))
                        return false;

                    if (ScheduleImportPlanner.Loose(Format(doc, spec, format, value)) == ScheduleImportPlanner.Loose(old))
                        return true;

                    return old.Length > 0 && TryDouble(doc, spec, format, null, old, out var shown) && Close(value, shown);

                case StorageType.Integer:
                    if (IsYesNo(spec))
                        return TryYesNo(text, out var left) && TryYesNo(old, out var right) && left == right;

                    return TryInteger(cell, text, out var number) && TryInteger(null, old, out var current) && number == current;

                default:
                    return false;
            }
        }

        /// <summary>The sheet's text as the value the parameter stores; the error says what was expected.</summary>
        public static bool TryParse(
            Document doc,
            StorageType storage,
            ForgeTypeId spec,
            FormatOptions format,
            XlsxValue cell,
            string text,
            out object value,
            out string error)
        {
            value = null;
            error = null;

            switch (storage)
            {
                case StorageType.String:
                    value = text;
                    return true;

                case StorageType.Integer:
                    if (IsYesNo(spec))
                    {
                        if (TryYesNo(text, out var flag))
                        {
                            value = flag ? 1 : 0;
                            return true;
                        }

                        error = Quote(text) + " is neither yes nor no";
                        return false;
                    }

                    if (TryInteger(cell, text, out var number))
                    {
                        value = number;
                        return true;
                    }

                    error = Quote(text) + " is not a whole number";
                    return false;

                case StorageType.Double:
                    if (TryDouble(doc, spec, format, cell, text, out var internalValue))
                    {
                        value = internalValue;
                        return true;
                    }

                    error = Quote(text) + " is not a number Revit can read here";
                    return false;

                default:
                    error = "This kind of value cannot be typed in";
                    return false;
            }
        }

        /// <summary>Whether a parameter now holds the value — the check after the commit.</summary>
        public static bool Holds(Parameter parameter, object value)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    // Revit may keep a line break as "\r\n" where the sheet had "\n" — the same text either way.
                    return string.Equals(
                        (parameter.AsString() ?? string.Empty).Replace("\r\n", "\n"),
                        (value as string ?? string.Empty).Replace("\r\n", "\n"),
                        StringComparison.Ordinal);
                case StorageType.Integer:
                    return value is int number && parameter.AsInteger() == number;
                case StorageType.Double:
                    return value is double real && Close(parameter.AsDouble(), real);
                default:
                    return false;
            }
        }

        /// <summary>The parameter's value as the schedule would show it, for the report.</summary>
        public static string Show(Parameter parameter)
        {
            try
            {
                return parameter.StorageType == StorageType.String ? parameter.AsString() ?? string.Empty : parameter.AsValueString() ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static bool Equal(object left, object right)
        {
            if (left is double a && right is double b)
                return Close(a, b);

            return Equals(left, right);
        }

        private static bool Close(double left, double right)
        {
            return Math.Abs(left - right) <= 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
        }

        private static bool IsYesNo(ForgeTypeId spec)
        {
            try
            {
                return spec != null && spec.Equals(SpecTypeId.Boolean.YesNo);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryYesNo(string text, out bool value)
        {
            var clean = (text ?? string.Empty).Trim().ToLowerInvariant();
            value = Yes.Contains(clean);
            return value || No.Contains(clean);
        }

        private static bool TryInteger(XlsxValue cell, string text, out int value)
        {
            if (cell?.Number != null && Math.Abs(cell.Number.Value - Math.Round(cell.Number.Value)) < 1e-9 &&
                Math.Abs(cell.Number.Value) <= int.MaxValue)
            {
                value = (int)Math.Round(cell.Number.Value);
                return true;
            }

            var digits = (text ?? string.Empty).Replace(" ", string.Empty).Replace(" ", string.Empty).Replace(" ", string.Empty);
            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.CurrentCulture, out value) ||
                   int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// A number cell is taken as it is, in the unit the schedule column shows — 12.5 under "m²" is
        /// 12.5 m² — and converted to Revit's internal units. A text cell goes through Revit's own
        /// parser with the column's format, so "1 200", "1200 mm" and "1,2 m" all read as Revit reads them.
        /// </summary>
        private static bool TryDouble(Document doc, ForgeTypeId spec, FormatOptions format, XlsxValue cell, string text, out double value)
        {
            if (cell?.Number != null)
            {
                value = ToInternal(doc, spec, format, cell.Number.Value);
                return true;
            }

            var units = doc.GetUnits();

            if (IsSpec(spec))
            {
                try
                {
                    var options = new ValueParsingOptions();
                    if (format != null)
                        options.SetFormatOptions(format);

                    if (UnitFormatUtils.TryParse(units, spec, text, options, out value))
                        return true;
                }
                catch (Exception)
                {
                }

                try
                {
                    if (UnitFormatUtils.TryParse(units, spec, text, out value))
                        return true;
                }
                catch (Exception)
                {
                }
            }

            var plain = (text ?? string.Empty).Replace(" ", string.Empty).Replace(" ", string.Empty).Replace(" ", string.Empty);
            if (double.TryParse(plain, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) ||
                double.TryParse(plain.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                value = ToInternal(doc, spec, format, number);
                return true;
            }

            value = 0;
            return false;
        }

        private static double ToInternal(Document doc, ForgeTypeId spec, FormatOptions format, double number)
        {
            try
            {
                if (!IsSpec(spec) || !UnitUtils.IsMeasurableSpec(spec))
                    return number;

                var options = format ?? doc.GetUnits().GetFormatOptions(spec);
                return UnitUtils.ConvertToInternalUnits(number, options.GetUnitTypeId());
            }
            catch (Exception)
            {
                return number;
            }
        }

        private static string Format(Document doc, ForgeTypeId spec, FormatOptions format, double value)
        {
            try
            {
                if (IsSpec(spec))
                {
                    var options = new FormatValueOptions();
                    if (format != null)
                        options.SetFormatOptions(format);

                    return UnitFormatUtils.Format(doc.GetUnits(), spec, value, false, options);
                }
            }
            catch (Exception)
            {
            }

            return value.ToString(CultureInfo.CurrentCulture);
        }

        private static bool IsSpec(ForgeTypeId spec)
        {
            try
            {
                return spec != null && !spec.Empty() && SpecUtils.IsSpec(spec);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Quote(string text)
        {
            return "«" + text + "»";
        }
    }
}
