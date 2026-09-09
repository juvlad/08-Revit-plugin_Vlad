using System;
using System.Collections.Generic;
using System.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>Итог подбора: номер корпуса, папка базовых файлов, кандидаты и непрочитанное.</summary>
    internal sealed class BaseFileScan
    {
        /// <summary>Номер корпуса из имени открытой модели; не нашёлся — пусто.</summary>
        public string Building { get; set; } = string.Empty;

        /// <summary>Папка базовых файлов, если она нашлась: её и показывают пользователю.</summary>
        public ModelFolder Folder { get; set; }

        /// <summary>
        /// Подходящие модели. Одна — можно подставлять; несколько — выбирает человек,
        /// то же правило, что при подборе рабочего набора и комплекта по корпусу.
        /// </summary>
        public List<LinkEntry> Hits { get; } = new List<LinkEntry>();

        /// <summary>Папки, которые хранилище не отдало: подбор из-за одной такой не прерывается.</summary>
        public List<string> Failures { get; } = new List<string>();

        /// <summary>
        /// Совпало только по корпусу: кода базовой модели («BM») в имени нет. Модель всё равно
        /// лежит в папке базовых файлов, поэтому она кандидат, — но сказать об этом обязаны.
        /// </summary>
        public bool IsLoose { get; set; }
    }

    /// <summary>
    /// Подбор базового (координационного) файла по имени открытой модели.
    ///
    /// Держится на том же соглашении, что и «Комплект по корпусу»: в имени каждой модели стоит
    /// номер корпуса (<c>MK3-VSC-B01-VOIDS</c> → <c>B01</c>), а базовые файлы всего проекта лежат
    /// в одной папке — <c>01_Base Model</c> — рядом с папками разделов. Значит, базовый файл
    /// корпуса можно назвать самому: <c>MK3-VSC-B01-BM</c>, и искать его в дереве незачем.
    ///
    /// Это эвристика того же уровня, что <see cref="DisciplineCatalog"/> и <see cref="ModelKit"/>:
    /// набор правил по токенам имени, а не разбор соглашения об именовании. Поэтому подобранное
    /// показывается в окне до того, как что-то будет связано, а когда подходящих моделей
    /// несколько — не берётся ни одна: гадать тут нельзя.
    ///
    /// Обход намеренно узкий — две папки, а не дерево проекта: папка самой модели и папка уровнем
    /// выше. Каждая папка облака стоит запроса по сети, а подбор идёт при открытии окна, то есть
    /// в тот момент, когда пользователь ждёт.
    /// </summary>
    internal static class BaseFileFinder
    {
        /// <summary>Папка, в которой лежат базовые файлы всех корпусов проекта.</summary>
        public const string DefaultFolderName = "01_Base Model";

        /// <summary>Код базовой модели в имени файла — по той же конвенции, что коды разделов.</summary>
        public const string DefaultCode = "BM";

        /// <param name="host">Открытая модель: из имени берётся корпус, из папки — где искать.</param>
        /// <param name="folderName">Имя папки базовых файлов; пусто — <see cref="DefaultFolderName"/>.</param>
        /// <param name="code">Код базовой модели в имени; пусто — <see cref="DefaultCode"/>.</param>
        /// <param name="buildingToken">Какой по счёту кусок имени считать номером корпуса.</param>
        public static BaseFileScan Find(HostModel host, string folderName, string code, int buildingToken)
        {
            var scan = new BaseFileScan();
            if (host == null)
                return scan;

            scan.Building = ModelKit.Building(host.Name, buildingToken);
            if (host.Folder == null || scan.Building.Length == 0)
                return scan;

            var folder = Locate(host.Folder, Words(Named(folderName, DefaultFolderName)), scan);
            if (folder == null)
                return scan;

            scan.Folder = folder;

            // Сама открытая модель из кандидатов вычёркивается: связаться с собой Revit
            // всё равно не даст, а лежать в этой же папке она вполне может.
            var models = Read(folder, scan)
                .Where(item => !item.IsFolder && item.Entry != null)
                .Select(item => item.Entry)
                .Where(entry => !string.Equals(entry.Key, host.Key, StringComparison.Ordinal))
                .ToList();

            var wanted = Named(code, DefaultCode);

            var strict = models
                .Where(entry => DisciplineCatalog.HasToken(entry.Name, scan.Building) &&
                                DisciplineCatalog.HasToken(entry.Name, wanted))
                .ToList();

            if (strict.Count > 0)
            {
                scan.Hits.AddRange(strict);
                return scan;
            }

            // Кода в имени нет — но модель нужного корпуса лежит в папке базовых файлов,
            // и это лучший ответ, чем «ничего не нашлось». Про натяжку скажет окно.
            var loose = models
                .Where(entry => DisciplineCatalog.HasToken(entry.Name, scan.Building))
                .ToList();

            scan.IsLoose = loose.Count > 0;
            scan.Hits.AddRange(loose);

            return scan;
        }

        // ───────────────────────────── где искать ─────────────────────────────

        /// <summary>
        /// Папка базовых файлов рядом с открытой моделью. Смотрим ровно в двух местах: там, где
        /// модель лежит (она сама может оказаться той папкой — базовые файлы правят из неё же),
        /// и уровнем выше — модель раздела стоит в «3.0_AR», а базовые файлы рядом, не внутри.
        /// </summary>
        private static ModelFolder Locate(ModelFolder start, IReadOnlyList<string> wanted, BaseFileScan scan)
        {
            if (wanted.Count == 0)
                return null;

            var here = ModelStore.WithCloudName(start);
            if (Matches(here.Name, wanted))
                return here;

            var found = Pick(Read(here, scan), wanted);
            if (found != null)
                return found;

            var above = Above(here, scan);
            if (above == null)
                return null;

            return Matches(above.Name, wanted) ? above : Pick(Read(above, scan), wanted);
        }

        /// <summary>Папка уровнем выше; не получилось — null и строка в непрочитанные.</summary>
        private static ModelFolder Above(ModelFolder folder, BaseFileScan scan)
        {
            try
            {
                return ModelStore.WithCloudName(ModelStore.Parent(folder));
            }
            catch (Exception exception)
            {
                scan.Failures.Add("папка выше " + folder.Display + " — " + LinkCatalog.Short(exception.Message));
                return null;
            }
        }

        private static ModelFolder Pick(IEnumerable<StoreItem> items, IReadOnlyList<string> wanted)
        {
            return items
                .Where(item => item.IsFolder && Matches(item.Name, wanted))
                .Select(item => item.Folder)
                .FirstOrDefault();
        }

        private static IReadOnlyList<StoreItem> Read(ModelFolder folder, BaseFileScan scan)
        {
            try
            {
                return ModelStore.Children(folder);
            }
            catch (Exception exception)
            {
                // Непрочитанная папка не отменяет подбор: вторую попробуем всё равно,
                // а причина уйдёт в подпись под выбранной моделью.
                scan.Failures.Add(folder.Display + " — " + LinkCatalog.Short(exception.Message));
                return new List<StoreItem>();
            }
        }

        // ───────────────────────────── имя папки ─────────────────────────────

        /// <summary>
        /// Имя папки сравнивается словами, без ведущих номеров: «01_Base Model», «02 Base Model»
        /// и «Base Model» — одна и та же папка. Номер в начале от проекта к проекту меняют,
        /// а само имя остаётся; сравнивать строкой целиком значило бы промахиваться на ровном месте.
        /// Слова при этом должны совпасть все и по порядку — «Base Models» уже другая папка,
        /// и такую подставляет пользователь через настройку, а не догадка.
        /// </summary>
        private static bool Matches(string name, IReadOnlyList<string> wanted)
        {
            var words = Words(name);

            return words.Count == wanted.Count &&
                   !words.Where((word, i) => !string.Equals(word, wanted[i], StringComparison.OrdinalIgnoreCase)).Any();
        }

        /// <summary>Слова имени без чисто числовых кусков; разбор — общий с подбором разделов.</summary>
        private static IReadOnlyList<string> Words(string text)
        {
            return DisciplineCatalog.Tokens(text).Where(token => !token.All(char.IsDigit)).ToList();
        }

        private static string Named(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }
}
