using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VladTools.Infrastructure;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// Убирает из открытого проекта то, что отмечено галочками в окне: листы, виды,
    /// легенды, спецификации, фильтры, группы модели и неиспользуемые семейства.
    ///
    /// Типовая работа: модель пришла со стороны и нужна только как геометрия —
    /// всё чужое оформление снимается разом, а не по одному узлу браузера.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CleanupCommand : IExternalCommand
    {
        private const string DialogTitle = "Очистка модели";

        /// <summary>
        /// Порядок выполнения — не тот, в котором пункты стоят в окне.
        /// Сначала уходит оформление (листы, виды, легенды, спецификации, фильтры),
        /// потом группы, и только в конце — неиспользуемые семейства: к этому моменту
        /// ненужными становятся ещё и рамки листов, марки и узловые элементы.
        /// Перечислены все значения <see cref="CleanupTarget"/>: пункта, которого здесь нет,
        /// окно предложит, а команда не выполнит.
        /// </summary>
        private static readonly CleanupTarget[] Order =
        {
            CleanupTarget.Sheets,
            CleanupTarget.Views,
            CleanupTarget.Legends,
            CleanupTarget.Schedules,
            CleanupTarget.Filters,
            CleanupTarget.ModelGroups,
            CleanupTarget.UnusedGroups,
            CleanupTarget.UnusedFamilies
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
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "Команда работает только в проекте.\n" +
                    "В редакторе семейств чистить нечего: ни листов, ни фильтров, ни групп там нет.");
                return Result.Cancelled;
            }

            try
            {
                // Активный вид не трогаем нигде: Revit не даёт удалить вид, на котором стоит
                // пользователь, и на нём же держится сеанс.
                var activeViewId = uidoc.ActiveView?.Id ?? ElementId.InvalidElementId;

                var options = Survey(doc, activeViewId);

                var window = new CleanupWindow(options);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var chosen = new HashSet<CleanupTarget>(window.Selected.Select(option => option.Target));

                var done = new List<string>();
                var notes = new List<string>();
                var failures = new List<string>();
                var warnings = new WarningSuppressor();

                using (var transaction = new Transaction(doc, "Очистка модели"))
                {
                    transaction.Start();

                    // Пакетное удаление видов, фильтров и семейств тянет за собой ворох
                    // предупреждений Revit. Модальное окно на каждое сорвало бы очистку,
                    // поэтому они гасятся и уходят в итоговый отчёт.
                    var failureOptions = transaction.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(warnings);
                    transaction.SetFailureHandlingOptions(failureOptions);

                    foreach (var target in Order)
                    {
                        if (chosen.Contains(target))
                            Run(doc, target, activeViewId, done, notes, failures);
                    }

                    if (done.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                Report(done, notes, failures, warnings.Messages);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── что есть в модели ─────────────────────────────

        /// <summary>
        /// Считает, сколько чего в модели, и собирает из этого пункты окна.
        /// Самый долгий здесь — подсчёт неиспользуемых семейств: он обходит все элементы
        /// документа, потому что занятость типоразмера иначе не узнать.
        /// </summary>
        private static IReadOnlyList<CleanupOption> Survey(Document doc, ElementId activeViewId)
        {
            var counts = new Dictionary<CleanupTarget, int>
            {
                { CleanupTarget.UnusedFamilies, UnusedSymbols(doc).Count },
                { CleanupTarget.Sheets, ViewsOf(doc, CleanupTarget.Sheets, activeViewId).Count },
                { CleanupTarget.Filters, Filters(doc).Count },
                { CleanupTarget.Views, ViewsOf(doc, CleanupTarget.Views, activeViewId).Count },
                { CleanupTarget.Legends, ViewsOf(doc, CleanupTarget.Legends, activeViewId).Count },
                { CleanupTarget.Schedules, ViewsOf(doc, CleanupTarget.Schedules, activeViewId).Count },
                { CleanupTarget.ModelGroups, ModelGroups(doc).Count },
                { CleanupTarget.UnusedGroups, UnusedGroupTypes(doc).Count }
            };

            return Enum.GetValues(typeof(CleanupTarget))
                .Cast<CleanupTarget>()
                .Select(target => CleanupOption.For(target, counts[target]))
                .ToList();
        }

        /// <summary>
        /// Выполняет один пункт. Списки собираются заново, а не берутся из окна: предыдущие
        /// пункты уже поменяли документ, и половины найденного могло не остаться.
        /// </summary>
        private static void Run(
            Document doc,
            CleanupTarget target,
            ElementId activeViewId,
            List<string> done,
            List<string> notes,
            List<string> failures)
        {
            switch (target)
            {
                case CleanupTarget.Sheets:
                    Removed(doc, ViewsOf(doc, target, activeViewId), "Лист", "Листов удалено: ", done, failures);
                    break;

                case CleanupTarget.Views:
                    Removed(doc, ViewsOf(doc, target, activeViewId), "Вид", "Видов удалено: ", done, failures);
                    break;

                case CleanupTarget.Legends:
                    Removed(doc, ViewsOf(doc, target, activeViewId), "Легенда", "Легенд удалено: ", done, failures);
                    break;

                case CleanupTarget.Schedules:
                    Removed(doc, ViewsOf(doc, target, activeViewId), "Спецификация", "Спецификаций удалено: ", done, failures);
                    break;

                case CleanupTarget.Filters:
                    Removed(doc, Filters(doc), "Фильтр", "Фильтров удалено: ", done, failures);
                    break;

                case CleanupTarget.ModelGroups:
                    Ungroup(doc, done, failures);
                    break;

                case CleanupTarget.UnusedGroups:
                    Removed(doc, UnusedGroupTypes(doc), "Тип группы", "Неиспользуемых групп удалено: ", done, failures);
                    break;

                case CleanupTarget.UnusedFamilies:
                    PurgeFamilies(doc, done, notes, failures);
                    break;
            }
        }

        // ───────────────────────────── виды, листы, фильтры ─────────────────────────────

        /// <summary>
        /// Виды нужного разряда. Шаблоны и активный вид не отдаются никогда: шаблон —
        /// не вид в браузере, а активный Revit удалить не даст.
        /// </summary>
        private static List<ElementId> ViewsOf(Document doc, CleanupTarget target, ElementId activeViewId)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate && view.Id != activeViewId && Kind(view) == target)
                .Select(view => view.Id)
                .ToList();
        }

        /// <summary>
        /// К какому пункту очистки относится вид. Служебные виды (браузеры, внутренние,
        /// отчёты расчётов) не относятся ни к какому и не удаляются вовсе.
        /// </summary>
        private static CleanupTarget? Kind(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.DrawingSheet:
                    return CleanupTarget.Sheets;

                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                case ViewType.Section:
                case ViewType.Elevation:
                case ViewType.Detail:
                case ViewType.ThreeD:
                case ViewType.Rendering:
                case ViewType.Walkthrough:
                case ViewType.DraftingView:
                    return CleanupTarget.Views;

                case ViewType.Legend:
                    return CleanupTarget.Legends;

                case ViewType.Schedule:
                case ViewType.ColumnSchedule:
                case ViewType.PanelSchedule:
                    return IsInternalSchedule(view) ? (CleanupTarget?)null : CleanupTarget.Schedules;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Спецификация, которая живёт не в браузере, а внутри рамки листа или самого файла.
        /// Такую удалять нельзя: рамка без своей спецификации изменений сломается.
        /// </summary>
        private static bool IsInternalSchedule(View view)
        {
            var schedule = view as ViewSchedule;
            if (schedule == null)
                return false;

            try
            {
                return schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule;
            }
            catch (Exception)
            {
                // Не смогли спросить — считаем служебной: не тронуть безопаснее.
                return true;
            }
        }

        /// <summary>
        /// Фильтры видов и фильтры выбора: и то и другое живёт в «Вид → Фильтры».
        /// Классы перечислены по отдельности, а не общим предком <c>FilterElement</c>:
        /// коллектор поддерживает не всякий абстрактный класс, а тут выбора и не из чего.
        /// </summary>
        private static List<ElementId> Filters(Document doc)
        {
            var classes = new List<Type> { typeof(ParameterFilterElement), typeof(SelectionFilterElement) };

            return new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticlassFilter(classes))
                .ToElementIds()
                .ToList();
        }

        // ───────────────────────────── группы ─────────────────────────────

        /// <summary>Размещённые группы модели; узловые и прикреплённые узловые — другая категория.</summary>
        private static List<Group> ModelGroups(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_IOSModelGroups)
                .WhereElementIsNotElementType()
                .OfClass(typeof(Group))
                .Cast<Group>()
                .ToList();
        }

        /// <summary>Типы групп, которых нет ни в одном месте модели.</summary>
        private static List<ElementId> UnusedGroupTypes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(GroupType))
                .Cast<GroupType>()
                .Where(type => IsUnplaced(type))
                .Select(type => type.Id)
                .ToList();
        }

        private static bool IsUnplaced(GroupType type)
        {
            try
            {
                var groups = type.Groups;
                return groups == null || groups.IsEmpty;
            }
            catch (Exception)
            {
                // Не смогли спросить — считаем размещённым и не трогаем.
                return false;
            }
        }

        /// <summary>
        /// Распускает все группы модели: элементы остаются на местах, исчезают только группы.
        ///
        /// Проходов несколько, как при удалении параметров: вложенную группу Revit не отдаёт,
        /// пока она внутри другой, — она распустится следующим проходом. Проход без единого
        /// успеха означает, что дальше не сдвинется, и его отказы идут в отчёт.
        /// </summary>
        private static void Ungroup(Document doc, List<string> done, List<string> failures)
        {
            var total = 0;

            while (true)
            {
                var groups = ModelGroups(doc);
                if (groups.Count == 0)
                    break;

                var before = total;
                var stuck = new List<string>();

                foreach (var group in groups)
                {
                    var name = SafeName(group);

                    try
                    {
                        // Закреплённую группу Revit распустить не даёт, а «разгруппировать всё»
                        // без открепления превратилось бы в список отказов.
                        if (group.Pinned)
                            group.Pinned = false;

                        group.UngroupMembers();
                        total++;
                    }
                    catch (Exception exception)
                    {
                        stuck.Add("Группа «" + name + "» — " + exception.Message);
                    }
                }

                if (total == before)
                {
                    failures.AddRange(stuck);
                    break;
                }
            }

            if (total > 0)
                done.Add("Групп модели разгруппировано: " + total + ".");
        }

        // ───────────────────────────── неиспользуемые семейства ─────────────────────────────

        /// <summary>
        /// Сносит загруженные семейства и типоразмеры, на которые в модели никто не ссылается.
        /// Семейство, у которого свободны все типоразмеры, удаляется целиком — иначе оно
        /// осталось бы висеть в браузере пустой веткой.
        /// </summary>
        private static void PurgeFamilies(Document doc, List<string> done, List<string> notes, List<string> failures)
        {
            var families = new List<ElementId>();
            var symbols = new List<ElementId>();
            var busy = 0;

            foreach (var group in UnusedSymbols(doc).GroupBy(symbol => FamilyIdOf(symbol)))
            {
                var free = new List<ElementId>();

                foreach (var symbol in group)
                {
                    if (HasInstances(symbol))
                        busy++;
                    else
                        free.Add(symbol.Id);
                }

                if (free.Count == 0)
                    continue;

                // Ключ невалиден, если у типоразмера не удалось спросить семейство:
                // тогда сносим его в одиночку, а не целую ветку браузера.
                var family = group.Key == ElementId.InvalidElementId
                    ? null
                    : doc.GetElement(group.Key) as Family;

                if (family != null && AllSymbolsFree(family, free))
                    families.Add(family.Id);
                else
                    symbols.AddRange(free);
            }

            var removedFamilies = Delete(doc, families, "Семейство", failures);
            var removedSymbols = Delete(doc, symbols, "Типоразмер", failures);

            if (removedFamilies > 0 || removedSymbols > 0)
            {
                done.Add("Неиспользуемых семейств удалено: " + removedFamilies +
                         ", отдельных типоразмеров: " + removedSymbols + ".");
            }

            if (busy > 0)
            {
                notes.Add("Типоразмеров оставлено: " + busy +
                          " — Revit сообщил, что вместе с ними из модели ушли бы элементы.");
            }
        }

        /// <summary>Все типоразмеры семейства свободны — значит и само семейство никому не нужно.</summary>
        private static bool AllSymbolsFree(Family family, IReadOnlyList<ElementId> free)
        {
            try
            {
                var known = new HashSet<ElementId>(free);
                return family.GetFamilySymbolIds().All(id => known.Contains(id));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Типоразмеры загруженных семейств, на которые в модели нет ни одной ссылки.
        /// Системные типы (стены, перекрытия, трубы) сюда не попадают — у них нет FamilySymbol.
        /// </summary>
        private static List<FamilySymbol> UnusedSymbols(Document doc)
        {
            var used = UsedTypeIds(doc);

            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(symbol => !used.Contains(symbol.Id))
                .ToList();
        }

        /// <summary>
        /// Идентификаторы типов, которыми в документе кто-то пользуется.
        ///
        /// Проход идёт по всем размещённым элементам, а не по одним FamilyInstance:
        /// типоразмер семейства держат и марки, и рамки листов, и узловые элементы —
        /// классы у них разные, а GetTypeId есть у каждого. Отдельно добираются типы
        /// (панель навесной стены и вложенный типоразмер лежат у них в параметре,
        /// а не в GetTypeId) и компоненты легенд.
        ///
        /// Проход по всей модели не бесплатен, но обойтись без него нельзя: команду
        /// «Удалить неиспользуемые» Revit 2022 через API не отдаёт.
        /// </summary>
        private static HashSet<ElementId> UsedTypeIds(Document doc)
        {
            var used = new HashSet<ElementId>();

            foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                    used.Add(typeId);
            }

            foreach (var type in new FilteredElementCollector(doc).WhereElementIsElementType())
                AddReferences(type, used);

            foreach (var component in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_LegendComponents)
                         .WhereElementIsNotElementType())
                AddReferences(component, used);

            return used;
        }

        /// <summary>
        /// Складывает в набор все элементы, на которые ссылаются параметры этого элемента.
        /// Ссылки на самого себя и на своё семейство пропускаются: встроенные параметры
        /// «Семейство» и «Тип» есть у каждого типоразмера, и без этой проверки занятыми
        /// оказались бы поголовно все.
        /// </summary>
        private static void AddReferences(Element element, HashSet<ElementId> used)
        {
            try
            {
                var symbol = element as FamilySymbol;
                var ownFamily = symbol == null ? ElementId.InvalidElementId : FamilyIdOf(symbol);

                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter.StorageType != StorageType.ElementId)
                        continue;

                    var id = parameter.AsElementId();

                    if (id == null || id == ElementId.InvalidElementId || id == element.Id || id == ownFamily)
                        continue;

                    used.Add(id);
                }
            }
            catch (Exception)
            {
                // Элемент не дал прочитать параметры — просто ничего от него не берём.
            }
        }

        /// <summary>
        /// Есть ли в модели элементы, которые Revit унесёт вместе с этим типоразмером.
        ///
        /// Проход по GetTypeId ловит почти всё, но ссылка бывает и косвенной, а цена ошибки —
        /// стёртая геометрия, поэтому перед самым удалением спрашиваем сам Revit. Фильтр
        /// «не типы» оставляет в ответе только размещённые элементы: сам типоразмер в него
        /// не попадает.
        /// </summary>
        private static bool HasInstances(FamilySymbol symbol)
        {
            try
            {
                var dependents = symbol.GetDependentElements(new ElementIsElementTypeFilter(true));
                return dependents != null && dependents.Count > 0;
            }
            catch (Exception)
            {
                // Не смогли спросить — считаем занятым: не дочистить безопаснее, чем стереть лишнее.
                return true;
            }
        }

        private static ElementId FamilyIdOf(FamilySymbol symbol)
        {
            try
            {
                var family = symbol.Family;
                return family == null ? ElementId.InvalidElementId : family.Id;
            }
            catch (Exception)
            {
                return ElementId.InvalidElementId;
            }
        }

        // ───────────────────────────── удаление ─────────────────────────────

        private static void Removed(
            Document doc,
            IReadOnlyList<ElementId> ids,
            string what,
            string report,
            List<string> done,
            List<string> failures)
        {
            var count = Delete(doc, ids, what, failures);

            if (count > 0)
                done.Add(report + count + ".");
        }

        /// <summary>
        /// Удаляет элементы по одному: отказ на одном не должен срывать всю пачку.
        /// Revit уносит за раз и связанное — зависимый вид вместе с основным, типоразмеры
        /// вместе с семейством, — поэтому перед каждым удалением проверяем, жив ли элемент.
        /// </summary>
        private static int Delete(Document doc, IReadOnlyList<ElementId> ids, string what, List<string> failures)
        {
            var count = 0;

            foreach (var id in ids)
            {
                var name = SafeName(doc, id);

                try
                {
                    if (doc.GetElement(id) == null)
                    {
                        count++;
                        continue;
                    }

                    var removed = doc.Delete(id);

                    if (removed != null && removed.Count > 0)
                        count++;
                    else
                        failures.Add(what + " «" + name + "» — Revit не отдал элемент на удаление");
                }
                catch (Exception exception)
                {
                    failures.Add(what + " «" + name + "» — " + exception.Message);
                }
            }

            return count;
        }

        private static string SafeName(Document doc, ElementId id)
        {
            try
            {
                var element = doc.GetElement(id);
                return element == null ? "id " + id.IntegerValue : SafeName(element);
            }
            catch (Exception)
            {
                return "id " + id.IntegerValue;
            }
        }

        private static string SafeName(Element element)
        {
            try
            {
                var name = element.Name;
                return string.IsNullOrEmpty(name) ? "id " + element.Id.IntegerValue : name;
            }
            catch (Exception)
            {
                return "id " + element.Id.IntegerValue;
            }
        }

        // ───────────────────────────── отчёт ─────────────────────────────

        private static void Report(
            IReadOnlyList<string> done,
            IReadOnlyList<string> notes,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
        {
            var text = done.Count > 0
                ? "Очистка выполнена.\n\n• " + string.Join("\n• ", done)
                : "Из модели ничего не убрано.";

            if (notes.Count > 0)
                text += "\n\n" + string.Join("\n", notes);

            if (failures.Count > 0)
            {
                const int limit = 15;
                text += "\n\nНе удалось убрать (" + failures.Count + "):\n• " +
                        string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… и ещё " + (failures.Count - limit);
            }

            if (warnings.Count > 0)
            {
                const int limit = 5;
                text += "\n\nПредупреждения Revit (" + warnings.Count + "):\n• " +
                        string.Join("\n• ", warnings.Take(limit));

                if (warnings.Count > limit)
                    text += "\n… и ещё " + (warnings.Count - limit);
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
