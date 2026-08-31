using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Одна сохранённая формула: имя параметра, сама формула и признак «отмечена галочкой».
    /// </summary>
    internal sealed class FormulaEntry
    {
        public FormulaEntry(string parameterName, string formula, bool isEnabled = true)
        {
            ParameterName = parameterName ?? string.Empty;
            Formula = formula ?? string.Empty;
            IsEnabled = isEnabled;
        }

        public string ParameterName { get; }

        public string Formula { get; }

        public bool IsEnabled { get; }
    }

    /// <summary>
    /// Список формул пользователя. Лежит в профиле Windows и переживает закрытие Revit —
    /// поэтому один и тот же набор подставляется в каждое следующее семейство.
    ///
    /// Формат файла: одна строка — одна формула, слева имя параметра, потом знак равенства, потом формула.
    /// Строка, начатая с решётки, читается как снятая галочка (а если знака равенства в ней нет — как комментарий).
    /// </summary>
    internal static class FormulaLibrary
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# Список формул VladTools — кнопка «Добавить формулы к общим параметрам».",
            "# Одна строка — одна формула: имя параметра, знак равенства, формула.",
            "# Решётка в начале строки означает снятую галочку в окне.",
            "# Файл можно править вручную — он перечитывается при каждом открытии окна."
        };

        /// <summary>Формулы, с которыми окно открывается в первый раз.</summary>
        public static IReadOnlyList<FormulaEntry> Defaults =>
            new List<FormulaEntry>
            {
                new FormulaEntry("ADSK_Размер_Диаметр", "if (1=1,PI_DN,0)"),
                new FormulaEntry("ADSK_Масса", "SP_Масса/1 кг")
            };

        /// <summary>%AppData%\VladTools\formulas.txt</summary>
        public static string FilePath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "formulas.txt");
            }
        }

        /// <summary>
        /// Читает список. Если файла ещё нет (первый запуск) — отдаёт формулы по умолчанию.
        /// Испорченный файл не должен ломать кнопку, поэтому ошибка чтения тоже даёт список по умолчанию.
        /// </summary>
        public static IReadOnlyList<FormulaEntry> Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return Defaults;

                var entries = File.ReadAllLines(FilePath, Encoding.UTF8)
                    .Select(Parse)
                    .Where(entry => entry != null)
                    .ToList();

                return entries.Count > 0 ? (IReadOnlyList<FormulaEntry>)entries : Defaults;
            }
            catch (Exception)
            {
                return Defaults;
            }
        }

        /// <summary>Перезаписывает файл целиком. Пустые строки таблицы не сохраняются.</summary>
        public static void Save(IEnumerable<FormulaEntry> entries)
        {
            var lines = new List<string>(FileHeader) { string.Empty };

            lines.AddRange(entries
                .Where(entry => entry != null)
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ParameterName) || !string.IsNullOrWhiteSpace(entry.Formula))
                .Select(Format));

            var folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            // BOM — чтобы кириллица открывалась в «Блокноте» как надо.
            File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
        }

        private static string Format(FormulaEntry entry)
        {
            var line = entry.ParameterName.Trim() + " " + Separator + " " + entry.Formula.Trim();
            return entry.IsEnabled ? line : "# " + line;
        }

        /// <summary>Разбирает строку файла. Возвращает null, если это комментарий или мусор.</summary>
        private static FormulaEntry Parse(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0)
                return null;

            var isEnabled = true;
            if (text[0] == '#')
            {
                isEnabled = false;
                text = text.TrimStart('#').Trim();
            }

            // Только первый знак равенства делит строку: дальше он может быть частью формулы, как в «if (1=1,…)».
            var separator = text.IndexOf(Separator);
            if (separator <= 0)
                return null;

            var name = text.Substring(0, separator).Trim();
            var formula = text.Substring(separator + 1).Trim();

            return name.Length == 0 || formula.Length == 0
                ? null
                : new FormulaEntry(name, formula, isEnabled);
        }
    }
}
