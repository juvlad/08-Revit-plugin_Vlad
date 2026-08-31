using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>Что проверка нашла в одном семействе.</summary>
    internal sealed class FamilyLabelRecord
    {
        public FamilyLabelRecord(string uniqueId, string version, IReadOnlyList<string> parameterGuids)
        {
            UniqueId = uniqueId;
            Version = version;
            ParameterGuids = parameterGuids ?? new List<string>();
        }

        /// <summary>UniqueId семейства — он переживает сохранение и работу с общими файлами.</summary>
        public string UniqueId { get; }

        /// <summary>`Element.VersionGuid` семейства на момент проверки.</summary>
        public string Version { get; }

        /// <summary>GUID общих параметров, которыми помечены размеры этого семейства.</summary>
        public IReadOnlyList<string> ParameterGuids { get; }
    }

    /// <summary>
    /// Сохранённый результат проверки семейств на метки размеров — чтобы не открывать
    /// сотни семейств заново при каждом открытии окна «Удалить общие параметры».
    ///
    /// Файл на проект: `%AppData%\VladTools\dimensions\&lt;имя&gt;_&lt;хэш пути&gt;.txt`.
    /// Строка на семейство, поэтому проверка идёт по нарастающей: заново открываются только
    /// те семейства, которых в файле нет или у которых сменилась версия.
    ///
    /// **Версия ловит не всё.** `Element.VersionGuid` по документации Revit меняется на
    /// сохранении и синхронизации, а не на каждой правке: семейство, перезагруженное
    /// в текущем сеансе без сохранения проекта, кэш считает прежним. Поэтому в окне есть
    /// «Проверить заново» — оно кэш игнорирует.
    ///
    /// Проект без пути (ещё ни разу не сохранён) не кэшируется: ключа нет.
    /// </summary>
    internal static class DimensionLabelCache
    {
        private const char Separator = '|';

        private static readonly string[] FileHeader =
        {
            "# Проверка семейств на метки размеров — кнопка «Удалить общие параметры» (панель «Проект»).",
            "# Строка: UniqueId семейства | версия элемента | GUID общих параметров через запятую.",
            "# Пустой список GUID значит: семейство проверено, параметров в размерах в нём нет.",
            "# Это кэш. Файл можно удалить — проверка просто пройдёт заново."
        };

        /// <summary>Папка со всеми файлами проверок.</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "dimensions");
            }
        }

        /// <summary>Файл проверки для конкретного проекта; для несохранённого проекта — null.</summary>
        public static string FilePathFor(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return null;

            var name = Path.GetFileNameWithoutExtension(projectPath) ?? string.Empty;

            foreach (var forbidden in Path.GetInvalidFileNameChars())
                name = name.Replace(forbidden, '_');

            if (name.Length > 40)
                name = name.Substring(0, 40);

            // Хэш пути — чтобы два разных файла с одинаковым именем не делили одну проверку.
            return Path.Combine(FolderPath, name + "_" + Hash(projectPath) + ".txt");
        }

        /// <summary>Когда проверку сохранили; файла нет — null.</summary>
        public static DateTime? SavedAt(string projectPath)
        {
            try
            {
                var path = FilePathFor(projectPath);
                return path != null && File.Exists(path) ? File.GetLastWriteTime(path) : (DateTime?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Читает сохранённую проверку, разложенную по UniqueId семейства.
        /// Файла нет или он испорчен — пустой словарь: проверка просто пройдёт заново.
        /// </summary>
        public static Dictionary<string, FamilyLabelRecord> Load(string projectPath)
        {
            var result = new Dictionary<string, FamilyLabelRecord>(StringComparer.Ordinal);

            try
            {
                var path = FilePathFor(projectPath);
                if (path == null || !File.Exists(path))
                    return result;

                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var record = Parse(line);
                    if (record != null)
                        result[record.UniqueId] = record;
                }
            }
            catch (Exception)
            {
                result.Clear();
            }

            return result;
        }

        /// <summary>
        /// Перезаписывает файл целиком — только тем, что сейчас загружено в проект,
        /// иначе файл рос бы вечно за счёт давно выгруженных семейств.
        /// Записать не удалось — беда небольшая: проверку придётся пройти ещё раз.
        /// </summary>
        public static void Save(string projectPath, IEnumerable<FamilyLabelRecord> records)
        {
            try
            {
                var path = FilePathFor(projectPath);
                if (path == null)
                    return;

                var lines = new List<string>(FileHeader) { "# Проект: " + projectPath, string.Empty };

                lines.AddRange(records
                    .Where(record => !string.IsNullOrEmpty(record?.UniqueId) && !string.IsNullOrEmpty(record.Version))
                    .Select(record => record.UniqueId + Separator + record.Version + Separator +
                                      string.Join(",", record.ParameterGuids)));

                Directory.CreateDirectory(FolderPath);

                // BOM — чтобы кириллица в шапке открывалась в «Блокноте» как надо.
                File.WriteAllLines(path, lines, new UTF8Encoding(true));
            }
            catch (Exception)
            {
                // Кэш — не результат работы команды: не пишется, значит не пишется.
            }
        }

        private static FamilyLabelRecord Parse(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return null;

            var parts = text.Split(Separator);
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0)
                return null;

            var guids = parts[2]
                .Split(',')
                .Select(guid => guid.Trim())
                .Where(guid => guid.Length > 0)
                .ToList();

            return new FamilyLabelRecord(parts[0], parts[1], guids);
        }

        private static string Hash(string projectPath)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(projectPath.ToLowerInvariant()));
                return BitConverter.ToString(bytes, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
