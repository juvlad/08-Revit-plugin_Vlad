using System;
using System.Collections.Generic;
using System.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>An entry in the contents of a Revit Server folder: a nested folder or a model.</summary>
    internal sealed class ServerEntry
    {
        public ServerEntry(string name, string folderPath, bool isFolder)
        {
            Name = name;
            FolderPath = folderPath;
            IsFolder = isFolder;
        }

        public string Name { get; }

        /// <summary>The path of the folder holding the entry, in the "|Projects|Stage D" form.</summary>
        public string FolderPath { get; }

        public bool IsFolder { get; }

        /// <summary>The path of the entry itself in the same form — used to ask for a nested folder's contents.</summary>
        public string Path => FolderPath == RevitServerClient.RootFolder
            ? RevitServerClient.RootFolder + Name
            : FolderPath + "|" + Name;
    }

    /// <summary>
    /// Browsing Revit Server. The Revit API does not list server folders at all — an RSN path can only
    /// be handed over for loading as a whole. Revit Server itself, however, has a
    /// RevitServerAdminRESTService&lt;year&gt; service that answers with plain JSON over HTTP: that is
    /// what we use, exactly as Revit's own "Open" dialog does.
    ///
    /// The service parses paths with a separator of its own — the vertical bar: the root is "|", a
    /// nested folder "|Projects|Stage D". A link, though, needs a path of a different shape —
    /// RSN://server/Projects/Stage D/model.rvt; <see cref="RsnPath"/> translates between them.
    ///
    /// The service demands three headers: who is asking, from which machine, and an operation id.
    /// Without them it refuses without even parsing the request.
    /// </summary>
    internal static class RevitServerClient
    {
        /// <summary>The server root folder in the form the service understands.</summary>
        public const string RootFolder = "|";

        /// <summary>
        /// The service version matches the Revit version: its name includes the year
        /// (RevitServerAdminRESTService2024), and the server answers 404 for the wrong year.
        /// <see cref="App.OnStartup"/> sets the value from the version of the running Revit —
        /// what is here is only the fallback, in case that did not happen for some reason.
        /// </summary>
        public static string ServiceVersion { get; set; } = "2022";

        /// <summary>Strips "RSN://", slashes and spaces — the user types the server name however they please.</summary>
        public static string NormalizeServer(string server)
        {
            var name = (server ?? string.Empty).Trim();

            if (name.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
                name = name.Substring("RSN://".Length);

            return name.Trim('/', '\\', ' ');
        }

        /// <summary>The path for a link: RSN://server/folder/model.rvt.</summary>
        public static string RsnPath(string server, string folderPath, string modelName)
        {
            var folder = (folderPath ?? RootFolder).Replace('|', '/').Trim('/');
            var tail = folder.Length == 0 ? modelName : folder + "/" + modelName;

            return "RSN://" + NormalizeServer(server) + "/" + tail;
        }

        /// <summary>
        /// Splits a link path <c>RSN://server/VSC/3.0_AR/model.rvt</c> into parts: the server name, the
        /// folder path in the form the service understands (<c>|VSC|3.0_AR</c>), and the model name.
        /// The inverse of <see cref="RsnPath"/>: given the path of an existing link or of the open
        /// project itself, we have to be able to get back to the folder holding it.
        /// </summary>
        public static bool TryParse(string rsnPath, out string server, out string folderPath, out string modelName)
        {
            server = string.Empty;
            folderPath = RootFolder;
            modelName = string.Empty;

            var text = (rsnPath ?? string.Empty).Trim();
            if (!text.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
                return false;

            var parts = text.Substring("RSN://".Length)
                .Split('/')
                .Where(part => part.Length > 0)
                .ToList();

            if (parts.Count == 0)
                return false;

            server = parts[0];
            modelName = parts.Count > 1 ? parts[parts.Count - 1] : string.Empty;

            // Between the server and the model are the folders; those we join with the service separator.
            var folders = parts.Skip(1).Take(Math.Max(0, parts.Count - 2)).ToList();
            folderPath = folders.Count == 0 ? RootFolder : RootFolder + string.Join("|", folders);

            return true;
        }

        /// <summary>
        /// Splits a **folder** path — <c>RSN://server/VSC/3.0_AR</c> — into the server name and the path
        /// in service form. Separate from <see cref="TryParse"/>: there the last piece of the path counts
        /// as the model, here it is a folder like all the others.
        /// </summary>
        public static bool TryParseFolder(string rsnPath, out string server, out string folderPath)
        {
            server = string.Empty;
            folderPath = RootFolder;

            var text = (rsnPath ?? string.Empty).Trim();
            if (!text.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
                return false;

            var parts = text.Substring("RSN://".Length)
                .Split('/')
                .Where(part => part.Length > 0)
                .ToList();

            if (parts.Count == 0)
                return false;

            server = parts[0];
            folderPath = parts.Count == 1 ? RootFolder : RootFolder + string.Join("|", parts.Skip(1));

            return true;
        }

        /// <summary>The folder holding this one: <c>|VSC|3.0_AR</c> → <c>|VSC</c>. Above the root — null.</summary>
        public static string ParentFolder(string folderPath)
        {
            var path = (folderPath ?? RootFolder).Trim();
            if (path.Length <= 1)
                return null;

            var cut = path.LastIndexOf('|');

            return cut <= 0 ? RootFolder : path.Substring(0, cut);
        }

        /// <summary>The folder's own name without the path: <c>|VSC|3.0_AR</c> → <c>3.0_AR</c>; empty at the root.</summary>
        public static string FolderName(string folderPath)
        {
            var path = (folderPath ?? RootFolder).Trim();

            return path.Length <= 1 ? string.Empty : path.Substring(path.LastIndexOf('|') + 1);
        }

        /// <summary>
        /// The contents of a server folder: nested folders first, then models.
        /// If the server is unreachable or refuses, an exception with ready-to-show text is raised.
        /// </summary>
        public static IReadOnlyList<ServerEntry> Contents(string server, string folderPath)
        {
            var path = string.IsNullOrEmpty(folderPath) ? RootFolder : folderPath;

            var url = "http://" + NormalizeServer(server) +
                      "/RevitServerAdminRESTService" + ServiceVersion +
                      "/AdminRESTService.svc/" + Uri.EscapeDataString(path) + "/contents";

            var body = Http.GetJson(url, Headers());

            var folders = Json.Items(body, "Folders")
                .Select(item => Json.Str(item, "Name"))
                .Where(name => name.Length > 0)
                .Select(name => new ServerEntry(name, path, true));

            // Server models live under Models; Files holds auxiliary files, nothing to link there.
            var models = Json.Items(body, "Models")
                .Select(item => Json.Str(item, "Name"))
                .Where(name => name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                .Select(name => new ServerEntry(name, path, false));

            return folders.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .Concat(models.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
                .ToList();
        }

        private static IDictionary<string, string> Headers()
        {
            return new Dictionary<string, string>
            {
                { "User-Name", Environment.UserName },
                { "User-Machine-Name", Environment.MachineName },
                { "Operation-GUID", Guid.NewGuid().ToString() }
            };
        }
    }
}
