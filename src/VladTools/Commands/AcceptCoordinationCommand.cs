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
    /// Принимает изменения координационного файла: ставит оси и уровни проекта туда, где они
    /// теперь в связи, и переименовывает их вслед за ней.
    ///
    /// В Revit это «Совместная работа → Просмотр координации → Выбрать связь» и дальше по одному
    /// изменению: развернуть узел, выбрать действие, повторить. На корпусе, где поехал десяток
    /// осей, это десятки щелчков, и делается заново после каждой выдачи базового файла.
    ///
    /// **Нажать «Принять» внутри самого «Просмотра координации» кнопка не может.** Этого диалога
    /// в Revit API нет вовсе: из всего мониторинга наружу выведены только
    /// <c>Element.IsMonitoringLinkElement</c>, <c>IsMonitoringLocalElement</c>,
    /// <c>GetMonitoredLinkElementIds</c> и <c>GetMonitoredLocalElementIds</c> — проверено
    /// рефлексией по RevitAPI.dll 2022, 2024 и 2025, ни чтения списка изменений, ни действий над
    /// ним там нет. Поэтому команда идёт с другой стороны: сама считает расхождения
    /// (<see cref="CoordinationCatalog"/>) и сама двигает элементы — то есть делает ровно то,
    /// что сделало бы действие «Переместить» в диалоге. Когда элемент встаёт на место, Revit
    /// перестаёт считать его расхождением, и список «Просмотра координации» пустеет сам.
    ///
    /// Чего команда не делает: не удаляет оси и уровни, пропавшие из координационного файла
    /// (за уровнем ушло бы всё, что на нём стоит), и не заводит мониторинг на новые элементы
    /// связи — создания связей мониторинга в API тоже нет. И то и другое показано в окне
    /// строкой, чтобы разобрать вручную.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AcceptCoordinationCommand : IExternalCommand
    {
        private const string DialogTitle = "Принять координационные изменения";

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
                    "Мониторинг координационного файла живёт в модели раздела, а не в семействе.");
                return Result.Cancelled;
            }

            try
            {
                var failures = new List<string>();
                var scans = CoordinationCatalog.Scan(doc, failures);

                if (scans.Count == 0)
                {
                    TaskDialog.Show(DialogTitle,
                        "В проекте нет ни одной оси или уровня, которые следят за связью.\n\n" +
                        "Сравнивать не с чем: кнопка работает по связям мониторинга, а они заводятся " +
                        "только вручную — «Совместная работа → Копирование/Мониторинг → Выбрать связь» " +
                        "(этот режим открывает и кнопка «Базовый файл» последним шагом)." +
                        Note(failures));
                    return Result.Cancelled;
                }

                var window = new AcceptCoordinationWindow(scans);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var rows = window.Selected;
                var chosen = window.Chosen;

                var done = new List<string>();
                var warnings = new WarningSuppressor();

                using (var transaction = new Transaction(doc, "Принять координационные изменения"))
                {
                    transaction.Start();

                    // Сдвиг уровня тянет за собой всё, что на нём стоит, и Revit почти наверняка
                    // о чём-нибудь предупредит: о разорванных присоединениях, о выехавших
                    // элементах. Модальное окно на каждое превратило бы одну кнопку
                    // в щёлканье по диалогам.
                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(warnings);
                    transaction.SetFailureHandlingOptions(options);

                    foreach (var row in rows.Where(item => item.Kind == CoordinationChangeKind.Position))
                        Move(doc, row, done, failures);

                    Rename(doc, rows.Where(item => item.Kind == CoordinationChangeKind.Name).ToList(), done, failures);

                    if (done.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                if (window.OpenReview)
                    OpenReview(commandData.Application, done, failures);

                Report(chosen, done, failures, warnings.Messages);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── правка ─────────────────────────────

        /// <summary>
        /// Ставит ось или уровень туда, где он теперь в связи.
        ///
        /// У оси это поворот вокруг её середины плюс сдвиг поперёк себя — совмещается прямая,
        /// а не отрезок: длину оси в проекте подрезают под свои виды, и к координации она
        /// отношения не имеет. У уровня — просто новая отметка.
        /// </summary>
        private static void Move(Document doc, CoordinationChangeRow row, List<string> done, List<string> failures)
        {
            var element = doc.GetElement(row.Update.HostId);
            if (element == null)
            {
                failures.Add(row.Title + " — этого элемента в проекте уже нет");
                return;
            }

            var pinned = false;

            try
            {
                // Оси и уровни базового файла обычно закреплены булавкой — иначе их двигают мышью.
                // Закреплённый элемент Revit двигать не даёт, поэтому булавка снимается на время
                // правки и возвращается сразу после неё.
                pinned = element.Pinned;
                if (pinned)
                    element.Pinned = false;

                var level = element as Level;
                if (level != null)
                {
                    level.Elevation = row.Update.Elevation;
                }
                else
                {
                    if (row.Update.Angle != 0)
                    {
                        var axis = Line.CreateBound(row.Update.Center, row.Update.Center + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, element.Id, axis, row.Update.Angle);
                    }

                    if (row.Update.Translation != null)
                        ElementTransformUtils.MoveElement(doc, element.Id, row.Update.Translation);
                }

                done.Add(row.Title + " — " + row.Detail);
            }
            catch (Exception exception)
            {
                failures.Add(row.Title + " — переставить не удалось: " + LinkCatalog.Short(exception.Message));
            }
            finally
            {
                Repin(element, pinned, failures, row.Title);
            }
        }

        /// <summary>
        /// Возвращает булавку на место. Отказ здесь не должен срывать остальное: элемент уже
        /// стоит правильно, просто не закреплён, — и об этом надо сказать, а не промолчать.
        /// </summary>
        private static void Repin(Element element, bool pinned, List<string> failures, string title)
        {
            if (!pinned)
                return;

            try
            {
                element.Pinned = true;
            }
            catch (Exception exception)
            {
                failures.Add(title + " — булавка не вернулась: " + LinkCatalog.Short(exception.Message));
            }
        }

        /// <summary>
        /// Переименовывает оси и уровни вслед за связью — в несколько проходов, как удаление
        /// параметров: пока имя занято соседом, которого тоже переименовывают, Revit его не отдаёт.
        /// Проход без единого успеха означает, что дело не в очереди, — тогда в отчёт.
        /// </summary>
        private static void Rename(
            Document doc,
            IReadOnlyList<CoordinationChangeRow> rows,
            List<string> done,
            List<string> failures)
        {
            var left = rows.ToList();

            while (left.Count > 0)
            {
                var stuck = new List<CoordinationChangeRow>();
                var reasons = new Dictionary<CoordinationChangeRow, string>();

                foreach (var row in left)
                {
                    var element = doc.GetElement(row.Update.HostId);
                    if (element == null)
                    {
                        failures.Add(row.Title + " — этого элемента в проекте уже нет");
                        continue;
                    }

                    try
                    {
                        element.Name = row.Update.NewName;
                        done.Add(row.Title + " — переименован в «" + row.Update.NewName + "»");
                    }
                    catch (Exception exception)
                    {
                        stuck.Add(row);
                        reasons[row] = LinkCatalog.Short(exception.Message);
                    }
                }

                if (stuck.Count == left.Count)
                {
                    foreach (var row in stuck)
                        failures.Add(row.Title + " — переименовать не удалось: " + reasons[row]);

                    return;
                }

                left = stuck;
            }
        }

        // ───────────────────────────── просмотр координации ─────────────────────────────

        /// <summary>
        /// Открывает «Просмотр координации → Выбрать связь».
        ///
        /// Не продолжение работы, а её проверка: нажать что-либо в этом диалоге из API нельзя,
        /// но после того как элементы встали по файлу, его список должен опустеть. Команда
        /// ставится в очередь Revit и срабатывает после закрытия отчёта.
        /// </summary>
        private static void OpenReview(UIApplication application, List<string> done, List<string> failures)
        {
            try
            {
                var command = RevitCommandId.LookupPostableCommandId(PostableCommand.CoordinationSelectLink);
                if (command == null || !application.CanPostCommand(command))
                {
                    failures.Add("«Просмотр координации» сейчас недоступен — откройте его вручную " +
                                 "(«Совместная работа → Просмотр координации»).");
                    return;
                }

                application.PostCommand(command);
                done.Add("Открывается «Просмотр координации → Выбрать связь» — проверьте, что список пуст");
            }
            catch (Exception exception)
            {
                failures.Add("Открыть «Просмотр координации» не удалось: " + LinkCatalog.Short(exception.Message));
            }
        }

        // ───────────────────────────── отчёт ─────────────────────────────

        private static void Report(
            CoordinationScan scan,
            IReadOnlyList<string> done,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
        {
            const int limit = 15;

            var text = scan != null ? "Координационный файл: " + scan.LinkName + ".\n\n" : string.Empty;

            text += done.Count == 0
                ? "Ничего не сделано."
                : "Принято (" + done.Count + "):\n• " + string.Join("\n• ", done.Take(limit)) +
                  (done.Count > limit ? "\n… и ещё " + (done.Count - limit) : string.Empty);

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

        /// <summary>Приписка к отказу «сравнивать нечего»: то, что не прочиталось, молчать не должно.</summary>
        private static string Note(IReadOnlyList<string> failures)
        {
            const int limit = 10;

            if (failures.Count == 0)
                return string.Empty;

            var text = "\n\nПри этом не удалось прочитать (" + failures.Count + "):\n• " +
                       string.Join("\n• ", failures.Take(limit));

            return failures.Count > limit ? text + "\n… и ещё " + (failures.Count - limit) : text;
        }
    }
}
