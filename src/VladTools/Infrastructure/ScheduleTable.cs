using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>One visible column of a schedule: which field it shows and whether an import may write it.</summary>
    internal sealed class ScheduleColumn
    {
        public ScheduleColumn(
            int index,
            string key,
            string heading,
            ScheduleFieldType fieldType,
            ElementId parameterId,
            string readOnlyReason,
            bool showsTotals,
            FormatOptions format,
            double width)
        {
            Index = index;
            Key = key;
            Heading = heading;
            FieldType = fieldType;
            ParameterId = parameterId;
            ReadOnlyReason = readOnlyReason;
            ShowsTotals = showsTotals;
            Format = format;
            Width = width;
        }

        /// <summary>The position among the schedule's visible columns, zero-based.</summary>
        public int Index { get; }

        /// <summary>
        /// What the column shows, independent of its heading and of this project's ids: the field kind
        /// plus a built-in parameter's name, a shared parameter's GUID or a project parameter's name.
        /// Written into an exported file so the import finds the column again even when the heading
        /// was edited or the columns were rearranged.
        /// </summary>
        public string Key { get; }

        public string Heading { get; }

        public ScheduleFieldType FieldType { get; }

        public ElementId ParameterId { get; }

        /// <summary>Why the column can never be written; null for a parameter the import may try.</summary>
        public string ReadOnlyReason { get; }

        /// <summary>
        /// The field adds up its values ("Calculate totals" or min/max). On an itemized row the cell is
        /// still the element's own value; on a grouped row it is the total, which no single element holds.
        /// </summary>
        public bool ShowsTotals { get; }

        /// <summary>The field's own number format; null when it follows the project units.</summary>
        public FormatOptions Format { get; }

        /// <summary>The width on a sheet, in feet.</summary>
        public double Width { get; }

        public bool IsTypeField => FieldType == ScheduleFieldType.ElementType;
    }

    /// <summary>One line of a schedule's body that stands for elements: its texts and the elements behind it.</summary>
    internal sealed class ScheduleDataRow
    {
        public ScheduleDataRow(int index, IReadOnlyList<string> cells, IReadOnlyList<ElementId> elementIds, string untiedReason)
        {
            Index = index;
            Cells = cells;
            ElementIds = elementIds ?? new List<ElementId>();
            UntiedReason = ElementIds.Count == 0 && untiedReason == null ? ScheduleTable.UnknownElements : untiedReason;
        }

        /// <summary>Zero-based among the schedule's element rows (headings, subtotals and totals are not counted).</summary>
        public int Index { get; }

        /// <summary>The cell texts exactly as the schedule shows them, one per visible column.</summary>
        public IReadOnlyList<string> Cells { get; }

        /// <summary>One element on an itemized row; every element the row stands for on a grouped one.</summary>
        public IReadOnlyList<ElementId> ElementIds { get; }

        /// <summary>Why the row is not tied to its elements; null when it is.</summary>
        public string UntiedReason { get; }

        public bool IsTied => UntiedReason == null;
    }

    /// <summary>One line of the schedule as Revit draws it, for an export that looks like the schedule.</summary>
    internal sealed class ScheduleDisplayRow
    {
        public ScheduleDisplayRow(IReadOnlyList<string> cells)
        {
            Cells = cells;
        }

        public IReadOnlyList<string> Cells { get; }

        /// <summary>The <see cref="ScheduleDataRow"/> this line is; -1 for headings, group lines and totals.</summary>
        public int DataRow { get; set; } = -1;

        public bool IsHeading { get; set; }
    }

    /// <summary>
    /// A schedule read out of Revit: its columns, the texts of every element row exactly as the
    /// schedule shows them, and — the part the Revit API does not give — which elements each row
    /// stands for.
    ///
    /// **The API has no "elements of this row".** <c>TableSectionData</c> hands out texts only
    /// (confirmed in RevitAPI.xml 2022/2024/2025), and the order of rows comes from sorting, grouping
    /// and itemizing that would have to be re-implemented to be predicted. So the rows are asked
    /// directly, inside a transaction that is always rolled back (<see cref="Probe"/>): a temporary
    /// text parameter is bound to the schedule's categories, every element gets a key of its own in
    /// it, the parameter is added to the schedule as a column, and each row reads back which keys it
    /// shows. Nothing is deleted and nothing reaches the document — not even the undo list.
    ///
    /// A grouped row (itemizing off) shows a key only when it holds a single element. For those the
    /// schedule is itemized for a moment, and every itemized row is put back into the grouped row
    /// whose sorting/grouping values it carries — which is exactly the rule by which Revit itself
    /// folds rows together when "Itemize every instance" is off.
    /// </summary>
    internal sealed class ScheduleTable
    {
        public const string UnknownElements =
            "its elements could not be identified — elements of a linked model, or of a category Revit lets no project parameter onto";

        private const string KeyPrefix = "#VTKEY:";
        private const string HeadingMarker = "#VTKEY-HEADING";
        private const char KeySeparator = '\u001F';

        private ScheduleTable(ViewSchedule schedule, ScheduleDefinition definition, IReadOnlyList<ScheduleColumn> columns)
        {
            Name = schedule.Name;
            Id = schedule.Id;
            UniqueId = schedule.UniqueId;
            Columns = columns;

            try
            {
                IsItemized = definition.IsItemized;
                IsKeySchedule = definition.IsKeySchedule;
                IsMaterialTakeoff = definition.IsMaterialTakeoff;
            }
            catch (Exception)
            {
            }
        }

        public string Name { get; }

        public ElementId Id { get; }

        public string UniqueId { get; }

        public bool IsItemized { get; }

        public bool IsKeySchedule { get; }

        public bool IsMaterialTakeoff { get; }

        public IReadOnlyList<ScheduleColumn> Columns { get; }

        public IReadOnlyList<ScheduleDataRow> Rows { get; private set; } = new List<ScheduleDataRow>();

        /// <summary>Why no row could be tied to elements at all; null when the rows were asked.</summary>
        public string TieProblem { get; private set; }

        /// <summary>The title area above the body (the schedule's name, usually), one line per row.</summary>
        public IReadOnlyList<string> TitleLines { get; private set; } = new List<string>();

        /// <summary>The body as Revit draws it — null when not asked for, or when it could not be lined up with the rows.</summary>
        public IReadOnlyList<ScheduleDisplayRow> DisplayRows { get; private set; }

        /// <summary>Why the body as drawn could not be used; null when it could, or was not asked for.</summary>
        public string LayoutProblem { get; private set; }

        public int TiedRows => Rows.Count(row => row.IsTied);

        /// <summary>
        /// Reads the schedule. <paramref name="withLayout"/> also reads the body as Revit draws it —
        /// headings, group lines, subtotals and the grand total — for an export that looks like the schedule.
        /// Must be called with no transaction open: the row probe opens (and rolls back) its own.
        /// </summary>
        public static ScheduleTable Read(Document doc, ViewSchedule schedule, bool withLayout)
        {
            var definition = schedule.Definition;
            var visible = definition.GetFieldOrder()
                .Select(definition.GetField)
                .Where(field => !field.IsHidden)
                .ToList();

            var table = new ScheduleTable(schedule, definition, Describe(doc, visible));

            // The drawn body is read before the probe: the probe changes how the schedule is laid out.
            if (withLayout)
                table.ReadLayout(schedule);

            try
            {
                table.Probe(doc, schedule, visible.Select(field => field.FieldId).ToList());
            }
            catch (Exception exception)
            {
                // No transaction could be opened at all (a read-only document, say). The schedule can
                // still be exported as drawn; it just cannot be written back into.
                table.TieProblem = "Revit would not let the rows be asked which elements they stand for (" + LinkCatalog.Short(exception.Message) + ")";
                table.ReadRowsAsDrawn(schedule);
                return table;
            }

            if (withLayout)
                table.Align();

            return table;
        }

        /// <summary>
        /// The fallback when the probe cannot run: every line below the headings, as drawn, is taken as
        /// a row — group lines and totals included, since nothing tells them apart without the probe —
        /// and none is tied to elements. Enough to export; nothing to write into.
        /// </summary>
        private void ReadRowsAsDrawn(ViewSchedule schedule)
        {
            DisplayRows = null;
            LayoutProblem = null;

            var rows = new List<ScheduleDataRow>();

            try
            {
                schedule.RefreshData();
                var body = schedule.GetTableData().GetSectionData(SectionType.Body);
                var headings = Columns.Select(column => column.Heading).ToList();
                var lines = new List<string[]>();

                for (var row = body.FirstRowNumber; row <= body.LastRowNumber; row++)
                {
                    var cells = new string[Columns.Count];
                    for (var column = 0; column < Columns.Count && column < body.NumberOfColumns; column++)
                        cells[column] = schedule.GetCellText(SectionType.Body, row, body.FirstColumnNumber + column) ?? string.Empty;
                    lines.Add(cells.Select(cell => cell ?? string.Empty).ToArray());
                }

                var heading = lines.FindIndex(cells => Same(cells, headings));
                foreach (var cells in lines.Skip(heading + 1))
                    rows.Add(new ScheduleDataRow(rows.Count, cells, null, TieProblem));
            }
            catch (Exception)
            {
            }

            Rows = rows;
        }

        /// <summary>Line breaks as "\n" and no space at the ends — one rule for the schedule and the file.</summary>
        public static string Clean(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        // ───────────────────────────── columns ─────────────────────────────

        private static IReadOnlyList<ScheduleColumn> Describe(Document doc, IReadOnlyList<ScheduleField> visible)
        {
            var columns = new List<ScheduleColumn>();
            var keys = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < visible.Count; i++)
            {
                var field = visible[i];

                var baseKey = FieldKey(doc, field);
                var key = baseKey;
                for (var number = 2; !keys.Add(key); number++)
                    key = baseKey + "#" + number;

                var heading = Clean(Safe(() => field.ColumnHeading, string.Empty));
                if (heading.Length == 0)
                    heading = Clean(Safe(field.GetName, string.Empty));

                columns.Add(new ScheduleColumn(
                    i,
                    key,
                    heading,
                    field.FieldType,
                    Safe(() => field.ParameterId, ElementId.InvalidElementId),
                    ReadOnlyReason(field),
                    Safe(() => field.DisplayType != ScheduleFieldDisplayType.Standard, false),
                    Safe(() =>
                    {
                        var options = field.GetFormatOptions();
                        return options == null || options.UseDefault ? null : options;
                    }, null),
                    Safe(() => field.SheetColumnWidth, 0.0)));
            }

            return columns;
        }

        private static string FieldKey(Document doc, ScheduleField field)
        {
            var kind = field.FieldType.ToString();
            var parameterId = Safe(() => field.ParameterId, ElementId.InvalidElementId);

            if (Safe(() => field.IsCalculatedField || field.IsCombinedParameterField, false) ||
                parameterId == null || parameterId == ElementId.InvalidElementId)
            {
                return kind + "|" + Safe(field.GetName, string.Empty);
            }

            return kind + "|" + ParameterIdentity(doc, parameterId);
        }

        /// <summary>
        /// What a parameter is called across projects. An element id would do inside one project, but a
        /// file exported from one model may well be imported into another, where every id differs.
        /// </summary>
        private static string ParameterIdentity(Document doc, ElementId parameterId)
        {
            var value = parameterId.IntegerValue;
            if (value < 0)
                return "BIP:" + ((BuiltInParameter)value);

            var element = doc.GetElement(parameterId);
            if (element is SharedParameterElement shared)
                return "GUID:" + shared.GuidValue.ToString("D");

            if (element is ParameterElement parameter)
                return "NAME:" + Safe(() => parameter.GetDefinition().Name, parameter.Name);

            return "ID:" + value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Why a column can never be written, in words for the window. Only instance and type
        /// parameters of the scheduled elements themselves are writable, the same as when typing
        /// straight into the schedule in Revit.
        /// </summary>
        private static string ReadOnlyReason(ScheduleField field)
        {
            if (Safe(() => field.IsCalculatedField, false))
                return "A calculated value — Revit works it out from a formula";

            if (Safe(() => field.IsCombinedParameterField, false))
                return "A combined parameter — it is put together from other parameters; change those";

            switch (field.FieldType)
            {
                case ScheduleFieldType.Instance:
                case ScheduleFieldType.ElementType:
                    var parameterId = Safe(() => field.ParameterId, ElementId.InvalidElementId);
                    return parameterId == null || parameterId == ElementId.InvalidElementId
                        ? "Revit gives this column no parameter to write"
                        : null;
                case ScheduleFieldType.Count:
                    return "The number of elements in the row — Revit counts it";
                case ScheduleFieldType.Room:
                case ScheduleFieldType.FromRoom:
                case ScheduleFieldType.ToRoom:
                    return "A parameter of the room the element belongs to — change it in a room schedule";
                case ScheduleFieldType.Space:
                    return "A parameter of the space the element belongs to — change it in a space schedule";
                case ScheduleFieldType.ProjectInfo:
                    return "A Project Information value, not one of these elements";
                case ScheduleFieldType.Material:
                case ScheduleFieldType.MaterialQuantity:
                case ScheduleFieldType.StructuralMaterial:
                    return "A value of a material — change it in the material's properties";
                case ScheduleFieldType.RevitLinkInstance:
                case ScheduleFieldType.RevitLinkType:
                    return "A property of the link";
                case ScheduleFieldType.ViewBased:
                    return "A value Revit works out from the view";
                case ScheduleFieldType.PhysicalInstance:
                case ScheduleFieldType.PhysicalType:
                case ScheduleFieldType.Analytical:
                    return "A value of the matching analytical or physical element";
                default:
                    return "Revit does not let this column be written from outside";
            }
        }

        // ───────────────────────────── the body as drawn ─────────────────────────────

        private void ReadLayout(ViewSchedule schedule)
        {
            try
            {
                schedule.RefreshData();
                var data = schedule.GetTableData();

                var titles = new List<string>();
                var header = data.GetSectionData(SectionType.Header);
                if (header != null && header.NumberOfRows > 0)
                {
                    for (var row = header.FirstRowNumber; row <= header.LastRowNumber; row++)
                    {
                        var texts = new List<string>();
                        for (var column = header.FirstColumnNumber; column <= header.LastColumnNumber; column++)
                        {
                            var text = Clean(schedule.GetCellText(SectionType.Header, row, column));
                            if (text.Length > 0 && !texts.Contains(text))
                                texts.Add(text);
                        }

                        if (texts.Count > 0)
                            titles.Add(string.Join("   ", texts));
                    }
                }

                TitleLines = titles;

                var body = data.GetSectionData(SectionType.Body);
                if (body == null)
                    return;

                if (body.NumberOfColumns != Columns.Count)
                {
                    LayoutProblem = "the schedule draws " + body.NumberOfColumns + " columns for its " + Columns.Count + " visible fields";
                    return;
                }

                var rows = new List<ScheduleDisplayRow>();
                for (var row = body.FirstRowNumber; row <= body.LastRowNumber; row++)
                {
                    var cells = new string[Columns.Count];
                    for (var column = 0; column < Columns.Count; column++)
                        cells[column] = schedule.GetCellText(SectionType.Body, row, body.FirstColumnNumber + column) ?? string.Empty;

                    rows.Add(new ScheduleDisplayRow(cells));
                }

                DisplayRows = rows;
            }
            catch (Exception exception)
            {
                DisplayRows = null;
                LayoutProblem = "Revit would not hand over the schedule as drawn (" + LinkCatalog.Short(exception.Message) + ")";
            }
        }

        /// <summary>
        /// Finds the element rows among the lines as drawn. Both come from Revit's own formatting of
        /// the same fields, so an element row reads the same in both, in the same order; headings,
        /// group lines and totals are the lines in between. If the two cannot be lined up all the way
        /// through, the drawn layout is given up rather than guessed — the export then writes the
        /// plain table, which is always right.
        /// </summary>
        private void Align()
        {
            if (DisplayRows == null)
                return;

            var next = 0;
            foreach (var line in DisplayRows)
            {
                if (next < Rows.Count && Same(line.Cells, Rows[next].Cells))
                    line.DataRow = next++;
            }

            if (next != Rows.Count)
            {
                LayoutProblem = "only " + next + " of its " + Rows.Count + " rows could be found among the lines Revit draws";
                DisplayRows = null;
                return;
            }

            // The headings are the lines down to the one that reads exactly the column headings —
            // grouped headings sit above it. Without such a line ("Show headers" off) there are none.
            var firstData = DisplayRows.TakeWhile(line => line.DataRow < 0).Count();
            var headings = Columns.Select(column => column.Heading).ToList();

            for (var i = Math.Min(firstData, DisplayRows.Count) - 1; i >= 0; i--)
            {
                if (!Same(DisplayRows[i].Cells, headings))
                    continue;

                for (var j = 0; j <= i; j++)
                    DisplayRows[j].IsHeading = true;
                break;
            }
        }

        private static bool Same(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left.Count != right.Count)
                return false;

            for (var i = 0; i < left.Count; i++)
            {
                if (!string.Equals(Clean(left[i]), Clean(right[i]), StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        // ───────────────────────────── which elements stand in each row ─────────────────────────────

        private sealed class ProbeRow
        {
            public string[] Cells;
            public string Key;
            public string Token;
        }

        private sealed class Run
        {
            public Run(string key)
            {
                Key = key;
            }

            public string Key { get; }
            public List<ElementId> Ids { get; } = new List<ElementId>();
            public int Unknown { get; set; }
        }

        private void Probe(Document doc, ViewSchedule schedule, IReadOnlyList<ScheduleFieldId> valueFields)
        {
            var elementIds = ScheduleElements(doc, schedule);
            var headings = Columns.Select(column => column.Heading).ToList();

            using (var transaction = new Transaction(doc, "Read schedule rows"))
            {
                // Nothing of this transaction is kept, and nothing of it may surface either: a warning
                // dialog about a parameter that never existed would only confuse.
                var options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(new WarningSuppressor());
                options.SetClearAfterRollback(true);
                transaction.SetFailureHandlingOptions(options);

                transaction.Start();

                try
                {
                    var definition = schedule.Definition;
                    var sortFields = Flatten(definition);

                    ScheduleFieldId keyField = null;
                    Dictionary<string, ElementId> tokens = null;

                    try
                    {
                        keyField = AddRowKeys(doc, schedule, elementIds, out tokens);
                    }
                    catch (Exception exception)
                    {
                        TieProblem = exception is InvalidOperationException
                            ? exception.Message
                            : "the rows could not be asked which elements they stand for (" + LinkCatalog.Short(exception.Message) + ")";
                    }

                    doc.Regenerate();
                    var current = ReadRows(schedule, valueFields, sortFields, keyField, headings);

                    if (keyField == null)
                    {
                        Rows = current.Select((row, index) => new ScheduleDataRow(index, row.Cells, null, TieProblem)).ToList();
                    }
                    else if (definition.IsItemized)
                    {
                        Rows = current.Select((row, index) => tokens.TryGetValue(row.Token, out var id)
                                ? new ScheduleDataRow(index, row.Cells, new[] { id }, null)
                                : new ScheduleDataRow(index, row.Cells, null, UnknownElements))
                            .ToList();
                    }
                    else
                    {
                        definition.IsItemized = true;
                        doc.Regenerate();
                        Rows = TieGroups(current, ReadRows(schedule, null, sortFields, keyField, null), tokens);
                    }
                }
                finally
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                        transaction.RollBack();
                }
            }
        }

        /// <summary>
        /// The elements the schedule shows. A view collector on a schedule returns exactly its rows'
        /// elements; where it returns nothing, every element of the schedule's category is taken —
        /// extra elements merely get a key no row shows.
        /// </summary>
        private static IReadOnlyList<ElementId> ScheduleElements(Document doc, ViewSchedule schedule)
        {
            try
            {
                var ids = new FilteredElementCollector(doc, schedule.Id).ToElementIds();
                if (ids.Count > 0)
                    return ids.ToList();
            }
            catch (Exception)
            {
            }

            try
            {
                var categoryId = schedule.Definition.CategoryId;
                if (categoryId != null && categoryId != ElementId.InvalidElementId)
                    return new FilteredElementCollector(doc).OfCategoryId(categoryId).WhereElementIsNotElementType().ToElementIds().ToList();
            }
            catch (Exception)
            {
            }

            return new List<ElementId>();
        }

        /// <summary>
        /// Leaves only element rows in the body — no headings, group headers and footers, blank lines
        /// or grand total — and brings the sorting/grouping fields into view, since their values are
        /// what a grouped row is recognised by. Returns those fields.
        /// </summary>
        private static List<ScheduleFieldId> Flatten(ScheduleDefinition definition)
        {
            try { definition.ShowHeaders = false; }
            catch (Exception) { }

            try { definition.ShowGrandTotal = false; }
            catch (Exception) { }

            var sorts = definition.GetSortGroupFields();
            foreach (var sort in sorts)
            {
                sort.ShowHeader = false;
                sort.ShowFooter = false;
                sort.ShowBlankLine = false;
            }

            try { definition.SetSortGroupFields(sorts); }
            catch (Exception) { }

            var ids = new List<ScheduleFieldId>();
            foreach (var sort in sorts)
            {
                try
                {
                    var field = definition.GetField(sort.FieldId);
                    if (field.IsHidden)
                        field.IsHidden = false;
                }
                catch (Exception)
                {
                }

                ids.Add(sort.FieldId);
            }

            return ids;
        }

        /// <summary>
        /// Binds the temporary key parameter, writes every element's key into it and adds it to the
        /// schedule as a column. Every refusal is an <see cref="InvalidOperationException"/> whose
        /// message is fit for the window.
        /// </summary>
        private static ScheduleFieldId AddRowKeys(
            Document doc,
            ViewSchedule schedule,
            IReadOnlyList<ElementId> elementIds,
            out Dictionary<string, ElementId> tokens)
        {
            tokens = new Dictionary<string, ElementId>(StringComparer.Ordinal);

            var definition = schedule.Definition;
            var elements = elementIds.Select(doc.GetElement).Where(element => element != null).ToList();

            var categories = new Dictionary<ElementId, Category>();
            foreach (var category in elements.Select(element => element.Category))
                Allow(categories, category);

            Allow(categories, Safe(() => Category.GetCategory(doc, definition.CategoryId), null));

            if (categories.Count == 0)
                throw new InvalidOperationException("Revit lets no project parameter onto this schedule's categories, so its rows cannot be tied to elements");

            var guid = BindKeyParameter(doc, categories.Values.ToList());

            var parameterElement = SharedParameterElement.Lookup(doc, guid)
                                   ?? throw new InvalidOperationException("the temporary key parameter did not appear in the project");

            // Copies of a model group share a parameter's value unless it may vary between them. A key
            // shared by two copies would tie a row to the wrong one of them.
            try { parameterElement.GetDefinition().SetAllowVaryBetweenGroups(doc, true); }
            catch (Exception) { }

            var number = 0;
            foreach (var element in elements)
            {
                var parameter = element.get_Parameter(guid);
                if (parameter == null || parameter.IsReadOnly)
                    continue;

                var token = KeyPrefix + (++number).ToString(CultureInfo.InvariantCulture);
                try
                {
                    if (parameter.Set(token))
                        tokens[token] = element.Id;
                }
                catch (Exception)
                {
                }
            }

            // A key counts only if it stayed on its own element and on no other — the read-back that
            // catches a value copied across model groups despite the setting above.
            var holders = elements
                .GroupBy(element => Safe(() => element.get_Parameter(guid)?.AsString(), null) ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

            foreach (var pair in tokens.ToList())
            {
                if (!holders.TryGetValue(pair.Key, out var holding) || holding.Count != 1 || holding[0].Id != pair.Value)
                    tokens.Remove(pair.Key);
            }

            if (tokens.Count == 0)
                throw new InvalidOperationException("none of the schedule's elements would take the temporary key, so its rows cannot be tied to elements");

            doc.Regenerate();

            var schedulable = definition.GetSchedulableFields()
                .Where(field => field.ParameterId == parameterElement.Id)
                .OrderBy(field => field.FieldType == ScheduleFieldType.Instance ? 0 : 1)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("the schedule would not take the temporary key column, so its rows cannot be tied to elements");

            var added = definition.AddField(schedulable);
            try { added.ColumnHeading = HeadingMarker; }
            catch (Exception) { }

            return added.FieldId;
        }

        private static void Allow(Dictionary<ElementId, Category> categories, Category category)
        {
            if (category == null || categories.ContainsKey(category.Id))
                return;

            if (Safe(() => category.AllowsBoundParameters, false))
                categories[category.Id] = category;
        }

        /// <summary>
        /// Creates the key parameter in a throwaway shared parameter file and binds it to the given
        /// categories. The file Revit is configured with is swapped out only for this and put back
        /// right after — the same swap "Parameter Sets" makes, for the same reason: the API cannot
        /// create a definition anywhere else. The binding itself goes with the rolled-back transaction.
        /// </summary>
        private static Guid BindKeyParameter(Document doc, IReadOnlyList<Category> categories)
        {
            var app = doc.Application;
            var original = Safe(() => app.SharedParametersFilename, null) ?? string.Empty;
            var path = Path.Combine(Path.GetTempPath(), "VladTools-rowkey-" + Guid.NewGuid().ToString("N") + ".txt");

            File.WriteAllText(path, string.Empty);

            try
            {
                app.SharedParametersFilename = path;

                var file = app.OpenSharedParameterFile()
                           ?? throw new InvalidOperationException("Revit would not open a temporary shared parameter file, so the rows cannot be tied to elements");

                var group = file.Groups.Create("VladTools");
                var name = "VladTools row key " + Guid.NewGuid().ToString("N").Substring(0, 8);
                var definition = group.Definitions.Create(new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text)) as ExternalDefinition
                                 ?? throw new InvalidOperationException("Revit would not create the temporary key parameter");

                var set = app.Create.NewCategorySet();
                foreach (var category in categories)
                    set.Insert(category);

                if (!doc.ParameterBindings.Insert(definition, app.Create.NewInstanceBinding(set)))
                    throw new InvalidOperationException("Revit would not bind the temporary key parameter to the schedule's categories");

                return definition.GUID;
            }
            finally
            {
                try { app.SharedParametersFilename = original; }
                catch (Exception) { }

                try { File.Delete(path); }
                catch (Exception) { }
            }
        }

        private List<ProbeRow> ReadRows(
            ViewSchedule schedule,
            IReadOnlyList<ScheduleFieldId> valueFields,
            IReadOnlyList<ScheduleFieldId> sortFields,
            ScheduleFieldId keyField,
            IReadOnlyList<string> headings)
        {
            var definition = schedule.Definition;
            var order = definition.GetFieldOrder().Where(id => !definition.GetField(id).IsHidden).ToList();

            int ColumnOf(ScheduleFieldId id) => order.FindIndex(other => other == id);

            var valueColumns = valueFields?.Select(ColumnOf).ToArray();
            var sortColumns = sortFields.Select(ColumnOf).ToArray();
            var keyColumn = keyField == null ? -1 : ColumnOf(keyField);

            // The definition was just changed: the rows must be rebuilt from it, not read from before.
            schedule.RefreshData();
            var body = schedule.GetTableData().GetSectionData(SectionType.Body);
            var firstColumn = body.FirstColumnNumber;

            string Text(int row, int column) =>
                column < 0 ? string.Empty : schedule.GetCellText(SectionType.Body, row, firstColumn + column) ?? string.Empty;

            // Should "Show headers" refuse to go off, the headings are still recognised and stepped over:
            // by the key column's own heading, or failing that by a line that reads the column headings.
            var first = body.FirstRowNumber;
            for (var row = body.FirstRowNumber; row <= Math.Min(body.LastRowNumber, body.FirstRowNumber + 20); row++)
            {
                var isHeading = keyColumn >= 0
                    ? Text(row, keyColumn) == HeadingMarker
                    : valueColumns != null && headings != null && Same(valueColumns.Select(column => Text(row, column)).ToList(), headings);

                if (isHeading)
                {
                    first = row + 1;
                    break;
                }
            }

            var rows = new List<ProbeRow>();
            for (var row = first; row <= body.LastRowNumber; row++)
            {
                rows.Add(new ProbeRow
                {
                    Cells = valueColumns?.Select(column => Text(row, column)).ToArray(),
                    Key = string.Join(KeySeparator.ToString(), sortColumns.Select(column => Clean(Text(row, column)))),
                    Token = keyColumn < 0 ? string.Empty : Text(row, keyColumn).Trim()
                });
            }

            return rows;
        }

        /// <summary>
        /// Gives every grouped row the elements of the itemized rows carrying its sorting/grouping
        /// values. Itemized rows come sorted by the same fields, so the elements of one grouped row
        /// stand together; when the two lists line up run for run, that order alone settles it. Where
        /// they do not (a rounded value can show two groups the same), the values are matched instead,
        /// and a grouped row whose values repeat is left untied rather than given a guess.
        /// </summary>
        private static List<ScheduleDataRow> TieGroups(List<ProbeRow> grouped, List<ProbeRow> itemized, IReadOnlyDictionary<string, ElementId> tokens)
        {
            var runs = new List<Run>();
            foreach (var row in itemized)
            {
                if (runs.Count == 0 || runs[runs.Count - 1].Key != row.Key)
                    runs.Add(new Run(row.Key));

                var run = runs[runs.Count - 1];
                if (tokens.TryGetValue(row.Token, out var id))
                    run.Ids.Add(id);
                else
                    run.Unknown++;
            }

            var assigned = new Run[grouped.Count];

            if (runs.Count == grouped.Count && runs.Select(run => run.Key).SequenceEqual(grouped.Select(row => row.Key), StringComparer.Ordinal))
            {
                for (var i = 0; i < grouped.Count; i++)
                    assigned[i] = runs[i];
            }
            else
            {
                var merged = new Dictionary<string, Run>(StringComparer.Ordinal);
                foreach (var run in runs)
                {
                    if (!merged.TryGetValue(run.Key, out var into))
                    {
                        into = new Run(run.Key);
                        merged[run.Key] = into;
                    }

                    into.Ids.AddRange(run.Ids);
                    into.Unknown += run.Unknown;
                }

                var repeated = grouped.GroupBy(row => row.Key, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToList();

                for (var i = 0; i < grouped.Count; i++)
                {
                    if (!repeated.Contains(grouped[i].Key) && merged.TryGetValue(grouped[i].Key, out var run))
                        assigned[i] = run;
                }
            }

            var rows = new List<ScheduleDataRow>();
            for (var i = 0; i < grouped.Count; i++)
            {
                var run = assigned[i];
                string reason = null;

                if (run == null)
                    reason = "the row could not be told apart from another one with the same sorting/grouping values";
                else if (run.Unknown > 0)
                    reason = run.Unknown + " of its " + (run.Unknown + run.Ids.Count) + " elements could not be identified — elements of a linked model, " +
                             "or of a category Revit lets no project parameter onto";

                rows.Add(new ScheduleDataRow(i, grouped[i].Cells, reason == null ? run.Ids : null, reason));
            }

            return rows;
        }

        private static T Safe<T>(Func<T> read, T fallback)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
