using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using VladTools.Infrastructure;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// Расставляет по сторонам выбранных помещений нитки размеров — аналог того, что в
    /// демонстрациях сторонних плагинов называется «Auto Dim Lines»: пользователь один раз
    /// вручную ставит несколько ниток вдоль одной стены, дальше кнопка повторяет тот же набор
    /// по всем сторонам всех выбранных помещений.
    ///
    /// Образец не копируется по ссылкам — ссылки образца принадлежат конкретным стенам и
    /// в другом помещении бессмысленны. Вместо этого заводится каталог видов ниток
    /// (<see cref="DimensionChainKind"/>), а образец лишь подсказывает, какие виды и с каким
    /// смещением взять — это подбор (эвристика), и результат всегда можно поправить в окне.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AutoDimensionCommand : IExternalCommand
    {
        private const string DialogTitle = "Авторазмеры";
        private const double LineMarginMm = 200;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "Нет активного документа.";
                return Result.Cancelled;
            }

            var doc = uidoc.Document;
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "Команда работает только в проекте — расставляет размеры по помещениям.\n" +
                    "В редакторе семейств помещений нет.");
                return Result.Cancelled;
            }

            var view = uidoc.ActiveView as ViewPlan;
            if (view == null || view.ViewType != ViewType.FloorPlan)
            {
                TaskDialog.Show(DialogTitle,
                    "Команда работает на планах этажей. Перейдите на план этажа и повторите.");
                return Result.Cancelled;
            }

            try
            {
                var rooms = SelectedRooms(uidoc, doc, view);
                if (rooms == null)
                    return Result.Cancelled;

                if (rooms.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "На этом виде нет ни одного размещённого помещения.");
                    return Result.Cancelled;
                }

                return RunWindow(commandData, uidoc, doc, view, rooms);
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── выбор помещений ─────────────────────────────

        /// <summary>Помещения из текущего выделения; пусто — предлагает взять все помещения активного вида.</summary>
        private static List<Room> SelectedRooms(UIDocument uidoc, Document doc, ViewPlan view)
        {
            var picked = uidoc.Selection.GetElementIds()
                .Select(doc.GetElement)
                .OfType<Room>()
                .Where(room => room.Area > 0)
                .ToList();

            if (picked.Count > 0)
                return picked;

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = "Помещения не выбраны.",
                MainContent = "Взять все размещённые помещения активного плана этажа?",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Yes
            };

            if (dialog.Show() != TaskDialogResult.Yes)
                return null;

            return new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(room => room.Area > 0)
                .ToList();
        }

        // ───────────────────────────── окно и цикл «взять образец» ─────────────────────────────

        private static Result RunWindow(ExternalCommandData commandData, UIDocument uidoc, Document doc, ViewPlan view, List<Room> rooms)
        {
            var dimensionTypes = DimensionTypes(doc);
            var prefs = AutoDimensionPreferences.Load();

            var boundary = prefs.Boundary;
            var outward = prefs.Outward;
            var removePrevious = prefs.RemovePrevious;
            var templateName = prefs.LastTemplate;

            IReadOnlyList<DimensionChainRow> rows = InitialRows(templateName, dimensionTypes, ref boundary, ref outward);

            while (true)
            {
                var templateNames = DimensionTemplateLibrary.Names();

                var window = new AutoDimensionWindow(
                    rooms.Count,
                    dimensionTypes,
                    templateNames,
                    rows,
                    boundary,
                    outward,
                    removePrevious,
                    templateName,
                    name => DimensionTemplateLibrary.Load(name),
                    (name, template) => DimensionTemplateLibrary.Save(name, template));

                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                var dialogResult = window.ShowDialog();

                boundary = window.Boundary;
                outward = window.Outward;
                removePrevious = window.RemovePrevious;
                templateName = window.TemplateName;
                rows = window.Rows;

                prefs.Boundary = boundary;
                prefs.Outward = outward;
                prefs.RemovePrevious = removePrevious;
                prefs.LastTemplate = templateName;
                prefs.Save();

                if (dialogResult != true)
                    return Result.Cancelled;

                if (!window.WantsSample)
                {
                    var selected = window.Rows.Where(r => r.IsEnabled).ToList();
                    return Place(doc, view, rooms, selected, boundary, outward, removePrevious);
                }

                rows = TakeSample(uidoc, doc, boundary, rows);
            }
        }

        private static IReadOnlyList<DimensionChainRow> InitialRows(
            string templateName,
            IReadOnlyList<DimensionTypeInfo> dimensionTypes,
            ref SpatialElementBoundaryLocation boundary,
            ref bool outward)
        {
            if (string.IsNullOrEmpty(templateName))
                return new List<DimensionChainRow>();

            var template = DimensionTemplateLibrary.Load(templateName);
            if (template.Chains.Count == 0)
                return new List<DimensionChainRow>();

            boundary = template.Boundary;
            outward = template.Outward;

            return template.Chains.Select(chain => new DimensionChainRow
            {
                Kind = chain.Kind,
                OffsetMm = chain.OffsetMm,
                DimensionTypeName = chain.DimensionTypeName
            }).ToList();
        }

        /// <summary>
        /// Выбор образцовых размеров вне модального окна (Revit не даёт вызвать PickObject, пока
        /// оно открыто) и их разбор в шаблон. Отмена выбора — не ошибка, просто открываем окно
        /// заново с тем, что в нём уже было.
        /// </summary>
        private static IReadOnlyList<DimensionChainRow> TakeSample(
            UIDocument uidoc,
            Document doc,
            SpatialElementBoundaryLocation boundary,
            IReadOnlyList<DimensionChainRow> current)
        {
            IList<Reference> picked;
            try
            {
                picked = uidoc.Selection.PickObjects(
                    ObjectType.Element,
                    new DimensionOnlyFilter(),
                    "Выделите образцовые размеры вдоль одной стены и нажмите «Готово»");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return current;
            }

            var samples = picked
                .Select(reference => doc.GetElement(reference.ElementId) as Dimension)
                .Where(dimension => dimension != null)
                .ToList();

            if (samples.Count == 0)
                return current;

            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = boundary };
            var result = DimensionSampleReader.Read(doc, samples, options);

            if (result.Messages.Count > 0)
            {
                const int limit = 10;
                var text = "Разобрано ниток: " + result.Rows.Count + " из " + samples.Count + " выделенных размеров.\n\n" +
                           "Не разобрано:\n• " + string.Join("\n• ", result.Messages.Take(limit));

                if (result.Messages.Count > limit)
                    text += "\n… и ещё " + (result.Messages.Count - limit);

                TaskDialog.Show(DialogTitle, text);
            }

            return result.Rows.Count > 0 ? result.Rows : current;
        }

        private sealed class DimensionOnlyFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is Dimension;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }

        // ───────────────────────────── расстановка ─────────────────────────────

        private static Result Place(
            Document doc,
            ViewPlan view,
            List<Room> rooms,
            List<DimensionChainRow> chains,
            SpatialElementBoundaryLocation boundary,
            bool outward,
            bool removePrevious)
        {
            if (chains.Count == 0)
            {
                TaskDialog.Show(DialogTitle, "Не отмечено ни одной нитки — расставлять нечего.");
                return Result.Cancelled;
            }

            var boundaryOptions = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = boundary };
            var collector = new DimensionReferenceCollector(doc);
            var defaultTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.LinearDimensionType);
            var typesByName = DimensionTypesByName(doc);

            var skippedRooms = new List<string>();
            var skippedChains = new List<string>();
            var pending = new List<PendingDimension>();

            // ФАЗА ЧТЕНИЯ — вся геометрия стен читается здесь, до единой правки документа.
            //
            // Создание Dimension — тоже правка документа: оно помечает геометрию как изменившуюся,
            // и любой Face/Reference, прочитанный ДО этого момента (в том числе из кэша
            // DimensionReferenceCollector — тот кэширует не глядя на транзакции), Revit после
            // такой правки больше не считает валидным. Раньше сбор ссылок и создание размера шли
            // вперемешку внутри одного цикла по ниткам — и вторая нитка на той же стороне читала
            // уже устаревшую грань, оставшуюся в кэше от первой, с непонятной ошибкой геометрического
            // ядра («The input curve is not bound», без адреса в нашем коде). Поэтому сначала —
            // читаем всё и складываем в pending, потом, отдельным проходом внутри транзакции, —
            // только пишем.
            foreach (var room in rooms)
            {
                var roomLabel = RoomLabel(room);

                List<RoomSide> sides;
                try
                {
                    sides = RoomSideBuilder.Build(room, boundaryOptions);
                }
                catch (Exception exception)
                {
                    skippedRooms.Add(roomLabel + " — не удалось построить границу: " + exception.Message);
                    continue;
                }

                if (sides.Count == 0)
                {
                    skippedRooms.Add(roomLabel + " — у помещения нет границы (не размещено?)");
                    continue;
                }

                var foundHere = 0;

                foreach (var side in sides)
                {
                    if (side.IsCurved)
                    {
                        skippedChains.Add(roomLabel + ", сторона " + SideLabel(side) + " — сторона криволинейна, пропущена");
                        continue;
                    }

                    if (!side.HasWall)
                        continue; // граница без стены (разделитель помещений) — засекать нечего

                    var chainIndex = 0;
                    foreach (var chain in chains)
                    {
                        chainIndex++;

                        var result = collector.Collect(side, sides, chain.Kind);
                        if (!result.Success)
                        {
                            skippedChains.Add(roomLabel + ", сторона " + SideLabel(side) + ", нитка «" +
                                               DimensionChainKindText.Caption(chain.Kind) + "» — " + result.FailureReason);
                            continue;
                        }

                        pending.Add(new PendingDimension
                        {
                            Line = ChainLine(side, chain.OffsetMm, outward),
                            References = result.References,
                            DimensionType = ResolveType(chain.DimensionTypeName, typesByName, doc, defaultTypeId),
                            RoomUniqueId = room.UniqueId,
                            ChainIndex = chainIndex,
                            Label = roomLabel + ", сторона " + SideLabel(side) + ", нитка «" +
                                    DimensionChainKindText.Caption(chain.Kind) + "»"
                        });
                        foundHere++;
                    }
                }

                if (foundHere == 0)
                    skippedRooms.Add(roomLabel + " — не найдено ни одной нитки для расстановки");
            }

            if (pending.Count == 0)
            {
                Report(0, rooms.Count, skippedRooms, skippedChains, new List<string>(), new List<string>());
                return Result.Cancelled;
            }

            // ФАЗА ЗАПИСИ — только создание размеров, никаких новых чтений геометрии стен.
            var placed = 0;
            var failures = new List<string>();
            var warnings = new WarningSuppressor();

            using (var transaction = new Transaction(doc, "Авторазмеры"))
            {
                transaction.Start();

                var failureOptions = transaction.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(warnings);
                transaction.SetFailureHandlingOptions(failureOptions);

                if (removePrevious)
                {
                    var roomUniqueIds = new HashSet<string>(rooms.Select(room => room.UniqueId));
                    var previous = AutoDimensionMarker.FindMarked(doc, view.Id, roomUniqueIds);

                    foreach (var id in previous)
                    {
                        try
                        {
                            doc.Delete(id);
                        }
                        catch (Exception)
                        {
                            // старый размер не снялся — новый всё равно встанет рядом, не критично
                        }
                    }
                }

                foreach (var item in pending)
                {
                    try
                    {
                        var dimension = item.DimensionType != null
                            ? doc.Create.NewDimension(view, item.Line, item.References, item.DimensionType)
                            : doc.Create.NewDimension(view, item.Line, item.References);

                        AutoDimensionMarker.Mark(dimension, item.RoomUniqueId, item.ChainIndex);
                        placed++;
                    }
                    catch (Exception exception)
                    {
                        failures.Add(item.Label + " — " + exception.Message);
                    }
                }

                if (placed == 0)
                    transaction.RollBack();
                else
                    transaction.Commit();
            }

            Report(placed, rooms.Count, skippedRooms, skippedChains, failures, warnings.Messages);
            return placed > 0 ? Result.Succeeded : Result.Cancelled;
        }

        /// <summary>Одна ещё не созданная нитка: геометрия под неё уже прочитана и годна к записи.</summary>
        private sealed class PendingDimension
        {
            public Line Line;
            public ReferenceArray References;
            public DimensionType DimensionType;
            public string RoomUniqueId;
            public int ChainIndex;
            public string Label;
        }

        private static Line ChainLine(RoomSide side, double offsetMm, bool outward)
        {
            var normal = outward ? side.InwardNormal.Negate() : side.InwardNormal;
            var offsetFeet = FeetOf(offsetMm);
            var marginFeet = FeetOf(LineMarginMm);

            var start = side.Start + normal.Multiply(offsetFeet) - side.Direction.Multiply(marginFeet);
            var end = side.End + normal.Multiply(offsetFeet) + side.Direction.Multiply(marginFeet);

            return Line.CreateBound(start, end);
        }

        private static DimensionType ResolveType(string name, Dictionary<string, DimensionType> byName, Document doc, ElementId defaultTypeId)
        {
            if (!string.IsNullOrEmpty(name))
            {
                DimensionType found;
                if (byName.TryGetValue(name, out found))
                    return found;
            }

            return defaultTypeId == null || defaultTypeId == ElementId.InvalidElementId
                ? null
                : doc.GetElement(defaultTypeId) as DimensionType;
        }

        private static IReadOnlyList<DimensionTypeInfo> DimensionTypes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DimensionType))
                .Cast<DimensionType>()
                .Where(type => SafeStyle(type) == DimensionStyleType.Linear)
                .Select(type => new DimensionTypeInfo(type.Id.IntegerValue, type.Name))
                .OrderBy(info => info.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static Dictionary<string, DimensionType> DimensionTypesByName(Document doc)
        {
            var result = new Dictionary<string, DimensionType>(StringComparer.OrdinalIgnoreCase);

            foreach (var type in new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>())
            {
                if (SafeStyle(type) != DimensionStyleType.Linear)
                    continue;

                var name = SafeName(type);
                if (name.Length > 0 && !result.ContainsKey(name))
                    result[name] = type;
            }

            return result;
        }

        private static DimensionStyleType? SafeStyle(DimensionType type)
        {
            try
            {
                return type.StyleType;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SafeName(Element element)
        {
            try
            {
                return element.Name ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string RoomLabel(Room room)
        {
            var name = SafeName(room);
            var numberText = string.IsNullOrEmpty(room.Number) ? string.Empty : room.Number + " ";
            return "Помещение " + numberText + name;
        }

        private static string SideLabel(RoomSide side)
        {
            return (side.LoopIndex + 1) + "." + side.Index;
        }

        private static double FeetOf(double millimeters)
        {
            return UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);
        }

        // ───────────────────────────── отчёт ─────────────────────────────

        private static void Report(
            int placed,
            int roomCount,
            List<string> skippedRooms,
            List<string> skippedChains,
            List<string> failures,
            IReadOnlyList<string> warnings)
        {
            var text = placed > 0
                ? "Размеров поставлено: " + placed + " (помещений: " + roomCount + ")."
                : "Не поставлено ни одного размера.";

            if (skippedRooms.Count > 0)
                text += "\n\n" + Bulleted("Пропущено помещений", skippedRooms, 10);

            if (skippedChains.Count > 0)
                text += "\n\n" + Bulleted("Не поставлено ниток", skippedChains, 15);

            if (failures.Count > 0)
                text += "\n\n" + Bulleted("Ошибки Revit", failures, 15);

            if (warnings.Count > 0)
                text += "\n\n" + Bulleted("Предупреждения Revit", warnings.ToList(), 5);

            TaskDialog.Show(DialogTitle, text);
        }

        private static string Bulleted(string title, List<string> items, int limit)
        {
            var text = title + " (" + items.Count + "):\n• " + string.Join("\n• ", items.Take(limit));
            if (items.Count > limit)
                text += "\n… и ещё " + (items.Count - limit);
            return text;
        }
    }
}
