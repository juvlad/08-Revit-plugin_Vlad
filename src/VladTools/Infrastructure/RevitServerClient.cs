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
        /// Разбирает путь связи <c>RSN://сервер/VSC/3.0_AR/модель.rvt</c> на части: имя сервера,
        /// путь папки в том виде, в каком его понимает служба (<c>|VSC|3.0_AR</c>), и имя модели.
        /// Обратное к <see cref="RsnPath"/>: по пути уже стоящей связи или самого открытого проекта
        /// надо уметь вернуться к папке, в которой он лежит.
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

            // Между сервером и моделью — папки; их и склеиваем разделителем службы.
            var folders = parts.Skip(1).Take(Math.Max(0, parts.Count - 2)).ToList();
            folderPath = folders.Count == 0 ? RootFolder : RootFolder + string.Join("|", folders);

            return true;
        }

        /// <summary>
        /// Разбирает путь **папки** — <c>RSN://сервер/VSC/3.0_AR</c> — на имя сервера и путь
        /// в виде службы. Отдельно от <see cref="TryParse"/>: там последний кусок пути считается
        /// моделью, здесь он такая же папка, как остальные.
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

        /// <summary>Папка, в которой лежит эта: <c>|VSC|3.0_AR</c> → <c>|VSC</c>. Выше корня — null.</summary>
        public static string ParentFolder(string folderPath)
        {
            var path = (folderPath ?? RootFolder).Trim();
            if (path.Length <= 1)
                return null;

            var cut = path.LastIndexOf('|');

            return cut <= 0 ? RootFolder : path.Substring(0, cut);
        }

        /// <summary>Имя самой папки без пути: <c>|VSC|3.0_AR</c> → <c>3.0_AR</c>; у корня — пусто.</summary>
        public static string FolderName(string folderPath)
        {
            var path = (folderPath ?? RootFolder).Trim();

            return path.Length <= 1 ? string.Empty : path.Substring(path.LastIndexOf('|') + 1);
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
