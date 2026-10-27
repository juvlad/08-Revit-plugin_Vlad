using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// "Upload to Server": the models to put on Revit Server, the folder they go into, and whether their
    /// links are pointed at the server afterwards.
    ///
    /// A file in the list is uploaded; a Revit Server model in it only has its links updated — that is how
    /// a run that broke off is finished, or a model uploaded by hand is brought in line. BIM360 is not
    /// offered as a source: a cloud model has to be downloaded first.
    ///
    /// The destination folder is read when it is chosen (the window reads Revit Server itself, as "Link
    /// Updater" does — plain HTTP, no Revit API), so the window can say before the run which models are
    /// already there; "New folder…" creates one inside it.
    ///
    /// The window knows nothing about the Revit API: it hands back the checked models, the folder and the
    /// two choices.
    /// </summary>
    internal sealed class ServerUploadWindow : Window
    {
        private const string WindowTitle = "Upload to Server";

        private readonly ServerUploadPreferences _preferences;
        private readonly LinkPreferences _linkPreferences;

        private readonly ModelListPanel _models;
        private readonly TextBlock _folderText;
        private readonly CheckBox _linksBox;
        private readonly CheckBox _subfoldersBox;
        private readonly ComboBox _existingBox;
        private readonly TextBlock _status;
        private readonly Button _runButton;

        private ModelFolder _destination;

        /// <summary>The destination and the folders below it, read as the summary needs them; null — not read.</summary>
        private ServerFolderCache _folders;

        /// <summary>The models in the destination folder, by name; null — not read (no folder, or it failed).</summary>
        private ISet<string> _onServer;

        /// <summary>The folders in the destination folder, by name — for "New folder…".</summary>
        private ISet<string> _subfolders;

        private string _folderError = string.Empty;

        /// <summary>A subfolder below the destination that could not be read — said in the summary, not blocking.</summary>
        private string _subfolderError = string.Empty;

        /// <summary>The models the user ran the upload on, in the table's order.</summary>
        public IReadOnlyList<BatchModelRow> Selected { get; private set; } = new List<BatchModelRow>();

        /// <summary>The Revit Server folder the files go into.</summary>
        public ModelFolder Destination => _destination;

        /// <summary>Point the links of every uploaded model, and of the server models in the list, at the server.</summary>
        public bool UpdateLinks => _linksBox.IsChecked == true;

        /// <summary>A model of the same name already in the folder is written over, rather than left as it is.</summary>
        public bool Replace => _existingBox.SelectedIndex == 1;

        /// <summary>Each file goes as far below the destination as it lies below the folder the files share.</summary>
        public bool KeepFolders => _subfoldersBox.IsChecked == true;

        public ServerUploadWindow(ServerUploadPreferences preferences, LinkPreferences linkPreferences)
        {
            _preferences = preferences ?? new ServerUploadPreferences();
            _linkPreferences = linkPreferences ?? new LinkPreferences();

            Title = WindowTitle;
            Width = 1000;
            Height = 640;
            MinWidth = 760;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _models = new ModelListPanel(WindowTitle, _linkPreferences, _preferences.Set, false);

            _folderText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0)
            };

            _linksBox = new CheckBox
            {
                Content = "Then point their links at the server — every link to the model of the same name, as \"Link Updater\" does",
                IsChecked = _preferences.UpdateLinks,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
                ToolTip = "Runs after every model is up, since a link can be pointed only at a model already on the server.\n" +
                          "A link goes to a model of its name uploaded in this run, or to the one model of its name found round\n" +
                          "the model's own folder on the server. Several of one name, a name that only resembles, or a link to a\n" +
                          "local file are left as they are and listed — finish those in \"Link Updater\"."
            };
            _linksBox.Checked += (s, e) => UpdateSummary();
            _linksBox.Unchecked += (s, e) => UpdateSummary();

            _subfoldersBox = new CheckBox
            {
                Content = "Recreate the subfolders — a file in …\\PS\\K1\\ goes into the server folder's own K1",
                IsChecked = _preferences.KeepFolders,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
                ToolTip = "The structure is taken below the deepest folder all the checked files share, to any depth.\n" +
                          "A folder missing on the server is made just before the first file that goes into it.\n" +
                          "Off — every file goes straight into the chosen folder."
            };
            _subfoldersBox.Checked += (s, e) => UpdateSummary();
            _subfoldersBox.Unchecked += (s, e) => UpdateSummary();

            // Starts on "Skip" every time, whatever was chosen before: see ServerUploadPreferences.
            _existingBox = new ComboBox { MinWidth = 260, VerticalAlignment = VerticalAlignment.Center };
            _existingBox.Items.Add("Skip it — leave the server model as it is");
            _existingBox.Items.Add("Replace it — write over the central model");
            _existingBox.SelectedIndex = 0;
            _existingBox.SelectionChanged += (s, e) => UpdateSummary();

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _runButton = new Button
            {
                Content = "Upload…",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _runButton.Click += OnRun;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(closeButton);

            _models.Changed += (s, e) => UpdateSummary();
            _models.Notice += (s, text) =>
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = text;
            };

            _models.Add(_preferences.Models, "model(s) from last time");

            var remembered = ModelFolder.Parse(_preferences.Folder);
            if (remembered != null && remembered.Origin == LinkOrigin.Server)
                _destination = remembered;

            UpdateSummary();

            // The remembered folder is read once the window is on screen: a server that does not answer
            // holds the window for the timeout, and a window that has not appeared yet looks like Revit hanging.
            ContentRendered += OnFirstShown;
        }

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // destination
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // models
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // options
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "Each checked file is opened detached, with every workset closed — nothing is loaded but what the file " +
                       "is made of, and the file itself is not changed — and saved into the Revit Server folder below as a " +
                       "central model, under its own name — into the same subfolder as on disk, if asked; missing folders are " +
                       "made. Then, if asked, every uploaded model is opened once more and its " +
                       "links are reloaded from the server models of the same name. A Revit Server model in the list only " +
                       "gets its links updated. Revit is busy until the whole list is done; the full report is written as it goes.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var destination = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            destination.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            destination.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            destination.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            destination.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = "Into the Revit Server folder:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(label, 0);
            destination.Children.Add(label);

            Grid.SetColumn(_folderText, 1);
            destination.Children.Add(_folderText);

            var choose = SmallButton("Choose…", "Pick the folder in the Revit Server tree.", OnChoose);
            Grid.SetColumn(choose, 2);
            destination.Children.Add(choose);

            var create = SmallButton("New folder…", "Create a folder inside the chosen one, and upload into it.", OnNewFolder);
            Grid.SetColumn(create, 3);
            destination.Children.Add(create);

            Grid.SetRow(destination, 1);
            root.Children.Add(destination);

            Grid.SetRow(_models, 2);
            root.Children.Add(_models);

            var options = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            options.Children.Add(_subfoldersBox);
            options.Children.Add(_linksBox);

            var existing = new StackPanel { Orientation = Orientation.Horizontal };
            existing.Children.Add(new TextBlock
            {
                Text = "A model of the same name already in the folder:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            existing.Children.Add(_existingBox);
            options.Children.Add(existing);

            Grid.SetRow(options, 3);
            root.Children.Add(options);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_runButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 4);
            root.Children.Add(bottom);

            return root;
        }

        private static Button SmallButton(string text, string tooltip, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tooltip
            };
            button.Click += handler;
            return button;
        }

        // ───────────────────────────── the destination ─────────────────────────────

        private void OnFirstShown(object sender, EventArgs e)
        {
            ContentRendered -= OnFirstShown;

            if (_destination != null)
                ReadFolder();
        }

        private void OnChoose(object sender, RoutedEventArgs e)
        {
            var folder = ModelPicker.PickServerFolder(this, WindowTitle, _linkPreferences);
            if (folder == null)
                return;

            _destination = folder;
            ReadFolder();
        }

        /// <summary>What the destination holds: the models (to say which are there already) and the folders.</summary>
        private void ReadFolder()
        {
            _folders = null;
            _onServer = null;
            _subfolders = null;
            _folderError = string.Empty;
            _subfolderError = string.Empty;

            if (_destination == null)
            {
                UpdateSummary();
                return;
            }

            var cursor = Mouse.OverrideCursor;
            _status.Foreground = SystemColors.GrayTextBrush;
            _status.Text = "Reading the folder on Revit Server…";

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                var folders = new ServerFolderCache(_destination.Server, _destination.Path);
                _onServer = folders.Models(_destination.Path);
                _subfolders = folders.Folders(_destination.Path);
                _folders = folders;
            }
            catch (Exception exception)
            {
                _folderError = LinkCatalog.Short(exception.Message);
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }

            UpdateSummary();
        }

        private void OnNewFolder(object sender, RoutedEventArgs e)
        {
            if (_destination == null || _subfolders == null)
            {
                MessageBox.Show(this, "Choose the folder the new one goes into first — \"Choose…\".", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var name = AskName();
            if (name == null)
                return;

            var problem = RevitServerClient.NameProblem(name);
            if (problem.Length > 0)
            {
                MessageBox.Show(this, "Revit Server will not take this name: " + problem + ".", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var path = _destination.Path == RevitServerClient.RootFolder
                ? RevitServerClient.RootFolder + name
                : _destination.Path + "|" + name;

            var folder = ModelFolder.ForServer(_destination.Server, path);

            if (!_subfolders.Contains(name))
            {
                var cursor = Mouse.OverrideCursor;

                try
                {
                    Mouse.OverrideCursor = Cursors.Wait;
                    RevitServerClient.CreateFolder(_destination.Server, path);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, "The folder could not be created.\n\n" + folder.Display + "\n\n" + exception.Message,
                        WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                finally
                {
                    Mouse.OverrideCursor = cursor;
                }
            }

            _destination = folder;

            // Reading the new folder is the check that it is there: a folder the server does not have
            // cannot be read.
            ReadFolder();
        }

        /// <summary>The name of a new folder, or null when the user thought better of it.</summary>
        private string AskName()
        {
            var box = new TextBox { MinWidth = 320, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(3, 2, 3, 2) };

            var dialog = new Window
            {
                Title = WindowTitle,
                Owner = this,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };

            var ok = new Button { Content = "Create", IsDefault = true, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (s, e) => dialog.DialogResult = true;

            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3) };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "A new folder in " + _destination.Display + ":" });
            panel.Children.Add(box);
            panel.Children.Add(buttons);

            dialog.Content = panel;
            dialog.Loaded += (s, e) => box.Focus();

            return dialog.ShowDialog() == true ? box.Text.Trim() : null;
        }

        // ───────────────────────────── the summary ─────────────────────────────

        /// <summary>What a run would do with the checked models — worked out once, for the status line and the confirmation alike.</summary>
        private sealed class Plan
        {
            public int Uploads;
            public int Existing;
            public int LinksOnly;
            public int ServerModels;
            public int Cloud;
            public List<string> Duplicates = new List<string>();

            /// <summary>The folder whose subfolders are recreated; null — none (switched off, or the files share none).</summary>
            public string Root;

            /// <summary>The server folders the run will make, in service form.</summary>
            public List<string> NewFolders = new List<string>();

            /// <summary>Files in a folder whose name Revit Server will not take — not uploaded.</summary>
            public int BadFolders;

            /// <summary>Some file goes into a subfolder of the destination rather than the destination itself.</summary>
            public bool Below;
        }

        private Plan Count(IReadOnlyList<BatchModelRow> marked)
        {
            var files = marked.Where(row => row.Entry.Origin == LinkOrigin.File).ToList();
            var destination = _destination?.Path ?? RevitServerClient.RootFolder;

            var plan = new Plan
            {
                ServerModels = marked.Count(row => row.Entry.Origin == LinkOrigin.Server),
                Cloud = marked.Count(row => row.Entry.Origin == LinkOrigin.Cloud),
                Root = KeepFolders && files.Count > 0 ? ServerUpload.CommonFolder(files.Select(row => row.Entry.Path)) : null
            };

            // Where each file goes — the run's own rule (ServerUpload.TargetPath), so the window can never
            // promise one folder while the run uses another.
            var targets = new List<KeyValuePair<BatchModelRow, string>>();
            foreach (var row in files)
            {
                var problem = string.Empty;
                var path = KeepFolders ? ServerUpload.TargetPath(destination, plan.Root, row.Entry.Path, out problem) : destination;

                if (path == null)
                    plan.BadFolders++;
                else
                    targets.Add(new KeyValuePair<BatchModelRow, string>(row, path));
            }

            plan.Below = targets.Any(pair => !string.Equals(pair.Value, destination, StringComparison.OrdinalIgnoreCase));

            var groups = targets.GroupBy(pair => pair.Value + "|" + pair.Key.Name, StringComparer.OrdinalIgnoreCase).ToList();
            plan.Duplicates = groups.Where(group => group.Count() > 1).Select(group => group.First().Key.Name).ToList();

            _subfolderError = string.Empty;

            if (_folders != null)
            {
                plan.Existing = groups.Count(group => Holds(group.First().Value, group.First().Key.Name));
                plan.NewFolders = targets
                    .Select(pair => pair.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(Missing)
                    .ToList();
            }

            // One file per name and folder goes up; a name already there only when it is to be replaced.
            // One left there still has its links updated: that is how a run that broke off after the
            // upload is finished — the same list again.
            plan.Uploads = groups.Count - (Replace ? 0 : plan.Existing);
            plan.LinksOnly = UpdateLinks ? plan.ServerModels + (Replace ? 0 : plan.Existing) : 0;

            return plan;
        }

        /// <summary>Whether a server folder already holds a model of this name; a folder that cannot be read is said, and counts as not.</summary>
        private bool Holds(string path, string name)
        {
            try
            {
                return _folders.Models(path).Contains(name);
            }
            catch (Exception exception)
            {
                _subfolderError = LinkCatalog.Short(exception.Message);
                return false;
            }
        }

        private bool Missing(string path)
        {
            try
            {
                return !_folders.Exists(path);
            }
            catch (Exception exception)
            {
                _subfolderError = LinkCatalog.Short(exception.Message);
                return false;
            }
        }

        /// <summary>"K1, K2/Old, ST" — the new folders as they sit below the destination.</summary>
        private string FolderNames(IReadOnlyList<string> paths, int limit)
        {
            var below = _destination == null ? string.Empty : _destination.Path.TrimEnd('|');

            var names = paths.Select(path => path.Substring(Math.Min(path.Length, below.Length)).Trim('|').Replace('|', '/')).ToList();
            var text = string.Join(", ", names.Take(limit));

            return names.Count > limit ? text + " and " + (names.Count - limit) + " more" : text;
        }

        private void UpdateSummary()
        {
            _folderText.Text = _destination == null ? "(not chosen)" : _destination.Display;
            _folderText.ToolTip = _destination?.Display;

            var marked = _models.Marked();
            var plan = Count(marked);

            if (_folderError.Length > 0)
            {
                Say(Brushes.Firebrick, "The folder could not be read: " + _folderError);
            }
            else if (_models.Rows.Count == 0)
            {
                Say(SystemColors.GrayTextBrush, "Add the models to upload — a whole folder with \"Folder…\", files, or a saved set.");
            }
            else if (marked.Count == 0)
            {
                Say(SystemColors.GrayTextBrush, "In the list: " + _models.Rows.Count + ". Nothing is checked.");
            }
            else if (_destination == null && plan.Uploads > 0)
            {
                Say(SystemColors.GrayTextBrush, "Choose the Revit Server folder the models go into — \"Choose…\".");
            }
            else
            {
                var parts = new List<string> { "Will be uploaded: " + plan.Uploads };
                var warning = false;

                if (plan.Existing > 0)
                {
                    parts.Add(Replace
                        ? "of them replace a model already there: " + plan.Existing
                        : "already in the folder, not uploaded again" + (UpdateLinks ? " (their links are still updated)" : string.Empty) + ": " + plan.Existing);
                    warning |= Replace;
                }

                if (plan.Duplicates.Count > 0)
                {
                    parts.Add("one name twice for one folder — only the first goes up: " + string.Join(", ", plan.Duplicates.Take(3)) +
                              (plan.Duplicates.Count > 3 ? "…" : string.Empty));
                    warning = true;
                }

                if (plan.NewFolders.Count > 0)
                    parts.Add("new folders on the server: " + plan.NewFolders.Count + " (" + FolderNames(plan.NewFolders, 4) + ")");

                if (plan.BadFolders > 0)
                {
                    parts.Add("in a folder whose name Revit Server will not take, not uploaded: " + plan.BadFolders);
                    warning = true;
                }

                if (KeepFolders && plan.Root == null && marked.Count(row => row.Entry.Origin == LinkOrigin.File) > 1)
                    parts.Add("the files share no folder — they all go straight into the chosen one");

                if (_subfolderError.Length > 0)
                    parts.Add("a subfolder could not be read (" + _subfolderError + ") — the run checks it again");

                if (plan.ServerModels > 0)
                    parts.Add(UpdateLinks
                        ? "links only, already on the server: " + plan.ServerModels
                        : "server models: " + plan.ServerModels + " — nothing to do with them unless the links are updated");

                if (plan.Cloud > 0)
                {
                    parts.Add("BIM360 models left out: " + plan.Cloud + " — download them first");
                    warning = true;
                }

                Say(warning ? Brushes.Firebrick : Brushes.DarkGreen, string.Join(". ", parts) + ".");
            }

            // Uploading needs the folder read — what is there already decides what is skipped. Updating the
            // links of models already on the server needs no destination at all.
            var canUpload = _destination != null && _folderError.Length == 0 && _onServer != null;
            _runButton.IsEnabled = plan.Uploads > 0 ? canUpload : plan.LinksOnly > 0;
            _runButton.Content = plan.Uploads > 0
                ? "Upload (" + plan.Uploads + ")…"
                : plan.LinksOnly > 0 ? "Update links (" + plan.LinksOnly + ")…" : "Upload…";
        }

        private void Say(Brush brush, string text)
        {
            _status.Foreground = brush;
            _status.Text = text;
        }

        // ───────────────────────────── the run ─────────────────────────────

        private void OnRun(object sender, RoutedEventArgs e)
        {
            var marked = _models.Marked();
            var plan = Count(marked);

            if ((plan.Uploads > 0 && _destination == null) || (plan.Uploads == 0 && plan.LinksOnly == 0))
                return;

            var text = plan.Uploads > 0
                ? "Upload " + plan.Uploads + " model(s) into\n" + _destination.Display + "?\n\n"
                : "Update the links in " + plan.LinksOnly + " model(s) already on Revit Server?\n\n";

            if (plan.Uploads > 0 && plan.Below)
            {
                text += "The subfolders are recreated as they are below\n" + plan.Root + "\n" +
                        (plan.NewFolders.Count > 0
                            ? "New folders on the server: " + FolderNames(plan.NewFolders, 12) + ".\n\n"
                            : "Every folder needed is on the server already.\n\n");
            }

            if (Replace && plan.Existing > 0)
            {
                text += plan.Existing + " model(s) already there will be REPLACED: their central model is written over, " +
                        "and anyone with a local copy of one has to make a new one.\n\n";
            }

            if (UpdateLinks)
            {
                text += "Then the links in " + (plan.Uploads > 0 ? "every uploaded model" : string.Empty) +
                        (plan.Uploads > 0 && plan.LinksOnly > 0 ? " and in " : string.Empty) +
                        (plan.LinksOnly > 0 ? plan.LinksOnly + " model(s) already on the server" : string.Empty) +
                        " are pointed at the server models of the same name, and each is synchronised.\n\n";
            }

            text += "Your files are not changed. Revit is busy until the whole list is done — about as long as opening " +
                    "each model once" + (UpdateLinks ? ", and once more with its links" : string.Empty) + ".\n\n" +
                    "The full report is written as it goes into\n" + ServerUploadPreferences.ReportsFolder;

            var image = Replace && plan.Existing > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question;

            if (MessageBox.Show(this, text, WindowTitle, MessageBoxButton.YesNo, image, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_destination != null)
                _preferences.Folder = _destination.Format();

            _preferences.UpdateLinks = UpdateLinks;
            _preferences.KeepFolders = KeepFolders;
            _preferences.Set = _models.SetName;

            // The list is remembered as it stands, check marks aside: a run that broke off is started
            // again over the same list, and what is on the server by then is skipped by name.
            _preferences.Models.Clear();
            _preferences.Models.AddRange(_models.Rows.Select(row => row.Entry));

            _preferences.Save();

            // The server names typed in here belong to the whole add-in, not to this window.
            _linkPreferences.Save();

            base.OnClosed(e);
        }
    }
}
