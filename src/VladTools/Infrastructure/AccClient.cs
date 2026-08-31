using System;
using System.Collections.Generic;
using System.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>Учётная запись (hub) BIM360/ACC. Регион нужен, чтобы собрать путь к облачной модели.</summary>
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

        /// <summary>«US», «EMEA» и т. п. — как его назвал Autodesk; Revit принимает эту же строку.</summary>
        public string Region { get; }
    }

    /// <summary>Проект внутри учётной записи.</summary>
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
    /// Строка содержимого папки: либо вложенная папка, либо модель Revit.
    /// У модели заполнены оба GUID — из них собирается путь для связи.
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

        /// <summary>Модель совмещённая (C4R) — только такую Revit умеет связать из облака.</summary>
        public bool IsCloudModel => !IsFolder && ProjectGuid != Guid.Empty && ModelGuid != Guid.Empty;
    }

    /// <summary>
    /// Чтение дерева BIM360/ACC через Autodesk Platform Services (Data Management API):
    /// учётные записи → проекты → корневые папки → содержимое папок.
    ///
    /// Токен берётся у самого Revit (<see cref="AutodeskSession"/>), поэтому ни регистрации
    /// приложения в APS, ни отдельного окна входа не нужно — работает та учётная запись,
    /// под которой пользователь уже сидит в Revit.
    ///
    /// Связать из облака можно только совмещённую модель (C4R): у неё в ответе есть
    /// projectGuid и modelGuid, а больше Revit ничего и не спрашивает. Обычный .rvt,
    /// просто положенный в папку ACC, такой пары не имеет и в список не попадает.
    /// </summary>
    internal static class AccClient
    {
        private const string Api = "https://developer.api.autodesk.com";

        /// <summary>Сколько страниц ответа готовы пролистать: защита от папки на десятки тысяч файлов.</summary>
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

                // Регион проекта точнее региона учётной записи, но есть не всегда.
                var region = Json.Str(item, "attributes", "extension", "data", "region");
                if (region.Length == 0)
                    region = hub.Region;

                projects.Add(new AccProject(id, Json.Str(item, "attributes", "name"), region));
            }

            return projects.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Корневые папки проекта — то, что в вебе видно как «Project Files», «Plans» и т. п.</summary>
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
        /// Содержимое папки: вложенные папки и модели Revit.
        /// GUID моделей берутся из раздела included того же ответа — там лежат последние версии
        /// файлов. Отдельный запрос за каждой моделью не нужен, а на папке в сотню файлов
        /// это разница между секундой и минутой.
        /// </summary>
        public static IReadOnlyList<AccEntry> Contents(string token, AccProject project, string folderId)
        {
            var url = Api + "/data/v1/projects/" + Escape(project.Id) + "/folders/" + Escape(folderId) + "/contents";

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

        // ───────────────────────────── разбор ответа ─────────────────────────────

        /// <summary>Последние версии файлов папки, разложенные по идентификатору самого файла.</summary>
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

        // ───────────────────────────── запросы ─────────────────────────────

        private static IEnumerable<object> Pages(string url, string token)
        {
            return RawPages(url, token).SelectMany(page => Json.Items(page, "data"));
        }

        /// <summary>
        /// Ответы Data Management разбиты на страницы: следующая лежит в links.next.href.
        /// Идём по ним, пока есть ссылка, но не больше <see cref="PageLimit"/> раз.
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
