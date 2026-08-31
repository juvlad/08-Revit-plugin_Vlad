using System;
using System.Collections.Generic;
using System.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>Строка содержимого папки Revit Server: вложенная папка или модель.</summary>
    internal sealed class ServerEntry
    {
        public ServerEntry(string name, string folderPath, bool isFolder)
        {
            Name = name;
            FolderPath = folderPath;
            IsFolder = isFolder;
        }

        public string Name { get; }

        /// <summary>Путь папки, в которой лежит запись, в виде «|Проекты|Стадия Р».</summary>
        public string FolderPath { get; }

        public bool IsFolder { get; }

        /// <summary>Путь самой записи в том же виде — им спрашивают содержимое вложенной папки.</summary>
        public string Path => FolderPath == RevitServerClient.RootFolder
            ? RevitServerClient.RootFolder + Name
            : FolderPath + "|" + Name;
    }

    /// <summary>
    /// Просмотр Revit Server. В Revit API папки сервера не листаются вовсе — путь RSN можно
    /// только отдать на загрузку целиком. Зато у самого Revit Server есть служба
    /// RevitServerAdminRESTService&lt;год&gt;, отвечающая обычным JSON по HTTP: ею и пользуемся,
    /// как это делает диалог «Открыть» самого Revit.
    ///
    /// Служба разбирает пути со своим разделителем — вертикальной чертой: корень «|»,
    /// вложенная папка «|Проекты|Стадия Р». Для связи же нужен путь другого вида —
    /// RSN://сервер/Проекты/Стадия Р/модель.rvt; перевод между ними — <see cref="RsnPath"/>.
    ///
    /// Служба требует три заголовка: кто спрашивает, с какой машины и идентификатор операции.
    /// Без них она отвечает отказом, содержимого запроса не разбирая.
    /// </summary>
    internal static class RevitServerClient
    {
        /// <summary>Корневая папка сервера в том виде, в каком её понимает служба.</summary>
        public const string RootFolder = "|";

        /// <summary>
        /// Версия службы совпадает с версией Revit: её имя включает год
        /// (RevitServerAdminRESTService2024), и на чужой год сервер отвечает 404.
        /// Значение выставляет <see cref="App.OnStartup"/> по версии запущенного Revit —
        /// здесь только запасное, на случай если это почему-то не произошло.
        /// </summary>
        public static string ServiceVersion { get; set; } = "2022";

        /// <summary>Убирает «RSN://», слэши и пробелы — пользователь вводит имя сервера как придётся.</summary>
        public static string NormalizeServer(string server)
        {
            var name = (server ?? string.Empty).Trim();

            if (name.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
                name = name.Substring("RSN://".Length);

            return name.Trim('/', '\\', ' ');
        }

        /// <summary>Путь для связи: RSN://сервер/папка/модель.rvt.</summary>
        public static string RsnPath(string server, string folderPath, string modelName)
        {
            var folder = (folderPath ?? RootFolder).Replace('|', '/').Trim('/');
            var tail = folder.Length == 0 ? modelName : folder + "/" + modelName;

            return "RSN://" + NormalizeServer(server) + "/" + tail;
        }

        /// <summary>
        /// Содержимое папки сервера: сначала вложенные папки, потом модели.
        /// Сервер недоступен или ответил отказом — исключение с готовым к показу текстом.
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

            // Модели сервера лежат в Models; Files — вспомогательные файлы, связывать нечего.
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
