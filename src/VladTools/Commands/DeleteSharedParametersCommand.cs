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
    /// Удаляет из открытого (родительского) семейства общие параметры, имена которых
    /// подходят под правило: «начинаются с …» или «содержат …».
    /// Сначала показывает список всех общих параметров и то, что попадёт под правило.
    /// Вложенные семейства не трогает — только сам открытый документ.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DeleteSharedParametersCommand : IExternalCommand
    {
        private const string DialogTitle = "Удалить параметры";

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
                var rows = CollectSharedParameters(manager, CollectDimensionLabels(doc));

                if (rows.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "В этом семействе нет общих параметров.");
                    return Result.Cancelled;
                }

                var window = new DeleteParametersWindow(rows);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var deleted = new List<string>();
                var failures = new List<string>();

                using (var transaction = new Transaction(doc, "Удалить общие параметры"))
                {
                    transaction.Start();

                    Remove(manager, window.Selected, deleted, failures);

                    if (deleted.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                Report(deleted, failures);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        private static List<SharedParameterRow> CollectSharedParameters(
            FamilyManager manager,
            ICollection<ElementId> dimensionLabels)
        {
            return manager.Parameters
                .Cast<FamilyParameter>()
                .Where(parameter => parameter.IsShared)
                .Select(parameter => Describe(parameter, dimensionLabels))
                .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static SharedParameterRow Describe(FamilyParameter parameter, ICollection<ElementId> dimensionLabels)
        {
            var definition = parameter.Definition;

            return new SharedParameterRow(
                parameter,
                definition?.Name ?? "(без имени)",
                GuidText(parameter),
                parameter.IsInstance ? "Экземпляр" : "Тип",
                GroupName(definition),
                dimensionLabels.Contains(parameter.Id));
        }

        /// <summary>
        /// Параметры, которые стоят метками на размерах: удалить такой — значит снять метку
        /// и сломать параметрику. Метка есть только у размера в документе семейства
        /// и достаётся единственным способом — <c>Dimension.FamilyLabel</c>, который
        /// у неразмечаемого размера бросает исключение вместо null.
        /// </summary>
        private static HashSet<ElementId> CollectDimensionLabels(Document doc)
        {
            var result = new HashSet<ElementId>();

            foreach (var dimension in new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                try
                {
                    var label = dimension.FamilyLabel;
                    if (label != null)
                        result.Add(label.Id);
                }
                catch (Exception)
                {
                    // Размер, который пометить нельзя, метки и не несёт.
                }
            }

            return result;
        }

        private static string GuidText(FamilyParameter parameter)
        {
            try
            {
                return parameter.GUID.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string GroupName(Definition definition)
        {
            if (definition == null)
                return string.Empty;

            try
            {
                return LabelUtils.GetLabelFor(definition.ParameterGroup);
            }
            catch (Exception)
            {
                return definition.ParameterGroup.ToString();
            }
        }

        /// <summary>
        /// Удаляет параметры в несколько проходов: параметр, на который ссылается формула
        /// другого параметра, не удаляется, пока жив этот другой. Повторяем, пока есть прогресс,
        /// и только после этого записываем оставшиеся ошибки.
        /// </summary>
        private static void Remove(
            FamilyManager manager,
            IReadOnlyList<SharedParameterRow> rows,
            List<string> deleted,
            List<string> failures)
        {
            var pending = rows.ToList();
            var lastErrors = new Dictionary<string, string>();

            while (pending.Count > 0)
            {
                var stuck = new List<SharedParameterRow>();

                foreach (var row in pending)
                {
                    try
                    {
                        manager.RemoveParameter(row.Parameter);
                        deleted.Add(row.Name);
                    }
                    catch (Exception exception)
                    {
                        lastErrors[row.Name] = exception.Message;
                        stuck.Add(row);
                    }
                }

                // Ни один параметр не удалось удалить за проход — дальше ничего не изменится.
                if (stuck.Count == pending.Count)
                {
                    failures.AddRange(stuck.Select(row => row.Name + " — " + lastErrors[row.Name]));
                    return;
                }

                pending = stuck;
            }
        }

        private static void Report(IReadOnlyList<string> deleted, IReadOnlyList<string> failures)
        {
            var text = deleted.Count > 0
                ? "Удалено параметров: " + deleted.Count + "."
                : "Ни один параметр не удалён.";

            if (failures.Count > 0)
            {
                const int limit = 15;
                text += "\n\nНе удалось удалить (" + failures.Count + "):\n• " +
                        string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… и ещё " + (failures.Count - limit);
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
