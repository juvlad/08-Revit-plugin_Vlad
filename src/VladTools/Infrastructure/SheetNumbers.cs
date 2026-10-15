using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The number "Sheets" shows for a sheet: Revit's own Sheet Number, or a parameter chosen in the
    /// window — an office's own sheet number, kept apart from Revit's because Revit's has to be unique in
    /// the model and the one printed on the sheet often does not (every volume numbered from 1).
    ///
    /// The parameter is read off the sheet, and, when the sheet gives no value, off its titleblock — the
    /// instance, then its type: offices keep it in any of the three — bound to the Sheets category, or
    /// inside the titleblock family as an instance or a type parameter. A shared parameter is found by its
    /// GUID, any other by its name (<see cref="SheetNumberParameter.IsByName"/>).
    /// </summary>
    internal static class SheetNumbers
    {
        public static Dictionary<ElementId, string> Read(Document doc, IReadOnlyList<ViewSheet> sheets, SheetNumberParameter parameter, out SheetNumbering numbering)
        {
            var numbers = sheets.ToDictionary(sheet => sheet.Id, sheet => sheet.SheetNumber ?? string.Empty);

            if (parameter == null || parameter.IsRevit)
            {
                numbering = SheetNumbering.Revit;
                return numbers;
            }

            var titleblocks = Titleblocks(doc);
            var values = new Dictionary<ElementId, string>();
            var found = false;

            foreach (var sheet in sheets)
            {
                found |= TryValue(sheet, parameter, out var value);

                if (value.Length == 0 && titleblocks.TryGetValue(sheet.Id, out var list))
                {
                    foreach (var titleblock in list)
                    {
                        found |= TryValue(titleblock, parameter, out value);
                        if (value.Length > 0)
                            break;
                    }
                }

                values[sheet.Id] = value;
            }

            // A parameter no sheet and no titleblock has would leave every number empty — the whole
            // table unreadable for a model that simply does not use it. Revit's numbers stand in, and
            // the window and the batch summary say so.
            if (!found)
            {
                numbering = new SheetNumbering(parameter, false, 0, Missing(doc, parameter));
                return numbers;
            }

            numbering = new SheetNumbering(parameter, true, values.Values.Count(value => value.Length == 0), string.Empty);
            return values;
        }

        /// <summary>
        /// Every parameter on the sheets or their titleblocks that could be a number — shared or not,
        /// Revit's own built-in ones aside — with how many sheets have it and how many get a value from it.
        /// Not only the shared parameter file's: the number is whatever the sheets were filled in with, and
        /// that may be a project parameter, or a titleblock family's own.
        /// </summary>
        public static IReadOnlyList<SheetParameterUse> Uses(Document doc, IReadOnlyList<ViewSheet> sheets)
        {
            var tallies = new Dictionary<string, Tally>(StringComparer.Ordinal);
            var titleblocks = Titleblocks(doc);

            foreach (var sheet in sheets)
            {
                var onSheet = Scan(new[] { sheet });
                var onTitleblock = titleblocks.TryGetValue(sheet.Id, out var list) ? Scan(list) : new Dictionary<string, Seen>();

                foreach (var seen in onSheet.Values.Concat(onTitleblock.Values))
                {
                    if (!tallies.ContainsKey(seen.Parameter.Key))
                        tallies[seen.Parameter.Key] = new Tally(seen.Parameter, seen.DataType);
                }

                foreach (var pair in onSheet)
                {
                    tallies[pair.Key].OnSheets++;
                    if (pair.Value.Filled)
                        tallies[pair.Key].FilledOnSheets++;
                }

                foreach (var pair in onTitleblock)
                {
                    tallies[pair.Key].OnTitleblocks++;
                    if (pair.Value.Filled)
                        tallies[pair.Key].FilledOnTitleblocks++;
                }

                // The value a sheet shows comes off the sheet, else off its titleblock — counted once.
                foreach (var key in onSheet.Keys.Union(onTitleblock.Keys))
                {
                    var filled = (onSheet.TryGetValue(key, out var a) && a.Filled) || (onTitleblock.TryGetValue(key, out var b) && b.Filled);
                    if (filled)
                        tallies[key].Filled++;
                }
            }

            return tallies.Values.Select(tally =>
            {
                var parts = new List<string>();
                if (tally.OnSheets > 0)
                    parts.Add("on " + tally.OnSheets + " of " + sheets.Count + " sheets, " + tally.FilledOnSheets + " with a value");
                if (tally.OnTitleblocks > 0)
                    parts.Add("on the titleblocks of " + tally.OnTitleblocks + " sheets, " + tally.FilledOnTitleblocks + " with a value");

                return new SheetParameterUse(tally.Parameter, tally.DataType, string.Join("; ", parts), tally.Filled);
            }).ToList();
        }

        private sealed class Tally
        {
            public Tally(SheetNumberParameter parameter, string dataType)
            {
                Parameter = parameter;
                DataType = dataType;
            }

            public SheetNumberParameter Parameter { get; }
            public string DataType { get; }
            public int OnSheets;
            public int FilledOnSheets;
            public int OnTitleblocks;
            public int FilledOnTitleblocks;
            public int Filled;
        }

        private sealed class Seen
        {
            public SheetNumberParameter Parameter;
            public string DataType;
            public bool Filled;
        }

        /// <summary>The candidate parameters on one sheet's worth of elements — the sheet, or its titleblocks and their types.</summary>
        private static Dictionary<string, Seen> Scan(IEnumerable<Element> elements)
        {
            var seen = new Dictionary<string, Seen>(StringComparer.Ordinal);

            foreach (var element in elements)
            {
                foreach (Parameter parameter in element.Parameters)
                {
                    var identity = Identify(parameter);
                    if (identity == null)
                        continue;

                    if (!seen.TryGetValue(identity.Key, out var item))
                    {
                        item = new Seen { Parameter = identity, DataType = SharedParameterFile.DataTypeLabel(parameter.Definition) };
                        seen[identity.Key] = item;
                    }

                    item.Filled |= Text(parameter).Length > 0;
                }
            }

            return seen;
        }

        /// <summary>
        /// What a parameter is known by across sheets and models — its GUID if shared, else its name;
        /// null for Revit's built-in parameters and for an element choice, which cannot be a number.
        /// </summary>
        private static SheetNumberParameter Identify(Parameter parameter)
        {
            try
            {
                var definition = parameter.Definition;
                if (definition == null || string.IsNullOrWhiteSpace(definition.Name))
                    return null;

                if (definition is InternalDefinition internalDefinition && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID)
                    return null;

                if (parameter.StorageType != StorageType.String && parameter.StorageType != StorageType.Integer && parameter.StorageType != StorageType.Double)
                    return null;

                return parameter.IsShared ? new SheetNumberParameter(parameter.GUID, definition.Name) : SheetNumberParameter.ByName(definition.Name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True when the element has the parameter at all; <paramref name="value"/> is empty when it gives none.</summary>
        private static bool TryValue(Element element, SheetNumberParameter parameter, out string value)
        {
            value = string.Empty;

            IEnumerable<Parameter> candidates;
            try
            {
                if (parameter.IsByName)
                {
                    candidates = element.GetParameters(parameter.Name).Where(candidate => Identify(candidate) != null).ToList();
                }
                else
                {
                    var shared = element.get_Parameter(parameter.Guid);
                    candidates = shared == null ? new Parameter[0] : new[] { shared };
                }
            }
            catch (Exception)
            {
                return false;
            }

            var found = false;
            foreach (var candidate in candidates)
            {
                found = true;
                value = Text(candidate);
                if (value.Length > 0)
                    break;
            }

            return found;
        }

        /// <summary>The value as Revit shows it — a text as typed, a number in the project's units.</summary>
        private static string Text(Parameter parameter)
        {
            try
            {
                if (parameter.StorageType == StorageType.String)
                    return (parameter.AsString() ?? string.Empty).Trim();

                return parameter.HasValue ? (parameter.AsValueString() ?? string.Empty).Trim() : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string Missing(Document doc, SheetNumberParameter parameter)
        {
            var inProject = false;

            if (parameter.IsShared)
            {
                try
                {
                    inProject = SharedParameterElement.Lookup(doc, parameter.Guid) != null;
                }
                catch (Exception)
                {
                    inProject = false;
                }
            }

            return "\"" + parameter.Name + "\"" +
                   (inProject ? " is in this model, but neither its sheets nor their titleblocks have it" : " is on no sheet and no titleblock of this model") +
                   ", so Revit's own Sheet Number is shown instead.";
        }

        /// <summary>
        /// Every titleblock by the sheet it stands on, each followed by its type — one pass over the
        /// project. The instance comes first: a value typed on the sheet's own titleblock wins over the type's.
        /// </summary>
        private static Dictionary<ElementId, List<Element>> Titleblocks(Document doc)
        {
            var titleblocks = new Dictionary<ElementId, List<Element>>();
            var types = new Dictionary<ElementId, Element>();

            foreach (var titleblock in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
            {
                if (!titleblocks.TryGetValue(titleblock.OwnerViewId, out var list))
                {
                    list = new List<Element>();
                    titleblocks[titleblock.OwnerViewId] = list;
                }

                list.Add(titleblock);

                var typeId = titleblock.GetTypeId();
                if (typeId == ElementId.InvalidElementId)
                    continue;

                if (!types.TryGetValue(typeId, out var type))
                {
                    type = doc.GetElement(typeId);
                    types[typeId] = type;
                }

                if (type != null)
                    list.Add(type);
            }

            return titleblocks;
        }
    }
}
