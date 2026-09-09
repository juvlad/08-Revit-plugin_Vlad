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
    /// Picking Revit models from all four sources: a file on disk, Revit Server, browsing
    /// BIM360, and typing in a pair of GUIDs by hand.
    ///
    /// Extracted from the "Link Manager" window once the "Base File" window needed the same
    /// four buttons. The code belongs to neither: it is about where to get a model from, not
    /// about what to do with it next.
    ///
    /// The same trees are used to pick a folder too — for the "Building Kit". The way down is
    /// the same either way, so both modes are built from one piece of code: only what leaves
    /// at the end differs.
    /// </summary>
    internal static class ModelPicker
    {
        private static readonly LinkEntry[] Nothing = new LinkEntry[0];

        /// <summary>Ordinary .rvt files: a disk or a network folder.</summary>
        public static IReadOnlyList<LinkEntry> Files(Window owner, bool multiple)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = multiple ? "Choose Revit models" : "Choose a Revit model",
                Filter = "Revit models (*.rvt)|*.rvt",
                Multiselect = multiple,
                CheckFileExists = true
            };

            if (dialog.ShowDialog(owner) != true)
                return Nothing;

            return dialog.FileNames.Select(LinkEntry.ForFile).ToList();
        }

        /// <summary>
        /// The Revit Server folder tree. The server name comes from the settings and is
        /// extended with whatever the user types in here: no reason to type it in again
        /// in every window.
        /// </summary>
        /// <param name="known">Keys of the models already gathered — those are shown greyed out.</param>
        public static IReadOnlyList<LinkEntry> Server(
            Window owner,
            string title,
            LinkPreferences preferences,
            Func<HashSet<string>> known)
        {
            var picker = ServerWindow(owner, title, preferences, known, null);

            return picker.ShowDialog() == true ? picker.Selected : Nothing;
        }

        /// <summary>The same path, but a folder leaves at the end: the "Building Kit" asks where to search.</summary>
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
                Text = "Server:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            strip.Children.Add(serverBox);

            var roots = preferences.Servers.Select(ServerRoot).ToList();

            var window = new ModelBrowserWindow(
                "Revit Server",
                "The server name — the same one you'd type in the Revit dialog: without \"RSN://\" and " +
                "without slashes. Folders are read as they are expanded, so the first request to a large " +
                "server takes a second or two.",
                strip,
                roots,
                node => ExpandServer(node, known),
                pickFolder)
            {
                Owner = owner
            };

            var addServer = new Button
            {
                Content = "Show server",
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            addServer.Click += (s, args) =>
            {
                var server = RevitServerClient.NormalizeServer(serverBox.Text);
                if (server.Length == 0)
                {
                    MessageBox.Show(window, "Type in the server name.", title,
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
        /// The BIM360/ACC tree under the account the user is signed in with inside Revit itself.
        /// No token — the window does not open at all, and the user is offered GUID entry instead.
        /// </summary>
        public static IReadOnlyList<LinkEntry> Cloud(Window owner, string title, Func<HashSet<string>> known)
        {
            var picker = CloudWindow(owner, title, known, null);

            return picker != null && picker.ShowDialog() == true ? picker.Selected : Nothing;
        }

        /// <summary>A BIM360 folder instead of models — for the "Building Kit".</summary>
        public static ModelFolder PickCloudFolder(Window owner, string title)
        {
            var picker = CloudWindow(owner, title, Nobody, node => ToFolder(node) != null);

            return picker != null && picker.ShowDialog() == true ? ToFolder(picker.SelectedFolder) : null;
        }

        /// <summary>The cloud tree window; if signing in was never obtained — null and an explanation for the user.</summary>
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
                    obstacle + "\n\nThe model can be given as a pair of GUIDs — the \"BIM360 by GUID…\" button.",
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
                MessageBox.Show(owner, "Could not get the list of Autodesk accounts.\n\n" + exception.Message,
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
                "Shown is what is available to the account you are signed in to Revit with" +
                (user.Length > 0 ? " (" + user + ")" : string.Empty) + ". " +
                "Only workshared models can be linked: a plain .rvt just sitting in a folder will not appear in the list.",
                null,
                roots,
                node => ExpandCloud(node, known),
                pickFolder)
            {
                Owner = owner
            };

            return window;
        }

        /// <summary>Entering a cloud model as a pair of GUIDs — the fallback when browsing is unavailable.</summary>
        public static IReadOnlyList<LinkEntry> CloudByGuid(Window owner, string region)
        {
            var window = new CloudLinkWindow(region) { Owner = owner };

            return window.ShowDialog() == true ? window.Selected : Nothing;
        }

        /// <summary>
        /// A folder on disk or on the network. It is chosen by pointing at any model inside it: WPF
        /// has no folder-picker dialog of its own in any of the three years this add-in is built for
        /// (it exists in .NET 8, not in .NET Framework 4.8), and dragging in WinForms just for that,
        /// in an add-in that lives inside somebody else's process, is a bad trade.
        /// </summary>
        public static ModelFolder PickFileFolder(Window owner)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Point to any model in the folder you need",
                Filter = "Revit models (*.rvt)|*.rvt",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(owner) != true)
                return null;

            var folder = System.IO.Path.GetDirectoryName(dialog.FileName);

            return string.IsNullOrEmpty(folder) ? null : ModelFolder.ForFile(folder);
        }

        /// <summary>
        /// A tree node as a folder. A BIM360 account or project has no folder: its contents have not
        /// been reached yet, and there is nothing to choose — the window's predicate relies on exactly that.
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

        /// <summary>In folder-picking mode there is no list of models already gathered — nothing to grey out.</summary>
        private static HashSet<string> Nobody()
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>Remembers the entered value as the first in the list, without duplicates.</summary>
        public static void Remember(List<string> values, string value)
        {
            values.RemoveAll(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
            values.Insert(0, value);
        }

        // ───────────────────────────── what to expand next ─────────────────────────────

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
                throw new InvalidOperationException("The Autodesk session has expired. Sign in to your account in Revit again.");

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

        /// <summary>A model in the tree: one already gathered is shown greyed out and cannot be checked.</summary>
        private static BrowseNode ModelNode(LinkEntry entry, HashSet<string> known)
        {
            var isKnown = known.Contains(entry.Key);
            return BrowseNode.Model(entry, isKnown ? "already in the list" : string.Empty, !isKnown);
        }

        /// <summary>A folder on Revit Server: the server plus a path with its own separator.</summary>
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

        /// <summary>A place in the cloud: the account, the project and the folder. No folder given means the project itself.</summary>
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
