using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// A folder of models — in any of the three stores at once: a network folder, Revit Server, BIM360.
    ///
    /// Introduced for the "Building Kit": walking the discipline folders requires being able to say
    /// "the contents of this folder" and "the folder one level up" without working out every time where
    /// exactly the models live. The browser tree (<see cref="UI.ModelBrowserWindow"/>) keeps contexts of
    /// its own for that, but they are private and fit for display only: all that was needed there was the way down.
    ///
    /// A cloud folder is addressed by a Data Management id (a URN) rather than a path: it has no path
    /// at all — exactly like a cloud model itself.
    /// </summary>
    internal sealed class ModelFolder
    {
        private ModelFolder(LinkOrigin origin)
        {
            Origin = origin;
        }

        /// <summary>An ordinary folder on disk or on the network.</summary>
        public static ModelFolder ForFile(string path)
        {
            return new ModelFolder(LinkOrigin.File) { Path = path ?? string.Empty };
        }

        /// <summary>A Revit Server folder: <paramref name="path"/> is in service form, "|VSC|3.0_AR".</summary>
        public static ModelFolder ForServer(string server, string path)
        {
            return new ModelFolder(LinkOrigin.Server)
            {
                Server = RevitServerClient.NormalizeServer(server),
                Path = string.IsNullOrEmpty(path) ? RevitServerClient.RootFolder : path
            };
        }

        /// <param name="projectId">The Data Management project id ("b.&lt;GUID&gt;").</param>
        /// <param name="display">The folder name for display: it cannot be recovered from the URN.</param>
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

        /// <summary>The Revit Server name; empty for the others.</summary>
        public string Server { get; private set; } = string.Empty;

        /// <summary>The path: to a folder on disk, or to a server folder in the "|VSC|3.0_AR" form.</summary>
        public string Path { get; private set; } = string.Empty;

        public string Region { get; private set; } = string.Empty;
        public string ProjectId { get; private set; } = string.Empty;
        public string FolderId { get; private set; } = string.Empty;

        /// <summary>The cloud folder name — what is visible in the BIM360 tree.</summary>
        public string CloudName { get; private set; } = string.Empty;

        /// <summary>The folder's own name without the path — it is what identifies the discipline.</summary>
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

        /// <summary>How to show the folder to the user: the whole path, or the cloud folder name.</summary>
        public string Display
        {
            get
            {
                switch (Origin)
                {
                    case LinkOrigin.Cloud:
                        return (CloudName.Length > 0 ? CloudName : "BIM360 folder") + " · " + Region;

                    case LinkOrigin.Server:
                        return RevitServerClient.RsnPath(Server, Path, string.Empty).TrimEnd('/');

                    default:
                        return Path;
                }
            }
        }

        /// <summary>
        /// A folder on a single line — by the same trick as <see cref="LinkSetLibrary.Format"/>: fields
        /// separated by a vertical bar. A Revit Server path is written in the <c>RSN://…</c> form rather
        /// than with the service separator precisely because of that bar — it is the separator there.
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

        /// <summary>Parses a line written by <see cref="Format"/>; garbage yields null.</summary>
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

        /// <summary>The key folders are compared by — the same rule as <see cref="LinkEntry.Key"/>.</summary>
        public string Key => Origin == LinkOrigin.Cloud
            ? "cloud|" + ProjectId.ToLowerInvariant() + "|" + FolderId.ToLowerInvariant()
            : "path|" + Server.ToLowerInvariant() + "|" + Path.Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>An entry in a folder's contents: either a nested folder or a Revit model.</summary>
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

        /// <summary>Filled in on a folder.</summary>
        public ModelFolder Folder { get; }

        /// <summary>Filled in on a model — this is exactly what will go into the link table.</summary>
        public LinkEntry Entry { get; }

        public bool IsFolder => Folder != null;
    }

    /// <summary>
    /// Walking model folders across the three stores. Only "what is inside" and "what is outside" live
    /// here: no filtering and no name parsing — <see cref="ModelKit"/> does that.
    ///
    /// A store failure is let out as an exception with ready-to-show text: whoever walks the tree
    /// decides for themselves whether to stop or to record the folder as unread.
    /// </summary>
    internal static class ModelStore
    {
        /// <summary>A folder's contents: nested folders first, then models.</summary>
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

        /// <summary>The folder one level up; null above the store root.</summary>
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
        /// The name of a cloud folder: all it has is a URN, and something human has to be shown.
        /// It costs a separate request, so it is called once — on the search root.
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
                // The name is decoration; the folder reads fine without it.
                return folder;
            }
        }

        // ───────────────────────────── the stores ─────────────────────────────

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
                throw new InvalidOperationException("The Autodesk session has expired. Sign in to your account in Revit again.");

            return token;
        }
    }
}
