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
    /// Загрузка координационного («базового») файла в модель раздела со всей сопутствующей
    /// настройкой: общие координаты, имя площадки, булавка, переход в рабочий набор.
    ///
    /// В Revit это пять действий в разных углах ленты, и порядок между ними важен: сначала
    /// связь, потом координаты из неё, потом площадка, и только после перехода в
    /// «00_Shared levels and grids» — копирование уровней и осей, чтобы копии легли в нужный
    /// набор. Каждое действие само по себе на полминуты, но делается в каждом разделе каждого
    /// проекта, и забытый шаг всплывает через месяц.
    ///
    /// **Само копирование мониторингом кнопка не делает и сделать не может.** В Revit API
    /// создания связей мониторинга нет вовсе — есть только чтение уже существующих
    /// (<c>Element.IsMonitoringLinkElement</c>, <c>GetMonitoredLinkElementIds</c>); проверено
    /// рефлексией по RevitAPI.dll 2022, 2024 и 2025. Поэтому последним шагом команда открывает
    /// сам режим «Копирование/Мониторинг → Выбрать связь» (<c>PostableCommand</c>), а выбор
    /// связи и элементов остаётся за пользователем.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BaseFileCommand : IExternalCommand
    {
        private const string DialogTitle = "Базовый файл";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
                return Result.Cancelled;

            var doc = uidoc.Document;
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "Команда работает только в проекте.\n" +
                    "В редактор семейств связи Revit не вставляются.");
                return Result.Cancelled;
            }

            try
            {
                var window = new BaseFileWindow(LinkCatalog.Existing(doc), LinkCatalog.HostWorksets(doc));
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var row = window.Selected;
                var preferences = window.Preferences;

                var done = new List<string>();
                var failures = new List<string>();
                var warnings = new WarningSuppressor();

                var target = WorksetId.InvalidWorksetId;

                using (var transaction = new Transaction(doc, "Базовый файл"))
                {
                    transaction.Start();

                    // Связь координационного файла почти всегда приезжает с предупреждениями —
                    // о координатах, о дублях имён, о версии файла. Модальное окно на каждое
                    // превратило бы одну кнопку в щёлканье по диалогам.
                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(warnings);
                    transaction.SetFailureHandlingOptions(options);

                    var instanceId = Link(doc, row, preferences, done, failures);

                    if (instanceId != ElementId.InvalidElementId)
                    {
                        // Без регенерации только что созданный экземпляр для Revit ещё
                        // не существует как геометрия, а «Получить координаты» работает
                        // именно с ней.
                        doc.Regenerate();

                        if (preferences.Acquire)
                            Acquire(doc, instanceId, done, failures);

                        if (preferences.Pin)
                            Pin(doc, instanceId, row.Name, done, failures);
                    }

                    if (preferences.Rename)
                        Rename(doc, preferences.Site, done, failures);

                    if (preferences.Activate)
                        target = Ensure(doc, preferences.Workset, done, failures);

                    if (done.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                // Активный рабочий набор — не содержимое документа, а состояние сеанса:
                // Ctrl+Z его возвращать не должен, поэтому переход идёт после Commit.
                if (preferences.Activate)
                    Activate(doc, target, preferences.Workset, done, failures);

                if (preferences.Monitor)
                    OpenMonitor(commandData.Application, done, failures);

                Report(done, failures, warnings.Messages);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── связь ─────────────────────────────

        /// <summary>
        /// Заводит связь на координационный файл и возвращает <see cref="ElementId"/> её
        /// экземпляра — именно он нужен и «Получить координаты», и булавке.
        /// Связь на эту модель уже стоит в проекте — берём её: Revit на повторную
        /// <c>Create</c> всё равно ответит «такая связь уже есть».
        /// </summary>
        private static ElementId Link(
            Document doc,
            LinkRow row,
            BaseFilePreferences preferences,
            List<string> done,
            List<string> failures)
        {
            var placement = LinkCatalog.Placement(preferences.Placement);
            var worksets = LinkCatalog.WorksetIds(doc);

            if (row.IsExisting)
            {
                var instances = LinkCatalog.Instances(doc, row.ExistingId);
                var instance = instances.FirstOrDefault();
                if (instance != null)
                {
                    done.Add("Связь «" + row.Name + "» уже в проекте — работаем с ней");
                    Move(doc, row, instances, worksets, done, failures);

                    return instance.Id;
                }

                // Тип связи загружен, а экземпляра в модели нет — такое остаётся после
                // «Удалить» на экземпляре. Вставляем экземпляр, тип трогать незачем.
                return Place(doc, row, row.ExistingId, placement, worksets,
                    "Связь «" + row.Name + "» вставлена (тип уже был загружен)", done, failures);
            }

            try
            {
                // Относительный путь бывает только у файла: у Revit Server и облака
                // путь всегда абсолютный, и Revit относительный там просто не примет.
                using (var options = new RevitLinkOptions(row.Entry.Origin == LinkOrigin.File))
                {
                    var result = RevitLinkType.Create(doc, LinkCatalog.ToModelPath(row.Entry), options);

                    if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                    {
                        failures.Add(row.Name + " — " + LinkCatalog.Describe(result.LoadResult));
                        return ElementId.InvalidElementId;
                    }

                    return Place(doc, row, result.ElementId, placement, worksets,
                        "Связь «" + row.Name + "» загружена", done, failures);
                }
            }
            catch (Exception exception)
            {
                failures.Add(row.Name + " — связать не удалось: " + LinkCatalog.Short(exception.Message));
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>
        /// Переносит уже стоящую связь в набор, выбранный в окне. Молчаливо пропустить это нельзя:
        /// набор в окне показан и его можно сменить, а несделанная правка выглядела бы как сделанная.
        /// Переезжают все экземпляры связи и её тип — так же, как при создании.
        /// </summary>
        private static void Move(
            Document doc,
            LinkRow row,
            IReadOnlyList<RevitLinkInstance> instances,
            Dictionary<string, WorksetId> worksets,
            List<string> done,
            List<string> failures)
        {
            WorksetId workset;
            if (row.Entry.Workset.Length == 0 || !worksets.TryGetValue(row.Entry.Workset, out workset))
                return;

            var moved = instances.Count(instance => LinkCatalog.Place(instance, workset, failures, row.Name));
            LinkCatalog.Place(doc.GetElement(row.ExistingId), workset, failures, row.Name);

            if (moved > 0)
                done.Add("Связь «" + row.Name + "» переложена в набор «" + row.Entry.Workset + "»");
        }

        /// <summary>Вставляет экземпляр связи и кладёт его в выбранный рабочий набор проекта.</summary>
        private static ElementId Place(
            Document doc,
            LinkRow row,
            ElementId typeId,
            ImportPlacement placement,
            Dictionary<string, WorksetId> worksets,
            string success,
            List<string> done,
            List<string> failures)
        {
            try
            {
                var instance = RevitLinkInstance.Create(doc, typeId, placement);

                // Набор задаётся уже созданным элементам, а не через активный набор документа:
                // так связь ложится туда, куда просили, независимо от того, где стоит пользователь.
                // Кладём и экземпляр, и тип — так же делает сам Revit.
                WorksetId workset;
                if (row.Entry.Workset.Length > 0 && worksets.TryGetValue(row.Entry.Workset, out workset))
                {
                    LinkCatalog.Place(instance, workset, failures, row.Name);
                    LinkCatalog.Place(doc.GetElement(typeId), workset, failures, row.Name);
                }

                done.Add(success);
                return instance.Id;
            }
            catch (Exception exception)
            {
                failures.Add(row.Name + " — вставить связь не удалось: " + LinkCatalog.Short(exception.Message));
                return ElementId.InvalidElementId;
            }
        }

        // ───────────────────────────── настройка проекта ─────────────────────────────

        /// <summary>
        /// «Координаты → Получить координаты»: общая система координат проекта становится
        /// такой же, как у базового файла.
        /// </summary>
        private static void Acquire(Document doc, ElementId instanceId, List<string> done, List<string> failures)
        {
            try
            {
                doc.AcquireCoordinates(instanceId);
                done.Add("Общие координаты получены из базового файла");
            }
            catch (Exception exception)
            {
                failures.Add("Получить общие координаты не удалось: " + LinkCatalog.Short(exception.Message));
            }
        }

        /// <summary>
        /// Переименование площадки проекта. Имя площадки должно быть уникальным среди площадок
        /// проекта — отказ Revit уходит в отчёт, остальные шаги от этого не срываются.
        /// </summary>
        private static void Rename(Document doc, string name, List<string> done, List<string> failures)
        {
            try
            {
                var location = doc.ActiveProjectLocation;
                if (location == null)
                {
                    failures.Add("Переименовать площадку не удалось: активной площадки в проекте нет.");
                    return;
                }

                if (string.Equals(location.Name, name, StringComparison.CurrentCulture))
                {
                    done.Add("Площадка проекта уже называется «" + name + "»");
                    return;
                }

                location.Name = name;
                done.Add("Площадка проекта переименована в «" + name + "»");
            }
            catch (Exception exception)
            {
                failures.Add("Переименовать площадку не удалось: " + LinkCatalog.Short(exception.Message));
            }
        }

        /// <summary>
        /// Закрепление связи булавкой. Базовый файл вставлен по координатам, и случайный сдвиг
        /// мышью потом ищут всей командой; снять булавку в Revit — одна кнопка, вернуть уехавшую
        /// связь на место — нет.
        /// </summary>
        private static void Pin(Document doc, ElementId instanceId, string name, List<string> done, List<string> failures)
        {
            try
            {
                var instance = doc.GetElement(instanceId);
                if (instance == null)
                    return;

                if (instance.Pinned)
                {
                    done.Add("Связь «" + name + "» уже закреплена");
                    return;
                }

                instance.Pinned = true;
                done.Add("Связь «" + name + "» закреплена булавкой");
            }
            catch (Exception exception)
            {
                failures.Add("Закрепить связь не удалось: " + LinkCatalog.Short(exception.Message));
            }
        }

        // ───────────────────────────── рабочий набор ─────────────────────────────

        /// <summary>
        /// Ищет рабочий набор по имени, а если такого нет — создаёт. Создание здесь не
        /// самодеятельность: в новом разделе «00_Shared levels and grids» часто ещё не заведён,
        /// а перейти в несуществующий набор нельзя, и кнопка молча не сделала бы главного.
        /// </summary>
        private static WorksetId Ensure(Document doc, string name, List<string> done, List<string> failures)
        {
            if (!doc.IsWorkshared)
            {
                failures.Add("Перейти в рабочий набор нельзя: проект не совмещённый.");
                return WorksetId.InvalidWorksetId;
            }

            WorksetId existing;
            if (LinkCatalog.WorksetIds(doc).TryGetValue(name, out existing))
                return existing;

            try
            {
                if (!WorksetTable.IsWorksetNameUnique(doc, name))
                {
                    failures.Add("Создать рабочий набор «" + name + "» нельзя: имя уже занято.");
                    return WorksetId.InvalidWorksetId;
                }

                var created = Workset.Create(doc, name);
                done.Add("Создан рабочий набор «" + name + "»");

                return created.Id;
            }
            catch (Exception exception)
            {
                failures.Add("Создать рабочий набор «" + name + "» не удалось: " + LinkCatalog.Short(exception.Message));
                return WorksetId.InvalidWorksetId;
            }
        }

        /// <summary>
        /// Переход в рабочий набор. Ради него всё и затевалось: уровни и оси, скопированные
        /// следом, попадут в тот набор, который активен в момент копирования.
        /// </summary>
        private static void Activate(Document doc, WorksetId workset, string name, List<string> done, List<string> failures)
        {
            // Набор не нашёлся и не создался — причина уже в отчёте, второй раз о ней незачем.
            if (workset == WorksetId.InvalidWorksetId)
                return;

            try
            {
                doc.GetWorksetTable().SetActiveWorksetId(workset);
                done.Add("Активный рабочий набор — «" + name + "»");
            }
            catch (Exception exception)
            {
                failures.Add("Перейти в рабочий набор «" + name + "» не удалось: " +
                             LinkCatalog.Short(exception.Message));
            }
        }

        // ───────────────────────────── копирование мониторингом ─────────────────────────────

        /// <summary>
        /// Открывает режим «Копирование/Мониторинг → Выбрать связь».
        ///
        /// Дальше этого автоматизация не идёт и пойти не может: создавать связи мониторинга
        /// Revit API не умеет. Команда ставится в очередь Revit и срабатывает после закрытия
        /// отчёта — пользователю остаётся щёлкнуть по связи и отметить уровни и оси.
        /// </summary>
        private static void OpenMonitor(UIApplication application, List<string> done, List<string> failures)
        {
            try
            {
                var command = RevitCommandId.LookupPostableCommandId(PostableCommand.CopyMonitorSelectLink);
                if (command == null || !application.CanPostCommand(command))
                {
                    failures.Add("Режим «Копирование/Мониторинг» сейчас недоступен — " +
                                 "перейдите на план этажа и запустите его вручную.");
                    return;
                }

                application.PostCommand(command);
                done.Add("Открывается «Копирование/Мониторинг → Выбрать связь»: " +
                         "выберите базовый файл и отметьте уровни и оси");
            }
            catch (Exception exception)
            {
                failures.Add("Открыть «Копирование/Мониторинг» не удалось: " +
                             LinkCatalog.Short(exception.Message));
            }
        }

        // ───────────────────────────── отчёт ─────────────────────────────

        private static void Report(
            IReadOnlyList<string> done,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
        {
            const int limit = 15;

            var text = done.Count == 0
                ? "Ничего не сделано."
                : "Сделано:\n• " + string.Join("\n• ", done);

            if (failures.Count > 0)
            {
                text += "\n\nНе получилось (" + failures.Count + "):\n• " +
                        string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… и ещё " + (failures.Count - limit);
            }

            if (warnings.Count > 0)
            {
                text += "\n\nRevit предупредил (" + warnings.Count + "):\n• " +
                        string.Join("\n• ", warnings.Take(limit));

                if (warnings.Count > limit)
                    text += "\n… и ещё " + (warnings.Count - limit);
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
