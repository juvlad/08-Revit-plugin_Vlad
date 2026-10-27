using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// The list of models a batch runs over: where they come from (a folder, files, Revit Server,
    /// BIM360, a pair of GUIDs, a saved set), the table with a check box per model, and the saved sets.
    ///
    /// Extracted from "Accept Changes → Other models…" once "Sheets" needed the same list for a batch
    /// of its own. What a batch does with the models differs between the two; where the models come
    /// from and how they are checked does not — so it is one piece of code, built in code like every
    /// window here, and each window puts its own settings and buttons round it.
    ///
    /// The window keeps its own status line; the panel tells it when the list or a check mark changed
    /// (<see cref="Changed"/>) and when a few words are worth showing there (<see cref="Notice"/>).
    /// </summary>
    internal sealed class ModelListPanel : Grid
    {
        /// <summary>
        /// The most folders one "Folder…" walk reads when it goes into the subfolders. On Revit Server
        /// and BIM360 every folder is a request; a limit reached is said out loud — it usually means the
        /// walk started from too high up.
        /// </summary>
        private const int FolderLimit = 200;

        /// <summary>Revit's own backups of a model that is not workshared: "Model.0001.rvt" next to "Model.rvt".</summary>
        private static readonly Regex BackupFile = new Regex(@"\.\d{4}\.rvt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly string _title;
        private readonly LinkPreferences _linkPreferences;

        private readonly ObservableCollection<BatchModelRow> _rows = new ObservableCollection<BatchModelRow>();

        private readonly DataGrid _grid;
        private readonly ComboBox _setBox;
        private readonly CheckBox _selectAll;

        private bool _syncingSelectAll;
        private bool _settingMany;

        /// <summary>A model was added or removed, or a check mark changed.</summary>
        public event EventHandler Changed;

        /// <summary>A line worth showing in the window's status — what an "Add" did.</summary>
        public event EventHandler<string> Notice;

        /// <param name="title">The window's title — the caption of the dialogs the panel shows.</param>
        /// <param name="set">The saved set worked with last, shown in the set box.</param>
        /// <param name="cloud">
        /// Offer BIM360 as a source. "Upload to Server" does not: it puts files on a server, and a cloud
        /// model has to be downloaded first — a button that offers one only to refuse it later is worse
        /// than no button.
        /// </param>
        public ModelListPanel(string title, LinkPreferences linkPreferences, string set, bool cloud = true)
        {
            _title = title;
            _linkPreferences = linkPreferences ?? new LinkPreferences();

            _setBox = new ComboBox
            {
                Width = 220,
                IsEditable = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 4),
                Text = set ?? string.Empty,
                ToolTip = "The same saved sets as in \"Link Manager\": a list of models gathered once."
            };

            _selectAll = new CheckBox
            {
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every model"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();

            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // sources
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // sets
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table

            var sources = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            sources.Children.Add(FolderButton(cloud));
            sources.Children.Add(SourceButton("Files…", "Ordinary .rvt files: a disk or a network folder.", OnAddFiles));
            sources.Children.Add(SourceButton("Revit Server…", "Browse folders and models on Revit Server.", OnBrowseServer));

            if (cloud)
            {
                sources.Children.Add(SourceButton("BIM360…", "Browse BIM360/ACC accounts, projects and folders.", OnBrowseCloud));
                sources.Children.Add(SourceButton("BIM360 by GUID…",
                    "Enter cloud models as pairs of GUIDs — when browsing is unavailable.", OnAddCloudByGuid));
            }

            sources.Children.Add(SourceButton("Remove from list",
                "Removes the checked rows from the table. The models themselves are left untouched.", OnRemove));
            SetRow(sources, 0);
            Children.Add(sources);

            var sets = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            sets.Children.Add(new TextBlock
            {
                Text = "Set:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            sets.Children.Add(_setBox);
            sets.Children.Add(SourceButton("Load set", "Adds the saved set's models into the table.", OnLoadSet));
            sets.Children.Add(SourceButton("Save set", "Saves the shown rows under the name in the field on the left.", OnSaveSet));
            SetRow(sets, 1);
            Children.Add(sets);

            SetRow(_grid, 2);
            Children.Add(_grid);

            ReloadSetNames();
        }

        /// <summary>Every model in the table, checked or not, in the table's order.</summary>
        public IReadOnlyList<BatchModelRow> Rows => _rows;

        /// <summary>The name in the set box, trimmed.</summary>
        public string SetName => (_setBox.Text ?? string.Empty).Trim();

        /// <summary>The checked models, in the table's order.</summary>
        public List<BatchModelRow> Marked()
        {
            return _rows.Where(row => row.IsSelected).ToList();
        }

        private Window Owner => Window.GetWindow(this);

        // ───────────────────────────── layout ─────────────────────────────

        private static Button SourceButton(string text, string tooltip, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tooltip
            };

            // A routed event refuses a null handler outright (ArgumentNullException "handler") — and
            // "Folder…" has none here: it opens its menu from a handler of its own.
            if (handler != null)
                button.Click += handler;

            return button;
        }

        /// <summary>"Folder…" with a menu of the stores a folder can be in.</summary>
        private Button FolderButton(bool cloud)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MenuEntry("On disk or the network…", (s, e) => AddFolder(ModelPicker.PickFileFolder(Owner))));
            menu.Items.Add(MenuEntry("On Revit Server…", (s, e) => AddFolder(ModelPicker.PickServerFolder(Owner, _title, _linkPreferences))));

            if (cloud)
                menu.Items.Add(MenuEntry("In BIM360…", (s, e) => AddFolder(ModelPicker.PickCloudFolder(Owner, _title))));

            var button = SourceButton("Folder…",
                "Every model in a folder — on disk or the network, on Revit Server" + (cloud ? " or in BIM360" : string.Empty) + " —\n" +
                "and, if you say so, in the folders inside it. Revit's own backups (Model.0001.rvt)\n" +
                "and the \"_backup\" folders are left out.", null);

            button.ContextMenu = menu;
            button.Click += (s, e) =>
            {
                menu.PlacementTarget = button;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            };

            return button;
        }

        private static MenuItem MenuEntry(string header, RoutedEventHandler handler)
        {
            var item = new MenuItem { Header = header };
            item.Click += handler;
            return item;
        }

        private DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                ItemsSource = _rows,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                CanUserSortColumns = true,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = SystemColors.ControlLightBrush,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                SelectionMode = DataGridSelectionMode.Extended,
                RowHeaderWidth = 0
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate(nameof(BatchModelRow.IsSelected))
            });

            grid.Columns.Add(GridBuilder.TextColumn("Model", nameof(BatchModelRow.Name), new DataGridLength(240)));
            grid.Columns.Add(GridBuilder.TextColumn("Source", nameof(BatchModelRow.Kind), new DataGridLength(100)));
            grid.Columns.Add(GridBuilder.TextColumn("Where it lives", nameof(BatchModelRow.Location),
                new DataGridLength(1, DataGridLengthUnitType.Star)));

            grid.MouseDoubleClick += (s, e) => ToggleSelectedRows();
            grid.PreviewKeyDown += OnGridKeyDown;

            return grid;
        }

        // ───────────────────────────── where the models come from ─────────────────────────────

        private void OnAddFiles(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.Files(Owner, true), "file(s)");
        }

        private void OnBrowseServer(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.Server(Owner, _title, _linkPreferences, Keys), "Revit Server model(s)");
        }

        private void OnBrowseCloud(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.Cloud(Owner, _title, Keys), "BIM360 model(s)");
        }

        private void OnAddCloudByGuid(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.CloudByGuid(Owner, DefaultRegion()), "cloud model(s)");
        }

        /// <summary>The region the GUID-entry window opens with: the same as the models already gathered.</summary>
        private string DefaultRegion()
        {
            var region = _rows
                .Where(row => row.Entry.Origin == LinkOrigin.Cloud)
                .Select(row => row.Entry.Region)
                .FirstOrDefault(value => !string.IsNullOrEmpty(value));

            return region ?? "US";
        }

        /// <summary>
        /// Every model in a folder, and on request in its folders too. The question is asked only when
        /// there are folders inside: on Revit Server and BIM360 each one costs a request, and a project
        /// root walked by accident can hold hundreds of them.
        /// </summary>
        private void AddFolder(ModelFolder folder)
        {
            if (folder == null)
                return;

            IReadOnlyList<StoreItem> items;

            try
            {
                items = Read(folder);
            }
            catch (Exception exception)
            {
                MessageBox.Show(Owner, "The folder could not be read.\n\n" + folder.Display + "\n\n" + exception.Message,
                    _title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var models = items.Where(item => !item.IsFolder && !IsBackup(item.Entry)).Select(item => item.Entry).ToList();
            var folders = items.Where(item => item.IsFolder && !IsBackup(item.Folder)).Select(item => item.Folder).ToList();
            var unread = new List<string>();
            var limited = false;

            if (folders.Count > 0)
            {
                var answer = MessageBox.Show(Owner,
                    "\"" + folder.Display + "\" holds " + models.Count + " model(s) and " + folders.Count + " folder(s).\n\n" +
                    "Take the models in the folders inside it as well — every level down?",
                    _title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);

                if (answer == MessageBoxResult.Cancel)
                    return;

                if (answer == MessageBoxResult.Yes)
                    limited = Walk(folders, models, unread);
            }

            if (models.Count == 0)
            {
                MessageBox.Show(Owner,
                    "No Revit model was found in \"" + folder.Display + "\"" + (folders.Count > 0 ? " itself." : ".") +
                    Unread(unread),
                    _title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Add(models, "model(s) from the folder");

            if (unread.Count > 0 || limited)
            {
                MessageBox.Show(Owner,
                    (limited
                        ? "The walk stopped after " + FolderLimit + " folders — the models found so far are in the list. " +
                          "It probably started too high up: pick the folder the models are actually in."
                        : "The models found are in the list.") +
                    Unread(unread),
                    _title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// The folders inside, breadth first, until <see cref="FolderLimit"/>. A folder that cannot be
        /// read is noted and passed over — one locked folder is no reason to lose the rest.
        /// Returns true when the limit was reached.
        /// </summary>
        private bool Walk(IEnumerable<ModelFolder> start, List<LinkEntry> models, List<string> unread)
        {
            var queue = new Queue<ModelFolder>(start);
            var read = 0;

            while (queue.Count > 0)
            {
                if (read >= FolderLimit)
                    return true;

                var folder = queue.Dequeue();
                read++;

                try
                {
                    foreach (var item in Read(folder))
                    {
                        if (item.IsFolder)
                        {
                            if (!IsBackup(item.Folder))
                                queue.Enqueue(item.Folder);
                        }
                        else if (!IsBackup(item.Entry))
                        {
                            models.Add(item.Entry);
                        }
                    }
                }
                catch (Exception exception)
                {
                    unread.Add(folder.Display + " — " + LinkCatalog.Short(exception.Message));
                }
            }

            return false;
        }

        private static IReadOnlyList<StoreItem> Read(ModelFolder folder)
        {
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                return ModelStore.Children(folder);
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }
        }

        /// <summary>A backup Revit keeps of a file next to it — never a model anybody means to list.</summary>
        private static bool IsBackup(LinkEntry entry)
        {
            return entry != null && entry.Origin == LinkOrigin.File && BackupFile.IsMatch(entry.Name);
        }

        /// <summary>The "_backup" folder Revit keeps next to a workshared model, and its own temporary folder.</summary>
        private static bool IsBackup(ModelFolder folder)
        {
            if (folder == null || folder.Origin != LinkOrigin.File)
                return false;

            var name = folder.Name;
            return name.EndsWith("_backup", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Revit_temp", StringComparison.OrdinalIgnoreCase);
        }

        private static string Unread(IReadOnlyList<string> unread)
        {
            if (unread.Count == 0)
                return string.Empty;

            const int limit = 10;
            var text = "\n\nNot read:\n" + string.Join("\n", unread.Take(limit).Select(line => "• " + line));

            return unread.Count > limit ? text + "\n… and " + (unread.Count - limit) + " more" : text;
        }

        /// <summary>Adds models to the table, dropping the ones already in it.</summary>
        public void Add(IReadOnlyList<LinkEntry> entries, string what)
        {
            if (entries == null || entries.Count == 0)
                return;

            var known = Keys();
            var added = 0;

            foreach (var entry in entries)
            {
                if (!known.Add(entry.Key))
                    continue;

                var row = new BatchModelRow(entry);
                row.PropertyChanged += OnRowChanged;
                _rows.Add(row);
                added++;
            }

            OnChanged();

            if (added < entries.Count)
                Notice?.Invoke(this, "Added " + what + ": " + added + ". Already in the list: " + (entries.Count - added) + ".");
        }

        private HashSet<string> Keys()
        {
            return new HashSet<string>(_rows.Select(row => row.Key), StringComparer.Ordinal);
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
                return;

            foreach (var row in marked)
            {
                row.PropertyChanged -= OnRowChanged;
                _rows.Remove(row);
            }

            OnChanged();
        }

        // ───────────────────────────── saved sets ─────────────────────────────

        private void ReloadSetNames()
        {
            var current = _setBox.Text;

            _setBox.Items.Clear();
            foreach (var name in LinkSetLibrary.Names())
                _setBox.Items.Add(name);

            _setBox.Text = current;
        }

        private void OnLoadSet(object sender, RoutedEventArgs e)
        {
            if (SetName.Length == 0)
            {
                MessageBox.Show(Owner, "Choose a set in the list or type in its name.", _title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var entries = LinkSetLibrary.Load(SetName);
            if (entries.Count == 0)
            {
                MessageBox.Show(Owner, "The set \"" + SetName + "\" is empty or does not exist.", _title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Add(entries, "model(s) from the set");
        }

        private void OnSaveSet(object sender, RoutedEventArgs e)
        {
            if (SetName.Length == 0)
            {
                MessageBox.Show(Owner, "Type in a set name in the field on the left.", _title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_rows.Count == 0)
            {
                MessageBox.Show(Owner, "There is nothing in the table to save.", _title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                LinkSetLibrary.Save(SetName, _rows.Select(row => row.Entry));
                ReloadSetNames();

                MessageBox.Show(Owner,
                    "The set \"" + SetName + "\" was saved: " + _rows.Count + " models.\n\n" +
                    LinkSetLibrary.FilePathFor(SetName),
                    _title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(Owner, "Could not save the set.\n\n" + exception.Message,
                    _title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ───────────────────────────── check marks ─────────────────────────────

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(row => value);
        }

        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<BatchModelRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);
            var affected = new HashSet<BatchModelRow>(rows);

            SetMany(row => affected.Contains(row) ? value : row.IsSelected);
        }

        /// <summary>Setting check marks in bulk: the window hears about it once, at the end.</summary>
        private void SetMany(Func<BatchModelRow, bool> value)
        {
            _settingMany = true;

            foreach (var row in _rows)
                row.IsSelected = value(row);

            _settingMany = false;

            OnChanged();
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space)
                return;

            ToggleSelectedRows();
            e.Handled = true;
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_settingMany || e.PropertyName != nameof(BatchModelRow.IsSelected))
                return;

            OnChanged();
        }

        private void OnChanged()
        {
            var marked = _rows.Count(row => row.IsSelected);

            _syncingSelectAll = true;
            _selectAll.IsChecked = _rows.Count == 0 || marked == 0
                ? false
                : marked == _rows.Count ? true : (bool?)null;
            _syncingSelectAll = false;

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
