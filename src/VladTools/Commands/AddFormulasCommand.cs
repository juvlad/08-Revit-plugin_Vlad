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
    /// Задаёт формулы параметрам открытого (родительского) семейства по сохранённому списку
    /// «параметр — формула». Список общий для всех семейств: один раз собрал — дальше он
    /// подставляется сам, и все формулы применяются одним пакетом.
    /// Вложенные семейства не трогает — только сам открытый документ.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AddFormulasCommand : IExternalCommand
    {
        private const string DialogTitle = "Добавить формулы";

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
                var manager = doc.FamilyManager;

                var window = new AddFormulasWindow(Describe(manager));
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var applied = new List<string>();
                var failures = new List<string>();

                using (var transaction = new Transaction(doc, "Добавить формулы к параметрам"))
                {
                    transaction.Start();

                    Apply(manager, window.Selected, applied, failures);

                    if (applied.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                Report(applied, failures);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Снимок всех параметров семейства для окна: по нему проверяется и сам параметр из строки,
        /// и имена внутри формулы. Берутся не только общие параметры — формула может ссылаться на любой.
        /// </summary>
        private static List<FamilyParameterInfo> Describe(FamilyManager manager)
        {
            return manager.Parameters
                .Cast<FamilyParameter>()
                .Where(parameter => parameter?.Definition != null)
                .Select(parameter => new FamilyParameterInfo(
                    parameter.Definition.Name,
                    CanAssignFormula(parameter),
                    !string.IsNullOrEmpty(parameter.Formula)))
                .ToList();
        }

        private static bool CanAssignFormula(FamilyParameter parameter)
        {
            try
            {
                return parameter.CanAssignFormula;
            }
            catch (Exception)
            {
                return true; // Не смогли спросить — не запрещаем, пусть решает сам Revit при записи.
            }
        }

        /// <summary>
        /// Пишет формулы одну за другой в общей транзакции. Отказ по одной строке
        /// (например, круговая ссылка) не отменяет остальные — он попадает в итоговый отчёт.
        /// </summary>
        private static void Apply(
            FamilyManager manager,
            IReadOnlyList<FormulaRule> rules,
            List<string> applied,
            List<string> failures)
        {
            foreach (var rule in rules)
            {
                var name = rule.TrimmedName;
                var parameter = Find(manager, name);

                if (parameter == null)
                {
                    failures.Add(name + " — параметр не найден");
                    continue;
                }

                try
                {
                    manager.SetFormula(parameter, rule.TrimmedFormula);
                    applied.Add(name);
                }
                catch (Exception exception)
                {
                    failures.Add(name + " — " + exception.Message);
                }
            }
        }

        private static FamilyParameter Find(FamilyManager manager, string name)
        {
            return manager.Parameters
                .Cast<FamilyParameter>()
                .FirstOrDefault(parameter =>
                    parameter?.Definition != null &&
                    string.Equals(parameter.Definition.Name, name, StringComparison.Ordinal));
        }

        private static void Report(IReadOnlyList<string> applied, IReadOnlyList<string> failures)
        {
            var text = applied.Count > 0
                ? "Формул задано: " + applied.Count + ".\n• " + string.Join("\n• ", applied)
                : "Ни одна формула не задана.";

            if (failures.Count > 0)
            {
                const int limit = 15;
                text += "\n\nНе удалось задать (" + failures.Count + "):\n• " +
                        string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… и ещё " + (failures.Count - limit);
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
