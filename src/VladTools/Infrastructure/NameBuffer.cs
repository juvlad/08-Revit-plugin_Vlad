using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Буфер имён: куски имён, которые пользователь вбивает снова и снова
    /// («(ФТ)-ФЛ_ГОСТ 33259-2015», «ДУ» и т.п.).
    ///
    /// Лежит в профиле Windows рядом со списком формул и переживает закрытие Revit —
    /// один раз сохранил, дальше подставляешь в каждом семействе.
    /// Формат файла: одна строка — одно значение. Строка с решётки — комментарий.
    /// </summary>
    internal static class NameBuffer
    {
        private static readonly string[] FileHeader =
        {
            "# Буфер имён VladTools — кнопка «Переименовать вложенные».",
            "# Одна строка — одно сохранённое значение, его можно подставить в поле окна.",
            "# Строка, начатая с решётки, считается комментарием.",
            "# Файл можно править вручную — он перечитывается при каждом открытии окна."
        };

        /// <summary>%AppData%\VladTools\names.txt</summary>
        public static string FilePath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "names.txt");
            }
        }

        /// <summary>
        /// Читает буфер. Файла ещё нет или он испорчен — буфер просто пустой:
        /// сломать этим кнопку нельзя.
        /// </summary>
        public static IReadOnlyList<string> Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new List<string>();

                return File.ReadAllLines(FilePath, Encoding.UTF8)
                    .Select(line => (line ?? string.Empty).Trim())
                    .Where(line => line.Length > 0 && line[0] != '#')
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>Перезаписывает файл целиком.</summary>
        public static void Save(IEnumerable<string> values)
        {
            var lines = new List<string>(FileHeader) { string.Empty };

            lines.AddRange(values
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal));

            var folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            // BOM — чтобы кириллица открывалась в «Блокноте» как надо.
            File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
        }
    }
}
