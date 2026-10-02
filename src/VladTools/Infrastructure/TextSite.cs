using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// One place in the open project where a person typed text and Revit keeps it exactly as typed:
    /// a view's name, a parameter value, a schedule's column heading, a view filter rule, a text note.
    ///
    /// Revit translates only what is its own — categories, built-in parameters, system families, view
    /// types. Everything a person typed is a plain string inside the file, whatever language the
    /// interface runs in; that is why a template made in a Russian Revit stays Russian in an English
    /// one. The "Translate" button walks these places, and every kind of place knows how to read its
    /// text and write it back — that is all this class is.
    ///
    /// The text is kept **normalised** (<see cref="Normalize"/>): line breaks as "\n", no surrounding
    /// whitespace. That is the form the dictionary is keyed by, so a text note ending in Revit's own
    /// trailing "\r" and a parameter holding the same words meet in one row of the window.
    /// </summary>
    internal abstract class TextSite
    {
        private readonly string _lineBreak;

        protected TextSite(string raw, string owner, string kind, string group)
        {
            raw = raw ?? string.Empty;

            Text = Normalize(raw);
            Owner = owner ?? string.Empty;
            Kind = kind ?? string.Empty;
            Group = group ?? string.Empty;

            // Written back with the line breaks the place had: a text note keeps Revit's "\r".
            _lineBreak = raw.Contains("\r\n") ? "\r\n" : raw.Contains("\r") ? "\r" : "\n";
        }

        /// <summary>The text as the dictionary knows it.</summary>
        public string Text { get; }

        /// <summary>Who holds the text, for the window and the report: <c>Floor plan "План 1 этажа"</c>.</summary>
        public string Owner { get; }

        /// <summary>What the text is to its owner: "view name", <c>value of "Раздел"</c>, "column heading"…</summary>
        public string Kind { get; }

        /// <summary>The coarse heading the report counts under: "Names", "Parameter values"…</summary>
        public string Group { get; }

        /// <summary>An element name — Revit refuses certain characters in one and wants it unique.</summary>
        public virtual bool IsName => false;

        /// <summary>Why this place cannot be changed through the Revit API at all; null when it can.</summary>
        public virtual string FixedReason => null;

        /// <summary>What to do by hand when Revit refuses the change; null when there is nothing to add.</summary>
        public virtual string Hint => null;

        /// <summary>The place's current text, normalised; null when the place is gone.</summary>
        public string Read(Document doc)
        {
            var raw = ReadRaw(doc);
            return raw == null ? null : Normalize(raw);
        }

        /// <summary>
        /// The place holds this text right now. Checked before writing (the original is still there)
        /// and after it (the translation really landed — an edit that raised no error is not yet an
        /// edit that happened).
        /// </summary>
        public virtual bool Holds(Document doc, string text)
        {
            return Read(doc) == text;
        }

        /// <summary>Writes a normalised text back into the place. Throws when Revit refuses it.</summary>
        public void Write(Document doc, string text)
        {
            var normalized = Normalize(text);
            WriteRaw(doc, _lineBreak == "\n" ? normalized : normalized.Replace("\n", _lineBreak));
        }

        protected abstract string ReadRaw(Document doc);

        protected abstract void WriteRaw(Document doc, string raw);

        public static string Normalize(string raw)
        {
            return TranslationDictionary.Normalize(raw);
        }

        public static bool HasCyrillic(string text)
        {
            return TranslationDictionary.HasCyrillic(text);
        }
    }

    /// <summary>The name of an element — a view, a type, a family, a material, a filter, a level…</summary>
    internal sealed class ElementNameSite : TextSite
    {
        private readonly ElementId _id;
        private readonly string _hint;

        public ElementNameSite(Element element, string name, string owner, string kind, string hint = null)
            : base(name, owner, kind, "Names")
        {
            _id = element.Id;
            _hint = hint;
        }

        public override bool IsName => true;

        public override string Hint => _hint;

        protected override string ReadRaw(Document doc)
        {
            return doc.GetElement(_id)?.Name;
        }

        protected override void WriteRaw(Document doc, string raw)
        {
            var element = doc.GetElement(_id) ?? throw new InvalidOperationException("the element is no longer in the project");
            element.Name = raw;
        }
    }

    /// <summary>
    /// The value of a text parameter — a project or shared parameter, or a built-in one like a
    /// room's name, a sheet's number or a view's "Title on Sheet". This is also where the folders of
    /// the Project Browser come from: it groups views by such values.
    /// </summary>
    internal sealed class ParameterSite : TextSite
    {
        private readonly ElementId _elementId;
        private readonly ElementId _parameterId;

        public ParameterSite(Element element, Parameter parameter, string value, string owner)
            : base(value, owner, "value of \"" + parameter.Definition?.Name + "\"", "Parameter values")
        {
            _elementId = element.Id;
            _parameterId = parameter.Id;
        }

        protected override string ReadRaw(Document doc)
        {
            return Find(doc)?.AsString();
        }

        protected override void WriteRaw(Document doc, string raw)
        {
            var parameter = Find(doc) ?? throw new InvalidOperationException("the parameter is no longer on the element");

            if (parameter.IsReadOnly)
                throw new InvalidOperationException("the parameter is read-only now");

            if (!parameter.Set(raw))
                throw new InvalidOperationException("Revit refused the value");
        }

        /// <summary>
        /// A parameter's id is unique per definition — negative for a built-in one, the
        /// <c>ParameterElement</c>'s id for a project or shared one — so it finds the very parameter
        /// the scan saw, even where two parameters share a name.
        /// </summary>
        private Parameter Find(Document doc)
        {
            var element = doc.GetElement(_elementId);
            if (element == null)
                return null;

            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter.Id == _parameterId)
                    return parameter;
            }

            return null;
        }
    }

    /// <summary>A schedule column's heading. It starts out as the parameter's name, and stays whatever it was typed as.</summary>
    internal sealed class ColumnHeadingSite : TextSite
    {
        private readonly ElementId _scheduleId;
        private readonly ScheduleFieldId _fieldId;

        public ColumnHeadingSite(ViewSchedule schedule, ScheduleFieldId fieldId, string heading, string owner)
            : base(heading, owner, "column heading", "Schedule column headings")
        {
            _scheduleId = schedule.Id;
            _fieldId = fieldId;
        }

        protected override string ReadRaw(Document doc)
        {
            return Field(doc)?.ColumnHeading;
        }

        protected override void WriteRaw(Document doc, string raw)
        {
            var field = Field(doc) ?? throw new InvalidOperationException("the column is no longer in the schedule");
            field.ColumnHeading = raw;
        }

        private ScheduleField Field(Document doc)
        {
            var schedule = doc.GetElement(_scheduleId) as ViewSchedule;
            if (schedule == null)
                return null;

            try
            {
                return schedule.Definition.GetField(_fieldId);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// A plain-text cell in a schedule's header. Only <c>CellType.Text</c> cells are ever taken: per
    /// the API, writing into a parameter cell turns it into plain text — the cell would stop following
    /// the parameter it shows, which is a loss nobody asked for.
    /// </summary>
    internal sealed class ScheduleCellSite : TextSite
    {
        private readonly ElementId _scheduleId;
        private readonly int _row;
        private readonly int _column;

        public ScheduleCellSite(ViewSchedule schedule, int row, int column, string text, string owner)
            : base(text, owner, "schedule header text", "Schedule header cells")
        {
            _scheduleId = schedule.Id;
            _row = row;
            _column = column;
        }

        protected override string ReadRaw(Document doc)
        {
            return Header(doc)?.GetCellText(_row, _column);
        }

        protected override void WriteRaw(Document doc, string raw)
        {
            var header = Header(doc) ?? throw new InvalidOperationException("the schedule has no header any more");
            header.SetCellText(_row, _column, raw);
        }

        private TableSectionData Header(Document doc)
        {
            var schedule = doc.GetElement(_scheduleId) as ViewSchedule;
            return schedule?.GetTableData()?.GetSectionData(SectionType.Header);
        }
    }

    /// <summary>
    /// The value a schedule filter compares against ("Раздел equals АР"). It has to move together
    /// with the parameter value it matches, or the schedule silently empties.
    /// </summary>
    internal sealed class ScheduleFilterSite : TextSite
    {
        private readonly ElementId _scheduleId;
        private readonly int _index;

        public ScheduleFilterSite(ViewSchedule schedule, int index, string value, string owner)
            : base(value, owner, "schedule filter value", "Schedule filter values")
        {
            _scheduleId = schedule.Id;
            _index = index;
        }

        protected override string ReadRaw(Document doc)
        {
            var filter = Filter(doc, out _);
            return filter != null && filter.IsStringValue ? filter.GetStringValue() : null;
        }

        protected override void WriteRaw(Document doc, string raw)
        {
            var filter = Filter(doc, out var definition) ?? throw new InvalidOperationException("the filter is no longer in the schedule");

            // GetFilter hands out a copy: the edit only lands once it is put back.
            filter.SetValue(raw);
            definition.SetFilter(_index, filter);
        }

        private ScheduleFilter Filter(Document doc, out ScheduleDefinition definition)
        {
            definition = (doc.GetElement(_scheduleId) as ViewSchedule)?.Definition;
            if (definition == null || _index >= definition.GetFilterCount())
                return null;

            return definition.GetFilter(_index);
        }
    }

    /// <summary>
    /// A string a view filter's rules compare against. One site per distinct string in one filter;
    /// writing replaces that string in every rule of the filter that carries it.
    ///
    /// The rules are rebuilt rather than edited in place: <c>GetElementFilter</c> and
    /// <c>GetRules</c> hand out copies, and nothing changes until the rebuilt tree goes back through
    /// <c>SetElementFilter</c>. Logical nodes keep their own inversion because they are edited in
    /// place; a parameter node is recreated with its <c>Inverted</c> flag carried across.
    /// </summary>
    internal sealed class ViewFilterRuleSite : TextSite
    {
        private readonly ElementId _filterId;

        public ViewFilterRuleSite(ParameterFilterElement filter, string ruleText, string owner)
            : base(ruleText, owner, "view filter rule", "View filter rules")
        {
            _filterId = filter.Id;
        }

        /// <summary>Every rule string of the filter, in the order the rules stand.</summary>
        public static IEnumerable<string> RuleStrings(ParameterFilterElement filter)
        {
            var strings = new List<string>();
            Collect(filter.GetElementFilter(), strings);
            return strings;
        }

        private static void Collect(ElementFilter filter, List<string> strings)
        {
            if (filter is ElementLogicalFilter logical)
            {
                foreach (var child in logical.GetFilters())
                    Collect(child, strings);
            }
            else if (filter is ElementParameterFilter parameterFilter)
            {
                foreach (var rule in parameterFilter.GetRules())
                    Collect(rule, strings);
            }
        }

        private static void Collect(FilterRule rule, List<string> strings)
        {
            if (rule is FilterInverseRule inverse)
                Collect(inverse.GetInnerRule(), strings);
            else if (rule is FilterStringRule text)
                strings.Add(text.RuleString);
        }

        /// <summary>
        /// A filter holds many strings, and this site is one of them: "holds" means some rule
        /// compares against the text — the original before the write, the translation after it.
        /// </summary>
        public override bool Holds(Document doc, string text)
        {
            var filter = doc.GetElement(_filterId) as ParameterFilterElement;
            return filter != null && RuleStrings(filter).Any(rule => Normalize(rule) == text);
        }

        protected override string ReadRaw(Document doc)
        {
            var filter = doc.GetElement(_filterId) as ParameterFilterElement;
            if (filter == null)
                return null;

            return RuleStrings(filter).FirstOrDefault(text => Normalize(text) == Text);
        }

        protected override void WriteRaw(Document doc, string raw)
        {
            var filter = doc.GetElement(_filterId) as ParameterFilterElement
                         ?? throw new InvalidOperationException("the filter is no longer in the project");

            var root = filter.GetElementFilter() ?? throw new InvalidOperationException("the filter has no rules any more");
            var rebuilt = Visit(root, text => Normalize(text) == Text ? raw : null);

            filter.SetElementFilter(rebuilt);
        }

        /// <summary>
        /// Rebuilds the filter tree. <paramref name="replace"/> returns the new string for a rule, or
        /// null to leave it be; the tree that comes back carries every replacement.
        /// </summary>
        private static ElementFilter Visit(ElementFilter filter, Func<string, string> replace)
        {
            if (filter is ElementLogicalFilter logical)
            {
                var children = logical.GetFilters().Select(child => Visit(child, replace)).ToList();
                logical.SetFilters(children);
                return logical;
            }

            if (filter is ElementParameterFilter parameterFilter)
            {
                var rules = parameterFilter.GetRules().Select(rule => Visit(rule, replace)).ToList();
                return new ElementParameterFilter(rules, parameterFilter.Inverted);
            }

            return filter;
        }

        private static FilterRule Visit(FilterRule rule, Func<string, string> replace)
        {
            if (rule is FilterInverseRule inverse)
            {
                inverse.SetInnerRule(Visit(inverse.GetInnerRule(), replace));
                return inverse;
            }

            if (rule is FilterStringRule text)
            {
                var replacement = replace(text.RuleString);
                if (replacement != null)
                    text.RuleString = replacement;
            }

            return rule;
        }
    }

    /// <summary>
    /// A text note. Written through <c>FormattedText</c> rather than <c>TextNote.Text</c>: per the
    /// API, setting the plain text drops every bit of formatting on the note. Bold, italic, underline
    /// and all caps that cover the whole note are put back; formatting on only a part of it has no
    /// counterpart in a translated sentence, and such a note is marked so the report can say so.
    /// </summary>
    internal sealed class TextNoteSite : TextSite
    {
        private readonly ElementId _id;

        public TextNoteSite(TextNote note, string text, string owner)
            : base(text, owner, "text note", "Text notes")
        {
            _id = note.Id;
        }

        /// <summary>The note had formatting on only part of its text, and that formatting is gone.</summary>
        public bool FormattingReset { get; private set; }

        protected override string ReadRaw(Document doc)
        {
            return (doc.GetElement(_id) as TextNote)?.Text;
        }

        protected override void WriteRaw(Document doc, string raw)
        {
            var note = doc.GetElement(_id) as TextNote ?? throw new InvalidOperationException("the text note is no longer in the project");

            var formatted = note.GetFormattedText();

            var bold = formatted.GetBoldStatus();
            var italic = formatted.GetItalicStatus();
            var underline = formatted.GetUnderlineStatus();
            var allCaps = formatted.GetAllCapsStatus();
            var list = formatted.GetListType(formatted.AsTextRange());

            formatted.SetPlainText(raw);

            if (bold == FormatStatus.All)
                formatted.SetBoldStatus(true);
            if (italic == FormatStatus.All)
                formatted.SetItalicStatus(true);
            if (underline == FormatStatus.All)
                formatted.SetUnderlineStatus(true);
            if (allCaps == FormatStatus.All)
                formatted.SetAllCapsStatus(true);
            if (list != ListType.None && list != ListType.Mixed)
                formatted.SetListType(formatted.AsTextRange(), list);

            note.SetFormattedText(formatted);

            FormattingReset = bold == FormatStatus.Mixed || italic == FormatStatus.Mixed ||
                              underline == FormatStatus.Mixed || allCaps == FormatStatus.Mixed ||
                              list == ListType.Mixed;
        }
    }

    /// <summary>
    /// A user workset's name. Addressed by the workset's GUID, never its <c>WorksetId</c>: by
    /// Autodesk's own documentation the id changes on synchronising, the GUID does not — the same
    /// rule the "Worksets" and "Link Manager" buttons keep.
    /// </summary>
    internal sealed class WorksetNameSite : TextSite
    {
        private readonly Guid _uniqueId;

        public WorksetNameSite(Workset workset)
            : base(workset.Name, "Workset \"" + workset.Name + "\"", "workset name", "Names")
        {
            _uniqueId = workset.UniqueId;
        }

        public override bool IsName => true;

        public override string Hint => "a workset owned by another user cannot be renamed until they relinquish it";

        protected override string ReadRaw(Document doc)
        {
            return doc.GetWorksetTable().GetWorkset(_uniqueId)?.Name;
        }

        protected override void WriteRaw(Document doc, string raw)
        {
            var workset = doc.GetWorksetTable().GetWorkset(_uniqueId)
                          ?? throw new InvalidOperationException("the workset is no longer in the project");

            WorksetTable.RenameWorkset(doc, workset.Id, raw);
        }
    }

    /// <summary>
    /// A place the Revit API can read but not change: a shared parameter's name, a subcategory's.
    /// It is still collected — the window has to show what will stay Russian, and why, rather than
    /// leave it to be discovered after the translation.
    /// </summary>
    internal sealed class FixedTextSite : TextSite
    {
        private readonly string _reason;

        public FixedTextSite(string text, string owner, string kind, string reason)
            : base(text, owner, kind, "Fixed")
        {
            _reason = reason;
        }

        public override string FixedReason => _reason;

        protected override string ReadRaw(Document doc)
        {
            return Text;
        }

        protected override void WriteRaw(Document doc, string raw)
        {
            throw new InvalidOperationException(_reason);
        }
    }
}
