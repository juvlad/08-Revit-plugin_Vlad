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

        /// <summary>
        /// Имена наборов, реально встретившиеся в связях за этот запуск загрузки.
        /// Нужно ровно для одного: отмеченное имя, которого не нашлось ни в одной связи, —
        /// самая частая причина «набор не закрылся». Закрывать в этом случае просто нечего,
        /// <see cref="Matches"/> такого набора не находит, и раньше об этом никто не узнавал:
        /// в окне столбец «Где есть» показывает «из прошлого раза» и когда набор не нашёлся,
        /// и когда его никто не искал.
        /// </summary>
        private readonly HashSet<string> _seenNames =
            new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

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
                var window = new LinkManagerWindow(
                    LinkCatalog.Existing(doc),
                    LinkCatalog.HostWorksets(doc),
                    rows => ReadWorksets(rows),
                    LinkCatalog.Host(doc));
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var preferences = window.Preferences;
                var chosen = window.Selected;

                // Идентификаторы наборов читаются заново. WorksetPreview.Id по документации
                // Autodesk меняется при синхронизации с центральной моделью, а между кнопкой
                // «Прочитать наборы» и «Загрузить» проходит сколько угодно времени. Устаревший
                // WorksetId и Open, и Close игнорируют молча — осечка, которую потом не найти.
                _worksets.Clear();
                _seenNames.Clear();

                var created = new List<string>();
                var reloaded = new List<string>();
                var moved = new List<string>();
                var healed = new List<string>();
                var unverified = new List<string>();
                var failures = new List<string>();
                var warnings = new WarningSuppressor();

                // Перезагрузка идёт первой и вне транзакции. LoadFrom требует, чтобы все транзакции
                // были закрыты, и вдобавок **стирает историю отмены документа**. Сделай её после
                // создания — и Ctrl+Z уже не вернул бы только что заведённые связи; так порядок
                // сохраняет отмену хотя бы для новых.
                Reload(doc, chosen.Where(row => row.IsExisting).ToList(), preferences,
                    reloaded, healed, unverified, failures);

                Apply(doc, chosen, preferences, created, moved, healed, unverified, failures, warnings);

                Report(created, reloaded, moved, healed, Missing(preferences), unverified, failures,
                    warnings.Messages);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
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
                    failures.Add(row.Name + " — " + LinkCatalog.Short(exception.Message));
                }
            }

            return new LinkWorksetScan(names.ToList(), scanned, failures);
        }

        /// <summary>
        /// Рабочие наборы одной модели. Несовмещённый файл наборов не имеет — это пустой
        /// список, а не отказ. Прочитанное запоминается, чтобы за одно нажатие «Загрузить»
        /// не ходить к тому же файлу дважды (создание, проверка, повторная загрузка), но
        /// кэш живёт только этот запуск: <see cref="Execute"/> чистит его перед загрузкой,
        /// потому что идентификаторы наборов между чтением и загрузкой могут смениться.
        /// </summary>
        private List<WorksetInfo> Worksets(LinkRow row, bool refresh)
        {
            List<WorksetInfo> cached;
            if (!refresh && _worksets.TryGetValue(row.Key, out cached))
                return cached;

            var worksets = WorksharingUtils.GetUserWorksetInfo(LinkCatalog.ToModelPath(row.Entry))
                .Select(preview => new WorksetInfo(preview.Name, preview.Id))
                .ToList();

            _worksets[row.Key] = worksets;

            foreach (var workset in worksets)
                _seenNames.Add(LinkPreferences.NormalizeWorkset(workset.Name));

            return worksets;
        }

        /// <summary>
        /// Собирает настройку рабочих наборов для одной связи: базовый режим плюс имена,
        /// отмеченные в окне, плюс правило по имени. Правило применяется здесь, а не в окне,
        /// именно потому, что наборы у каждой модели свои: «00_» ловит и «00_Shared Levels
        /// and Grids», и «00_Общие уровни и оси», хотя в списке окна ни того ни другого нет.
        ///
        /// Заказ всегда формулируется как «закрыть все, открыть перечисленные», а не
        /// «открыть все, закрыть перечисленные». По документации обе формулировки равноправны,
        /// но CloseAllWorksets + Open — единственная, которой пользуется пример Autodesk
        /// именно для связей (Developers Guide → Linked Files → Revit Links), и единственная,
        /// про которую нет сообщений, что она молча не срабатывает. OpenAllWorksets + Close
        /// на связях оставляет наборы открытыми, отчитываясь об успехе, — ровно то, из-за чего
        /// кнопка «не справлялась с закрытием». Поэтому «закрыть отмеченные» считается через
        /// дополнение: открыть все наборы связи, кроме отмеченных. Список наборов для этого
        /// всё равно уже прочитан.
        ///
        /// Наборы прочитать не удалось — отдаём один базовый режим: связь всё равно загрузится,
        /// просто без выборочного закрытия.
        /// </summary>
        private WorksetConfiguration Configuration(LinkRow row, LinkPreferences preferences, List<string> notes)
        {
            var hasNames = preferences.Worksets.Count > 0;
            var hasPattern = !string.IsNullOrEmpty(preferences.WorksetPattern);

            // Ничего не отмечено — ходить к файлу за наборами незачем.
            if (!hasNames && !hasPattern)
                return new WorksetConfiguration(BaseOption(preferences.WorksetMode));

            List<WorksetInfo> worksets;
            try
            {
                worksets = Worksets(row, false);
            }
            catch (Exception exception)
            {
                notes.Add(row.Name + " — рабочие наборы прочитать не удалось (" + LinkCatalog.Short(exception.Message) +
                          "), связь загружена как есть");
                return new WorksetConfiguration(BaseOption(preferences.WorksetMode));
            }

            // Пустой список при удачном чтении — подозрительно: у совмещённой модели всегда есть
            // хотя бы набор по умолчанию. Скорее всего, GetUserWorksetInfo не смог достучаться
            // до файла и просто не бросил исключение. Не отличать это от «имена не подошли» —
            // значит потерять единственную зацепку, если Revit тихо не закрыл заказанное.
            if (worksets.Count == 0)
            {
                notes.Add(row.Name + " — файл вернул пустой список рабочих наборов " +
                          "(похоже на сбой чтения, а не на его отсутствие), связь загружена без изменения наборов");
                return new WorksetConfiguration(BaseOption(preferences.WorksetMode));
            }

            var marked = worksets.Where(workset => Matches(workset.Name, preferences))
                .Select(workset => workset.Id)
                .ToList();

            var rest = worksets.Where(workset => !Matches(workset.Name, preferences))
                .Select(workset => workset.Id)
                .ToList();

            // Отмеченных в этой связи нет — закрывать нечего, отдаём чистый базовый режим.
            // Заодно обходит неоднозначность в XML-doc метода Open: «if all worksets are set
            // to open, the configuration will be unchanged». Выяснять на живом проекте, не
            // означает ли это, что CloseAll + Open(все) оставит связь закрытой целиком, —
            // не та цена ошибки.
            if (marked.Count == 0)
                return new WorksetConfiguration(BaseOption(preferences.WorksetMode));

            // «Как при последнем открытии» через Open не выразить: что открывалось в прошлый раз,
            // знает только Revit, и дополнения тут не посчитать. Остаётся Close — с той самой
            // ненадёжностью; если Revit его не выполнит, это поймает Check и скажет в отчёте,
            // а не проглотит.
            if (preferences.WorksetMode == LinkWorksetMode.LastViewed)
            {
                var lastViewed = new WorksetConfiguration(WorksetConfigurationOption.OpenLastViewed);
                lastViewed.Close(marked);

                return lastViewed;
            }

            // При «закрыть все» галочка значит обратное: открыть только отмеченное.
            var open = preferences.WorksetMode == LinkWorksetMode.CloseAll ? marked : rest;

            var configuration = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);

            if (open.Count > 0)
                configuration.Open(open);

            return configuration;
        }

        /// <summary>
        /// Базовый режим, когда перечислять наборы нечем: отмеченных нет или список наборов
        /// связи прочитать не удалось.
        ///
        /// Здесь принципиально, что при «открыть все» это именно <c>OpenAllWorksets</c>:
        /// подставить сюда <c>CloseAllWorksets</c>, как в основном пути, значит на любом
        /// сбое чтения молча загрузить связь пустой.
        /// </summary>
        private static WorksetConfigurationOption BaseOption(LinkWorksetMode mode)
        {
            switch (mode)
            {
                case LinkWorksetMode.CloseAll:
                    return WorksetConfigurationOption.CloseAllWorksets;
                case LinkWorksetMode.LastViewed:
                    return WorksetConfigurationOption.OpenLastViewed;
                default:
                    return WorksetConfigurationOption.OpenAllWorksets;
            }
        }

        /// <summary>
        /// Каким должен стать набор с этим именем после загрузки: <c>true</c> — открытым,
        /// <c>false</c> — закрытым, <c>null</c> — про него ничего не обещали, и проверять
        /// его нельзя. Зеркало <see cref="Configuration"/>: если правишь там — правь и здесь.
        /// </summary>
        private static bool? Expected(string name, LinkPreferences preferences)
        {
            var marked = Matches(name, preferences);

            switch (preferences.WorksetMode)
            {
                case LinkWorksetMode.CloseAll:
                    return marked;

                case LinkWorksetMode.OpenAll:
                    return !marked;

                default:
                    // «Как при последнем открытии»: что стало с неотмеченными, знает только Revit.
                    return marked ? (bool?)false : null;
            }
        }

        /// <summary>
        /// Перечитывает уже загруженный документ связи и сверяет, что Revit на самом деле сделал
        /// с наборами, с тем, что было заказано. Без этого узнать, послушался ли он, можно было
        /// только руками — через «Управление рабочими наборами» самого Revit. WorksetConfiguration
        /// лишь передаёт пожелание; проверка здесь — единственный способ поймать случай, когда
        /// Revit его не выполнил, и не гадать вслепую.
        ///
        /// Исходов три, а не два, и это здесь главное. Раньше «проверить не удалось» отдавалось
        /// как «всё так, как просили»: связь не отдала документ, коллектор отказал — а в отчёте
        /// «Загружена» и ни слова про наборы. Именно так отказ закрытия и оставался незамеченным,
        /// поэтому <see cref="WorksetVerdict.Unknown"/> теперь доходит до отчёта отдельной строкой.
        /// </summary>
        private static WorksetCheck Check(Document linkDocument, LinkPreferences preferences)
        {
            var hasNames = preferences.Worksets.Count > 0;
            var hasPattern = !string.IsNullOrEmpty(preferences.WorksetPattern);
            if (!hasNames && !hasPattern)
                return WorksetCheck.NotRequested();

            if (linkDocument == null)
                return WorksetCheck.Unknown("связь не отдала свой документ — наборы проверить не удалось");

            try
            {
                // Несовмещённая связь наборов не имеет вовсе: закрывать нечего, и это не отказ.
                if (!linkDocument.IsWorkshared)
                    return WorksetCheck.NotRequested();

                var worksets = new FilteredWorksetCollector(linkDocument)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToWorksets()
                    .ToList();

                if (worksets.Count == 0)
                    return WorksetCheck.Unknown("у совмещённой связи не нашлось ни одного набора — проверить нечем");

                var wrong = new List<string>();

                foreach (var workset in worksets)
                {
                    var expected = Expected(workset.Name, preferences);

                    if (expected.HasValue && workset.IsOpen != expected.Value)
                        wrong.Add(workset.Name);
                }

                return WorksetCheck.Verified(wrong);
            }
            catch (Exception exception)
            {
                return WorksetCheck.Unknown("наборы проверить не удалось: " + LinkCatalog.Short(exception.Message));
            }
        }

        /// <summary>Документ связи — или <c>null</c>, если связь его не отдаёт.</summary>
        private static Document LinkDocument(Document doc, ElementId typeId)
        {
            return LinkCatalog.Instances(doc, typeId).FirstOrDefault()?.GetLinkDocument();
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
        private WorksetCheck EnsureWorksets(Document doc, LinkRow row, ElementId typeId, LinkPreferences preferences, out bool retried)
        {
            retried = false;

            var check = Check(LinkDocument(doc, typeId), preferences);

            // Повторяем только тогда, когда точно знаем, что Revit не послушался. При
            // Unknown второй LoadFrom стрелял бы вслепую, а он стирает историю отмены
            // документа — цена слишком велика для догадки. Отчёт скажет «не проверено».
            if (check.Verdict != WorksetVerdict.Mismatched)
                return check;

            var type = doc.GetElement(typeId) as RevitLinkType;
            if (type == null)
                return check;

            var notes = new List<string>();

            try
            {
                using (var configuration = Configuration(row, preferences, notes))
                {
                    // Повторять есть смысл только с полноценной конфигурацией. Если
                    // Configuration не смогла прочитать наборы связи, она отдала базовый
                    // режим: такой LoadFrom ничего не изменит, зато сотрёт историю отмены.
                    // Причина уже ушла в отчёт из Create/Reload — сообщаем несовпадение как есть.
                    if (notes.Count > 0)
                        return check;

                    retried = true;

                    var result = type.LoadFrom(LinkCatalog.ToModelPath(row.Entry), configuration);
                    if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                        return check;
                }
            }
            catch (Exception)
            {
                return check;
            }

            return Check(LinkDocument(doc, typeId), preferences);
        }

        /// <summary>
        /// Раскладывает итог сверки по спискам отчёта. Отдельной строкой сюда попадает
        /// и «проверить не удалось»: молчать об этом — значит вернуться к тому, из-за чего
        /// незакрытые наборы никто не замечал.
        /// </summary>
        private static void Account(
            LinkRow row,
            WorksetCheck check,
            bool retried,
            List<string> healed,
            List<string> unverified,
            List<string> failures)
        {
            switch (check.Verdict)
            {
                case WorksetVerdict.Unknown:
                    unverified.Add(row.Name + " — " + check.Reason);
                    break;

                case WorksetVerdict.Mismatched:
                    failures.Add(row.Name + (retried
                                     ? " — рабочие наборы не применились даже после повторной загрузки: "
                                     : " — Revit не применил рабочие наборы: ") +
                                 string.Join(", ", check.Names));
                    break;

                case WorksetVerdict.Satisfied:
                    if (retried)
                        healed.Add(row.Name);
                    break;
            }
        }

        private static bool Matches(string name, LinkPreferences preferences)
        {
            // Пробелы по краям обрезаются и здесь, и в правиле: имя набора у смежника
            // с висящим пробелом иначе не закрывается вовсе — см. LinkPreferences.NormalizeWorkset.
            var trimmed = LinkPreferences.NormalizeWorkset(name);

            if (preferences.Worksets.Any(chosen => LinkPreferences.SameWorkset(chosen, trimmed)))
                return true;

            var pattern = preferences.WorksetPattern;
            if (string.IsNullOrEmpty(pattern))
                return false;

            return preferences.WorksetPatternContains
                ? trimmed.IndexOf(pattern, StringComparison.CurrentCultureIgnoreCase) >= 0
                : trimmed.StartsWith(pattern, StringComparison.CurrentCultureIgnoreCase);
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
            List<string> unverified,
            List<string> failures,
            WarningSuppressor warnings)
        {
            var fresh = rows.Where(row => !row.IsExisting).ToList();
            var existing = rows.Where(row => row.IsExisting).ToList();

            if (fresh.Count == 0 && existing.Count == 0)
                return;

            var placement = LinkCatalog.Placement(preferences.Placement);
            var worksets = LinkCatalog.WorksetIds(doc);

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
                        failures.Add(row.Name + " — " + LinkCatalog.Short(exception.Message));
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
                        failures.Add(row.Name + " — " + LinkCatalog.Short(exception.Message));
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
                    var check = EnsureWorksets(doc, pair.Key, pair.Value, preferences, out retried);
                    Account(pair.Key, check, retried, healed, unverified, failures);
                }
                catch (Exception exception)
                {
                    failures.Add(pair.Key.Name + " — " + LinkCatalog.Short(exception.Message));
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

            var changed = LinkCatalog.Instances(doc, row.ExistingId)
                .Count(instance => LinkCatalog.Place(instance, workset, failures, row.Name));

            LinkCatalog.Place(type, workset, failures, row.Name);

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
                    var result = RevitLinkType.Create(doc, LinkCatalog.ToModelPath(row.Entry), options);

                    if (result.LoadResult == LinkLoadResultType.LinkExists)
                    {
                        row.Note = "Уже в проекте";
                        failures.Add(row.Name + " — такая связь в проекте уже есть");
                        return ElementId.InvalidElementId;
                    }

                    if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                    {
                        row.Note = LinkCatalog.Describe(result.LoadResult);
                        failures.Add(row.Name + " — " + LinkCatalog.Describe(result.LoadResult));
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
                        failures.Add(row.Name + " — закрепить связь не удалось: " + LinkCatalog.Short(exception.Message));
                    }

                    // Набор задаётся уже созданным элементам, а не через активный набор документа:
                    // так связь ложится туда, куда просили, независимо от того, где стоит пользователь.
                    WorksetId hostWorkset;
                    if (row.Entry.Workset.Length > 0 && worksets.TryGetValue(row.Entry.Workset, out hostWorkset))
                    {
                        LinkCatalog.Place(instance, hostWorkset, failures, row.Name);
                        LinkCatalog.Place(type, hostWorkset, failures, row.Name);
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
            List<string> unverified,
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
                        var result = type.LoadFrom(LinkCatalog.ToModelPath(row.Entry), configuration);

                        if (!LinkLoadResult.IsCodeSuccess(result.LoadResult))
                        {
                            row.Note = LinkCatalog.Describe(result.LoadResult);
                            failures.Add(row.Name + " — " + LinkCatalog.Describe(result.LoadResult));
                            continue;
                        }
                    }

                    row.Note = "Перезагружена";
                    reloaded.Add(row.Name);
                    failures.AddRange(notes);

                    // Reload уже идёт вне транзакции, поэтому долечивать (EnsureWorksets → LoadFrom)
                    // можно сразу же, не дожидаясь отдельного прохода, как для новых связей.
                    bool retried;
                    var check = EnsureWorksets(doc, row, row.ExistingId, preferences, out retried);
                    Account(row, check, retried, healed, unverified, failures);
                }
                catch (Exception exception)
                {
                    failures.Add(row.Name + " — " + LinkCatalog.Short(exception.Message));
                }
            }
        }

        // ───────────────────────────── отчёт ─────────────────────────────

        /// <summary>
        /// Отмеченные имена наборов, которых не нашлось ни в одной связи этого запуска.
        ///
        /// Пустой <see cref="_seenNames"/> значит, что наборы не удалось прочитать вообще
        /// ни у одной связи, — тогда «не нашлось» сказать не про что: причина другая, и она
        /// уже в отчёте отдельной строкой.
        /// </summary>
        private List<string> Missing(LinkPreferences preferences)
        {
            if (_seenNames.Count == 0)
                return new List<string>();

            return preferences.Worksets
                .Where(name => !_seenNames.Contains(LinkPreferences.NormalizeWorkset(name)))
                .ToList();
        }

        private static void Report(
            IReadOnlyList<string> created,
            IReadOnlyList<string> reloaded,
            IReadOnlyList<string> moved,
            IReadOnlyList<string> healed,
            IReadOnlyList<string> missing,
            IReadOnlyList<string> unverified,
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

            // Имя, которого нет ни в одной связи, — не отказ Revit, а опечатка или другое
            // имя набора у смежника. Отличать это от «Revit не послушался» приходится
            // пользователю, значит, и сказать надо разными словами.
            if (missing.Count > 0)
                text += "\n\nЭтих отмеченных наборов не нашлось ни в одной связи (" + missing.Count +
                        ") — сверьте имя: в окне отметьте связи и нажмите «Прочитать наборы», " +
                        "столбец «Где есть» покажет, где какой набор есть:\n• " +
                        string.Join("\n• ", missing.Take(limit)) +
                        (missing.Count > limit ? "\n… и ещё " + (missing.Count - limit) : string.Empty);

            // Отдельный раздел, а не тишина: связь загружена, но применились ли наборы —
            // команда не знает. Молчать здесь — значит выдавать «не проверено» за «сделано».
            if (unverified.Count > 0)
                text += "\n\nНаборы заказаны, но проверить их не удалось (" + unverified.Count +
                        ") — посмотрите в «Управление рабочими наборами» связи:\n• " +
                        string.Join("\n• ", unverified.Take(limit)) +
                        (unverified.Count > limit ? "\n… и ещё " + (unverified.Count - limit) : string.Empty);

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

        /// <summary>Что выяснилось про рабочие наборы уже загруженной связи.</summary>
        private enum WorksetVerdict
        {
            /// <summary>Ничего не заказывали — проверять нечего.</summary>
            NotRequested,

            /// <summary>Всё так, как просили.</summary>
            Satisfied,

            /// <summary>Часть наборов не в том состоянии — Revit заказанное не выполнил.</summary>
            Mismatched,

            /// <summary>Проверить не удалось. Это не то же самое, что «всё хорошо».</summary>
            Unknown
        }

        /// <summary>
        /// Итог сверки наборов связи с заказанным. Существует ради того, чтобы
        /// <see cref="WorksetVerdict.Unknown"/> нельзя было случайно посчитать успехом:
        /// прежняя проверка отдавала пустой список и в том случае, когда проверила и всё
        /// сошлось, и в том, когда не смогла проверить вовсе.
        /// </summary>
        private sealed class WorksetCheck
        {
            private static readonly string[] None = new string[0];

            private WorksetCheck(WorksetVerdict verdict, IReadOnlyList<string> names, string reason)
            {
                Verdict = verdict;
                Names = names ?? None;
                Reason = reason ?? string.Empty;
            }

            public WorksetVerdict Verdict { get; }

            /// <summary>Имена наборов, состояние которых не совпало с заказанным.</summary>
            public IReadOnlyList<string> Names { get; }

            /// <summary>Почему проверить не удалось; у остальных исходов пусто.</summary>
            public string Reason { get; }

            public static WorksetCheck NotRequested()
            {
                return new WorksetCheck(WorksetVerdict.NotRequested, None, null);
            }

            public static WorksetCheck Unknown(string reason)
            {
                return new WorksetCheck(WorksetVerdict.Unknown, None, reason);
            }

            public static WorksetCheck Verified(IReadOnlyList<string> wrong)
            {
                return wrong.Count == 0
                    ? new WorksetCheck(WorksetVerdict.Satisfied, None, null)
                    : new WorksetCheck(WorksetVerdict.Mismatched, wrong, null);
            }
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
