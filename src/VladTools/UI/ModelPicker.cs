using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// Выбор моделей Revit из всех четырёх источников: файл с диска, Revit Server,
    /// просмотр BIM360 и ввод пары GUID вручную.
    ///
    /// Вынесено из окна «Link Manager», когда те же четыре кнопки понадобились окну
    /// «Базовый файл». Код не принадлежит ни одному из них: он про то, где взять модель,
    /// а не про то, что с ней делать дальше.
    ///
    /// Теми же деревьями выбирается и папка — для «Комплекта по корпусу». Дорога вниз
    /// одна и та же, поэтому оба режима собираются одним кодом: различается только то,
    /// что уходит наружу.
    /// </summary>
    internal static class ModelPicker
    {
        private static readonly LinkEntry[] Nothing = new LinkEntry[0];

        /// <summary>Обычные файлы .rvt: диск или сетевая папка.</summary>
        public static IReadOnlyList<LinkEntry> Files(Window owner, bool multiple)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = multiple ? "Выберите модели Revit" : "Выберите модель Revit",
                Filter = "Модели Revit (*.rvt)|*.rvt",
                Multiselect = multiple,
                CheckFileExists = true
            };

            if (dialog.ShowDialog(owner) != true)
                return Nothing;

            return dialog.FileNames.Select(LinkEntry.ForFile).ToList();
        }

        /// <summary>
        /// Дерево папок Revit Server. Имя сервера берётся из настроек и пополняется тем,
        /// что пользователь ввёл здесь: набирать его заново в каждом окне незачем.
        /// </summary>
        /// <param name="known">Ключи уже собранных моделей — такие показываются серыми.</param>
        public static IReadOnlyList<LinkEntry> Server(
            Window owner,
            string title,
            LinkPreferences preferences,
            Func<HashSet<string>> known)
        {
            var picker = ServerWindow(owner, title, preferences, known, null);

            return picker.ShowDialog() == true ? picker.Selected : Nothing;
        }

        /// <summary>Та же дорога, но наружу уходит папка: «Комплект по корпусу» спрашивает, где искать.</summary>
        public static ModelFolder PickServerFolder(Window owner, string title, LinkPreferences preferences)
        {
            var picker = ServerWindow(owner, title, preferences, Nobody, node => ToFolder(node) != null);

            return picker.ShowDialog() == true ? ToFolder(picker.SelectedFolder) : null;
        }

        private static ModelBrowserWindow ServerWindow(
            Window owner,
            string title,
            LinkPreferences preferences,
            Func<HashSet<string>> known,
            Func<BrowseNode, bool> pickFolder)
        {
            var serverBox = new TextBox
            {
                MinWidth = 220,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2),
                Margin = new Thickness(0, 0, 8, 0),
                Text = preferences.Servers.FirstOrDefault() ?? string.Empty
            };

            var strip = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            strip.Children.Add(new TextBlock
            {
                Text = "Сервер:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            strip.Children.Add(serverBox);

            var roots = preferences.Servers.Select(ServerRoot).ToList();

            var window = new ModelBrowserWindow(
                "Revit Server",
                "Имя сервера — то же, что в диалоге Revit: без «RSN://» и без слэшей. " +
                "Папки читаются по мере раскрытия, поэтому первое обращение к большому серверу занимает секунду-другую.",
                strip,
                roots,
                node => ExpandServer(node, known),
                pickFolder)
            {
                Owner = owner
            };

            var addServer = new Button
            {
                Content = "Показать сервер",
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            addServer.Click += (s, args) =>
            {
                var server = RevitServerClient.NormalizeServer(serverBox.Text);
                if (server.Length == 0)
                {
                    MessageBox.Show(window, "Впишите имя сервера.", title,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (window.HasRoot(node => string.Equals(node.Name, server, StringComparison.OrdinalIgnoreCase)))
                    return;

                window.AddRoot(ServerRoot(server));
                Remember(preferences.Servers, server);
            };
            strip.Children.Add(addServer);

            return window;
        }

        /// <summary>
        /// Дерево BIM360/ACC под учётной записью, которой пользователь вошёл в сам Revit.
        /// Токена нет — окно не открывается вовсе, а пользователю предлагается ввод по GUID.
        /// </summary>
        public static IReadOnlyList<LinkEntry> Cloud(Window owner, string title, Func<HashSet<string>> known)
        {
            var picker = CloudWindow(owner, title, known, null);

            return picker != null && picker.ShowDialog() == true ? picker.Selected : Nothing;
        }

        /// <summary>Папка BIM360 вместо моделей — для «Комплекта по корпусу».</summary>
        public static ModelFolder PickCloudFolder(Window owner, string title)
        {
            var picker = CloudWindow(owner, title, Nobody, node => ToFolder(node) != null);

            return picker != null && picker.ShowDialog() == true ? ToFolder(picker.SelectedFolder) : null;
        }

        /// <summary>Окно дерева облака; вход в учётную запись не получен — null и объяснение пользователю.</summary>
        private static ModelBrowserWindow CloudWindow(
            Window owner,
            string title,
            Func<HashSet<string>> known,
            Func<BrowseNode, bool> pickFolder)
        {
            var obstacle = AutodeskSession.Obstacle();
            if (obstacle.Length > 0)
            {
                MessageBox.Show(
                    owner,
                    obstacle + "\n\nМодель можно указать парой GUID — кнопка «BIM360 по GUID…».",
                    title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return null;
            }

            IReadOnlyList<AccHub> hubs;
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                hubs = AccClient.Hubs(AutodeskSession.Token);
            }
            catch (Exception exception)
            {
                MessageBox.Show(owner, "Не удалось получить список учётных записей Autodesk.\n\n" + exception.Message,
                    title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }

            var roots = hubs.Select(hub =>
            {
                var node = BrowseNode.Folder(hub.Name, hub);
                node.Note = hub.Region;
                return node;
            }).ToList();

            var user = AutodeskSession.UserName;

            var window = new ModelBrowserWindow(
                "BIM360 / Autodesk Docs",
                "Показано то, что доступно учётной записи, под которой вы вошли в Revit" +
                (user.Length > 0 ? " (" + user + ")" : string.Empty) + ". " +
                "Связать можно только совмещённые модели: обычный .rvt, просто лежащий в папке, в списке не появится.",
                null,
                roots,
                node => ExpandCloud(node, known),
                pickFolder)
            {
                Owner = owner
            };

            return window;
        }

        /// <summary>Ввод облачной модели парой GUID — запасной путь, когда просмотр недоступен.</summary>
        public static IReadOnlyList<LinkEntry> CloudByGuid(Window owner, string region)
        {
            var window = new CloudLinkWindow(region) { Owner = owner };

            return window.ShowDialog() == true ? window.Selected : Nothing;
        }

        /// <summary>
        /// Папка на диске или в сети. Выбирается указанием любой модели внутри неё: своего
        /// диалога выбора папки у WPF нет ни в одном из трёх собираемых годов (в .NET 8 он
        /// появился, в .NET Framework 4.8 — нет), а тащить ради него WinForms в надстройку,
        /// живущую в чужом процессе, — плохой размен.
        /// </summary>
        public static ModelFolder PickFileFolder(Window owner)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Укажите любую модель в нужной папке",
                Filter = "Модели Revit (*.rvt)|*.rvt",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(owner) != true)
                return null;

            var folder = System.IO.Path.GetDirectoryName(dialog.FileName);

            return string.IsNullOrEmpty(folder) ? null : ModelFolder.ForFile(folder);
        }

        /// <summary>
        /// Узел дерева как папка. У учётной записи и проекта BIM360 папки нет: до содержимого
        /// там ещё не добрались, и выбирать нечего — предикат окна на это и опирается.
        /// </summary>
        private static ModelFolder ToFolder(BrowseNode node)
        {
            if (node == null)
                return null;

            var server = node.Context as ServerFolder;
            if (server != null)
                return ModelFolder.ForServer(server.Server, server.Path);

            var cloud = node.Context as CloudFolder;
            if (cloud != null && cloud.FolderId != null)
                return ModelFolder.ForCloud(cloud.Project.Region, cloud.Project.Id, cloud.FolderId, node.Name);

            return null;
        }

        /// <summary>Список уже собранных моделей в режиме выбора папки не нужен: серым красить нечего.</summary>
        private static HashSet<string> Nobody()
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>Запоминает введённое значение первым в списке, без повторов.</summary>
        public static void Remember(List<string> values, string value)
        {
            values.RemoveAll(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
            values.Insert(0, value);
        }

        // ───────────────────────────── что раскрывать дальше ─────────────────────────────

        private static BrowseNode ServerRoot(string server)
        {
            var name = RevitServerClient.NormalizeServer(server);
            var node = BrowseNode.Folder(name, new ServerFolder(name, RevitServerClient.RootFolder));
            node.Note = "Revit Server";

            return node;
        }

        private static IReadOnlyList<BrowseNode> ExpandServer(BrowseNode node, Func<HashSet<string>> known)
        {
            var folder = node.Context as ServerFolder;
            if (folder == null)
                return new List<BrowseNode>();

            var keys = known();

            return RevitServerClient.Contents(folder.Server, folder.Path)
                .Select(entry => entry.IsFolder
                    ? BrowseNode.Folder(entry.Name, new ServerFolder(folder.Server, entry.Path))
                    : ModelNode(LinkEntry.ForServer(RevitServerClient.RsnPath(folder.Server, folder.Path, entry.Name)), keys))
                .ToList();
        }

        private static IReadOnlyList<BrowseNode> ExpandCloud(BrowseNode node, Func<HashSet<string>> known)
        {
            var token = AutodeskSession.Token;
            if (token == null)
                throw new InvalidOperationException("Сеанс Autodesk истёк. Войдите в учётную запись в Revit заново.");

            var hub = node.Context as AccHub;
            if (hub != null)
            {
                return AccClient.Projects(token, hub)
                    .Select(project => BrowseNode.Folder(project.Name, new CloudFolder(hub, project, null)))
                    .ToList();
            }

            var folder = node.Context as CloudFolder;
            if (folder == null)
                return new List<BrowseNode>();

            if (folder.FolderId == null)
            {
                return AccClient.TopFolders(token, folder.Hub, folder.Project)
                    .Select(entry => BrowseNode.Folder(entry.Name, new CloudFolder(folder.Hub, folder.Project, entry.Id)))
                    .ToList();
            }

            var keys = known();

            return AccClient.Contents(token, folder.Project, folder.FolderId)
                .Where(entry => entry.IsFolder || entry.IsCloudModel)
                .Select(entry => entry.IsFolder
                    ? BrowseNode.Folder(entry.Name, new CloudFolder(folder.Hub, folder.Project, entry.Id))
                    : ModelNode(
                        LinkEntry.ForCloud(
                            folder.Project.Region,
                            entry.ProjectGuid.ToString(),
                            entry.ModelGuid.ToString(),
                            entry.Name),
                        keys))
                .ToList();
        }

        /// <summary>Модель в дереве: уже собранную показываем серой и отметить не даём.</summary>
        private static BrowseNode ModelNode(LinkEntry entry, HashSet<string> known)
        {
            var isKnown = known.Contains(entry.Key);
            return BrowseNode.Model(entry, isKnown ? "уже в списке" : string.Empty, !isKnown);
        }

        /// <summary>Папка на Revit Server: сервер плюс путь со своим разделителем.</summary>
        private sealed class ServerFolder
        {
            public ServerFolder(string server, string path)
            {
                Server = server;
                Path = path;
            }

            public string Server { get; }
            public string Path { get; }
        }

        /// <summary>Место в облаке: учётная запись, проект и папка. Папка не задана — это сам проект.</summary>
        private sealed class CloudFolder
        {
            public CloudFolder(AccHub hub, AccProject project, string folderId)
            {
                Hub = hub;
                Project = project;
                FolderId = folderId;
            }

            public AccHub Hub { get; }
            public AccProject Project { get; }
            public string FolderId { get; }
        }
    }
}
