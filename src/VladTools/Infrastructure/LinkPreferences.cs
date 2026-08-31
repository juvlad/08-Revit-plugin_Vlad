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

        /// <summary>%AppData%\VladTools\links\_settings.txt</summary>
        public static string FilePath => Path.Combine(LinkSetLibrary.FolderPath, "_settings.txt");

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

                lines.AddRange(Clean(Worksets).Select(name => Line("CLOSE", name)));
                lines.AddRange(Clean(Servers).Select(name => Line("SERVER", name)));

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
            }
        }

        private static T Parse<T>(string value, T fallback) where T : struct
        {
            T parsed;
            return Enum.TryParse(value, true, out parsed) ? parsed : fallback;
        }
    }
}
