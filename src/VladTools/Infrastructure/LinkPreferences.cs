using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Способ размещения связи. Повторяет список Revit из диалога «Связь с файлом Revit»,
    /// но своим перечислением: окно про Revit API знать не должно, а команда переводит
    /// это в <c>ImportPlacement</c> одной строкой.
    /// </summary>
    internal enum LinkPlacement
    {
        /// <summary>По общим координатам — то, чем пользуются в 99 случаях из 100.</summary>
        Shared,

        /// <summary>Совмещение внутренних начал.</summary>
        Origin,

        /// <summary>Центр в центр.</summary>
        Centered,

        /// <summary>По расположению площадки проекта.</summary>
        Site
    }

    /// <summary>Что делать с рабочими наборами связи в момент загрузки.</summary>
    internal enum LinkWorksetMode
    {
        /// <summary>Открыть все, кроме отмеченных.</summary>
        OpenAll,

        /// <summary>Закрыть все, кроме отмеченных.</summary>
        CloseAll,

        /// <summary>Как при последнем открытии модели; отмеченные всё равно закрываются.</summary>
        LastViewed
    }

    /// <summary>
    /// Настройки окна «Link Manager», которые переживают закрытие Revit: способ размещения,
    /// тип связи и — главное — какие рабочие наборы закрывать.
    ///
    /// Именно ради последнего всё и хранится. Набор «00_Shared levels and grids» закрывают
    /// в каждом проекте и в каждой связи; вбивать его заново каждый раз — ровно та работа,
    /// от которой кнопка избавляет. Наборы запоминаются по имени, а не по идентификатору:
    /// в каждой модели идентификаторы свои, а имя у общего набора одно на всех.
    ///
    /// Файл: `%AppData%\VladTools\links\_settings.txt`, строка вида «КЛЮЧ = значение».
    /// Ключи CLOSE и SERVER могут повторяться — это списки.
    /// </summary>
    internal sealed class LinkPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# Настройки окна «Link Manager» — панель «Проект».",
            "# PLACEMENT — Shared | Origin | Centered | Site",
            "# ATTACHMENT — Overlay | Attachment",
            "# WORKSETS — OpenAll | CloseAll | LastViewed",
            "# CLOSE — имя рабочего набора, отмеченного в списке (строк может быть много)",
            "# RULE / RULE_CONTAINS — правило по имени набора: строка и «содержит» вместо «начинается с»",
            "# SERVER — имя сервера Revit Server, которое подставляется в просмотр (строк может быть много)",
            "# MATCH_WORKSET — 1/0: подбирать набор проекта по коду раздела в имени модели",
            "# DISCIPLINE — код раздела для этого подбора (строк может быть много); нет ни одной — берётся список по умолчанию",
            "# KIT — раздел комплекта по корпусу (строк может быть много); нет ни одной — берётся список по умолчанию",
            "# KIT_TOKEN — какой по счёту кусок имени модели считать номером корпуса (MK3-VSC-B01-AR → 3)",
            "# KIT_DEEP — 1/0: заходить ли внутрь вложенных папок раздела",
            "# KIT_BUILDING — номер корпуса из прошлого раза",
            "# KIT_ROOT — папка, в которой искать: FILE | путь, SERVER | RSN://…, CLOUD | регион | проект | папка | имя",
            "# Файл перезаписывается при каждом закрытии окна."
        };

        public LinkPlacement Placement { get; set; } = LinkPlacement.Shared;

        /// <summary>Прикрепление вместо наложения. По умолчанию наложение — как и в самом Revit.</summary>
        public bool IsAttachment { get; set; }

        /// <summary>Относительный путь к файлу связи. Для Revit Server и облака смысла не имеет.</summary>
        public bool IsRelativePath { get; set; } = true;

        public LinkWorksetMode WorksetMode { get; set; } = LinkWorksetMode.OpenAll;

        /// <summary>Имена рабочих наборов, отмеченных в списке окна.</summary>
        public List<string> Worksets { get; } = new List<string>();

        /// <summary>Строка правила по имени набора; пустая — правила нет.</summary>
        public string WorksetPattern { get; set; } = string.Empty;

        /// <summary>Правило ищет вхождение, а не начало строки.</summary>
        public bool WorksetPatternContains { get; set; }

        /// <summary>Имена серверов Revit Server, которые пользователь уже вводил.</summary>
        public List<string> Servers { get; } = new List<string>();

        /// <summary>
        /// Подбирать рабочий набор проекта по коду раздела в имени модели: у новой связи без
        /// заданного набора плагин ищет набор вида «01_Link_OV» по коду «OV» из имени файла.
        /// Ручной выбор в таблице никогда не затирается — подбор только заполняет пустое.
        /// </summary>
        public bool MatchProjectWorkset { get; set; } = true;

        /// <summary>
        /// Коды разделов для этого подбора. Пустой список = <see cref="DisciplineCatalog.Defaults"/>:
        /// пользователь дописывает свой код в _settings.txt, а весь список туда же и сохраняется,
        /// чтобы было что править.
        /// </summary>
        public List<string> Disciplines { get; } = new List<string>();

        /// <summary>
        /// Коды разделов с подстановкой умолчаний, когда пользователь список не трогал.
        /// Коды комплекта сюда входят всегда: раз пользователь назвал раздел в «Комплекте
        /// по корпусу», странно было бы не узнать тот же код в имени модели при подборе
        /// рабочего набора. Заодно это чинит старый файл настроек, записанный до того,
        /// как код появился в списке по умолчанию: список в файле перекрывает умолчания.
        /// </summary>
        public IReadOnlyList<string> EffectiveDisciplines
        {
            get
            {
                if (Disciplines.Count == 0)
                    return DisciplineCatalog.Defaults;

                return Disciplines
                    .Concat(EffectiveKit)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>
        /// Разделы, которые «Комплект по корпусу» ищет в папках проекта. Список отдельный
        /// от <see cref="Disciplines"/>: там все коды, какие вообще встречаются в именах файлов,
        /// а здесь — те, чьи модели нужно грузить связями в каждую модель.
        /// </summary>
        public List<string> Kit { get; } = new List<string>();

        /// <summary>Разделы комплекта с подстановкой умолчаний.</summary>
        public IReadOnlyList<string> EffectiveKit =>
            Kit.Count > 0 ? (IReadOnlyList<string>)Kit : DisciplineCatalog.KitDefaults;

        /// <summary>Какой по счёту кусок имени модели считать номером корпуса: <c>MK3-VSC-B01-AR</c> → третий.</summary>
        public int BuildingToken { get; set; } = ModelKit.DefaultBuildingToken;

        /// <summary>Заходить ли внутрь вложенных папок раздела при поиске комплекта.</summary>
        public bool KitDeep { get; set; } = true;

        /// <summary>Номер корпуса из прошлого раза — подставляется, когда из имени открытой модели его не вышло.</summary>
        public string KitBuilding { get; set; } = string.Empty;

        /// <summary>
        /// Папка, в которой искать комплект, строкой <see cref="ModelFolder.Format"/>. Обычно
        /// не нужна: папку кнопка определяет по самой открытой модели. Пригождается там, где
        /// определить нечего — проект открыт как отсоединённый файл, а модели лежат на сервере.
        /// </summary>
        public string KitRoot { get; set; } = string.Empty;

        /// <summary>%AppData%\VladTools\links\_settings.txt</summary>
        public static string FilePath => Path.Combine(LinkSetLibrary.FolderPath, "_settings.txt");

        /// <summary>
        /// Имя рабочего набора в сравнимом виде: без пробелов по краям.
        ///
        /// Обрезка тут не косметика. Набор у смежника легко называется «00_Reference planes »
        /// с висящим пробелом — в списке Revit это никак не видно. Пока окно обрезало имя при
        /// добавлении в список, а сравнивали имена как есть, такой набор пропадал дважды:
        /// отдельной строкой не появлялся (обрезанное имя выглядело дублем уже отмеченного),
        /// в столбце «Где есть» показывал «нет ни в одной» — и **не закрывался вовсе**,
        /// потому что <c>Matches</c> его не находил.
        /// </summary>
        public static string NormalizeWorkset(string name)
        {
            return (name ?? string.Empty).Trim();
        }

        /// <summary>
        /// Одно и то же имя рабочего набора или нет. Регистр не учитывается — Revit его
        /// в именах наборов тоже не различает. Правило одно на всех: и окно, и команда
        /// сравнивают имена наборов связей только через этот метод.
        /// </summary>
        public static bool SameWorkset(string first, string second)
        {
            return string.Equals(
                NormalizeWorkset(first),
                NormalizeWorkset(second),
                StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>Читает настройки. Файла нет или он испорчен — значения по умолчанию.</summary>
        public static LinkPreferences Load()
        {
            var preferences = new LinkPreferences();

            try
            {
                if (!File.Exists(FilePath))
                    return preferences;

                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    preferences.Apply(line);
            }
            catch (Exception)
            {
                // Испорченный файл настроек — не повод не открывать окно.
            }

            return preferences;
        }

        /// <summary>Перезаписывает файл целиком. Отказ записи проглатывается: это настройки, не данные.</summary>
        public void Save()
        {
            try
            {
                var lines = new List<string>(FileHeader) { string.Empty };

                lines.Add(Line("PLACEMENT", Placement.ToString()));
                lines.Add(Line("ATTACHMENT", IsAttachment ? "Attachment" : "Overlay"));
                lines.Add(Line("RELATIVE", IsRelativePath ? "1" : "0"));
                lines.Add(Line("WORKSETS", WorksetMode.ToString()));
                lines.Add(Line("RULE", WorksetPattern ?? string.Empty));
                lines.Add(Line("RULE_CONTAINS", WorksetPatternContains ? "1" : "0"));
                lines.Add(Line("MATCH_WORKSET", MatchProjectWorkset ? "1" : "0"));

                lines.Add(Line("KIT_TOKEN", BuildingToken.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                lines.Add(Line("KIT_DEEP", KitDeep ? "1" : "0"));
                lines.Add(Line("KIT_BUILDING", KitBuilding ?? string.Empty));
                lines.Add(Line("KIT_ROOT", KitRoot ?? string.Empty));

                lines.AddRange(Clean(Worksets).Select(name => Line("CLOSE", name)));
                lines.AddRange(Clean(Servers).Select(name => Line("SERVER", name)));

                // Пустой список подразумевает умолчания, но в файл кладём то, что реально
                // действует, — иначе править было бы нечего.
                lines.AddRange(Clean(EffectiveDisciplines).Select(code => Line("DISCIPLINE", code)));
                lines.AddRange(Clean(EffectiveKit).Select(code => Line("KIT", code)));

                Directory.CreateDirectory(LinkSetLibrary.FolderPath);

                // BOM — чтобы кириллица открывалась в «Блокноте» как надо.
                File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
            }
            catch (Exception)
            {
            }
        }

        private static IEnumerable<string> Clean(IEnumerable<string> values)
        {
            return values
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase);
        }

        private static string Line(string key, string value)
        {
            return key + " " + Separator + " " + value;
        }

        private void Apply(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return;

            var separator = text.IndexOf(Separator);
            if (separator <= 0)
                return;

            var key = text.Substring(0, separator).Trim().ToUpperInvariant();
            var value = text.Substring(separator + 1).Trim();

            switch (key)
            {
                case "PLACEMENT":
                    Placement = Parse(value, LinkPlacement.Shared);
                    break;

                case "ATTACHMENT":
                    IsAttachment = string.Equals(value, "Attachment", StringComparison.OrdinalIgnoreCase);
                    break;

                case "RELATIVE":
                    IsRelativePath = value != "0";
                    break;

                case "WORKSETS":
                    WorksetMode = Parse(value, LinkWorksetMode.OpenAll);
                    break;

                case "RULE":
                    WorksetPattern = value;
                    break;

                case "RULE_CONTAINS":
                    WorksetPatternContains = value == "1";
                    break;

                case "CLOSE":
                    if (value.Length > 0)
                        Worksets.Add(value);
                    break;

                case "SERVER":
                    if (value.Length > 0)
                        Servers.Add(value);
                    break;

                case "MATCH_WORKSET":
                    MatchProjectWorkset = value != "0";
                    break;

                case "DISCIPLINE":
                    if (value.Length > 0)
                        Disciplines.Add(value);
                    break;

                case "KIT":
                    if (value.Length > 0)
                        Kit.Add(value);
                    break;

                case "KIT_TOKEN":
                    int token;
                    if (int.TryParse(value, out token) && token >= 1)
                        BuildingToken = token;
                    break;

                case "KIT_DEEP":
                    KitDeep = value != "0";
                    break;

                case "KIT_BUILDING":
                    KitBuilding = value;
                    break;

                case "KIT_ROOT":
                    KitRoot = value;
                    break;
            }
        }

        private static T Parse<T>(string value, T fallback) where T : struct
        {
            T parsed;
            return Enum.TryParse(value, true, out parsed) ? parsed : fallback;
        }
    }
}
