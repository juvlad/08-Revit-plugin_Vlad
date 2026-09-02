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
    /// Пакетная загрузка связей Revit: файлы, Revit Server и BIM360 — одним списком,
    /// с одним способом размещения и одной настройкой рабочих наборов на всю пачку.
    ///
    /// Типовая работа: в новый проект нужно завести два десятка связей смежников,
    /// все «по общим координатам» и у всех закрыт «00_Shared levels and grids».
    /// В самом Revit это двадцать диалогов подряд, в каждом из которых заново выбирается
    /// одно и то же.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LinkManagerCommand : IExternalCommand
    {
        private const string DialogTitle = "Link Manager";

        /// <summary>
        /// Прочитанные рабочие наборы связей: ключ строки — список «имя набора → его Id».
        /// Идентификаторы у каждой модели свои, поэтому имена, отмеченные в окне,
        /// превращаются в идентификаторы отдельно для каждой связи.
        /// Заполняется при чтении по кнопке и дочитывается при загрузке.
        /// </summary>
        private readonly Dictionary<string, List<WorksetInfo>> _worksets =
            new Dictionary<string, List<WorksetInfo>>(StringComparer.Ordinal);

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
                var window = new LinkManagerWindow(Existing(doc), HostWorksets(doc), rows => ReadWorksets(rows));
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var preferences = window.Preferences;
                var chosen = window.Selected;

                var created = new List<string>();
                var reloaded = new List<string>();
                var moved = new List<string>();
                var healed = new List<string>();
                var failures = new List<string>();
                var warnings = new WarningSuppressor();

                // Перезагрузка идёт первой и вне транзакции. LoadFrom требует, чтобы все транзакции
                // были закрыты, и вдобавок **стирает историю отмены документа**. Сделай её после
                // создания — и Ctrl+Z уже не вернул бы только что заведённые связи; так порядок
                // сохраняет отмену хотя бы для новых.
                Reload(doc, chosen.Where(row => row.IsExisting).ToList(), preferences, reloaded, healed, failures);

                Apply(doc, chosen, preferences, created, moved, healed, failures, warnings);

                Report(created, reloaded, moved, healed, failures, warnings.Messages);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── что уже есть в проекте ─────────────────────────────

        /// <summary>
        /// Связи, уже стоящие в проекте. Вложенные не берутся: они приезжают вместе
        /// со своим носителем, и грузить их отдельно нельзя.
        /// </summary>
        private static IReadOnlyList<LinkRow> Existing(Document doc)
        {
            var rows = new List<LinkRow>();

            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .Where(type => !type.IsNestedLink)
                .OrderBy(type => type.Name, StringComparer.CurrentCultureIgnoreCase);

            foreach (var type in types)
            {
                var entry = Describe(doc, type);
                if (entry == null)
                    continue;

                // Набор показываем тот, в котором связь лежит сейчас: пользователь должен видеть,
                // что менять, а не выбирать вслепую. Смотрим по экземпляру — именно он стоит в модели.
                entry.Workset = WorksetName(doc, Instances(doc, type.Id).FirstOrDefault() ?? (Element)type);

                rows.Add(new LinkRow(entry, type.Id));
            }

            return rows;
        }

        /// <summary>Экземпляры одной связи: их может быть несколько, и набор меняется у всех.</summary>
        private static List<RevitLinkInstance> Instances(Document doc, ElementId typeId)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(instance => instance.GetTypeId() == typeId)
                .ToList();
        }

        // ───────────────────────────── рабочие наборы проекта ─────────────────────────────

        /// <summary>
        /// Рабочие наборы открытого проекта — те, куда можно положить связь.
        /// Проект не совмещённый — наборов нет вовсе, и окно прячет весь столбец.
        /// </summary>
        private static IReadOnlyList<string> HostWorksets(Document doc)
        {
            if (!doc.IsWorkshared)
                return new List<string>();

            return new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .ToWorksets()
                .Select(workset => workset.Name)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Имя набора, в котором лежит элемент; в несовмещённом проекте — пустая строка.</summary>
        private static string WorksetName(Document doc, Element element)
        {
            if (element == null || !doc.IsWorkshared)
                return string.Empty;

            try
            {
                var workset = doc.GetWorksetTable().GetWorkset(element.WorksetId);
                return workset == null || workset.Kind != WorksetKind.UserWorkset ? string.Empty : workset.Name;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>Имена наборов проекта в их идентификаторы — по имени окно и выбирает.</summary>
        private static Dictionary<string, WorksetId> WorksetIds(Document doc)
        {
            var map = new Dictionary<string, WorksetId>(StringComparer.CurrentCultureIgnoreCase);

            if (!doc.IsWorkshared)
                return map;

            foreach (var workset in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets())
                map[workset.Name] = workset.Id;

            return map;
        }

        /// <summary>
        /// Кладёт элемент в рабочий набор проекта. Отказ не должен срывать загрузку: связь уже
        /// создана и работает, просто лежит не там, — поэтому он уходит строкой в отчёт.
        /// </summary>
        private static bool Place(Element element, WorksetId workset, List<string> failures, string what)
        {
            try
            {
                if (element == null || element.WorksetId == workset)
                    return false;

                var parameter = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (parameter == null || parameter.IsReadOnly)
                {
                    failures.Add(what + " — рабочий набор сменить нельзя: параметр недоступен");
                    return false;
                }

                parameter.Set(workset.IntegerValue);
                return true;
            }
            catch (Exception exception)
            {
                failures.Add(what + " — рабочий набор сменить не удалось: " + Short(exception.Message));
                return false;
            }
        }

        /// <summary>
        /// Откуда приехала связь. Облачную узнаём по самому пути: у него есть регион
        /// и пара GUID, и больше ничего — обычного пути у неё не существует.
        /// </summary>
        private static LinkEntry Describe(Document doc, RevitLinkType type)
        {
            try
            {
                var reference = ExternalFileUtils.GetExternalFileReference(doc, type.Id);
                var path = reference.GetAbsolutePath();

                if (path.CloudPath)
                {
                    return LinkEntry.ForCloud(
                        path.Region,
                        path.GetProjectGUID().ToString(),
                        path.GetModelGUID().ToString(),
                        type.Name);
                }

                var visible = ModelPathUtils.ConvertModelPathToUserVisiblePath(path);

                return visible.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase)
                    ? LinkEntry.ForServer(visible)
                    : LinkEntry.ForFile(visible);
            }
            catch (Exception)
            {
                // Путь недоступен — связь просто не попадёт в список; это не повод не открывать окно.
                return null;
            }
        }

        // ───────────────────────────── рабочие наборы ─────────────────────────────

        /// <summary>
        /// Читает имена рабочих наборов отмеченных моделей, не открывая их. Идёт по сети
        /// к каждому файлу, поэтому в окне висит на кнопке, а не срабатывает само.
        /// Заодно наполняет <see cref="_worksets"/> — при загрузке эти же данные пригодятся,
        /// чтобы перевести отмеченные имена в идентификаторы каждой модели.
        /// </summary>
        private LinkWorksetScan ReadWorksets(IReadOnlyList<LinkRow> rows)
        {
            var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var failures = new List<string>();
            var scanned = 0;

            foreach (var row in rows)
            {
                try
                {
                    var worksets = Worksets(row, true);
                    row.WorksetNames = worksets.Select(workset => workset.Name).ToList();
                    row.Note = string.Empty;

                    foreach (var workset in worksets)
                        names.Add(workset.Name);

                    scanned++;
                }
                catch (Exception exception)
                {
                    row.Note = "Наборы не прочитаны";
                    failures.Add(row.Name + " — " + Short(exception.Message));
                }
            }

            return new LinkWorksetScan(names.ToList(), scanned, failures);
        }

        /// <summary>
        /// Рабочие наборы одной модели. Несовмещённый файл наборов не имеет — это пустой
        /// список, а не отказ. Прочитанное запоминается: за одно нажатие «Загрузить»
        /// к тому же файлу иначе пришлось бы ходить дважды.
        /// </summary>
        private List<WorksetInfo> Worksets(LinkRow row, bool refresh)
        {
            List<WorksetInfo> cached;
            if (!refresh && _worksets.TryGetValue(row.Key, out cached))
                return cached;

            var worksets = WorksharingUtils.GetUserWorksetInfo(ToModelPath(row.Entry))
                .Select(preview => new WorksetInfo(preview.Name, preview.Id))
                .ToList();

            _worksets[row.Key] = worksets;
            return worksets;
        }

        /// <summary>
        /// Собирает настройку рабочих наборов для одной связи: базовый режим плюс имена,
        /// отмеченные в окне, плюс правило по имени. Правило применяется здесь, а не в окне,
        /// именно потому, что наборы у каждой модели свои: «00_» ловит и «00_Shared Levels
        /// and Grids», и «00_Общие уровни и оси», хотя в списке окна ни того ни другого нет.
        ///
        /// Наборы прочитать не удалось — отдаём один базовый режим: связь всё равно загрузится,
        /// просто без выборочного закрытия.
        /// </summary>
        private WorksetConfiguration Configuration(LinkRow row, LinkPreferences preferences, List<string> notes)
        {
            var option = preferences.WorksetMode == LinkWorksetMode.CloseAll
                ? WorksetConfigurationOption.CloseAllWorksets
                : preferences.WorksetMode == LinkWorksetMode.LastViewed
                    ? WorksetConfigurationOption.OpenLastViewed
                    : WorksetConfigurationOption.OpenAllWorksets;

            var configuration = new WorksetConfiguration(option);

            var hasNames = preferences.Worksets.Count > 0;
            var hasPattern = !string.IsNullOrEmpty(preferences.WorksetPattern);

            if (!hasNames && !hasPattern)
                return configuration;

            List<WorksetInfo> worksets;
            try
            {
                worksets = Worksets(row, false);
            }
            catch (Exception exception)
            {
                notes.Add(row.Name + " — рабочие наборы прочитать не удалось (" + Short(exception.Message) +
                          "), связь загружена как есть");
                return configuration;
            }

            // Пустой список при удачном чтении — подозрительно: у совмещённой модели всегда есть
            // хотя бы набор по умолчанию. Скорее всего, GetUserWorksetInfo не смог достучаться
            // до файла и просто не бросил исключение. Не отличать это от «имена не подошли» —
            // значит потерять единственную зацепку, если Revit тихо не закрыл заказанное.
            if (worksets.Count == 0)
            {
                notes.Add(row.Name + " — файл вернул пустой список рабочих наборов " +
                          "(похоже на сбой чтения, а не на его отсутствие), связь загружена без изменения наборов");
                return configuration;
            }

            var chosen = worksets
                .Where(workset => Matches(workset.Name, preferences))
                .Select(workset => workset.Id)
                .ToList();

            if (chosen.Count == 0)
                return configuration;

            // При «закрыть все» галочка значит обратное: открыть только отмеченное.
            if (preferences.WorksetMode == LinkWorksetMode.CloseAll)
                configuration.Open(chosen);
            else
                configuration.Close(chosen);

            return configuration;
        }

        /// <summary>
        /// Перечитывает уже загруженный документ связи и сверяет, что Revit на самом деле сделал
        /// с наборами, с тем, что было заказано. Без этого узнать, послушался ли он, можно было
        /// только руками — через «Управление рабочими наборами» самого Revit. WorksetConfiguration
        /// лишь передаёт пожелание; проверка здесь — единственный способ поймать случай, когда
        /// Revit его не выполнил, и не гадать вслепую.
        /// </summary>
        /// <summary>
        /// Сверяет уже загруженный документ связи с тем, что было заказано. Пустой список
        /// значит «всё так, как просили»; ничего не заказано — тоже пустой список, проверять нечего.
        /// </summary>
        private static List<string> MismatchedWorksets(Document linkDocument, LinkRow row, LinkPreferences preferences)
        {
            if (linkDocument == null)
                return new List<string>();

            var hasNames = preferences.Worksets.Count > 0;
            var hasPattern = !string.IsNullOrEmpty(preferences.WorksetPattern);
            if (!hasNames && !hasPattern)
                return new List<string>();

            // При «закрыть все» отмеченные должны остаться открытыми — зеркально Configuration().
            var expectOpen = preferences.WorksetMode == LinkWorksetMode.CloseAll;

            try
            {
                return new FilteredWorksetCollector(linkDocument)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToWorksets()
                    .Where(workset => Matches(workset.Name, preferences) && workset.IsOpen != expectOpen)
                    .Select(workset => workset.Name)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Проверяет наборы уже загруженной связи и, если Revit заказанное не выполнил,
        /// пробует продавить настройку ещё раз через <c>LoadFrom</c> — тем же путём, которым
        /// команда меняет наборы у существующих связей.
        ///
        /// На практике для связей с Revit Server самое первое применение workset-конфигурации
        /// (что при <c>RevitLinkType.Create</c>, что при первом <c>LoadFrom</c>) иногда молча
        /// не срабатывает: Revit отчитывается об успехе, но оставляет наборы как были.
        /// Повторный вызов с той же конфигурацией это обычно исправляет — похоже на особенность
        /// самого Revit при первом обращении к серверной модели, а не на ошибку в переданных
        /// данных: несовпадение находится именно в том, что реально загрузилось, а не в том,
        /// что было запрошено. Не файлы и не облако — там расхождений не наблюдалось.
        ///
        /// Вызывать можно только вне транзакции: `LoadFrom` внутри открытой транзакции работать
        /// отказывается.
        /// </summary>
        /// <param name="retried">Пришлось ли пробовать второй раз — если нет, отчёту сообщать нечего.</param>
        private List<string> EnsureWorksets(Document doc, LinkRow row, ElementId typeId, LinkPreferences preferences, out bool retried)
        {
            retried = false;

            var linkDocument = Instances(doc, typeId).FirstOrDefault()?.GetLinkDocument();
            var mismatched = MismatchedWorksets(linkDocument, row, preferences);
            if (mismatched.Count == 0)
                return mismatched;

            var type = doc.GetElement(typeId) as RevitLinkType;
            if (type == null)
                return mismatched;

            retried = true;
            var notes = new List<string>();

            try
            {
                using (var configuration = Configuration(row, preferences, notes))
                {
                    var result = type.LoadFrom(ToModelPath(row.Entry), configuration);
                    if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                        return mismatched;
                }
            }
            catch (Exception)
            {
                return mismatched;
            }

            linkDocument = Instances(doc, typeId).FirstOrDefault()?.GetLinkDocument();
            return MismatchedWorksets(linkDocument, row, preferences);
        }

        private static bool Matches(string name, LinkPreferences preferences)
        {
            if (preferences.Worksets.Any(chosen => string.Equals(chosen, name, StringComparison.CurrentCultureIgnoreCase)))
                return true;

            var pattern = preferences.WorksetPattern;
            if (string.IsNullOrEmpty(pattern))
                return false;

            return preferences.WorksetPatternContains
                ? name.IndexOf(pattern, StringComparison.CurrentCultureIgnoreCase) >= 0
                : name.StartsWith(pattern, StringComparison.CurrentCultureIgnoreCase);
        }

        // ───────────────────────────── загрузка ─────────────────────────────

        /// <summary>
        /// Создаёт новые связи и переносит в другой рабочий набор те, что уже стоят в проекте.
        /// Всё одной транзакцией: пользователь откатывает пачку одним Ctrl+Z. Каждая связь
        /// в своём try — отказ одной не отменяет остальных.
        /// </summary>
        private void Apply(
            Document doc,
            IReadOnlyList<LinkRow> rows,
            LinkPreferences preferences,
            List<string> created,
            List<string> moved,
            List<string> healed,
            List<string> failures,
            WarningSuppressor warnings)
        {
            var fresh = rows.Where(row => !row.IsExisting).ToList();
            var existing = rows.Where(row => row.IsExisting).ToList();

            if (fresh.Count == 0 && existing.Count == 0)
                return;

            var placement = Placement(preferences.Placement);
            var worksets = WorksetIds(doc);

            // Кого проверить и, если понадобится, долечить через LoadFrom — обязательно вне
            // транзакции, поэтому список собирается внутри, а разбирается снаружи.
            var toVerify = new List<KeyValuePair<LinkRow, ElementId>>();

            using (var transaction = new Transaction(doc, "Загрузка связей"))
            {
                transaction.Start();

                // Связь почти всегда приезжает с предупреждениями — о координатах, о дублях
                // имён, о версии файла. Модальное окно на каждое сорвало бы пакетную загрузку.
                var options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(warnings);
                transaction.SetFailureHandlingOptions(options);

                foreach (var row in fresh)
                {
                    try
                    {
                        var typeId = Create(doc, row, preferences, placement, worksets, created, failures);
                        if (typeId != ElementId.InvalidElementId)
                            toVerify.Add(new KeyValuePair<LinkRow, ElementId>(row, typeId));
                    }
                    catch (Exception exception)
                    {
                        failures.Add(row.Name + " — " + Short(exception.Message));
                    }
                }

                foreach (var row in existing)
                {
                    try
                    {
                        Move(doc, row, worksets, moved, failures);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(row.Name + " — " + Short(exception.Message));
                    }
                }

                if (created.Count == 0 && moved.Count == 0)
                    transaction.RollBack();
                else
                    transaction.Commit();
            }

            foreach (var pair in toVerify)
            {
                try
                {
                    bool retried;
                    var stillWrong = EnsureWorksets(doc, pair.Key, pair.Value, preferences, out retried);
                    if (!retried)
                        continue;

                    if (stillWrong.Count == 0)
                        healed.Add(pair.Key.Name);
                    else
                        failures.Add(pair.Key.Name + " — рабочие наборы не закрылись даже после повторной " +
                                     "загрузки: " + string.Join(", ", stillWrong));
                }
                catch (Exception exception)
                {
                    failures.Add(pair.Key.Name + " — " + Short(exception.Message));
                }
            }
        }

        /// <summary>
        /// Переносит уже стоящую в проекте связь в выбранный рабочий набор — вместе со всеми
        /// её экземплярами. Набор не выбран или тот же самый — не трогаем: в окне это
        /// обычное состояние, а не отказ.
        /// </summary>
        private static void Move(
            Document doc,
            LinkRow row,
            Dictionary<string, WorksetId> worksets,
            List<string> moved,
            List<string> failures)
        {
            WorksetId workset;
            if (row.Entry.Workset.Length == 0 || !worksets.TryGetValue(row.Entry.Workset, out workset))
                return;

            var type = doc.GetElement(row.ExistingId) as RevitLinkType;
            if (type == null)
                return;

            var changed = Instances(doc, row.ExistingId)
                .Count(instance => Place(instance, workset, failures, row.Name));

            Place(type, workset, failures, row.Name);

            if (changed > 0)
            {
                row.Note = "Перенесена в «" + row.Entry.Workset + "»";
                moved.Add(row.Name + " → " + row.Entry.Workset);
            }
        }

        /// <summary>Возвращает Id новой связи — или <c>InvalidElementId</c>, если не получилось.</summary>
        private ElementId Create(
            Document doc,
            LinkRow row,
            LinkPreferences preferences,
            ImportPlacement placement,
            Dictionary<string, WorksetId> worksets,
            List<string> created,
            List<string> failures)
        {
            var notes = new List<string>();

            using (var configuration = Configuration(row, preferences, notes))
            {
                // Относительный путь бывает только у файла: у Revit Server и облака
                // путь всегда абсолютный, и Revit относительный там просто не примет.
                var relative = preferences.IsRelativePath && row.Entry.Origin == LinkOrigin.File;

                using (var options = new RevitLinkOptions(relative, configuration))
                {
                    var result = RevitLinkType.Create(doc, ToModelPath(row.Entry), options);

                    if (result.LoadResult == LinkLoadResultType.LinkExists)
                    {
                        row.Note = "Уже в проекте";
                        failures.Add(row.Name + " — такая связь в проекте уже есть");
                        return ElementId.InvalidElementId;
                    }

                    if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                    {
                        row.Note = Describe(result.LoadResult);
                        failures.Add(row.Name + " — " + Describe(result.LoadResult));
                        return ElementId.InvalidElementId;
                    }

                    var type = doc.GetElement(result.ElementId) as RevitLinkType;
                    if (type != null && preferences.IsAttachment)
                        type.AttachmentType = AttachmentType.Attachment;

                    var instance = RevitLinkInstance.Create(doc, result.ElementId, placement);

                    // Новую связь сразу закрепляем булавкой: смежник вставлен по координатам,
                    // и случайный сдвиг мышью потом ищут всей командой. Снять закрепление
                    // вручную — одна кнопка, вернуть уехавшую связь на место — нет.
                    try
                    {
                        instance.Pinned = true;
                    }
                    catch (Exception exception)
                    {
                        failures.Add(row.Name + " — закрепить связь не удалось: " + Short(exception.Message));
                    }

                    // Набор задаётся уже созданным элементам, а не через активный набор документа:
                    // так связь ложится туда, куда просили, независимо от того, где стоит пользователь.
                    WorksetId hostWorkset;
                    if (row.Entry.Workset.Length > 0 && worksets.TryGetValue(row.Entry.Workset, out hostWorkset))
                    {
                        Place(instance, hostWorkset, failures, row.Name);
                        Place(type, hostWorkset, failures, row.Name);
                    }

                    row.Note = "Загружена";
                    created.Add(row.Name);
                    failures.AddRange(notes);

                    // Наборы связи проверяются и, если нужно, долечиваются позже, вне транзакции
                    // (Apply.toVerify) — LoadFrom внутри неё работать отказывается.
                    return result.ElementId;
                }
            }
        }

        /// <summary>
        /// Перезагружает связи, уже стоящие в проекте, — ради новой настройки рабочих наборов.
        /// Размещение при этом не меняется: у существующей связи Revit его переопределить не даёт.
        ///
        /// Зовётся вне транзакции и до создания новых связей: <c>LoadFrom</c> требует, чтобы все
        /// транзакции были закрыты, и стирает историю отмены документа. Сама перезагрузка поэтому
        /// не откатывается никогда, но идущее следом создание новых связей — откатывается.
        /// </summary>
        private void Reload(
            Document doc,
            IReadOnlyList<LinkRow> rows,
            LinkPreferences preferences,
            List<string> reloaded,
            List<string> healed,
            List<string> failures)
        {
            foreach (var row in rows)
            {
                try
                {
                    var type = doc.GetElement(row.ExistingId) as RevitLinkType;
                    if (type == null)
                    {
                        failures.Add(row.Name + " — связи в проекте больше нет");
                        continue;
                    }

                    var notes = new List<string>();

                    using (var configuration = Configuration(row, preferences, notes))
                    {
                        var result = type.LoadFrom(ToModelPath(row.Entry), configuration);

                        if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                        {
                            row.Note = Describe(result.LoadResult);
                            failures.Add(row.Name + " — " + Describe(result.LoadResult));
                            continue;
                        }
                    }

                    row.Note = "Перезагружена";
                    reloaded.Add(row.Name);
                    failures.AddRange(notes);

                    // Reload уже идёт вне транзакции, поэтому долечивать (EnsureWorksets → LoadFrom)
                    // можно сразу же, не дожидаясь отдельного прохода, как для новых связей.
                    bool retried;
                    var stillWrong = EnsureWorksets(doc, row, row.ExistingId, preferences, out retried);
                    if (!retried)
                        continue;

                    if (stillWrong.Count == 0)
                        healed.Add(row.Name);
                    else
                        failures.Add(row.Name + " — рабочие наборы не закрылись даже после повторной " +
                                     "загрузки: " + string.Join(", ", stillWrong));
                }
                catch (Exception exception)
                {
                    failures.Add(row.Name + " — " + Short(exception.Message));
                }
            }
        }

        private static ImportPlacement Placement(LinkPlacement placement)
        {
            switch (placement)
            {
                case LinkPlacement.Origin:
                    return ImportPlacement.Origin;
                case LinkPlacement.Centered:
                    return ImportPlacement.Centered;
                case LinkPlacement.Site:
                    return ImportPlacement.Site;
                default:
                    return ImportPlacement.Shared;
            }
        }

        /// <summary>
        /// Путь к модели. У облачной модели обычного пути нет вовсе: она адресуется
        /// регионом и парой GUID, и это единственный способ до неё добраться.
        /// </summary>
        private static ModelPath ToModelPath(LinkEntry entry)
        {
            if (entry.Origin != LinkOrigin.Cloud)
                return ModelPathUtils.ConvertUserVisiblePathToModelPath(entry.Path);

            Guid project;
            Guid model;

            if (!Guid.TryParse(entry.ProjectGuid, out project) || !Guid.TryParse(entry.ModelGuid, out model))
                throw new InvalidOperationException("GUID облачной модели записан неверно.");

            return ModelPathUtils.ConvertCloudGUIDsToCloudPath(entry.Region, project, model);
        }

        // ───────────────────────────── отчёт ─────────────────────────────

        /// <summary>Код отказа Revit словами: сам по себе он пользователю ничего не говорит.</summary>
        private static string Describe(LinkLoadResultType result)
        {
            switch (result)
            {
                case LinkLoadResultType.LinkNotFound:
                    return "файл не найден";
                case LinkLoadResultType.LinkNotOpenable:
                    return "файл не открывается: повреждён или занят";
                case LinkLoadResultType.LinkOpenAsHost:
                    return "этот файл уже открыт как проект";
                case LinkLoadResultType.SameModelAsHost:
                case LinkLoadResultType.SameCentralModelAsHost:
                    return "это сам открытый проект";
                case LinkLoadResultType.LinkExists:
                    return "такая связь в проекте уже есть";
                case LinkLoadResultType.ExternalServerMissing:
                    return "сервер недоступен";
                case LinkLoadResultType.LinkNotLoadedOtherError:
                    return "Revit не смог загрузить связь";
                default:
                    return "загрузка не удалась (" + result + ")";
            }
        }

        /// <summary>Сообщения Revit бывают в несколько абзацев — в списке нужна одна строка.</summary>
        private static string Short(string message)
        {
            var text = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 160 ? text.Substring(0, 160) + "…" : text;
        }

        private static void Report(
            IReadOnlyList<string> created,
            IReadOnlyList<string> reloaded,
            IReadOnlyList<string> moved,
            IReadOnlyList<string> healed,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
        {
            const int limit = 15;

            var text = created.Count == 0 && reloaded.Count == 0 && moved.Count == 0
                ? "Ни одной связи загрузить не удалось."
                : "Связей загружено: " + created.Count +
                  (reloaded.Count > 0 ? ", перезагружено: " + reloaded.Count : string.Empty) +
                  (moved.Count > 0 ? ", перенесено в другой набор: " + moved.Count : string.Empty) + ".";

            if (created.Count > 0)
                text += "\n\n• " + string.Join("\n• ", created.Take(limit)) +
                        (created.Count > limit ? "\n… и ещё " + (created.Count - limit) : string.Empty);

            if (healed.Count > 0)
                text += "\n\nС первого раза Revit не закрыл наборы как просили, помогла повторная " +
                        "загрузка (она тоже стирает историю отмены документа):\n• " +
                        string.Join("\n• ", healed.Take(limit)) +
                        (healed.Count > limit ? "\n… и ещё " + (healed.Count - limit) : string.Empty);

            if (moved.Count > 0)
                text += "\n\nПеренесены в другой рабочий набор:\n• " + string.Join("\n• ", moved.Take(limit)) +
                        (moved.Count > limit ? "\n… и ещё " + (moved.Count - limit) : string.Empty);

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

        /// <summary>Рабочий набор связи: имя, отмеченное в окне, и его идентификатор в этой модели.</summary>
        private sealed class WorksetInfo
        {
            public WorksetInfo(string name, WorksetId id)
            {
                Name = name ?? string.Empty;
                Id = id;
            }

            public string Name { get; }
            public WorksetId Id { get; }
        }
    }
}
