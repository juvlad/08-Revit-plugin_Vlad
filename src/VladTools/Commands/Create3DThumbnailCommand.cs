using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// Создаёт в открытом семействе 3D-вид «3д миниатюра» и настраивает его:
    /// аннотации выключены, соединители скрыты, реалистичная графика, высокая детализация.
    /// Повторный запуск не плодит виды — настройки применяются к существующему.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Create3DThumbnailCommand : IExternalCommand
    {
        public const string ViewName = "3д миниатюра";

        private const string DialogTitle = "3д миниатюра";

        /// <summary>Категории соединителей, которые надо скрыть на виде.</summary>
        private static readonly BuiltInCategory[] ConnectorCategories =
        {
            BuiltInCategory.OST_ConnectorElem,
            BuiltInCategory.OST_ConnectorElemXAxis,
            BuiltInCategory.OST_ConnectorElemYAxis,
            BuiltInCategory.OST_ConnectorElemZAxis
        };

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

            var warnings = new List<string>();

            try
            {
                View3D view;

                using (var transaction = new Transaction(doc, "Создать «" + ViewName + "»"))
                {
                    transaction.Start();

                    view = FindView(doc, ViewName);
                    if (view == null)
                    {
                        var viewTypeId = Get3DViewFamilyTypeId(doc);
                        if (viewTypeId == ElementId.InvalidElementId)
                        {
                            transaction.RollBack();
                            message = "В этом семействе нет типа 3D-вида (ViewFamilyType), создать вид невозможно.";
                            return Result.Failed;
                        }

                        view = View3D.CreateIsometric(doc, viewTypeId);
                        Rename(view, ViewName, warnings);
                    }

                    ApplySettings(view, warnings);

                    transaction.Commit();
                }

                ActivateView(uidoc, view, warnings);

                if (warnings.Count > 0)
                {
                    TaskDialog.Show(DialogTitle,
                        "Вид «" + view.Name + "» готов, но часть настроек применить не удалось:\n\n• " +
                        string.Join("\n• ", warnings));
                }

                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        /// <summary>Применяет все требуемые настройки вида. Каждая — независимо от остальных.</summary>
        private static void ApplySettings(View3D view, List<string> warnings)
        {
            Try(() => view.AreAnnotationCategoriesHidden = true, "выключить аннотации", warnings);
            Try(() => HideConnectors(view, warnings), "скрыть соединители", warnings);
            Try(() => view.DisplayStyle = DisplayStyle.Realistic, "включить реалистичную графику", warnings);
            Try(() => view.DetailLevel = ViewDetailLevel.Fine, "выставить высокую детализацию", warnings);
        }

        /// <summary>
        /// Скрывает соединители двумя способами сразу: категориями V/G и поэлементно.
        /// Категории в семействе может не быть в Settings.Categories, поэтому обращаемся
        /// к ней напрямую по ElementId, а не через Category.GetCategory (тот возвращает null).
        /// </summary>
        private static void HideConnectors(View3D view, List<string> warnings)
        {
            var problems = new List<string>();
            var hidSomething = HideConnectorCategories(view, problems);

            // Фильтруем в памяти: OfClass(typeof(ConnectorElement)) поддерживается не всегда,
            // а документ семейства маленький, так что перебор безопаснее.
            var connectors = new FilteredElementCollector(view.Document)
                .WhereElementIsNotElementType()
                .OfType<ConnectorElement>()
                .Cast<Element>()
                .ToList();

            if (HideConnectorElements(view, connectors, problems))
                hidSomething = true;

            // Молчим, только если скрывать было нечего или всё получилось.
            if (hidSomething || connectors.Count == 0)
                return;

            warnings.Add("не удалось скрыть соединители (" + connectors.Count + " шт.): " +
                         string.Join("; ", problems));
        }

        private static bool HideConnectorCategories(View3D view, List<string> problems)
        {
            var result = false;

            foreach (var builtInCategory in ConnectorCategories)
            {
                var categoryId = new ElementId(builtInCategory);
                try
                {
                    if (!view.GetCategoryHidden(categoryId))
                        view.SetCategoryHidden(categoryId, true);

                    result = true;
                }
                catch (Exception exception)
                {
                    problems.Add("категория " + builtInCategory + " — " + exception.Message);
                }
            }

            return result;
        }

        private static bool HideConnectorElements(View3D view, ICollection<Element> connectors, List<string> problems)
        {
            var hideable = connectors.Where(element => element.CanBeHidden(view) && !element.IsHidden(view))
                                     .Select(element => element.Id)
                                     .ToList();
            if (hideable.Count == 0)
                return false;

            try
            {
                view.HideElements(hideable);
                return true;
            }
            catch (Exception exception)
            {
                problems.Add("скрытие элементов — " + exception.Message);
                return false;
            }
        }

        private static View3D FindView(Document document, string name)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(view => !view.IsTemplate &&
                                        string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static ElementId Get3DViewFamilyTypeId(Document document)
        {
            var viewFamilyType = new FilteredElementCollector(document)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(type => type.ViewFamily == ViewFamily.ThreeDimensional);

            return viewFamilyType?.Id ?? ElementId.InvalidElementId;
        }

        private static void Rename(View3D view, string name, List<string> warnings)
        {
            // Имя может быть занято видом другого типа (план, разрез) — тогда оставляем имя по умолчанию.
            Try(() => view.Name = name, "переименовать вид в «" + name + "»", warnings);
        }

        private static void ActivateView(UIDocument uidoc, View3D view, List<string> warnings)
        {
            if (uidoc.ActiveView != null && uidoc.ActiveView.Id == view.Id)
                return;

            Try(() => uidoc.ActiveView = view, "открыть вид", warnings);
        }

        private static void Try(Action action, string what, List<string> warnings)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                warnings.Add("не удалось " + what + " (" + exception.Message + ")");
            }
        }
    }
}
