using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// A parameter as "the same parameter" across the elements of a category — what the "Copy
    /// Parameters" lists show, what the settings remember, and what is looked up on every element when
    /// the copy runs.
    ///
    /// Three identities, in the order Revit itself trusts them: a built-in parameter by its enum value, a
    /// shared parameter by its GUID, and anything else — a project parameter, or a family parameter that
    /// is not shared — by its name. A family parameter has no identity across families but its name:
    /// "Width" in one door family and "Width" in another are two definitions with nothing in common
    /// except what a person called them, and that is exactly what the user means by "the Width of every
    /// door". The instance/type level is part of the key: a parameter that is an instance one in one
    /// family and a type one in another is two entries, because it is written in two different places.
    /// </summary>
    internal sealed class ParameterKey
    {
        private const string Instance = "I";
        private const string Type = "T";

        private ParameterKey(bool isType, BuiltInParameter builtIn, Guid guid, string name, string text)
        {
            IsType = isType;
            BuiltIn = builtIn;
            Guid = guid;
            Name = name;
            Text = text;
        }

        /// <summary>The parameter sits on the element's type, not on the element.</summary>
        public bool IsType { get; }

        /// <summary><c>INVALID</c> unless the parameter is a built-in one.</summary>
        public BuiltInParameter BuiltIn { get; }

        /// <summary><c>Guid.Empty</c> unless the parameter is a shared one.</summary>
        public Guid Guid { get; }

        /// <summary>The name a project or family parameter is found by; empty for the other two kinds.</summary>
        public string Name { get; }

        /// <summary>The key as a line of text: "I|BIP|-1010106", "T|GUID|…", "I|NAME|Width".</summary>
        public string Text { get; }

        public static ParameterKey Of(Parameter parameter, bool isType)
        {
            var definition = parameter?.Definition;
            if (definition == null)
                return null;

            var level = isType ? Type : Instance;

            try
            {
                if (parameter.IsShared)
                {
                    var guid = parameter.GUID;
                    return new ParameterKey(isType, BuiltInParameter.INVALID, guid, string.Empty, level + "|GUID|" + guid.ToString("D"));
                }

                var builtIn = BuiltInOf(parameter);
                if (builtIn != BuiltInParameter.INVALID)
                    return new ParameterKey(isType, builtIn, Guid.Empty, string.Empty,
                        level + "|BIP|" + ((int)builtIn).ToString(CultureInfo.InvariantCulture));

                return new ParameterKey(isType, BuiltInParameter.INVALID, Guid.Empty, definition.Name, level + "|NAME|" + definition.Name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The key back from its text; <c>null</c> for anything that is not one.</summary>
        public static ParameterKey Parse(string text)
        {
            var parts = (text ?? string.Empty).Split(new[] { '|' }, 3);
            if (parts.Length != 3 || (parts[0] != Instance && parts[0] != Type) || parts[2].Length == 0)
                return null;

            var isType = parts[0] == Type;

            switch (parts[1])
            {
                case "BIP":
                    return int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value != (int)BuiltInParameter.INVALID
                        ? new ParameterKey(isType, (BuiltInParameter)value, Guid.Empty, string.Empty, text)
                        : null;
                case "GUID":
                    return Guid.TryParse(parts[2], out var guid) ? new ParameterKey(isType, BuiltInParameter.INVALID, guid, string.Empty, text) : null;
                case "NAME":
                    return new ParameterKey(isType, BuiltInParameter.INVALID, Guid.Empty, parts[2], text);
                default:
                    return null;
            }
        }

        /// <summary>The parameter on the element, or on its type for a type parameter.</summary>
        public Parameter Find(Document doc, Element element)
        {
            return On(IsType ? TypeOf(doc, element) : element);
        }

        /// <summary>
        /// The parameter on this very element. A name is matched only among parameters that are neither
        /// built-in nor shared: a family's own "Comments" must not be taken for Revit's.
        /// </summary>
        public Parameter On(Element owner)
        {
            if (owner == null)
                return null;

            try
            {
                if (BuiltIn != BuiltInParameter.INVALID)
                    return owner.get_Parameter(BuiltIn);

                if (Guid != Guid.Empty)
                    return owner.get_Parameter(Guid);

                foreach (var parameter in owner.GetParameters(Name))
                {
                    if (!parameter.IsShared && BuiltInOf(parameter) == BuiltInParameter.INVALID)
                        return parameter;
                }
            }
            catch (Exception)
            {
                // An element that cannot be asked simply does not have it.
            }

            return null;
        }

        public static Element TypeOf(Document doc, Element element)
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

        private static BuiltInParameter BuiltInOf(Parameter parameter)
        {
            return (parameter.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
        }
    }

    /// <summary>A source value read once, in every form a target might want it in.</summary>
    internal sealed class SourceValue
    {
        public StorageType Storage { get; set; }

        public ForgeTypeId Spec { get; set; }

        /// <summary>Nothing to copy: an empty or blank text, a number or yes/no never set, no element chosen.</summary>
        public bool IsEmpty { get; set; }

        /// <summary>The value as Revit shows it — what a text target receives.</summary>
        public string Text { get; set; } = string.Empty;

        public string String { get; set; } = string.Empty;

        public int Integer { get; set; }

        /// <summary>In Revit's internal units.</summary>
        public double Double { get; set; }

        public ElementId Id { get; set; } = ElementId.InvalidElementId;
    }

    /// <summary>
    /// Reading a parameter's value and writing it into a parameter that stores another kind of value —
    /// the "Copy Parameters" button's whole point. Revit stores four kinds (text, whole number, real
    /// number, element) and refuses <c>Parameter.Set</c> of the wrong one, so every pair needs a rule,
    /// and the rule is always "what a person would have typed":
    ///
    /// • into text — the value exactly as Revit shows it (a length with the project's units and
    ///   rounding, yes/no as Revit's own word, an element by its name);
    /// • into a real number — a number of the same kind (length into length) is copied exactly; a number
    ///   of another kind is taken as the figure the source shows, in the target's units (a length of
    ///   "1200" into a plain number gives 1200, not the 3.937 feet Revit keeps inside); a text is read the
    ///   way Revit reads a value typed into the Properties palette ("1200", "1200 mm", "1.2 m");
    /// • into a whole number — the same, rounded; into yes/no — zero or "no" is No, anything else Yes;
    /// • into an element choice — only from another element choice: a name means nothing without
    ///   knowing which kind of element the target chooses.
    ///
    /// A value that cannot be converted is never guessed at: the reason comes back, and the window and
    /// the report show it.
    /// </summary>
    internal static class ParameterValueConverter
    {
        private static readonly Regex FirstNumber = new Regex(@"[-+]?\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant);
        private static readonly Regex DigitGroups = new Regex(@"(?<=\d)[    ](?=\d{3}(?!\d))", RegexOptions.CultureInvariant);

        public const string NotANumber = "the text is not a number";
        public const string NotYesNo = "the text is neither yes nor no";
        public const string TooLarge = "the number is too large for a whole-number parameter";
        public const string NotAnElement = "the target chooses another element (a level, a material, a type…) — only a parameter that chooses one too can be copied into it";
        public const string Rounded = "rounded to a whole number";
        public const string Extracted = "the number was taken out of the text";

        // ───────────────────────────── reading ─────────────────────────────

        public static SourceValue Read(Document doc, Parameter parameter, bool noUnitSymbol)
        {
            var value = new SourceValue { Storage = parameter.StorageType, Spec = SpecOf(parameter) };

            switch (value.Storage)
            {
                case StorageType.String:
                    value.String = parameter.AsString() ?? string.Empty;
                    value.Text = value.String;
                    value.IsEmpty = string.IsNullOrWhiteSpace(value.String);
                    break;

                case StorageType.Integer:
                    value.IsEmpty = !parameter.HasValue;
                    value.Integer = parameter.AsInteger();
                    value.Text = value.IsEmpty ? string.Empty : ValueString(parameter) ?? value.Integer.ToString(CultureInfo.CurrentCulture);
                    break;

                case StorageType.Double:
                    value.IsEmpty = !parameter.HasValue;
                    value.Double = parameter.AsDouble();
                    value.Text = value.IsEmpty ? string.Empty : NumberText(doc, parameter, value.Spec, noUnitSymbol);
                    break;

                case StorageType.ElementId:
                    value.Id = parameter.AsElementId() ?? ElementId.InvalidElementId;
                    value.IsEmpty = value.Id == ElementId.InvalidElementId;
                    value.Text = value.IsEmpty ? string.Empty : ElementText(doc, parameter, value.Id);
                    break;

                default:
                    value.IsEmpty = true;
                    break;
            }

            return value;
        }

        /// <summary>The parameter holds nothing worth keeping — what "only where the target is empty" asks.</summary>
        public static bool IsEmpty(Parameter parameter)
        {
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return string.IsNullOrWhiteSpace(parameter.AsString());
                    case StorageType.ElementId:
                        return (parameter.AsElementId() ?? ElementId.InvalidElementId) == ElementId.InvalidElementId;
                    default:
                        return !parameter.HasValue;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The value as the Properties palette shows it — the "now" column of the preview.</summary>
        public static string Current(Parameter parameter)
        {
            return IsEmpty(parameter) ? string.Empty : ScheduleValues.Show(parameter);
        }

        /// <summary>
        /// A number as Revit shows it. Without the unit symbol the project's own format is kept — the
        /// unit, the rounding, the digit grouping — and only the symbol is dropped: "1 200" rather than
        /// "1 200 mm", never the 3.937 feet Revit keeps inside.
        /// </summary>
        private static string NumberText(Document doc, Parameter parameter, ForgeTypeId spec, bool noUnitSymbol)
        {
            if (noUnitSymbol && IsMeasurable(spec))
            {
                try
                {
                    var options = new FormatOptions(doc.GetUnits().GetFormatOptions(spec)) { UseDefault = false };
                    if (options.CanHaveSymbol())
                        options.SetSymbolTypeId(new ForgeTypeId());

                    var text = parameter.AsValueString(options);
                    if (!string.IsNullOrEmpty(text))
                        return text;
                }
                catch (Exception)
                {
                    // The project's own format, symbol included, is the fallback.
                }
            }

            return ValueString(parameter) ?? parameter.AsDouble().ToString(CultureInfo.CurrentCulture);
        }

        private static string ElementText(Document doc, Parameter parameter, ElementId id)
        {
            var text = ValueString(parameter);
            if (!string.IsNullOrEmpty(text))
                return text;

            try
            {
                var name = doc.GetElement(id)?.Name;
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            catch (Exception)
            {
            }

            return id.ToString();
        }

        private static string ValueString(Parameter parameter)
        {
            try
            {
                var text = parameter.AsValueString();
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ───────────────────────────── converting ─────────────────────────────

        /// <summary>
        /// The source value as the target stores it. <paramref name="error"/> is one of the constants
        /// above, so a hundred texts that are not numbers are one line of the report; <paramref name="note"/>
        /// says what was done to the value on the way (rounded, taken out of a text).
        /// </summary>
        public static bool TryConvert(
            Document doc,
            SourceValue source,
            Parameter target,
            bool numberFromText,
            out object value,
            out string note,
            out string error)
        {
            value = null;
            note = null;
            error = null;

            var spec = SpecOf(target);

            switch (target.StorageType)
            {
                case StorageType.String:
                    value = source.Storage == StorageType.String ? source.String : source.Text;
                    return true;

                case StorageType.Integer:
                    return IsYesNo(spec)
                        ? ToYesNo(source, out value, out error)
                        : ToInteger(doc, source, numberFromText, out value, out note, out error);

                case StorageType.Double:
                    return ToDouble(doc, source, spec, numberFromText, out value, out note, out error);

                case StorageType.ElementId:
                    if (source.Storage == StorageType.ElementId)
                    {
                        value = source.Id;
                        return true;
                    }

                    error = NotAnElement;
                    return false;

                default:
                    error = "Revit gives no way to write this parameter";
                    return false;
            }
        }

        private static bool ToYesNo(SourceValue source, out object value, out string error)
        {
            value = null;
            error = null;

            switch (source.Storage)
            {
                case StorageType.Integer:
                    value = source.Integer != 0 ? 1 : 0;
                    return true;

                case StorageType.Double:
                    value = Math.Abs(source.Double) > 1e-9 ? 1 : 0;
                    return true;

                default:
                    if (ScheduleValues.TryYesNo(source.Text, out var flag))
                    {
                        value = flag ? 1 : 0;
                        return true;
                    }

                    if (TryPlainNumber(source.Text, out var number))
                    {
                        value = Math.Abs(number) > 1e-9 ? 1 : 0;
                        return true;
                    }

                    error = NotYesNo;
                    return false;
            }
        }

        private static bool ToInteger(Document doc, SourceValue source, bool numberFromText, out object value, out string note, out string error)
        {
            value = null;
            note = null;
            error = null;

            double number;

            switch (source.Storage)
            {
                case StorageType.Integer:
                    value = source.Integer;
                    return true;

                case StorageType.Double:
                    number = DisplayNumber(doc, source);
                    break;

                default:
                    if (!TryNumber(source.Text, numberFromText, out number, out var extracted))
                    {
                        error = NotANumber;
                        return false;
                    }

                    if (extracted)
                        note = Extracted;
                    break;
            }

            var whole = Math.Round(number, MidpointRounding.AwayFromZero);
            if (Math.Abs(whole) > int.MaxValue)
            {
                error = TooLarge;
                return false;
            }

            if (Math.Abs(number - whole) > 1e-9)
                note = note == null ? Rounded : note + ", " + Rounded;

            value = (int)whole;
            return true;
        }

        private static bool ToDouble(Document doc, SourceValue source, ForgeTypeId spec, bool numberFromText, out object value, out string note, out string error)
        {
            value = null;
            note = null;
            error = null;

            switch (source.Storage)
            {
                case StorageType.Double:
                    value = SameSpec(source.Spec, spec) ? source.Double : ToInternal(doc, spec, DisplayNumber(doc, source));
                    return true;

                case StorageType.Integer:
                    value = ToInternal(doc, spec, source.Integer);
                    return true;

                default:
                    if (TryParse(doc, spec, source.Text, out var parsed))
                    {
                        value = parsed;
                        return true;
                    }

                    if (numberFromText && TryFirstNumber(source.Text, out var first))
                    {
                        value = ToInternal(doc, spec, first);
                        note = Extracted;
                        return true;
                    }

                    error = NotANumber;
                    return false;
            }
        }

        /// <summary>The figure a number parameter shows, in the project's display unit for its kind.</summary>
        private static double DisplayNumber(Document doc, SourceValue source)
        {
            if (IsMeasurable(source.Spec))
            {
                try
                {
                    return UnitUtils.ConvertFromInternalUnits(source.Double, doc.GetUnits().GetFormatOptions(source.Spec).GetUnitTypeId());
                }
                catch (Exception)
                {
                }
            }

            return source.Double;
        }

        /// <summary>A figure in the project's display unit for the target's kind, as Revit stores it.</summary>
        private static double ToInternal(Document doc, ForgeTypeId spec, double number)
        {
            if (IsMeasurable(spec))
            {
                try
                {
                    return UnitUtils.ConvertToInternalUnits(number, doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId());
                }
                catch (Exception)
                {
                }
            }

            return number;
        }

        /// <summary>Revit's own parser first — it knows the project's units and reads "1200 mm" and "1.2 m" — then a bare number.</summary>
        private static bool TryParse(Document doc, ForgeTypeId spec, string text, out double value)
        {
            value = 0;
            var clean = (text ?? string.Empty).Trim();
            if (clean.Length == 0)
                return false;

            if (IsSpec(spec))
            {
                try
                {
                    if (UnitFormatUtils.TryParse(doc.GetUnits(), spec, clean, out value))
                        return true;
                }
                catch (Exception)
                {
                }
            }

            if (TryPlainNumber(clean, out var number))
            {
                value = ToInternal(doc, spec, number);
                return true;
            }

            return false;
        }

        private static bool TryNumber(string text, bool numberFromText, out double value, out bool extracted)
        {
            extracted = false;

            if (TryPlainNumber(text, out value))
                return true;

            if (numberFromText && TryFirstNumber(text, out value))
            {
                extracted = true;
                return true;
            }

            return false;
        }

        /// <summary>A text that is a number and nothing else: "1200", "1 200", "12,5", "12.5".</summary>
        private static bool TryPlainNumber(string text, out double value)
        {
            var plain = (text ?? string.Empty).Trim()
                .Replace(" ", string.Empty).Replace(" ", string.Empty).Replace(" ", string.Empty).Replace(" ", string.Empty);

            return double.TryParse(plain, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(plain.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>The first number in a text — "DN 50" gives 50, "Ø110×10" gives 110, "1 200 mm" gives 1200.</summary>
        private static bool TryFirstNumber(string text, out double value)
        {
            value = 0;

            var match = FirstNumber.Match(DigitGroups.Replace(text ?? string.Empty, string.Empty));
            return match.Success && TryPlainNumber(match.Value, out value);
        }

        // ───────────────────────────── writing ─────────────────────────────

        public static bool Write(Parameter parameter, object value)
        {
            switch (value)
            {
                case string text:
                    return parameter.Set(text);
                case int number:
                    return parameter.Set(number);
                case double real:
                    return parameter.Set(real);
                case ElementId id:
                    return parameter.Set(id);
                default:
                    return false;
            }
        }

        /// <summary>Whether a parameter holds the value — "already holds it" before the copy, "it took" after.</summary>
        public static bool Holds(Parameter parameter, object value)
        {
            try
            {
                if (parameter.StorageType == StorageType.ElementId)
                    return value is ElementId id && parameter.AsElementId() == id;

                return ScheduleValues.Holds(parameter, value);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>A converted value as the target will show it — the "after" column of the preview.</summary>
        public static string Show(Document doc, Parameter target, object value)
        {
            switch (value)
            {
                case string text:
                    return text;

                case int number:
                    return IsYesNo(SpecOf(target)) ? (number != 0 ? "Yes" : "No") : number.ToString(CultureInfo.CurrentCulture);

                case double real:
                    var spec = SpecOf(target);
                    if (IsSpec(spec))
                    {
                        try
                        {
                            return UnitFormatUtils.Format(doc.GetUnits(), spec, real, false);
                        }
                        catch (Exception)
                        {
                        }
                    }

                    return real.ToString(CultureInfo.CurrentCulture);

                case ElementId id:
                    try
                    {
                        return doc.GetElement(id)?.Name ?? id.ToString();
                    }
                    catch (Exception)
                    {
                        return id.ToString();
                    }

                default:
                    return string.Empty;
            }
        }

        // ───────────────────────────── describing ─────────────────────────────

        /// <summary>What kind of value a parameter holds, in Revit's own words: "Length", "Text", "Yes/No".</summary>
        public static string ValueKind(Parameter parameter)
        {
            var spec = SpecOf(parameter);
            if (IsSpec(spec))
            {
                try
                {
                    var label = LabelUtils.GetLabelForSpec(spec);
                    if (!string.IsNullOrEmpty(label))
                        return label;
                }
                catch (Exception)
                {
                }
            }

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return "Text";
                case StorageType.Integer:
                    return "Integer";
                case StorageType.Double:
                    return "Number";
                case StorageType.ElementId:
                    return "Element";
                default:
                    return "—";
            }
        }

        /// <summary>
        /// The rule this pair of parameters is copied by, in one sentence — shown above the preview, so
        /// nobody has to guess what "a length into a text" will look like.
        /// </summary>
        public static string Describe(Parameter source, Parameter target, bool noUnitSymbol, bool numberFromText)
        {
            var head = ValueKind(source) + " → " + ValueKind(target) + ": ";
            var sourceSpec = SpecOf(source);
            var targetSpec = SpecOf(target);
            var sourceYesNo = source.StorageType == StorageType.Integer && IsYesNo(sourceSpec);

            switch (target.StorageType)
            {
                case StorageType.String:
                    switch (source.StorageType)
                    {
                        case StorageType.String:
                            return head + "the text is copied as it is.";
                        case StorageType.Double:
                            return head + (noUnitSymbol
                                ? "the number as Revit shows it — the project's units and rounding — without the unit symbol."
                                : "the number exactly as Revit shows it — the project's units and rounding, the unit symbol where the project shows one.");
                        case StorageType.Integer:
                            return head + (sourceYesNo ? "Revit's own word for it — Yes or No." : "the value as Revit shows it.");
                        default:
                            return head + "the name of the element it chooses.";
                    }

                case StorageType.Double:
                    switch (source.StorageType)
                    {
                        case StorageType.Double:
                            return SameSpec(sourceSpec, targetSpec)
                                ? head + "the same kind of number — copied exactly."
                                : head + "the figure the source shows (in the project's units for it) is taken as it is, in the project's units for the target.";
                        case StorageType.Integer:
                            return head + (sourceYesNo ? "Yes gives 1, No gives 0." : "the whole number as it is, in the project's units for the target.");
                        default:
                            return head + "the text is read the way Revit reads a value typed into Properties (\"1200\", \"1200 mm\", \"1.2 m\")" +
                                   (numberFromText ? "; failing that, the first number in it (\"DN 50\" → 50)." : "; a text that is not a number is not written.");
                    }

                case StorageType.Integer:
                    if (IsYesNo(targetSpec))
                    {
                        return source.StorageType == StorageType.String || source.StorageType == StorageType.ElementId
                            ? head + "\"Yes\", \"Да\", \"1\" give Yes; \"No\", \"Нет\", \"0\" give No; any other text is not written."
                            : head + "zero gives No, anything else Yes.";
                    }

                    switch (source.StorageType)
                    {
                        case StorageType.Integer:
                            return head + "the whole number is copied as it is.";
                        case StorageType.Double:
                            return head + "the figure the source shows, rounded to a whole number.";
                        default:
                            return head + "the text is read as a number and rounded" +
                                   (numberFromText ? "; failing that, the first number in it is taken." : "; a text that is not a number is not written.");
                    }

                case StorageType.ElementId:
                    return source.StorageType == StorageType.ElementId
                        ? head + "the same element is chosen — where Revit accepts it for this parameter."
                        : head + "cannot be done: " + NotAnElement + ".";

                default:
                    return head + "Revit gives no way to write this parameter.";
            }
        }

        // ───────────────────────────── specs ─────────────────────────────

        public static ForgeTypeId SpecOf(Parameter parameter)
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

        public static bool IsYesNo(ForgeTypeId spec)
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

        private static bool SameSpec(ForgeTypeId left, ForgeTypeId right)
        {
            try
            {
                return left != null && right != null && !left.Empty() && left.Equals(right);
            }
            catch (Exception)
            {
                return false;
            }
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

        private static bool IsMeasurable(ForgeTypeId spec)
        {
            try
            {
                return IsSpec(spec) && UnitUtils.IsMeasurableSpec(spec);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
