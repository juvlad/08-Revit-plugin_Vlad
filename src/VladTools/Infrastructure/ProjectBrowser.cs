using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// What the Project Browser itself says about where a view or a sheet sits: the folders of the
    /// organization in use, whether its filter lets the element through, the value it sorts by, and
    /// the order it lists names in. Shared by "Sheets" (the sheet organization) and "Views to Sheets"
    /// (the view organization) — the API is the same for both.
    ///
    /// Every read here is wrapped: a browser organization that cannot answer is no reason for a button
    /// to fail, and each method says what it falls back to.
    /// </summary>
    internal static class ProjectBrowser
    {
        /// <summary>What the browser itself shows for an element whose grouping parameter is empty.</summary>
        public const string EmptyFolder = "???";

        public const string FilteredFolder = "(Hidden by the Project Browser's filter)";

        public static readonly IComparer<string> NaturalOrder = Comparer<string>.Create(Natural);

        /// <summary>The folders the element sits in, outermost first; empty when there is no organization or it cannot say.</summary>
        public static IReadOnlyList<string> Folders(BrowserOrganization organization, ElementId id)
        {
            if (organization == null)
                return new string[0];

            try
            {
                return organization.GetFolderItems(id)
                    .Select(item => string.IsNullOrWhiteSpace(item.Name) ? EmptyFolder : item.Name)
                    .ToList();
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        /// <summary>A failed check counts as passing: an element must not end up under "hidden" because Revit threw.</summary>
        public static bool Passes(BrowserOrganization organization, ElementId id)
        {
            if (organization == null)
                return true;

            try
            {
                return organization.AreFiltersSatisfied(id);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// The value the browser sorts by, or <paramref name="fallback"/> when the organization names no
        /// parameter. The parameter is found by its id among the element's own, which covers built-in,
        /// project and shared parameters alike.
        /// </summary>
        public static string SortValue(Element element, ElementId sortingId, string fallback)
        {
            if (sortingId != null && sortingId != ElementId.InvalidElementId)
            {
                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter.Id != sortingId)
                        continue;

                    try
                    {
                        return (parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString()) ?? string.Empty;
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
            }

            return fallback ?? string.Empty;
        }

        public static string Name(Element element)
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

        /// <summary>
        /// "A2" before "A10": runs of digits compare as numbers, the rest ignoring case. Two texts equal
        /// that way still compare ordinally at the end, so two folders differing only in case never
        /// interleave — a window starts a new folder line wherever a name changes.
        /// </summary>
        public static int Natural(string a, string b)
        {
            a = a ?? string.Empty;
            b = b ?? string.Empty;

            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (IsDigit(a[i]) && IsDigit(b[j]))
                {
                    var startA = i;
                    while (i < a.Length && IsDigit(a[i]))
                        i++;

                    var startB = j;
                    while (j < b.Length && IsDigit(b[j]))
                        j++;

                    var numberA = a.Substring(startA, i - startA).TrimStart('0');
                    var numberB = b.Substring(startB, j - startB).TrimStart('0');

                    var result = numberA.Length.CompareTo(numberB.Length);
                    if (result == 0)
                        result = string.CompareOrdinal(numberA, numberB);
                    if (result != 0)
                        return result;
                }
                else
                {
                    var result = string.Compare(a, i, b, j, 1, StringComparison.CurrentCultureIgnoreCase);
                    if (result != 0)
                        return result;

                    i++;
                    j++;
                }
            }

            var rest = (a.Length - i).CompareTo(b.Length - j);
            return rest != 0 ? rest : string.CompareOrdinal(a, b);
        }

        private static bool IsDigit(char character)
        {
            return character >= '0' && character <= '9';
        }
    }
}
