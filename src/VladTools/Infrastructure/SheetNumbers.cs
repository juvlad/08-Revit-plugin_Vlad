using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The number "Sheets" shows for a sheet: Revit's own Sheet Number, or a shared parameter chosen in
    /// the window — an office's own sheet number, kept apart from Revit's because Revit's has to be
    /// unique in the model and the one printed on the sheet often does not (every volume numbered from 1).
    ///
    /// The shared parameter is read off the sheet, and off its titleblock when the sheet gives no value:
    /// offices bind it either way — to the Sheets category, or inside the titleblock family.
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
                found |= TryValue(sheet, parameter.Guid, out var value);

                if (value.Length == 0 && titleblocks.TryGetValue(sheet.Id, out var list))
                {
                    foreach (var titleblock in list)
                    {
                        found |= TryValue(titleblock, parameter.Guid, out value);
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
        /// Every shared parameter on the sheets or their titleblocks, with how many sheets have it and
        /// how many give a value — what the picker shows beside a file's definitions.
        /// </summary>
        public static IReadOnlyList<SheetParameterUse> Uses(Document doc, IReadOnlyList<ViewSheet> sheets)
        {
            var onSheets = new Dictionary<Guid, Tally>();
            var onTitleblocks = new Dictionary<Guid, Tally>();
            var titleblocks = Titleblocks(doc);

            foreach (var sheet in sheets)
            {
                Count(onSheets, new[] { sheet });

                if (titleblocks.TryGetValue(sheet.Id, out var list))
                    Count(onTitleblocks, list);
            }

            var result = new List<SheetParameterUse>();

            foreach (var guid in onSheets.Keys.Union(onTitleblocks.Keys))
            {
                onSheets.TryGetValue(guid, out var sheetTally);
                onTitleblocks.TryGetValue(guid, out var titleblockTally);

                var parts = new List<string>();
                if (sheetTally != null)
                    parts.Add("on " + sheetTally.Having + " of " + sheets.Count + " sheets, " + sheetTally.Filled + " with a value");
                if (titleblockTally != null)
                    parts.Add("on the titleblocks of " + titleblockTally.Having + " sheets, " + titleblockTally.Filled + " with a value");

                var any = sheetTally ?? titleblockTally;
                result.Add(new SheetParameterUse(guid, any.Name, any.DataType, string.Join("; ", parts)));
            }

            return result;
        }

        private sealed class Tally
        {
            public string Name = string.Empty;
            public string DataType = string.Empty;
            public int Having;
            public int Filled;
        }

        /// <summary>One sheet's worth: every shared parameter on any of <paramref name="elements"/> counts the sheet once.</summary>
        private static void Count(Dictionary<Guid, Tally> tallies, IEnumerable<Element> elements)
        {
            var having = new Dictionary<Guid, bool>();

            foreach (var element in elements)
            {
                foreach (Parameter parameter in element.Parameters)
                {
                    Guid guid;
                    try
                    {
                        if (!parameter.IsShared)
                            continue;

                        guid = parameter.GUID;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (!tallies.TryGetValue(guid, out var tally))
                    {
                        tally = new Tally { Name = parameter.Definition?.Name ?? guid.ToString("D"), DataType = SharedParameterFile.DataTypeLabel(parameter.Definition) };
                        tallies[guid] = tally;
                    }

                    having.TryGetValue(guid, out var filled);
                    having[guid] = filled || Text(parameter).Length > 0;
                }
            }

            foreach (var pair in having)
            {
                tallies[pair.Key].Having++;
                if (pair.Value)
                    tallies[pair.Key].Filled++;
            }
        }

        /// <summary>True when the element has the parameter at all; <paramref name="value"/> is empty when it gives none.</summary>
        private static bool TryValue(Element element, Guid guid, out string value)
        {
            value = string.Empty;

            Parameter parameter;
            try
            {
                parameter = element.get_Parameter(guid);
            }
            catch (Exception)
            {
                return false;
            }

            if (parameter == null)
                return false;

            value = Text(parameter);
            return true;
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
            bool inProject;
            try
            {
                inProject = SharedParameterElement.Lookup(doc, parameter.Guid) != null;
            }
            catch (Exception)
            {
                inProject = false;
            }

            return "\"" + parameter.Name + "\"" +
                   (inProject ? " is in this model, but neither its sheets nor their titleblocks have it" : " is not in this model") +
                   ", so Revit's own Sheet Number is shown instead.";
        }

        /// <summary>Every titleblock, by the sheet it stands on — one pass over the project.</summary>
        private static Dictionary<ElementId, List<Element>> Titleblocks(Document doc)
        {
            var titleblocks = new Dictionary<ElementId, List<Element>>();

            foreach (var titleblock in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
            {
                if (!titleblocks.TryGetValue(titleblock.OwnerViewId, out var list))
                {
                    list = new List<Element>();
                    titleblocks[titleblock.OwnerViewId] = list;
                }

                list.Add(titleblock);
            }

            return titleblocks;
        }
    }
}
