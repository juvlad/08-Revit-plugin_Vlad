using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>Одна нитка внутри сохранённого шаблона: вид, смещение и тип размера по имени.</summary>
    internal sealed class DimensionTemplateChain
    {
        public DimensionChainKind Kind { get; set; }
        public double OffsetMm { get; set; }

        /// <summary>Имя, а не Id — шаблон должен переноситься между проектами, где Id у типов свои.</summary>
        public string DimensionTypeName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Шаблон авторазмеров: по какой линии идёт граница помещения, куда смотрят нитки
    /// и сам список ниток. Ровно то, что показывает окно «Авторазмеры» и что можно сохранить
    /// кнопкой «Сохранить шаблон» — а можно и не сохранять, шаблон нужен только для переноса
    /// набора ниток между проектами.
    /// </summary>
    internal sealed class DimensionTemplate
    {
        public SpatialElementBoundaryLocation Boundary { get; set; } = SpatialElementBoundaryLocation.CoreBoundary;

        /// <summary>Нитки ставятся наружу помещения, а не внутрь (по умолчанию — внутрь).</summary>
        public bool Outward { get; set; }

        public List<DimensionTemplateChain> Chains { get; } = new List<DimensionTemplateChain>();
    }

    /// <summary>
    /// Хранилище шаблонов авторазмеров: `%AppData%\VladTools\autodim\&lt;имя&gt;.txt`.
    ///
    /// Отдельная папка, не `dimensions\` — та занята кэшем проверки семейств на метки размеров
    /// (см. <see cref="DimensionLabelCache"/>); смешивать разные по смыслу файлы нельзя.
    /// Формат — строка на сущность, поля через вертикальную черту, как у наборов связей
    /// (<see cref="LinkSetLibrary"/>): черты не бывает ни в имени типа размера, ни в номере.
    /// </summary>
    internal static class DimensionTemplateLibrary
    {
        private const char Separator = '|';

        private static readonly string[] FileHeader =
        {
            "# Шаблон авторазмеров VladTools — кнопка «Авто размеры» (панель «Проект»).",
            "# ГРАНИЦА | Finish | Center | CoreBoundary | CoreCenter — по какой линии идёт граница помещения.",
            "# СТОРОНА  | Внутрь | Наружу — куда смотрят нитки.",
            "# НИТКА    | номер | смещение_мм | вид нитки | имя типа размера",
            "#   вид нитки — одно из: Overall, OpeningEdges, OpeningCenters, Partitions, WallFaces, Combined",
            "# Номер нитки — только для удобства чтения файла глазами, при загрузке не используется:",
            "# порядок ниток — это порядок строк НИТКА. Файл можно править вручную."
        };

        /// <summary>%AppData%\VladTools\autodim</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "autodim");
            }
        }

        /// <summary>Имена сохранённых шаблонов по алфавиту. Имена с подчёркивания — служебные, в список не входят.</summary>
        public static IReadOnlyList<string> Names()
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                    return new List<string>();

                return Directory.GetFiles(FolderPath, "*.txt")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrEmpty(name) && name[0] != '_')
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>Читает шаблон. Файла нет или он испорчен — пустой шаблон: сломать этим кнопку нельзя.</summary>
        public static DimensionTemplate Load(string templateName)
        {
            var template = new DimensionTemplate();

            try
            {
                var file = FilePathFor(templateName);
                if (file == null || !File.Exists(file))
                    return template;

                foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                    Apply(template, line);
            }
            catch (Exception)
            {
                return new DimensionTemplate();
            }

            return template;
        }

        /// <summary>Перезаписывает шаблон целиком.</summary>
        public static void Save(string templateName, DimensionTemplate template)
        {
            var file = FilePathFor(templateName);
            if (file == null)
                throw new ArgumentException("Имя шаблона пустое или состоит из недопустимых знаков.");

            var lines = new List<string>(FileHeader) { string.Empty };
            lines.Add(Field("ГРАНИЦА", template.Boundary.ToString()));
            lines.Add(Field("СТОРОНА", template.Outward ? "Наружу" : "Внутрь"));
            lines.Add(string.Empty);

            var number = 1;
            foreach (var chain in template.Chains)
            {
                lines.Add(string.Join(" " + Separator + " ", new[]
                {
                    "НИТКА",
                    number.ToString(CultureInfo.InvariantCulture),
                    chain.OffsetMm.ToString(CultureInfo.InvariantCulture),
                    chain.Kind.ToString(),
                    chain.DimensionTypeName
                }));
                number++;
            }

            Directory.CreateDirectory(FolderPath);

            // BOM — чтобы кириллица открывалась в «Блокноте» как надо.
            File.WriteAllLines(file, lines, new UTF8Encoding(true));
        }

        public static void Delete(string templateName)
        {
            var file = FilePathFor(templateName);
            if (file != null && File.Exists(file))
                File.Delete(file);
        }

        /// <summary>Путь к файлу шаблона; имя пустое или из одних недопустимых знаков — null.</summary>
        public static string FilePathFor(string templateName)
        {
            var name = (templateName ?? string.Empty).Trim();
            if (name.Length == 0)
                return null;

            foreach (var forbidden in Path.GetInvalidFileNameChars())
                name = name.Replace(forbidden, '_');

            name = name.Trim('_', ' ');

            return name.Length == 0 ? null : Path.Combine(FolderPath, name + ".txt");
        }

        private static void Apply(DimensionTemplate template, string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return;

            var parts = text.Split(Separator).Select(part => part.Trim()).ToArray();
            if (parts.Length < 2)
                return;

            switch (parts[0].ToUpperInvariant())
            {
                case "ГРАНИЦА":
                    SpatialElementBoundaryLocation boundary;
                    if (Enum.TryParse(parts[1], true, out boundary))
                        template.Boundary = boundary;
                    break;

                case "СТОРОНА":
                    template.Outward = string.Equals(parts[1], "Наружу", StringComparison.OrdinalIgnoreCase);
                    break;

                case "НИТКА":
                    var chain = ParseChain(parts);
                    if (chain != null)
                        template.Chains.Add(chain);
                    break;
            }
        }

        private static DimensionTemplateChain ParseChain(string[] parts)
        {
            // НИТКА | номер | смещение | вид | имя типа — номер не используется, но должен быть на месте.
            if (parts.Length < 4)
                return null;

            double offset;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out offset))
                return null;

            DimensionChainKind kind;
            if (!Enum.TryParse(parts[3], true, out kind))
                return null;

            return new DimensionTemplateChain
            {
                OffsetMm = offset,
                Kind = kind,
                DimensionTypeName = parts.Length > 4 ? parts[4] : string.Empty
            };
        }

        private static string Field(string key, string value)
        {
            return key + " " + Separator + " " + value;
        }
    }

    /// <summary>
    /// Настройки окна «Авторазмеры», которые переживают закрытие Revit: последний использованный
    /// шаблон, граница и направление. Перезаписываются при любом закрытии окна — как настройки
    /// «Link Manager» (<see cref="LinkPreferences"/>), в отличие от самих шаблонов, которые
    /// сохраняются только по кнопке.
    ///
    /// Файл: `%AppData%\VladTools\autodim\_settings.txt`. Имя «_settings» занято этим файлом —
    /// `DimensionTemplateLibrary.Names()` пропускает всё, что начинается с подчёркивания.
    /// </summary>
    internal sealed class AutoDimensionPreferences
    {
        public string LastTemplate { get; set; } = string.Empty;
        public SpatialElementBoundaryLocation Boundary { get; set; } = SpatialElementBoundaryLocation.CoreBoundary;
        public bool Outward { get; set; }
        public bool RemovePrevious { get; set; } = true;

        public static string FilePath => Path.Combine(DimensionTemplateLibrary.FolderPath, "_settings.txt");

        public static AutoDimensionPreferences Load()
        {
            var preferences = new AutoDimensionPreferences();

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

        public void Save()
        {
            try
            {
                var lines = new List<string>
                {
                    "# Настройки окна «Авторазмеры» — панель «Проект».",
                    "# Файл перезаписывается при каждом закрытии окна.",
                    string.Empty,
                    Line("TEMPLATE", LastTemplate),
                    Line("BOUNDARY", Boundary.ToString()),
                    Line("OUTWARD", Outward ? "1" : "0"),
                    Line("REMOVE_PREVIOUS", RemovePrevious ? "1" : "0")
                };

                Directory.CreateDirectory(DimensionTemplateLibrary.FolderPath);

                // BOM — чтобы кириллица открывалась в «Блокноте» как надо.
                File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
            }
            catch (Exception)
            {
                // Настройки, не данные — отказ записи не должен мешать работе кнопки.
            }
        }

        private static string Line(string key, string value)
        {
            return key + " = " + value;
        }

        private void Apply(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return;

            var separator = text.IndexOf('=');
            if (separator <= 0)
                return;

            var key = text.Substring(0, separator).Trim().ToUpperInvariant();
            var value = text.Substring(separator + 1).Trim();

            switch (key)
            {
                case "TEMPLATE":
                    LastTemplate = value;
                    break;

                case "BOUNDARY":
                    SpatialElementBoundaryLocation boundary;
                    if (Enum.TryParse(value, true, out boundary))
                        Boundary = boundary;
                    break;

                case "OUTWARD":
                    Outward = value == "1";
                    break;

                case "REMOVE_PREVIOUS":
                    RemovePrevious = value != "0";
                    break;
            }
        }
    }
}
