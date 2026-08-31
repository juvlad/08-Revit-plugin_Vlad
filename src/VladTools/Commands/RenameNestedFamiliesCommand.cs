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
    /// Пакетно переименовывает вложенные семейства открытого семейства (и, если нужно, их типоразмеры)
    /// по правилу «найти и заменить», как в Excel. Само открытое семейство не трогает — его имя
    /// задаётся именем файла.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RenameNestedFamiliesCommand : IExternalCommand
    {
        private const string DialogTitle = "Переименовать вложенные";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "Нет активного документа.";
                return Result.Cancelled;
            }

            var doc = uidoc.Document;
            if (!doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "Команда работает только в редакторе семейств.\n" +
                    "Откройте семейство (.rfa) и повторите.");
                return Result.Cancelled;
            }

            try
            {
                var rows = Collect(doc);
                if (rows.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "В этом семействе нет вложенных семейств.");
                    return Result.Cancelled;
                }

                var window = new RenameNestedWindow(rows);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var renamed = new List<string>();
                var failures = new List<string>();

                using (var transaction = new Transaction(doc, "Переименовать вложенные семейства"))
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

        /// <summary>Вложенные семейства и их типоразмеры одним списком: сначала семейство, следом его типы.</summary>
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
        /// Считает расставленные экземпляры за один проход по документу — столбец «Экз.» в окне
        /// подсказывает, какое вложенное семейство реально используется, а какое просто загружено.
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
        /// Переименовывает в несколько проходов: пока имя занято соседом, которого тоже переименовывают,
        /// Revit его не отдаёт. Повторяем, пока есть прогресс, и только потом записываем ошибки.
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

                // Ни одного имени за проход — дальше ничего не изменится.
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
                ? "Переименовано: " + renamed.Count + "."
                : "Ничего не переименовано.";

            if (renamed.Count > 0)
            {
                const int shown = 15;
                text += "\n\n• " + string.Join("\n• ", renamed.Take(shown));

                if (renamed.Count > shown)
                    text += "\n… и ещё " + (renamed.Count - shown);
            }

            if (failures.Count > 0)
            {
                const int limit = 15;
                text += "\n\nНе удалось переименовать (" + failures.Count + "):\n• " +
                        string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… и ещё " + (failures.Count - limit);
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
