using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>Откуда берётся модель связи. От этого зависит и путь, и то, как его записать в файл.</summary>
    internal enum LinkOrigin
    {
        /// <summary>Обычный файл: диск, сетевая папка.</summary>
        File,

        /// <summary>Revit Server: путь вида RSN://сервер/папка/модель.rvt.</summary>
        Server,

        /// <summary>BIM360/ACC: регион и два GUID — проекта и модели.</summary>
        Cloud
    }

    /// <summary>
    /// Одна модель в сохранённом наборе. Для файла и Revit Server всё держится на пути,
    /// для облака пути нет вовсе: облачная модель адресуется регионом и парой GUID,
    /// а имя хранится отдельно — иначе в списке было бы не разобрать, что есть что.
    /// </summary>
    internal sealed class LinkEntry
    {
        public LinkEntry(LinkOrigin origin, string name, string path, string region, string projectGuid, string modelGuid)
        {
            Origin = origin;
            Name = name ?? string.Empty;
            Path = path ?? string.Empty;
            Region = region ?? string.Empty;
            ProjectGuid = projectGuid ?? string.Empty;
            ModelGuid = modelGuid ?? string.Empty;
        }

        public static LinkEntry ForFile(string path)
        {
            return new LinkEntry(LinkOrigin.File, System.IO.Path.GetFileName(path), path, null, null, null);
        }

        public static LinkEntry ForServer(string rsnPath)
        {
            var name = (rsnPath ?? string.Empty).Split('/').LastOrDefault() ?? string.Empty;
            return new LinkEntry(LinkOrigin.Server, name, rsnPath, null, null, null);
        }

        public static LinkEntry ForCloud(string region, string projectGuid, string modelGuid, string name)
        {
            return new LinkEntry(LinkOrigin.Cloud, name, null, region, projectGuid, modelGuid);
        }

        public LinkOrigin Origin { get; }

        /// <summary>Имя модели вместе с расширением — то, что видно в таблице.</summary>
        public string Name { get; }

        /// <summary>Путь к файлу или RSN-путь; у облачной модели пусто.</summary>
        public string Path { get; }

        public string Region { get; }
        public string ProjectGuid { get; }
        public string ModelGuid { get; }

        /// <summary>
        /// Рабочий набор **открытого проекта**, в который встанет эта связь; пусто — активный набор,
        /// как это делает сам Revit. Единственное изменяемое поле записи: набор выбирается в окне
        /// и сохраняется вместе с набором связей, чтобы «АР в 01_Связи_АР» не расставлять заново
        /// в каждом проекте.
        /// </summary>
        public string Workset { get; set; } = string.Empty;

        /// <summary>
        /// Ключ, по которому две записи считаются одной моделью: путь без учёта регистра
        /// либо пара GUID. Им же ловятся дубли при добавлении и связи, уже стоящие в проекте.
        /// </summary>
        public string Key => Origin == LinkOrigin.Cloud
            ? "cloud|" + ProjectGuid.ToLowerInvariant() + "|" + ModelGuid.ToLowerInvariant()
            : "path|" + Path.Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// Сохранённые наборы связей: «Стадия Р», «Смежники», «Подоснова». Набор — это список
    /// моделей, который собирается один раз и подставляется в каждый следующий проект.
    ///
    /// Для BIM360 набор — не просто удобство, а единственный способ обойтись без просмотра
    /// облака: собранный однажды список GUID работает и тогда, когда до Autodesk не достучаться.
    ///
    /// Файл на набор: `%AppData%\VladTools\links\&lt;имя&gt;.txt`, строка на модель.
    /// Разделитель полей — вертикальная черта: в путях Windows её быть не может,
    /// а в именах облачных моделей — тем более.
    /// </summary>
    internal static class LinkSetLibrary
    {
        private const char Separator = '|';

        private static readonly string[] FileHeader =
        {
            "# Набор связей VladTools — кнопка «Link Manager» (панель «Проект»).",
            "# Строка на модель, поля разделены вертикальной чертой:",
            "#   FILE   | путь к файлу",
            "#   SERVER | RSN://сервер/папка/модель.rvt",
            "#   CLOUD  | регион | GUID проекта | GUID модели | имя модели",
            "# Последним полем можно дописать рабочий набор проекта, в который грузить связь;",
            "# нет его — связь встанет в активный набор, как это делает сам Revit.",
            "# Файл можно править вручную — он перечитывается при каждом открытии окна."
        };

        /// <summary>%AppData%\VladTools\links</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "links");
            }
        }

        /// <summary>Имена сохранённых наборов по алфавиту. Папки ещё нет — пустой список.</summary>
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

        /// <summary>Читает набор. Нет файла или он испорчен — пустой список: сломать этим кнопку нельзя.</summary>
        public static IReadOnlyList<LinkEntry> Load(string setName)
        {
            try
            {
                var file = FilePathFor(setName);
                if (file == null || !File.Exists(file))
                    return new List<LinkEntry>();

                return File.ReadAllLines(file, Encoding.UTF8)
                    .Select(Parse)
                    .Where(entry => entry != null)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<LinkEntry>();
            }
        }

        /// <summary>Перезаписывает набор целиком.</summary>
        public static void Save(string setName, IEnumerable<LinkEntry> entries)
        {
            var file = FilePathFor(setName);
            if (file == null)
                throw new ArgumentException("Имя набора пустое или состоит из недопустимых знаков.");

            var lines = new List<string>(FileHeader) { string.Empty };
            lines.AddRange(entries.Where(entry => entry != null).Select(Format));

            Directory.CreateDirectory(FolderPath);

            // BOM — чтобы кириллица открывалась в «Блокноте» как надо.
            File.WriteAllLines(file, lines, new UTF8Encoding(true));
        }

        public static void Delete(string setName)
        {
            var file = FilePathFor(setName);
            if (file != null && File.Exists(file))
                File.Delete(file);
        }

        /// <summary>Путь к файлу набора; имя пустое или из одних недопустимых знаков — null.</summary>
        public static string FilePathFor(string setName)
        {
            var name = (setName ?? string.Empty).Trim();
            if (name.Length == 0)
                return null;

            foreach (var forbidden in Path.GetInvalidFileNameChars())
                name = name.Replace(forbidden, '_');

            name = name.Trim('_', ' ');

            return name.Length == 0 ? null : Path.Combine(FolderPath, name + ".txt");
        }

        // ───────────────────────────── строка файла ─────────────────────────────

        /// <summary>Запись набора одной строкой файла. Тем же форматом кнопка «Базовый файл»
        /// запоминает последнюю выбранную модель — чтобы не заводить второй.</summary>
        public static string Format(LinkEntry entry)
        {
            var fields = entry.Origin == LinkOrigin.Cloud
                ? new List<string> { "CLOUD", entry.Region, entry.ProjectGuid, entry.ModelGuid, entry.Name }
                : new List<string> { entry.Origin == LinkOrigin.Server ? "SERVER" : "FILE", entry.Path };

            // Рабочий набор — последним и только если он выбран: пустое поле в конце строки
            // ничего не значит, а файл им замусорится.
            if (entry.Workset.Length > 0)
                fields.Add(entry.Workset);

            return string.Join(" " + Separator + " ", fields);
        }

        /// <summary>Разбирает строку, записанную <see cref="Format"/>; мусор и комментарий — null.</summary>
        public static LinkEntry Parse(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return null;

            var parts = text.Split(Separator).Select(part => part.Trim()).ToArray();
            if (parts.Length < 2)
                return null;

            switch (parts[0].ToUpperInvariant())
            {
                case "FILE":
                    return parts[1].Length == 0 ? null : WithWorkset(LinkEntry.ForFile(parts[1]), parts, 2);

                case "SERVER":
                    return parts[1].Length == 0 ? null : WithWorkset(LinkEntry.ForServer(parts[1]), parts, 2);

                case "CLOUD":
                    // Регион, GUID проекта, GUID модели и имя; без любого из GUID строка бессмысленна.
                    if (parts.Length < 4 || parts[2].Length == 0 || parts[3].Length == 0)
                        return null;

                    var name = parts.Length > 4 ? parts[4] : parts[3];
                    return WithWorkset(LinkEntry.ForCloud(parts[1], parts[2], parts[3], name), parts, 5);

                default:
                    return null;
            }
        }

        /// <summary>Дописывает рабочий набор, если он в строке есть: у старых файлов этого поля нет.</summary>
        private static LinkEntry WithWorkset(LinkEntry entry, string[] parts, int index)
        {
            if (parts.Length > index)
                entry.Workset = parts[index];

            return entry;
        }
    }
}
