using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Настройки кнопки «Базовый файл»: что именно она делает с координационной моделью
    /// и под каким именем сохранять площадку проекта.
    ///
    /// Хранить это стоит ровно по той же причине, что и настройки «Link Manager»: имя площадки
    /// и рабочий набор «00_Shared levels and grids» в конторе одни и те же из проекта в проект,
    /// а вбиваются заново в каждом. Заодно запоминается и сама модель — в новом разделе того же
    /// объекта координационный файл тот же, и выбирать его в дереве сервера второй раз незачем.
    ///
    /// Файл: `%AppData%\VladTools\basefile\_settings.txt`, строка вида «КЛЮЧ = значение».
    /// </summary>
    internal sealed class BaseFilePreferences
    {
        private const char Separator = '=';

        /// <summary>Имя набора, куда переходят перед копированием уровней и осей.</summary>
        public const string SharedLevelsWorkset = "00_Shared levels and grids";

        /// <summary>
        /// Набор, в который кладут саму связь базового файла. По той же конвенции, что
        /// «01_Link_OV» у смежников: BM — базовая модель.
        /// </summary>
        public const string BaseLinkWorkset = "01_Link_BM";

        private static readonly string[] FileHeader =
        {
            "# Настройки кнопки «Базовый файл» — панель «Проект».",
            "# MODEL — координационная модель строкой того же вида, что в наборах связей:",
            "#   FILE | путь · SERVER | RSN://… · CLOUD | регион | GUID проекта | GUID модели | имя",
            "#   последним полем — рабочий набор проекта, в который класть саму связь",
            "# PLACEMENT — Shared | Origin | Centered | Site (по умолчанию Origin:",
            "#   общие координаты из базового файла ещё только предстоит получить)",
            "# SITE — имя, которое получит площадка проекта",
            "# LINK_WORKSET — рабочий набор проекта, в который встанет сама связь (пусто — активный)",
            "# WORKSET — рабочий набор, в который команда переходит перед копированием уровней и осей",
            "# ACQUIRE / RENAME / PIN / ACTIVATE / MONITOR — 1/0: делать ли соответствующий шаг",
            "# Файл перезаписывается при каждом закрытии окна."
        };

        /// <summary>Координационная модель, выбранная в прошлый раз; ни разу не выбирали — null.</summary>
        public LinkEntry Model { get; set; }

        /// <summary>
        /// Размещение связи. По умолчанию «Совмещение внутренних начал»: общие координаты
        /// из базового файла в этот момент ещё не получены, и вставлять по ним нечего.
        /// </summary>
        public LinkPlacement Placement { get; set; } = LinkPlacement.Origin;

        /// <summary>Имя, которое получит площадка проекта.</summary>
        public string Site { get; set; } = string.Empty;

        /// <summary>
        /// Рабочий набор проекта, в который встанет сама связь. Хранится отдельно от модели:
        /// набор один и тот же во всех разделах, а базовый файл в новом объекте другой.
        /// Пусто — активный набор, как это делает сам Revit.
        /// </summary>
        public string LinkWorkset { get; set; } = BaseLinkWorkset;

        /// <summary>Рабочий набор, в который команда переходит перед копированием уровней и осей.</summary>
        public string Workset { get; set; } = SharedLevelsWorkset;

        public bool Acquire { get; set; } = true;
        public bool Rename { get; set; } = true;
        public bool Pin { get; set; } = true;
        public bool Activate { get; set; } = true;

        /// <summary>Открывать ли в конце режим «Копирование/Мониторинг».</summary>
        public bool Monitor { get; set; } = true;

        /// <summary>%AppData%\VladTools\basefile</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "basefile");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Читает настройки. Файла нет или он испорчен — значения по умолчанию.</summary>
        public static BaseFilePreferences Load()
        {
            var preferences = new BaseFilePreferences();

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

                if (Model != null)
                    lines.Add(Line("MODEL", LinkSetLibrary.Format(Model)));

                lines.Add(Line("PLACEMENT", Placement.ToString()));
                lines.Add(Line("SITE", Site ?? string.Empty));
                lines.Add(Line("LINK_WORKSET", LinkWorkset ?? string.Empty));
                lines.Add(Line("WORKSET", Workset ?? string.Empty));
                lines.Add(Line("ACQUIRE", Acquire ? "1" : "0"));
                lines.Add(Line("RENAME", Rename ? "1" : "0"));
                lines.Add(Line("PIN", Pin ? "1" : "0"));
                lines.Add(Line("ACTIVATE", Activate ? "1" : "0"));
                lines.Add(Line("MONITOR", Monitor ? "1" : "0"));

                Directory.CreateDirectory(FolderPath);

                // BOM — чтобы кириллица открывалась в «Блокноте» как надо.
                File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
            }
            catch (Exception)
            {
            }
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
                case "MODEL":
                    Model = LinkSetLibrary.Parse(value);
                    break;

                case "PLACEMENT":
                    LinkPlacement placement;
                    if (Enum.TryParse(value, true, out placement))
                        Placement = placement;
                    break;

                case "SITE":
                    Site = value;
                    break;

                case "LINK_WORKSET":
                    LinkWorkset = value;
                    break;

                case "WORKSET":
                    Workset = value;
                    break;

                case "ACQUIRE":
                    Acquire = value != "0";
                    break;

                case "RENAME":
                    Rename = value != "0";
                    break;

                case "PIN":
                    Pin = value != "0";
                    break;

                case "ACTIVATE":
                    Activate = value != "0";
                    break;

                case "MONITOR":
                    Monitor = value != "0";
                    break;
            }
        }
    }
}
