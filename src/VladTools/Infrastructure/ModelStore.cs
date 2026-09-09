using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Папка с моделями — в любом из трёх хранилищ сразу: сетевая папка, Revit Server, BIM360.
    ///
    /// Заведена ради «Комплекта по корпусу»: чтобы обойти папки разделов, нужно уметь сказать
    /// «содержимое вот этой папки» и «папка уровнем выше», не разбираясь каждый раз, где именно
    /// модели лежат. Дерево просмотра (<see cref="UI.ModelBrowserWindow"/>) держит для этого
    /// свои контексты, но они частные и годятся только для показа: там нужна была лишь дорога вниз.
    ///
    /// Облачная папка адресуется идентификатором Data Management (URN), а не путём: пути у неё
    /// не существует вовсе — ровно как у самой облачной модели.
    /// </summary>
    internal sealed class ModelFolder
    {
        private ModelFolder(LinkOrigin origin)
        {
            Origin = origin;
        }

        /// <summary>Обычная папка на диске или в сети.</summary>
        public static ModelFolder ForFile(string path)
        {
            return new ModelFolder(LinkOrigin.File) { Path = path ?? string.Empty };
        }

        /// <summary>Папка Revit Server: <paramref name="path"/> — в виде службы, «|VSC|3.0_AR».</summary>
        public static ModelFolder ForServer(string server, string path)
        {
            return new ModelFolder(LinkOrigin.Server)
            {
                Server = RevitServerClient.NormalizeServer(server),
                Path = string.IsNullOrEmpty(path) ? RevitServerClient.RootFolder : path
            };
        }

        /// <param name="projectId">Идентификатор проекта Data Management («b.&lt;GUID&gt;»).</param>
        /// <param name="display">Имя папки для показа: по URN его не восстановить.</param>
        public static ModelFolder ForCloud(string region, string projectId, string folderId, string display)
        {
            return new ModelFolder(LinkOrigin.Cloud)
            {
                Region = region ?? string.Empty,
                ProjectId = projectId ?? string.Empty,
                FolderId = folderId ?? string.Empty,
                CloudName = display ?? string.Empty
            };
        }

        public LinkOrigin Origin { get; private set; }

        /// <summary>Имя сервера Revit Server; у остальных пусто.</summary>
        public string Server { get; private set; } = string.Empty;

        /// <summary>Путь: к папке на диске либо к папке сервера в виде «|VSC|3.0_AR».</summary>
        public string Path { get; private set; } = string.Empty;

        public string Region { get; private set; } = string.Empty;
        public string ProjectId { get; private set; } = string.Empty;
        public string FolderId { get; private set; } = string.Empty;

        /// <summary>Имя облачной папки — то, что видно в дереве BIM360.</summary>
        public string CloudName { get; private set; } = string.Empty;

        /// <summary>Имя самой папки без пути — по нему и опознаётся раздел.</summary>
        public string Name
        {
            get
            {
                switch (Origin)
                {
                    case LinkOrigin.Cloud:
                        return CloudName;

                    case LinkOrigin.Server:
                        return RevitServerClient.FolderName(Path);

                    default:
                        return System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
                }
            }
        }

        /// <summary>Как показать папку пользователю: путь целиком либо имя облачной папки.</summary>
        public string Display
        {
            get
            {
                switch (Origin)
                {
                    case LinkOrigin.Cloud:
                        return (CloudName.Length > 0 ? CloudName : "папка BIM360") + " · " + Region;

                    case LinkOrigin.Server:
                        return RevitServerClient.RsnPath(Server, Path, string.Empty).TrimEnd('/');

                    default:
                        return Path;
                }
            }
        }

        /// <summary>
        /// Папка одной строкой — тем же приёмом, что <see cref="LinkSetLibrary.Format"/>:
        /// поля через вертикальную черту. Путь Revit Server записывается в виде <c>RSN://…</c>,
        /// а не разделителем службы, именно из-за этой черты — она в нём и есть разделитель.
        /// </summary>
        public string Format()
        {
            switch (Origin)
            {
                case LinkOrigin.Server:
                    return string.Join(" | ", "SERVER", Display);

                case LinkOrigin.Cloud:
                    return string.Join(" | ", "CLOUD", Region, ProjectId, FolderId, CloudName);

                default:
                    return string.Join(" | ", "FILE", Path);
            }
        }

        /// <summary>Разбирает строку, записанную <see cref="Format"/>; мусор — null.</summary>
        public static ModelFolder Parse(string line)
        {
            var parts = (line ?? string.Empty).Split('|').Select(part => part.Trim()).ToArray();
            if (parts.Length < 2 || parts[1].Length == 0)
                return null;

            switch (parts[0].ToUpperInvariant())
            {
                case "FILE":
                    return ForFile(parts[1]);

                case "SERVER":
                    string server;
                    string folder;
                    return RevitServerClient.TryParseFolder(parts[1], out server, out folder)
                        ? ForServer(server, folder)
                        : null;

                case "CLOUD":
                    if (parts.Length < 4 || parts[2].Length == 0 || parts[3].Length == 0)
                        return null;

                    return ForCloud(parts[1], parts[2], parts[3], parts.Length > 4 ? parts[4] : string.Empty);

                default:
                    return null;
            }
        }

        /// <summary>Ключ сравнения папок — тем же правилом, что у <see cref="LinkEntry.Key"/>.</summary>
        public string Key => Origin == LinkOrigin.Cloud
            ? "cloud|" + ProjectId.ToLowerInvariant() + "|" + FolderId.ToLowerInvariant()
            : "path|" + Server.ToLowerInvariant() + "|" + Path.Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>Строка содержимого папки: либо вложенная папка, либо модель Revit.</summary>
    internal sealed class StoreItem
    {
        private StoreItem(string name, ModelFolder folder, LinkEntry entry)
        {
            Name = name ?? string.Empty;
            Folder = folder;
            Entry = entry;
        }

        public static StoreItem ForFolder(ModelFolder folder)
        {
            return new StoreItem(folder.Name, folder, null);
        }

        public static StoreItem ForModel(LinkEntry entry)
        {
            return new StoreItem(entry.Name, null, entry);
        }

        public string Name { get; }

        /// <summary>Заполнено у папки.</summary>
        public ModelFolder Folder { get; }

        /// <summary>Заполнено у модели — это и есть то, что уйдёт в таблицу связей.</summary>
        public LinkEntry Entry { get; }

        public bool IsFolder => Folder != null;
    }

    /// <summary>
    /// Обход папок с моделями поверх трёх хранилищ. Здесь только «что внутри» и «что снаружи»:
    /// ни отбора, ни разбора имён — этим занимается <see cref="ModelKit"/>.
    ///
    /// Отказ хранилища наружу выпускается исключением с готовым к показу текстом: тот, кто
    /// обходит дерево, решает сам, прервать работу или записать папку в непрочитанные.
    /// </summary>
    internal static class ModelStore
    {
        /// <summary>Содержимое папки: сначала вложенные папки, потом модели.</summary>
        public static IReadOnlyList<StoreItem> Children(ModelFolder folder)
        {
            if (folder == null)
                return new List<StoreItem>();

            switch (folder.Origin)
            {
                case LinkOrigin.Server:
                    return ServerChildren(folder);

                case LinkOrigin.Cloud:
                    return CloudChildren(folder);

                default:
                    return FileChildren(folder);
            }
        }

        /// <summary>Папка уровнем выше; выше корня хранилища — null.</summary>
        public static ModelFolder Parent(ModelFolder folder)
        {
            if (folder == null)
                return null;

            switch (folder.Origin)
            {
                case LinkOrigin.Server:
                    var above = RevitServerClient.ParentFolder(folder.Path);
                    return above == null ? null : ModelFolder.ForServer(folder.Server, above);

                case LinkOrigin.Cloud:
                    var info = AccClient.Folder(Token(), folder.ProjectId, folder.FolderId);
                    return info.ParentId.Length == 0
                        ? null
                        : ModelFolder.ForCloud(folder.Region, folder.ProjectId, info.ParentId, string.Empty);

                default:
                    var parent = Directory.GetParent(folder.Path.TrimEnd('\\', '/'));
                    return parent == null ? null : ModelFolder.ForFile(parent.FullName);
            }
        }

        /// <summary>
        /// Имя облачной папки: у неё есть только URN, а показать нужно что-то человеческое.
        /// Отдельным запросом, поэтому зовётся один раз — на корень поиска.
        /// </summary>
        public static ModelFolder WithCloudName(ModelFolder folder)
        {
            if (folder == null || folder.Origin != LinkOrigin.Cloud || folder.CloudName.Length > 0)
                return folder;

            try
            {
                var info = AccClient.Folder(Token(), folder.ProjectId, folder.FolderId);
                return ModelFolder.ForCloud(folder.Region, folder.ProjectId, folder.FolderId, info.Name);
            }
            catch (Exception)
            {
                // Имя — украшение; без него папка всё равно читается.
                return folder;
            }
        }

        // ───────────────────────────── хранилища ─────────────────────────────

        private static IReadOnlyList<StoreItem> FileChildren(ModelFolder folder)
        {
            var items = new List<StoreItem>();

            foreach (var path in Directory.GetDirectories(folder.Path).OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase))
                items.Add(StoreItem.ForFolder(ModelFolder.ForFile(path)));

            foreach (var file in Directory.GetFiles(folder.Path, "*.rvt").OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase))
                items.Add(StoreItem.ForModel(LinkEntry.ForFile(file)));

            return items;
        }

        private static string NameOf(string path)
        {
            return System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        }

        private static IReadOnlyList<StoreItem> ServerChildren(ModelFolder folder)
        {
            return RevitServerClient.Contents(folder.Server, folder.Path)
                .Select(entry => entry.IsFolder
                    ? StoreItem.ForFolder(ModelFolder.ForServer(folder.Server, entry.Path))
                    : StoreItem.ForModel(LinkEntry.ForServer(RevitServerClient.RsnPath(folder.Server, folder.Path, entry.Name))))
                .ToList();
        }

        private static IReadOnlyList<StoreItem> CloudChildren(ModelFolder folder)
        {
            var token = Token();

            return AccClient.Contents(token, folder.ProjectId, folder.FolderId)
                .Where(entry => entry.IsFolder || entry.IsCloudModel)
                .Select(entry => entry.IsFolder
                    ? StoreItem.ForFolder(ModelFolder.ForCloud(folder.Region, folder.ProjectId, entry.Id, entry.Name))
                    : StoreItem.ForModel(LinkEntry.ForCloud(
                        folder.Region,
                        entry.ProjectGuid.ToString(),
                        entry.ModelGuid.ToString(),
                        entry.Name)))
                .ToList();
        }

        private static string Token()
        {
            var token = AutodeskSession.Token;
            if (token == null)
                throw new InvalidOperationException("Сеанс Autodesk истёк. Войдите в учётную запись в Revit заново.");

            return token;
        }
    }
}
