using System;
using System.Collections.Generic;
using System.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>A BIM360/ACC account (hub). The region is needed to build the path to a cloud model.</summary>
    internal sealed class AccHub
    {
        public AccHub(string id, string name, string region)
        {
            Id = id;
            Name = name;
            Region = region;
        }

        public string Id { get; }
        public string Name { get; }

        /// <summary>"US", "EMEA" and so on — as Autodesk named it; Revit accepts the very same string.</summary>
        public string Region { get; }
    }

    /// <summary>A project inside an account.</summary>
    internal sealed class AccProject
    {
        public AccProject(string id, string name, string region)
        {
            Id = id;
            Name = name;
            Region = region;
        }

        public string Id { get; }
        public string Name { get; }
        public string Region { get; }
    }

    /// <summary>
    /// An entry in a folder's contents: either a nested folder or a Revit model.
    /// A model has both GUIDs filled in — the link path is built from them.
    /// </summary>
    internal sealed class AccEntry
    {
        public AccEntry(string id, string name, bool isFolder, Guid projectGuid, Guid modelGuid)
        {
            Id = id;
            Name = name;
            IsFolder = isFolder;
            ProjectGuid = projectGuid;
            ModelGuid = modelGuid;
        }

        public string Id { get; }
        public string Name { get; }
        public bool IsFolder { get; }

        public Guid ProjectGuid { get; }
        public Guid ModelGuid { get; }

        /// <summary>The model is workshared (C4R) — that is the only kind Revit can link from the cloud.</summary>
        public bool IsCloudModel => !IsFolder && ProjectGuid != Guid.Empty && ModelGuid != Guid.Empty;
    }

    /// <summary>
    /// The folder itself: what it is called and which folder holds it. Needed for climbing the tree
    /// upwards — from the folder holding the open model to the project folder with the discipline folders.
    /// </summary>
    internal sealed class AccFolder
    {
        public AccFolder(string id, string name, string parentId)
        {
            Id = id;
            Name = name;
            ParentId = parentId;
        }

        public string Id { get; }
        public string Name { get; }

        /// <summary>The folder one level up; empty on the root folder.</summary>
        public string ParentId { get; }
    }

    /// <summary>
    /// Reading the BIM360/ACC tree through Autodesk Platform Services (the Data Management API):
    /// accounts → projects → root folders → folder contents.
    ///
    /// The token comes from Revit itself (<see cref="AutodeskSession"/>), so neither registering an
    /// application in APS nor a separate sign-in window is needed — it works under the account the
    /// user is already signed in with inside Revit.
    ///
    /// Only a workshared model (C4R) can be linked from the cloud: the response carries a projectGuid
    /// and a modelGuid for it, and Revit asks for nothing else. A plain .rvt merely dropped into an
    /// ACC folder has no such pair and does not make it into the list.
    /// </summary>
    internal static class AccClient
    {
        private const string Api = "https://developer.api.autodesk.com";

        /// <summary>How many response pages we are willing to leaf through: a guard against a folder with tens of thousands of files.</summary>
        private const int PageLimit = 20;

        public static IReadOnlyList<AccHub> Hubs(string token)
        {
            var hubs = new List<AccHub>();

            foreach (var item in Pages(Api + "/project/v1/hubs", token))
            {
                var id = Json.Str(item, "id");
                if (id.Length == 0)
                    continue;

                hubs.Add(new AccHub(
                    id,
                    Json.Str(item, "attributes", "name"),
                    Json.Str(item, "attributes", "region")));
            }

            return hubs.OrderBy(hub => hub.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static IReadOnlyList<AccProject> Projects(string token, AccHub hub)
        {
            var projects = new List<AccProject>();
            var url = Api + "/project/v1/hubs/" + Escape(hub.Id) + "/projects";

            foreach (var item in Pages(url, token))
            {
                var id = Json.Str(item, "id");
                if (id.Length == 0)
                    continue;

                // The project region is more precise than the account region, but it is not always there.
                var region = Json.Str(item, "attributes", "extension", "data", "region");
                if (region.Length == 0)
                    region = hub.Region;

                projects.Add(new AccProject(id, Json.Str(item, "attributes", "name"), region));
            }

            return projects.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>The project root folders — what the web shows as "Project Files", "Plans" and so on.</summary>
        public static IReadOnlyList<AccEntry> TopFolders(string token, AccHub hub, AccProject project)
        {
            var url = Api + "/project/v1/hubs/" + Escape(hub.Id) + "/projects/" + Escape(project.Id) + "/topFolders";

            return Pages(url, token)
                .Select(item => new AccEntry(Json.Str(item, "id"), DisplayName(item), true, Guid.Empty, Guid.Empty))
                .Where(entry => entry.Id.Length > 0)
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// A folder's contents: nested folders and Revit models.
        /// The model GUIDs are taken from the "included" section of the same response — the latest file
        /// versions live there. A separate request per model is not needed, and on a folder of a hundred
        /// files that is the difference between a second and a minute.
        /// </summary>
        public static IReadOnlyList<AccEntry> Contents(string token, AccProject project, string folderId)
        {
            return Contents(token, project.Id, folderId);
        }

        /// <summary>
        /// The same, given a project id alone. It is also assembled from the GUID of the open cloud
        /// model (<see cref="ProjectId"/>) when the project is known but the hub is not: there is no
        /// point in walking the whole storeroom just for the hub name.
        /// </summary>
        public static IReadOnlyList<AccEntry> Contents(string token, string projectId, string folderId)
        {
            var url = Api + "/data/v1/projects/" + Escape(projectId) + "/folders/" + Escape(folderId) + "/contents";

            var folders = new List<AccEntry>();
            var models = new List<AccEntry>();

            foreach (var page in RawPages(url, token))
            {
                var versions = VersionsByItem(page);

                foreach (var item in Json.Items(page, "data"))
                {
                    var id = Json.Str(item, "id");
                    var name = DisplayName(item);
                    if (id.Length == 0 || name.Length == 0)
                        continue;

                    if (string.Equals(Json.Str(item, "type"), "folders", StringComparison.Ordinal))
                    {
                        folders.Add(new AccEntry(id, name, true, Guid.Empty, Guid.Empty));
                        continue;
                    }

                    if (!name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                        continue;

                    object version;
                    if (!versions.TryGetValue(id, out version))
                        continue;

                    models.Add(new AccEntry(
                        id,
                        name,
                        false,
                        Parse(Json.Str(version, "attributes", "extension", "data", "projectGuid")),
                        Parse(Json.Str(version, "attributes", "extension", "data", "modelGuid"))));
                }
            }

            return folders.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .Concat(models.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Information about the folder itself: its name and the folder one level up. Data Management
        /// returns both in a single request, and that is the only way up the tree — downwards there are
        /// contents and topFolders, upwards there is nothing.
        /// </summary>
        public static AccFolder Folder(string token, string projectId, string folderId)
        {
            var url = Api + "/data/v1/projects/" + Escape(projectId) + "/folders/" + Escape(folderId);
            var body = Http.GetJson(url, new Dictionary<string, string> { { "Authorization", "Bearer " + token } });
            var data = Json.At(body, "data");

            var name = Json.Str(data, "attributes", "displayName");
            if (name.Length == 0)
                name = Json.Str(data, "attributes", "name");

            return new AccFolder(Json.Str(data, "id"), name, Json.Str(data, "relationships", "parent", "data", "id"));
        }

        /// <summary>
        /// The Data Management project id from the project GUID of a Revit cloud path: in BIM360/ACC it
        /// is the same GUID with a "b." prefix. It checks itself — with a wrong id the service refuses,
        /// and the button offers picking the folder by hand.
        /// </summary>
        public static string ProjectId(string projectGuid)
        {
            var guid = (projectGuid ?? string.Empty).Trim();

            return guid.Length == 0 || guid.StartsWith("b.", StringComparison.OrdinalIgnoreCase) ? guid : "b." + guid;
        }

        // ───────────────────────────── parsing the response ─────────────────────────────

        /// <summary>The latest versions of the folder's files, keyed by the file id itself.</summary>
        private static Dictionary<string, object> VersionsByItem(object page)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var included in Json.Items(page, "included"))
            {
                var itemId = Json.Str(included, "relationships", "item", "data", "id");
                if (itemId.Length > 0 && !map.ContainsKey(itemId))
                    map[itemId] = included;
            }

            return map;
        }

        private static string DisplayName(object item)
        {
            var name = Json.Str(item, "attributes", "displayName");
            return name.Length > 0 ? name : Json.Str(item, "attributes", "name");
        }

        private static Guid Parse(string text)
        {
            Guid value;
            return Guid.TryParse(text, out value) ? value : Guid.Empty;
        }

        // ───────────────────────────── requests ─────────────────────────────

        private static IEnumerable<object> Pages(string url, string token)
        {
            return RawPages(url, token).SelectMany(page => Json.Items(page, "data"));
        }

        /// <summary>
        /// Data Management responses come in pages: the next one is at links.next.href.
        /// We follow them while a link exists, but no more than <see cref="PageLimit"/> times.
        /// </summary>
        private static IEnumerable<object> RawPages(string url, string token)
        {
            var headers = new Dictionary<string, string> { { "Authorization", "Bearer " + token } };

            for (var page = 0; page < PageLimit && !string.IsNullOrEmpty(url); page++)
            {
                var body = Http.GetJson(url, headers);
                yield return body;

                var next = Json.Str(body, "links", "next", "href");
                url = next.Length > 0 ? next : null;
            }
        }

        private static string Escape(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }
    }
}
