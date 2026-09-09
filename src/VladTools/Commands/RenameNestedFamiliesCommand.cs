using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// Renames the nested families of the open family (and, if asked, their types) in a batch, by a
    /// find-and-replace rule, the way Excel does it. It does not touch the open family itself — its
    /// name is set by the file name.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RenameNestedFamiliesCommand : IExternalCommand
    {
        private const string DialogTitle = "Rename Nested";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "There is no active document.";
                return Result.Cancelled;
            }

            var doc = uidoc.Document;
            if (!doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "The command works only in the family editor.\n" +
                    "Open a family (.rfa) and try again.");
                return Result.Cancelled;
            }

            try
            {
                var rows = Collect(doc);
                if (rows.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "This family contains no nested families.");
                    return Result.Cancelled;
                }

                var window = new RenameNestedWindow(rows);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var renamed = new List<string>();
                var failures = new List<string>();

                using (var transaction = new Transaction(doc, "Rename nested families"))
                {
                    transaction.Start();

                    Rename(window.Selected, renamed, failures);

                    if (renamed.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                Report(renamed, failures);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        /// <summary>Nested families and their types in one list: the family first, its types right after.</summary>
        private static List<NestedFamilyRow> Collect(Document doc)
        {
            var byFamily = new Dictionary<int, int>();
            var bySymbol = new Dictionary<int, int>();
            CountInstances(doc, byFamily, bySymbol);

            var rows = new List<NestedFamilyRow>();

            var families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .OrderBy(family => family.Name, StringComparer.CurrentCultureIgnoreCase);

            foreach (var family in families)
            {
                rows.Add(new NestedFamilyRow(family, NestedKind.Family, family.Name, string.Empty, Count(byFamily, family.Id)));

                foreach (var symbol in Symbols(doc, family))
                    rows.Add(new NestedFamilyRow(symbol, NestedKind.Symbol, symbol.Name, family.Name, Count(bySymbol, symbol.Id)));
            }

            return rows;
        }

        private static IEnumerable<FamilySymbol> Symbols(Document doc, Family family)
        {
            return family.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id) as FamilySymbol)
                .Where(symbol => symbol != null)
                .OrderBy(symbol => symbol.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        /// <summary>
        /// Counts the placed instances in a single pass over the document — the "Inst." column in the
        /// window shows which nested family is actually used and which is merely loaded.
        /// </summary>
        private static void CountInstances(Document doc, Dictionary<int, int> byFamily, Dictionary<int, int> bySymbol)
        {
            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

            foreach (var instance in instances)
            {
                var symbol = instance.Symbol;
                if (symbol == null)
                    continue;

                Bump(bySymbol, symbol.Id);

                if (symbol.Family != null)
                    Bump(byFamily, symbol.Family.Id);
            }
        }

        private static void Bump(Dictionary<int, int> counts, ElementId id)
        {
            int count;
            counts[id.IntegerValue] = counts.TryGetValue(id.IntegerValue, out count) ? count + 1 : 1;
        }

        private static int Count(Dictionary<int, int> counts, ElementId id)
        {
            int count;
            return counts.TryGetValue(id.IntegerValue, out count) ? count : 0;
        }

        /// <summary>
        /// Renames in several passes: while a name is held by a neighbour that is being renamed too,
        /// Revit will not release it. We repeat while there is progress, and only then record the errors.
        /// </summary>
        private static void Rename(IReadOnlyList<NestedFamilyRow> rows, List<string> renamed, List<string> failures)
        {
            var pending = rows.ToList();
            var lastErrors = new Dictionary<NestedFamilyRow, string>();

            while (pending.Count > 0)
            {
                var stuck = new List<NestedFamilyRow>();

                foreach (var row in pending)
                {
                    try
                    {
                        row.Element.Name = row.TrimmedNewName;
                        renamed.Add(row.CurrentName + " → " + row.TrimmedNewName);
                    }
                    catch (Exception exception)
                    {
                        lastErrors[row] = exception.Message;
                        stuck.Add(row);
                    }
                }

                // Not a single name in a whole pass — nothing will change from here on.
                if (stuck.Count == pending.Count)
                {
                    failures.AddRange(stuck.Select(row => row.CurrentName + " — " + lastErrors[row]));
                    return;
                }

                pending = stuck;
            }
        }

        private static void Report(IReadOnlyList<string> renamed, IReadOnlyList<string> failures)
        {
            var text = renamed.Count > 0
                ? "Renamed: " + renamed.Count + "."
                : "Nothing was renamed.";

            if (renamed.Count > 0)
            {
                const int shown = 15;
                text += "\n\n• " + string.Join("\n• ", renamed.Take(shown));

                if (renamed.Count > shown)
                    text += "\n… and " + (renamed.Count - shown) + " more";
            }

            if (failures.Count > 0)
            {
                const int limit = 15;
                text += "\n\nCould not be renamed (" + failures.Count + "):\n• " +
                        string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… and " + (failures.Count - limit) + " more";
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
