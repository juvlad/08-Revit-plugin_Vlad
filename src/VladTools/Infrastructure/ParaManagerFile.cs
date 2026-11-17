using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>One category a parameter of the file goes onto, as the running Revit knows it.</summary>
    internal sealed class BatchCategory
    {
        public BatchCategory(BuiltInCategory builtIn, string label)
        {
            BuiltIn = builtIn;
            Label = label ?? string.Empty;
        }

        public BuiltInCategory BuiltIn { get; }

        /// <summary>The category as the file names it — the user's own words for it in the report.</summary>
        public string Label { get; }
    }

    /// <summary>
    /// One parameter of a DiRoots ParaManager export: what the file says about it, and what that turned out to
    /// be in the Revit running — the spec, the group and the categories as ids rather than words.
    /// </summary>
    internal sealed class BatchParameter
    {
        /// <summary>The row of the file, from 1 — how the user finds the line in Excel.</summary>
        public int Row { get; set; }

        public Guid Guid { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>"Type of Parameter" as the file writes it ("Текст", "Длина").</summary>
        public string TypeLabel { get; set; } = string.Empty;

        public string DisciplineLabel { get; set; } = string.Empty;

        /// <summary>The spec the definition is created with; null when nothing in this Revit answers to the file's words.</summary>
        public ForgeTypeId Spec { get; set; }

        /// <summary>"Group Under" as the file writes it.</summary>
        public string GroupLabel { get; set; } = string.Empty;

        /// <summary>The parameter group; empty is Revit's "Other".</summary>
        public ForgeTypeId Group { get; set; } = new ForgeTypeId();

        public bool IsInstance { get; set; } = true;

        public List<BatchCategory> Categories { get; } = new List<BatchCategory>();

        /// <summary>Categories the file names that this Revit has no category for — a newer Revit's, as a rule.</summary>
        public List<string> UnknownCategories { get; } = new List<string>();

        /// <summary>"Values can vary by group instance"; meaningless for a type parameter.</summary>
        public bool VariesAcrossGroups { get; set; }

        public string Description { get; set; } = string.Empty;

        public bool UserModifiable { get; set; } = true;

        public bool Visible { get; set; } = true;

        public bool HideWhenNoValue { get; set; }

        /// <summary>Why the parameter cannot be added at all; empty when it can.</summary>
        public string Problem { get; set; } = string.Empty;

        /// <summary>What is worth knowing about the row that does not stop it.</summary>
        public List<string> Notes { get; } = new List<string>();

        public bool IsUsable => Problem.Length == 0;
    }

    /// <summary>What reading one ParaManager file gave.</summary>
    internal sealed class ParameterFileScan
    {
        public ParameterFileScan(string path)
        {
            Path = path ?? string.Empty;
        }

        public string Path { get; }

        /// <summary>Why the file gave no parameters at all; empty when it was read.</summary>
        public string Error { get; set; } = string.Empty;

        public List<BatchParameter> Parameters { get; } = new List<BatchParameter>();

        /// <summary>About the file as a whole — the missing translation sheet, the Revit it came from.</summary>
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// Reads the Excel file DiRoots ParaManager exports its parameter list to ("Export to Excel"), and turns
    /// its words into what the Revit API takes.
    ///
    /// The "Parameter List" sheet is one row per parameter: GUID, name, "Shared Parameter", discipline, type,
    /// group, Instance/Type, the categories comma separated, user modifiable, visible, description, "hide when
    /// no value", and the group-instance rule. The columns are found by their headings, not their letters.
    ///
    /// **The words are the Revit's ParaManager ran in — Russian on this office's files — and are never trusted
    /// as words.** ParaManager writes its own dictionary next to the list, on the hidden sheet
    /// "DiRootsTranslationMapping": every category with its id (the <see cref="BuiltInCategory"/> value), every
    /// spec with its ForgeTypeId and discipline, every group with its <c>PG_…</c> name. So a category is taken
    /// by its id, a type by its ForgeTypeId, a group by its built-in name — the same in a Russian, English or
    /// German Revit. Only where that sheet is missing (or does not name something) are the words matched
    /// against this Revit's own labels, which works only when both speak the same language.
    ///
    /// **A ForgeTypeId's version is not part of its identity.** The sheet was written by Revit 2024
    /// (<c>autodesk.spec.aec:length-2.0.1</c>), and another year may know the same spec under another version
    /// suffix. A spec is therefore looked up among this Revit's own by <c>ForgeTypeId.NameEquals</c> — the
    /// comparison Revit itself uses for <c>==</c> — and this Revit's instance is the one used.
    /// </summary>
    internal static class ParaManagerFile
    {
        private const string ListSheet = "Parameter List";
        private const string MappingSheet = "DiRootsTranslationMapping";

        /// <summary>Reads and resolves the file. Never throws: a file that cannot be used says why in <see cref="ParameterFileScan.Error"/>.</summary>
        public static ParameterFileScan Read(string path)
        {
            var scan = new ParameterFileScan(path);

            IReadOnlyList<XlsxReadSheet> sheets;

            try
            {
                sheets = XlsxReader.Read(path);
            }
            catch (Exception exception)
            {
                scan.Error = "the file could not be read: " + LinkCatalog.Short(exception.Message);
                return scan;
            }

            var table = FindList(sheets);
            if (table == null)
            {
                scan.Error = "none of its sheets has the columns of a ParaManager export (\"GUID\", \"Parameter Name\", " +
                             "\"Instance/Type\", \"Categories\"…). Export the parameters from DiRoots ParaManager to Excel and pick that file.";
                return scan;
            }

            var mapping = Mapping.Read(sheets);
            if (mapping.IsEmpty)
            {
                scan.Notes.Add("The file has no DiRoots translation sheet, so types, groups and categories are matched by their " +
                               "names against this Revit's own — that works only when the file was written in the language this Revit runs in.");
            }
            else if (mapping.RevitVersion.Length > 0)
            {
                scan.Notes.Add("Written by ParaManager in Revit " + mapping.RevitVersion + ".");
            }

            var resolver = new Resolver(mapping);
            var byGuid = new Dictionary<Guid, BatchParameter>();
            var byName = new Dictionary<string, BatchParameter>(StringComparer.Ordinal);

            for (var row = table.Header + 1; row < table.Sheet.RowCount; row++)
            {
                if (table.Sheet.IsRowEmpty(row))
                    continue;

                var parameter = ReadRow(table, row, resolver);
                if (parameter == null)
                    continue;

                // Two rows for one parameter would bind it twice with two answers; two parameters of one name
                // would put two of that name in every model. The first row wins, the second says why it did not.
                if (parameter.IsUsable)
                {
                    BatchParameter first;
                    if (byGuid.TryGetValue(parameter.Guid, out first))
                        parameter.Problem = "the same GUID as row " + first.Row + " (\"" + first.Name + "\")";
                    else if (byName.TryGetValue(parameter.Name, out first))
                        parameter.Problem = "the same name as row " + first.Row + ", with another GUID — two parameters of one name in a model are never told apart";
                    else
                    {
                        byGuid[parameter.Guid] = parameter;
                        byName[parameter.Name] = parameter;
                    }
                }

                scan.Parameters.Add(parameter);
            }

            if (scan.Parameters.Count == 0)
                scan.Error = "the sheet \"" + table.Sheet.Name + "\" lists no parameters under its headings.";

            return scan;
        }

        // ───────────────────────────── one row ─────────────────────────────

        private static BatchParameter ReadRow(ListTable table, int row, Resolver resolver)
        {
            string Cell(string column) => table.Text(row, column);

            var name = Cell(Columns.Name);
            var guidText = Cell(Columns.Guid);

            // A row of nothing but leftovers (a stray value in a column the export does not fill) is no parameter.
            if (name.Length == 0 && guidText.Length == 0)
                return null;

            var parameter = new BatchParameter
            {
                Row = row + 1,
                Name = name,
                TypeLabel = Cell(Columns.Type),
                DisciplineLabel = Cell(Columns.Discipline),
                GroupLabel = Cell(Columns.Group),
                Description = Description(Cell(Columns.Description)),
                UserModifiable = !IsNo(Cell(Columns.UserModifiable)),
                Visible = !IsNo(Cell(Columns.Visible)),
                HideWhenNoValue = IsYes(Cell(Columns.HideWhenNoValue)),
                VariesAcrossGroups = Varies(Cell(Columns.Groups))
            };

            var problems = new List<string>();

            if (name.Length == 0)
                problems.Add("no name");

            var kind = Cell(Columns.Kind);
            if (kind.Length > 0 && !IsShared(kind))
            {
                problems.Add("it is a \"" + kind + "\", not a shared parameter — the Revit API cannot create a project parameter " +
                             "that is not shared; make it shared in ParaManager");
            }

            Guid guid;
            if (guidText.Length == 0)
                problems.Add("no GUID — ParaManager gives a new row its GUID only when the file is imported back, and the same parameter needs one GUID in every model");
            else if (!Guid.TryParse(guidText, out guid))
                problems.Add("\"" + guidText + "\" is not a GUID");
            else
                parameter.Guid = guid;

            var binding = Cell(Columns.Binding);
            if (IsWord(binding, "instance", "экземпляр"))
                parameter.IsInstance = true;
            else if (IsWord(binding, "type", "тип"))
                parameter.IsInstance = false;
            else
                problems.Add(binding.Length == 0 ? "\"Instance/Type\" is empty" : "\"Instance/Type\" reads \"" + binding + "\"");

            string specProblem;
            parameter.Spec = resolver.Spec(parameter.TypeLabel, parameter.DisciplineLabel, out specProblem);
            if (parameter.Spec == null)
                problems.Add(specProblem);

            string groupNote;
            parameter.Group = resolver.Group(parameter.GroupLabel, out groupNote);
            if (groupNote.Length > 0)
                parameter.Notes.Add(groupNote);

            foreach (var label in SplitCategories(Cell(Columns.Categories)))
            {
                var builtIn = resolver.Category(label);
                if (builtIn.HasValue)
                {
                    if (parameter.Categories.All(known => known.BuiltIn != builtIn.Value))
                        parameter.Categories.Add(new BatchCategory(builtIn.Value, label));
                }
                else
                {
                    parameter.UnknownCategories.Add(label);
                }
            }

            if (parameter.Categories.Count == 0)
            {
                problems.Add(parameter.UnknownCategories.Count == 0
                    ? "no categories"
                    : "none of its categories is known to this Revit (" + string.Join(", ", parameter.UnknownCategories) + ")");
            }
            else if (parameter.UnknownCategories.Count > 0)
            {
                parameter.Notes.Add("not known to this Revit, left out: " + string.Join(", ", parameter.UnknownCategories));
            }

            parameter.Problem = string.Join("; ", problems);
            return parameter;
        }

        private static bool IsShared(string kind)
        {
            return kind.IndexOf("shared", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   kind.IndexOf("общ", StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private static bool IsYes(string text)
        {
            return IsWord(text, "yes", "да", "true", "1");
        }

        private static bool IsNo(string text)
        {
            return IsWord(text, "no", "нет", "false", "0");
        }

        private static bool IsWord(string text, params string[] words)
        {
            var value = (text ?? string.Empty).Trim();
            return words.Any(word => string.Equals(value, word, StringComparison.CurrentCultureIgnoreCase));
        }

        /// <summary>"Values can vary by group instance" against "Values are aligned per group type".</summary>
        private static bool Varies(string text)
        {
            return (text ?? string.Empty).IndexOf("vary", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (text ?? string.Empty).IndexOf("различ", StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        /// <summary>
        /// The categories, comma separated. A sub-category row of Revit's own dialog reads "Roofs | Gutters" and is
        /// one category — the comma is the only separator.
        /// </summary>
        private static IEnumerable<string> SplitCategories(string text)
        {
            return (text ?? string.Empty)
                .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0);
        }

        /// <summary>
        /// Excel keeps a carriage return in a shared string as "_x000D_"; the reader hands it over as it stands.
        /// The description goes into the parameter's tooltip, so it is turned back into text, one line break each.
        /// </summary>
        private static string Description(string text)
        {
            var decoded = Regex.Replace(text ?? string.Empty, "_x([0-9A-Fa-f]{4})_",
                match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

            return decoded.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        }

        // ───────────────────────────── the parameter sheet ─────────────────────────────

        private static class Columns
        {
            public const string Guid = "guid";
            public const string Name = "parameter name";
            public const string Kind = "parameter type";
            public const string Discipline = "discipline";
            public const string Type = "type of parameter";
            public const string Group = "group under";
            public const string Binding = "instance/type";
            public const string Categories = "categories";
            public const string UserModifiable = "user modifiable";
            public const string Visible = "visibility";
            public const string Description = "description";
            public const string HideWhenNoValue = "hide when no value";
            public const string Groups = "groups";
        }

        private sealed class ListTable
        {
            public XlsxReadSheet Sheet;
            public int Header;
            public Dictionary<string, int> Columns = new Dictionary<string, int>(StringComparer.Ordinal);

            public string Text(int row, string column)
            {
                int index;
                return Columns.TryGetValue(column, out index) ? (Sheet.Text(row, index) ?? string.Empty).Trim() : string.Empty;
            }
        }

        /// <summary>
        /// The sheet of the list: "Parameter List" first, then any sheet with the headings in one of its first rows —
        /// a user may well have renamed the sheet or moved the list.
        /// </summary>
        private static ListTable FindList(IReadOnlyList<XlsxReadSheet> sheets)
        {
            var ordered = sheets
                .OrderBy(sheet => string.Equals(sheet.Name, ListSheet, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(sheet => sheet.IsHidden ? 1 : 0);

            foreach (var sheet in ordered)
            {
                for (var row = 0; row < Math.Min(10, sheet.RowCount); row++)
                {
                    var columns = new Dictionary<string, int>(StringComparer.Ordinal);

                    for (var column = 0; column < sheet.ColumnCount; column++)
                    {
                        var heading = Regex.Replace(sheet.Text(row, column) ?? string.Empty, @"\s+", " ").Trim().ToLowerInvariant();
                        if (heading.Length > 0 && !columns.ContainsKey(heading))
                            columns[heading] = column;
                    }

                    if (columns.ContainsKey(Columns.Guid) && columns.ContainsKey(Columns.Name) &&
                        columns.ContainsKey(Columns.Binding) && columns.ContainsKey(Columns.Categories))
                    {
                        return new ListTable { Sheet = sheet, Header = row, Columns = columns };
                    }
                }
            }

            return null;
        }

        // ───────────────────────────── the translation sheet ─────────────────────────────

        /// <summary>What "DiRootsTranslationMapping" says: the file's words for categories, specs and groups.</summary>
        private sealed class Mapping
        {
            public readonly Dictionary<string, int> Categories = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly List<KeyValuePair<string, string>> SpecNames = new List<KeyValuePair<string, string>>();
            public readonly List<string> SpecIds = new List<string>();
            public readonly Dictionary<string, string> Groups = new Dictionary<string, string>(StringComparer.Ordinal);

            public string RevitVersion = string.Empty;

            public bool IsEmpty => Categories.Count == 0 && SpecIds.Count == 0 && Groups.Count == 0;

            /// <summary>
            /// Every cell of the sheet that holds a JSON list is read, and each entry is told apart by its ForgeId rather
            /// than by the cell it sits in: the layout of a hidden sheet is ParaManager's to change.
            /// </summary>
            public static Mapping Read(IReadOnlyList<XlsxReadSheet> sheets)
            {
                var mapping = new Mapping();
                var sheet = sheets.FirstOrDefault(candidate => string.Equals(candidate.Name, MappingSheet, StringComparison.OrdinalIgnoreCase));
                if (sheet == null)
                    return mapping;

                for (var row = 0; row < sheet.RowCount; row++)
                {
                    for (var column = 0; column < sheet.ColumnCount; column++)
                    {
                        var text = (sheet.Text(row, column) ?? string.Empty).Trim();
                        if (text.Length == 0)
                            continue;

                        var node = Json.Parse(text);

                        if (node is Dictionary<string, object>)
                        {
                            var version = Json.Str(node, "RevitVersion");
                            if (version.Length > 0)
                                mapping.RevitVersion = version;
                            continue;
                        }

                        var items = node as object[];
                        if (items == null)
                            continue;

                        foreach (var item in items)
                            mapping.Add(item);
                    }
                }

                return mapping;
            }

            private void Add(object item)
            {
                var name = Json.Str(item, "Name").Trim();
                if (name.Length == 0)
                    return;

                var forgeId = Json.Str(item, "ForgeId").Trim();
                var discipline = Json.Str(item, "Discipline").Trim();

                if (forgeId.Length == 0)
                {
                    var id = Json.At(item, "Id");
                    if (id is double number && number < -1 && !Categories.ContainsKey(name))
                        Categories[name] = (int)number;
                    return;
                }

                if (forgeId.StartsWith("PG_", StringComparison.Ordinal) || forgeId == "INVALID")
                {
                    if (!Groups.ContainsKey(name))
                        Groups[name] = forgeId;
                    return;
                }

                if (forgeId.StartsWith("autodesk.spec", StringComparison.Ordinal) && forgeId.IndexOf("discipline", StringComparison.Ordinal) < 0)
                {
                    SpecNames.Add(new KeyValuePair<string, string>(name, discipline));
                    SpecIds.Add(forgeId);
                }
            }
        }

        // ───────────────────────────── words into ids ─────────────────────────────

        /// <summary>The file's words turned into this Revit's ids, each catalogue of this Revit read once, when first needed.</summary>
        private sealed class Resolver
        {
            private readonly Mapping _mapping;

            private List<ForgeTypeId> _specs;
            private List<ForgeTypeId> _groups;
            private Dictionary<string, BuiltInCategory> _categoryLabels;

            public Resolver(Mapping mapping)
            {
                _mapping = mapping;
            }

            // ── specs ──

            public ForgeTypeId Spec(string label, string discipline, out string problem)
            {
                problem = string.Empty;

                if (label.Length == 0)
                {
                    problem = "\"Type of Parameter\" is empty";
                    return null;
                }

                var specs = Specs();

                // The file's own dictionary first: its ForgeId, looked up among this Revit's specs by name, version aside.
                var candidates = new List<string>();
                for (var index = 0; index < _mapping.SpecNames.Count; index++)
                {
                    if (string.Equals(_mapping.SpecNames[index].Key, label, StringComparison.Ordinal))
                        candidates.Add(_mapping.SpecIds[index]);
                }

                if (candidates.Count > 1 && discipline.Length > 0)
                {
                    var narrowed = new List<string>();
                    for (var index = 0; index < _mapping.SpecNames.Count; index++)
                    {
                        if (string.Equals(_mapping.SpecNames[index].Key, label, StringComparison.Ordinal) &&
                            string.Equals(_mapping.SpecNames[index].Value, discipline, StringComparison.Ordinal))
                            narrowed.Add(_mapping.SpecIds[index]);
                    }

                    if (narrowed.Count > 0)
                        candidates = narrowed;
                }

                foreach (var candidate in candidates)
                {
                    var wanted = new ForgeTypeId(candidate);
                    var match = specs.FirstOrDefault(spec => spec.NameEquals(wanted));
                    if (match != null)
                        return match;
                }

                if (candidates.Count > 0)
                {
                    problem = "its type \"" + label + "\" (" + candidates[0] + ") is not one this Revit has";
                    return null;
                }

                // No dictionary entry: this Revit's own words, the discipline settling a name two disciplines share.
                var byLabel = specs.Where(spec => string.Equals(Safe(() => LabelUtils.GetLabelForSpec(spec)), label, StringComparison.CurrentCultureIgnoreCase)).ToList();

                if (byLabel.Count > 1 && discipline.Length > 0)
                {
                    var narrowed = byLabel.Where(spec => string.Equals(DisciplineOf(spec), discipline, StringComparison.CurrentCultureIgnoreCase)).ToList();
                    if (narrowed.Count > 0)
                        byLabel = narrowed;
                }

                if (byLabel.Count > 0)
                    return byLabel[0];

                problem = "its type \"" + label + "\"" + (discipline.Length > 0 ? " (" + discipline + ")" : string.Empty) +
                          " is not one this Revit knows by that name";
                return null;
            }

            /// <summary>
            /// Every spec this Revit has. <c>SpecUtils.GetAllSpecs</c> is the catalogue; the properties of
            /// <c>SpecTypeId</c>'s nested classes (Text, Integer, Yes/No, URL, Material…) are added in case it leaves the
            /// ones that measure nothing out — either way both are this Revit's own, with its own version suffixes.
            /// </summary>
            private List<ForgeTypeId> Specs()
            {
                if (_specs != null)
                    return _specs;

                var result = new List<ForgeTypeId>();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                void Take(ForgeTypeId spec)
                {
                    if (spec != null && !spec.Empty() && seen.Add(spec.TypeId))
                        result.Add(spec);
                }

                try
                {
                    foreach (var spec in SpecUtils.GetAllSpecs())
                        Take(spec);
                }
                catch (Exception)
                {
                }

                foreach (var type in new[] { typeof(SpecTypeId) }.Concat(typeof(SpecTypeId).GetNestedTypes(BindingFlags.Public)))
                {
                    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (property.PropertyType != typeof(ForgeTypeId))
                            continue;

                        try { Take(property.GetValue(null) as ForgeTypeId); }
                        catch (Exception) { }
                    }
                }

                _specs = result;
                return result;
            }

            private static string DisciplineOf(ForgeTypeId spec)
            {
                try
                {
                    return UnitUtils.IsMeasurableSpec(spec)
                        ? LabelUtils.GetLabelForDiscipline(UnitUtils.GetDiscipline(spec))
                        : string.Empty;
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }

            // ── groups ──

            /// <summary>
            /// The group, or "Other" with a note when it cannot be found: a group only decides where the parameter is
            /// listed in Properties, and it is no reason to leave a parameter out of every model.
            /// </summary>
            public ForgeTypeId Group(string label, out string note)
            {
                note = string.Empty;

                if (label.Length == 0)
                    return new ForgeTypeId();

                string builtInName;
                if (_mapping.Groups.TryGetValue(label, out builtInName))
                {
                    if (builtInName == "INVALID")
                        return new ForgeTypeId();

                    var byName = FromBuiltInName(builtInName);
                    if (byName != null)
                        return byName;
                }

                var byLabel = Groups().FirstOrDefault(group =>
                    string.Equals(Safe(() => LabelUtils.GetLabelForGroup(group)), label, StringComparison.CurrentCultureIgnoreCase));
                if (byLabel != null)
                    return byLabel;

                note = "its group \"" + label + "\" is not one this Revit has — the parameter goes under \"Other\"";
                return new ForgeTypeId();
            }

            private List<ForgeTypeId> Groups()
            {
                if (_groups != null)
                    return _groups;

                try { _groups = ParameterUtils.GetAllBuiltInGroups().ToList(); }
                catch (Exception) { _groups = new List<ForgeTypeId>(); }

                return _groups;
            }

            /// <summary>
            /// "PG_IDENTITY_DATA" as a group id. Revit 2022 and 2024 still have <c>BuiltInParameterGroup</c> and
            /// <c>ParameterUtils.GetParameterGroupTypeId</c>, which answer exactly; Revit 2025 removed both, so they are
            /// reached by reflection — one build for every year — and in 2025 the name is matched to a property of
            /// <c>GroupTypeId</c> instead: IDENTITY_DATA is IdentityData, PROFILE_1 is Profilen1. Whatever comes back
            /// must be one of this Revit's built-in groups.
            /// </summary>
            private ForgeTypeId FromBuiltInName(string builtInName)
            {
                ForgeTypeId found = null;

                try
                {
                    var enumType = typeof(ParameterUtils).Assembly.GetType("Autodesk.Revit.DB.BuiltInParameterGroup");
                    var method = enumType == null
                        ? null
                        : typeof(ParameterUtils).GetMethod("GetParameterGroupTypeId", BindingFlags.Public | BindingFlags.Static, null, new[] { enumType }, null);

                    if (method != null && Enum.GetNames(enumType).Contains(builtInName))
                        found = method.Invoke(null, new[] { Enum.Parse(enumType, builtInName) }) as ForgeTypeId;
                }
                catch (Exception)
                {
                    found = null;
                }

                if (found == null)
                {
                    var wanted = Squeeze(builtInName.StartsWith("PG_", StringComparison.Ordinal) ? builtInName.Substring(3) : builtInName);

                    foreach (var property in typeof(GroupTypeId).GetProperties(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (property.PropertyType != typeof(ForgeTypeId))
                            continue;

                        if (Squeeze(Regex.Replace(property.Name, "n(?=[0-9]+$)", string.Empty)) != wanted)
                            continue;

                        try { found = property.GetValue(null) as ForgeTypeId; }
                        catch (Exception) { found = null; }
                        break;
                    }
                }

                if (found == null || found.Empty())
                    return null;

                return Groups().FirstOrDefault(group => group.NameEquals(found));
            }

            private static string Squeeze(string name)
            {
                return (name ?? string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            }

            // ── categories ──

            public BuiltInCategory? Category(string label)
            {
                // Cast first, then asked: the id may name a category of a newer Revit than this one.
                int id;
                if (_mapping.Categories.TryGetValue(label, out id) && Enum.IsDefined(typeof(BuiltInCategory), (BuiltInCategory)id))
                    return (BuiltInCategory)id;

                // "Roofs | Gutters" is the gutters category; Revit's own label for it is the part after the bar.
                var own = label.Contains("|") ? label.Substring(label.LastIndexOf('|') + 1).Trim() : label;

                BuiltInCategory builtIn;
                if (CategoryLabels().TryGetValue(own, out builtIn))
                    return builtIn;

                return null;
            }

            /// <summary>This Revit's own name of every built-in category, for a file with no dictionary of its own.</summary>
            private Dictionary<string, BuiltInCategory> CategoryLabels()
            {
                if (_categoryLabels != null)
                    return _categoryLabels;

                // Built only when the file's own dictionary misses a name, once, in well under a second.
                _categoryLabels = new Dictionary<string, BuiltInCategory>(StringComparer.CurrentCultureIgnoreCase);

                foreach (BuiltInCategory builtIn in Enum.GetValues(typeof(BuiltInCategory)))
                {
                    if (builtIn == BuiltInCategory.INVALID)
                        continue;

                    var label = Safe(() => LabelUtils.GetLabelFor(builtIn));
                    if (label.Length > 0 && !_categoryLabels.ContainsKey(label))
                        _categoryLabels[label] = builtIn;
                }

                return _categoryLabels;
            }

            private static string Safe(Func<string> read)
            {
                try { return read() ?? string.Empty; }
                catch (Exception) { return string.Empty; }
            }
        }

        /// <summary>A short "Instance · Text · 12 categories" kind of line, for a report header.</summary>
        public static string Describe(BatchParameter parameter)
        {
            var builder = new StringBuilder();
            builder.Append(parameter.Name);
            builder.Append(" (").Append(parameter.IsInstance ? "instance" : "type");
            if (parameter.TypeLabel.Length > 0)
                builder.Append(", ").Append(parameter.TypeLabel);
            builder.Append(", ").Append(parameter.Categories.Count).Append(" categor").Append(parameter.Categories.Count == 1 ? "y" : "ies");
            builder.Append(")");
            return builder.ToString();
        }
    }
}
