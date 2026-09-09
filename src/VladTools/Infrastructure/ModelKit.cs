using System;
using System.Collections.Generic;
using System.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>Одна найденная модель комплекта: чей раздел, что за модель и где нашлась.</summary>
    internal sealed class ModelKitHit
    {
        public ModelKitHit(string discipline, LinkEntry entry, string folder, bool isKnown)
        {
            Discipline = discipline ?? string.Empty;
            Entry = entry;
            Folder = folder ?? string.Empty;
            IsKnown = isKnown;
        }

        public string Discipline { get; }

        public LinkEntry Entry { get; }

        /// <summary>Имя папки, в которой модель лежит: «3.0_AR».</summary>
        public string Folder { get; }

        /// <summary>Модель уже в таблице связей или в проекте — предлагать её второй раз незачем.</summary>
        public bool IsKnown { get; }
    }

    /// <summary>Итог обхода: что нашлось, сколько папок прочитано и что прочитать не удалось.</summary>
    internal sealed class ModelKitScan
    {
        public List<ModelKitHit> Hits { get; } = new List<ModelKitHit>();

        /// <summary>Папки, которые хранилище не отдало: обход из-за одной такой не прерывается.</summary>
        public List<string> Failures { get; } = new List<string>();

        /// <summary>Сколько папок прочитано — по нему видно, что обход шёл там, где ожидалось.</summary>
        public int Folders { get; set; }

        /// <summary>Обход упёрся в предел по числу папок: показанное может быть неполным.</summary>
        public bool IsTruncated { get; set; }

        /// <summary>
        /// Папка, в которой обход в итоге и шёл. Может отличаться от заказанной: если в ней
        /// папок разделов не нашлось, поиск поднимается на уровень выше — открытая модель
        /// вполне может лежать в «01_Base Model» рядом с ними, а не над ними.
        /// </summary>
        public ModelFolder Root { get; set; }

        /// <summary>Разделы, для которых не нашлось ни одной модели, — в порядке заказанного списка.</summary>
        public IReadOnlyList<string> Missing(IEnumerable<string> asked)
        {
            var found = new HashSet<string>(Hits.Select(hit => hit.Discipline), StringComparer.OrdinalIgnoreCase);

            return asked.Where(code => !found.Contains(code)).ToList();
        }
    }

    /// <summary>
    /// Подбор комплекта связей по номеру корпуса: «в модель корпуса B01 нужны АР, КР, ES, PS,
    /// PT, OV и VK того же корпуса».
    ///
    /// Держится на том, как устроены папки проекта, и ни на чём больше: рядом лежат папки
    /// разделов — <c>3.0_AR</c>, <c>4.2_KR</c>, <c>5.1_ES</c>, — а в имени каждой модели стоит
    /// номер корпуса (<c>MK3-VSC-B01-AR</c>). Значит, комплект можно собрать самому: взять папки,
    /// в имени которых код раздела стоит отдельным словом, и в каждой — модели с нужным корпусом.
    ///
    /// Это эвристика того же уровня, что <see cref="FormulaParser"/> и <see cref="DisciplineCatalog"/>:
    /// набор простых правил по токенам имени, а не разбор соглашения об именовании. Поэтому
    /// найденное показывается таблицей до того, как что-то будет связано, а раздел, в котором
    /// моделей оказалось несколько, отмечается без галочки — гадать тут нельзя.
    ///
    /// Обход намеренно узкий: корень и папки разделов в нём, а не всё дерево проекта. Каждая
    /// папка облака — это запрос по сети, и полный обход проекта с сотней папок означал бы
    /// минуту ожидания вместо секунды.
    /// </summary>
    internal static class ModelKit
    {
        /// <summary>Номер корпуса в имени модели обычно третий: <c>MK3-VSC-B01-AR</c>.</summary>
        public const int DefaultBuildingToken = 3;

        /// <summary>Сколько уровней вложенности проходить внутри папки раздела при «искать во вложенных».</summary>
        private const int InnerDepth = 2;

        /// <summary>
        /// Предел на число прочитанных папок. Защита от промаха мимо корня: если корнем окажется
        /// вершина проекта, обход не должен превратиться в получасовой опрос облака.
        /// </summary>
        private const int FolderLimit = 60;

        /// <summary>Сколько папок просмотреть на втором заходе, когда в корне разделов не нашлось.</summary>
        private const int SecondPassLimit = 25;

        /// <summary>
        /// Номер корпуса из имени модели: <paramref name="position"/>-й кусок имени, считая с единицы.
        /// Имя дробится тем же разбором, что у <see cref="DisciplineCatalog"/>, поэтому дефисы,
        /// подчёркивания и расширение уходят сами: <c>MK3-VSC-B01-AR.rvt</c> → третий кусок <c>B01</c>.
        /// Кусков меньше — пустая строка: подставлять «какой-нибудь» нельзя, ошибка была бы тихой.
        /// </summary>
        public static string Building(string modelName, int position)
        {
            var tokens = Tokens(modelName);

            return position >= 1 && position <= tokens.Count ? tokens[position - 1] : string.Empty;
        }

        /// <summary>
        /// Папка, с которой начинать поиск, по папке самой открытой модели: если та названа
        /// разделом («3.0_AR»), искать надо уровнем выше — там, где стоят папки остальных
        /// разделов; иначе — прямо в ней.
        ///
        /// Стоит одного-двух запросов к облаку, поэтому зовётся один раз, при открытии окна,
        /// и под курсором ожидания.
        /// </summary>
        public static ModelFolder Root(ModelFolder folder, IReadOnlyList<string> disciplines)
        {
            if (folder == null)
                return null;

            var named = ModelStore.WithCloudName(folder);

            // Раздел ищется и по списку комплекта, и по общему списку кодов: папка может
            // называться разделом, которого в комплекте нет («2.0_PZU»), — подниматься
            // всё равно надо.
            var codes = (disciplines ?? DisciplineCatalog.KitDefaults).Concat(DisciplineCatalog.Defaults).ToList();
            if (DisciplineCatalog.Detect(named.Name, codes).Length == 0)
                return named;

            try
            {
                var above = ModelStore.Parent(named);
                return above == null ? named : ModelStore.WithCloudName(above);
            }
            catch (Exception)
            {
                // Не поднялись — ищем там, где стоим, а папку можно указать руками.
                return named;
            }
        }

        /// <summary>Куски имени без расширения — из них пользователь и выбирает номер корпуса.</summary>
        public static IReadOnlyList<string> Tokens(string modelName)
        {
            var name = (modelName ?? string.Empty).Trim();

            if (name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            return DisciplineCatalog.Tokens(name);
        }

        /// <summary>
        /// Собирает комплект: обходит папки разделов в <paramref name="root"/> и берёт из них
        /// модели, в имени которых стоит <paramref name="building"/>.
        /// </summary>
        /// <param name="deep">Заходить и во вложенные папки раздела («3.0_AR\Модели»).</param>
        /// <param name="isKnown">Модель уже в таблице или в проекте — по ключу <see cref="LinkEntry.Key"/>.</param>
        public static ModelKitScan Find(
            ModelFolder root,
            string building,
            IReadOnlyList<string> disciplines,
            bool deep,
            Func<string, bool> isKnown)
        {
            var scan = new ModelKitScan();
            if (root == null || string.IsNullOrWhiteSpace(building) || disciplines == null || disciplines.Count == 0)
                return scan;

            var codes = disciplines
                .Select(code => (code ?? string.Empty).Trim())
                .Where(code => code.Length > 0)
                .ToList();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = root;
            var items = Read(current, scan);
            var folders = DisciplineFolders(items, codes);

            // Папок разделов в заказанной папке нет — смотрим уровнем выше. Открытая модель
            // не обязана лежать в папке своего раздела: она вполне может стоять в «01_Base
            // Model» или «0.0_Federated Model», то есть рядом с папками разделов, а не в них.
            // Один запрос, зато поиск не возвращает пустоту там, где всё на месте.
            if (folders.Count == 0)
            {
                var above = Above(current, scan);
                if (above != null)
                {
                    var aboveItems = Read(above, scan);
                    var aboveFolders = DisciplineFolders(aboveItems, codes);

                    if (aboveFolders.Count > 0)
                    {
                        current = above;
                        items = aboveItems;
                        folders = aboveFolders;
                    }
                }
            }

            // Папки разделов бывают и уровнем ниже («03_Модели\3.0_AR»).
            // Заход стоит запроса на папку, поэтому делается, только когда искать больше негде.
            if (folders.Count == 0)
            {
                foreach (var item in items.Where(item => item.IsFolder).Take(SecondPassLimit))
                {
                    if (scan.Folders >= FolderLimit)
                        break;

                    folders.AddRange(DisciplineFolders(Read(item.Folder, scan), codes));
                }
            }

            // Модели, лежащие прямо в папке поиска, тоже считаются: раздел у них берётся
            // из имени. Так комплект собирается и там, где папок разделов нет вовсе.
            foreach (var item in items.Where(item => !item.IsFolder))
                Take(scan, seen, item.Entry, DisciplineCatalog.Detect(item.Entry.Name, codes), current.Name, building, isKnown);

            foreach (var folder in folders)
                Walk(scan, seen, folder.Item1, folder.Item2, building, codes, deep ? InnerDepth : 0, isKnown);

            scan.Root = current;

            return scan;
        }

        // ───────────────────────────── обход ─────────────────────────────

        /// <summary>Папка уровнем выше; не получилось — null и строка в непрочитанные.</summary>
        private static ModelFolder Above(ModelFolder folder, ModelKitScan scan)
        {
            try
            {
                var above = ModelStore.Parent(folder);

                return above == null ? null : ModelStore.WithCloudName(above);
            }
            catch (Exception exception)
            {
                scan.Failures.Add("папка выше " + folder.Display + " — " + LinkCatalog.Short(exception.Message));
                return null;
            }
        }

        /// <summary>Папки, чьё имя несёт код раздела отдельным словом: «3.0_AR» → AR.</summary>
        private static List<Tuple<ModelFolder, string>> DisciplineFolders(IEnumerable<StoreItem> items, IReadOnlyList<string> codes)
        {
            var folders = new List<Tuple<ModelFolder, string>>();

            foreach (var item in items.Where(item => item.IsFolder))
            {
                var code = DisciplineCatalog.Detect(item.Name, codes);
                if (code.Length > 0)
                    folders.Add(Tuple.Create(item.Folder, code));
            }

            return folders;
        }

        private static void Walk(
            ModelKitScan scan,
            HashSet<string> seen,
            ModelFolder folder,
            string code,
            string building,
            IReadOnlyList<string> codes,
            int depth,
            Func<string, bool> isKnown)
        {
            if (scan.Folders >= FolderLimit)
            {
                scan.IsTruncated = true;
                return;
            }

            var items = Read(folder, scan);

            foreach (var item in items)
            {
                if (!item.IsFolder)
                {
                    Take(scan, seen, item.Entry, code, folder.Name, building, isKnown);
                    continue;
                }

                if (depth <= 0)
                    continue;

                // Вложенная папка чужого раздела внутри своей — не наше дело: её разберёт
                // собственный проход по этому разделу, если он заказан.
                var inner = DisciplineCatalog.Detect(item.Name, codes);
                if (inner.Length > 0 && !string.Equals(inner, code, StringComparison.OrdinalIgnoreCase))
                    continue;

                Walk(scan, seen, item.Folder, code, building, codes, depth - 1, isKnown);
            }
        }

        /// <summary>Кладёт модель в итог, если у неё есть раздел и нужный номер корпуса в имени.</summary>
        private static void Take(
            ModelKitScan scan,
            HashSet<string> seen,
            LinkEntry entry,
            string code,
            string folder,
            string building,
            Func<string, bool> isKnown)
        {
            if (entry == null || code.Length == 0)
                return;

            if (!DisciplineCatalog.HasToken(StripExtension(entry.Name), building))
                return;

            if (!seen.Add(entry.Key))
                return;

            scan.Hits.Add(new ModelKitHit(code, entry, folder, isKnown != null && isKnown(entry.Key)));
        }

        private static IReadOnlyList<StoreItem> Read(ModelFolder folder, ModelKitScan scan)
        {
            if (scan.Folders >= FolderLimit)
            {
                scan.IsTruncated = true;
                return new List<StoreItem>();
            }

            try
            {
                scan.Folders++;
                return ModelStore.Children(folder);
            }
            catch (Exception exception)
            {
                // Одна непрочитанная папка не должна отменять весь комплект: остальные разделы
                // соберутся, а про эту будет сказано отдельной строкой.
                scan.Failures.Add(folder.Display + " — " + LinkCatalog.Short(exception.Message));
                return new List<StoreItem>();
            }
        }

        private static string StripExtension(string name)
        {
            var text = name ?? string.Empty;

            return text.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
                ? text.Substring(0, text.Length - 4)
                : text;
        }
    }
}
