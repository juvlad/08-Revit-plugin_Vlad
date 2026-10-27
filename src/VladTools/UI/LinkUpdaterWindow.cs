using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// The "Link Updater" window: every link of the open project, where it points now, and the model of
    /// the same name on Revit Server it could point at instead.
    ///
    /// Made for a move from BIM360 to Revit Server: the models arrive on the server, and every link in
    /// every one of them still points at the cloud. The search starts where the open model itself lies —
    /// its own folder with everything under it — and climbs a level at a time while a link is still not
    /// found, up to the folder just under the server root (a whole server can hold every project of the
    /// office). "Search wider" and "Another folder…" go further by hand.
    ///
    /// A link is matched by its name: that is all that survives the move. Exactly one model of that name
    /// is checked on its own; several, or only a name that differs in punctuation or a version suffix,
    /// are offered in the drop-down but left for the user to check — the same "several matches mean the
    /// user chooses" rule as the "Building Kit" and the workset guess.
    ///
    /// The window knows nothing about the Revit API: it is handed <see cref="LinkPathInfo"/> snapshots,
    /// reads the server through <see cref="ServerModelIndex"/> (plain HTTP) and hands back rows — or
    /// <see cref="WantsBatch"/>, for the command to open <see cref="LinkUpdaterBatchWindow"/> instead.
    /// </summary>
    internal sealed class LinkUpdaterWindow : Window
    {
        private const string WindowTitle = "Link Updater";

        private readonly IReadOnlyList<LinkUpdateRow> _rows;
        private readonly HostModel _host;
        private readonly int _nested;
        private readonly LinkPreferences _linkPreferences;
        private readonly LinkUpdaterPreferences _preferences;
        private readonly ServerModelIndex _index = new ServerModelIndex();

        /// <summary>The folders searched from, in order — what the search line names.</summary>
        private readonly List<ModelFolder> _searched = new List<ModelFolder>();

        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _searchText;
        private readonly Button _widerButton;
        private readonly Button _chooseButton;
        private readonly TextBlock _status;
        private readonly Button _updateButton;

        /// <summary>Where distances are measured from: the open model's folder, else the first folder searched.</summary>
        private ModelFolder _origin;

        /// <summary>The highest folder of the climb so far — "Search wider" goes one above it.</summary>
        private ModelFolder _reach;

        private bool _syncingSelectAll;
        private bool _settingMany;
        private bool _matching;

        /// <summary>The links the user confirmed, each with its <see cref="LinkUpdateRow.Target"/>.</summary>
        public IReadOnlyList<LinkUpdateRow> Selected { get; private set; } = new List<LinkUpdateRow>();

        /// <summary>"Several models…": the command shows the batch window once this one has closed.</summary>
        public bool WantsBatch { get; private set; }

        /// <param name="links">Every top-level Revit link of the open project.</param>
        /// <param name="host">Where the open model lies — the search starts there.</param>
        /// <param name="nested">How many nested links were left out: they follow the models they are nested in.</param>
        public LinkUpdaterWindow(IReadOnlyList<LinkPathInfo> links, HostModel host, int nested)
        {
            _rows = (links ?? new List<LinkPathInfo>()).Select(info => new LinkUpdateRow(info)).ToList();
            _host = host;
            _nested = nested;
            _linkPreferences = LinkPreferences.Load();
            _preferences = LinkUpdaterPreferences.Load();

            Title = WindowTitle;
            Width = 1220;
            Height = 660;
            MinWidth = 820;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _selectAll = new CheckBox
            {
                IsChecked = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every link that has somewhere new to point"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();

            _searchText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush
            };

            _widerButton = SmallButton("Search wider",
                "Read the folder one level above everything searched so far — up to the whole server.", OnWider);

            var otherButton = SmallButton("Another folder…",
                "Add a folder on any Revit Server to the search — for models that went somewhere else.", OnOtherFolder);

            _chooseButton = SmallButton("Choose on server…",
                "Pick the server model for the highlighted link by hand, in the Revit Server tree.", OnChoose);

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _updateButton = new Button
            {
                Content = "Update paths",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _updateButton.Click += OnUpdate;

            var batchButton = new Button
            {
                Content = "Several models…",
                MinWidth = 130,
                Padding = new Thickness(10, 4, 10, 4),
                ToolTip = "Closes this window and opens a list of models on Revit Server — the whole server, folders or single\n" +
                          "models. Each is opened in turn, its links are pointed at the server models of the same name, and it\n" +
                          "is synchronised back. Only certain matches are made; the rest are listed in the report."
            };
            batchButton.Click += (s, e) =>
            {
                WantsBatch = true;
                DialogResult = true;
            };

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(otherButton, batchButton, closeButton);

            foreach (var row in _rows)
                row.PropertyChanged += OnRowChanged;

            _grid.ItemsSource = _rows;
            _grid.SelectionChanged += (s, e) => UpdateChooseButton();

            Closed += (s, e) =>
            {
                _preferences.Save();
                _linkPreferences.Save();
            };

            ShowSearch();
            Revalidate();
            UpdateChooseButton();

            // The search runs on its own once the window is on screen: finding the server models is
            // what the window was opened for, and it is drawn first so the wait has something to look at.
            ContentRendered += (s, e) => SearchFromStart();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button otherButton, Button batchButton, Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // the open model
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // where it searched
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text =
                    "Every link of the open model and where it points now. The add-in looks for a model of the same name " +
                    "on the Revit Server this model lies on — starting from its own folder — and offers it as the new path. " +
                    "A link found exactly once is checked; anything less certain is left for you to check. " +
                    "The links stay the same elements: placement, pins, worksets, view settings and Copy/Monitor survive.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var model = new TextBlock
            {
                Text = "Open model: " + HostText(),
                Foreground = SystemColors.GrayTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 4)
            };
            model.ToolTip = model.Text;
            Grid.SetRow(model, 1);
            root.Children.Add(model);

            var search = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            search.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            search.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_searchText, 0);
            search.Children.Add(_searchText);

            var searchButtons = new StackPanel { Orientation = Orientation.Horizontal };
            searchButtons.Children.Add(_widerButton);
            searchButtons.Children.Add(otherButton);
            searchButtons.Children.Add(_chooseButton);
            Grid.SetColumn(searchButtons, 1);
            search.Children.Add(searchButtons);

            Grid.SetRow(search, 2);
            root.Children.Add(search);

            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(batchButton);
            buttons.Children.Add(_updateButton);
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
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tooltip
            };
            button.Click += handler;

            return button;
        }

        private DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                CanUserSortColumns = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = SystemColors.ControlLightBrush,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                SelectionMode = DataGridSelectionMode.Extended,
                IsReadOnly = true,
                RowHeaderWidth = 0,
                Margin = new Thickness(0, 6, 0, 0)
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate(
                    nameof(LinkUpdateRow.IsSelected),
                    nameof(LinkUpdateRow.CanUpdate))
            });

            grid.Columns.Add(Text("Link", nameof(LinkUpdateRow.Name), new DataGridLength(210)));
            grid.Columns.Add(Text("Now", nameof(LinkUpdateRow.LoadState), new DataGridLength(90)));
            grid.Columns.Add(Text("Points to", nameof(LinkUpdateRow.Location), new DataGridLength(1, DataGridLengthUnitType.Star)));

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "New path on Revit Server",
                Width = new DataGridLength(1.3, DataGridLengthUnitType.Star),
                MinWidth = 220,
                CanUserSort = false,
                CellTemplate = TargetTemplate()
            });

            var status = Text("State", nameof(LinkUpdateRow.StatusText), new DataGridLength(1, DataGridLengthUnitType.Star));
            status.MinWidth = 180;
            status.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(LinkUpdateRow.StatusBrush))));
            grid.Columns.Add(status);

            grid.MouseDoubleClick += (s, e) =>
            {
                if (!InsideComboBox(e.OriginalSource))
                    ToggleSelectedRows();
            };
            grid.PreviewKeyDown += OnGridKeyDown;

            return grid;
        }

        private static DataGridTextColumn Text(string header, string property, DataGridLength width)
        {
            var column = GridBuilder.TextColumn(header, property, width);
            column.IsReadOnly = true;

            return column;
        }

        /// <summary>
        /// The drop-down of server models. Not <see cref="GridBuilder.ComboTemplate"/>: every row has a
        /// list of its own, so the items are bound to the row rather than shared — and, as there, one
        /// binding for the choice and never a <c>DataGridComboBoxColumn</c> (see CLAUDE.md).
        /// </summary>
        private static DataTemplate TargetTemplate()
        {
            var combo = new FrameworkElementFactory(typeof(ComboBox));
            combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(LinkUpdateRow.Targets)));
            combo.SetValue(ItemsControl.DisplayMemberPathProperty, nameof(LinkTarget.RsnPath));
            combo.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));
            combo.SetValue(Selector.IsSynchronizedWithCurrentItemProperty, (bool?)false);
            combo.SetBinding(Selector.SelectedItemProperty,
                new Binding(nameof(LinkUpdateRow.Picked)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            combo.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(LinkUpdateRow.Target) + "." + nameof(LinkTarget.RsnPath)));

            return new DataTemplate { VisualTree = combo };
        }

        private static bool InsideComboBox(object source)
        {
            var element = source as DependencyObject;

            while (element != null)
            {
                if (element is ComboBox)
                    return true;

                element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
            }

            return false;
        }

        private string HostText()
        {
            var entry = _host?.Entry;
            if (entry == null)
                return "not saved — choose the folder to search in with \"Another folder…\"";

            switch (entry.Origin)
            {
                case LinkOrigin.Server:
                    return entry.Path;

                case LinkOrigin.Cloud:
                    return "BIM360 · " + _host.Name + " — not on Revit Server; choose the folder to search in with \"Another folder…\"";

                default:
                    return entry.Path + " — a file, not on Revit Server; choose the folder to search in with \"Another folder…\"";
            }
        }

        // ───────────────────────────── the search ─────────────────────────────

        /// <summary>
        /// The first search: from the open model's own folder when it lies on a Revit Server, climbing
        /// while a link is still not found; otherwise from the folder searched last by hand, as it is.
        /// </summary>
        private void SearchFromStart()
        {
            if (_host?.Entry != null && _host.Entry.Origin == LinkOrigin.Server && _host.Folder != null)
            {
                _origin = _host.Folder;
                Search(() =>
                {
                    Walk(_host.Folder);
                    _reach = _host.Folder;
                    Climb();
                });
                return;
            }

            var remembered = ModelFolder.Parse(_preferences.Root);
            if (remembered == null || remembered.Origin != LinkOrigin.Server)
                return;

            _origin = remembered;
            Search(() =>
            {
                Walk(remembered);
                _reach = remembered;
            });
        }

        /// <summary>
        /// Climbs a level at a time while some link is still not found by its name — but never onto the
        /// server root on its own: that is every project on the server, and a link missing from this
        /// project's folders is far more likely not moved yet than moved into another project.
        /// </summary>
        private void Climb()
        {
            while (_reach != null && !_index.LimitReached && Missing().Count > 0)
            {
                var above = RevitServerClient.ParentFolder(_reach.Path);
                if (above == null || above == RevitServerClient.RootFolder)
                    return;

                _reach = ModelFolder.ForServer(_reach.Server, above);
                Walk(_reach);
            }
        }

        /// <summary>The links with no model of their name in what has been read — what a wider search is for.</summary>
        private List<LinkUpdateRow> Missing()
        {
            return _rows
                .Where(row => row.Info.Obstacle.Length == 0)
                .Where(row => _index.Named(row.Name).Count == 0)
                .ToList();
        }

        private void Walk(ModelFolder folder)
        {
            if (_index.Covers(folder))
                return;

            _index.Walk(folder);

            _searched.RemoveAll(earlier => IsUnder(earlier, folder));
            _searched.Add(folder);
        }

        /// <summary>Whether <paramref name="folder"/> lies inside <paramref name="root"/> (or is it).</summary>
        private static bool IsUnder(ModelFolder folder, ModelFolder root)
        {
            if (!string.Equals(folder.Server, root.Server, StringComparison.OrdinalIgnoreCase))
                return false;

            if (root.Path == RevitServerClient.RootFolder)
                return true;

            return string.Equals(folder.Path, root.Path, StringComparison.CurrentCultureIgnoreCase) ||
                   folder.Path.StartsWith(root.Path + "|", StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>Runs a server read under a wait cursor, then matches the links and redraws the window.</summary>
        private void Search(Action read)
        {
            var cursor = Mouse.OverrideCursor;
            _searchText.Text = "Reading Revit Server…";

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                read();
            }
            catch (Exception exception)
            {
                // The index keeps what it read; one folder or one server failing is a line, not the end.
                MessageBox.Show(this, "Revit Server could not be read.\n\n" + exception.Message, WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }

            Match();
            ShowSearch();
            Revalidate();
        }

        private void OnWider(object sender, RoutedEventArgs e)
        {
            if (_reach == null)
                return;

            var above = RevitServerClient.ParentFolder(_reach.Path);
            if (above == null)
                return;

            Search(() =>
            {
                _reach = ModelFolder.ForServer(_reach.Server, above);
                Walk(_reach);
            });
        }

        private void OnOtherFolder(object sender, RoutedEventArgs e)
        {
            var folder = ModelPicker.PickServerFolder(this, WindowTitle, _linkPreferences);
            if (folder == null)
                return;

            _preferences.Root = folder.Format();

            if (_origin == null)
                _origin = folder;

            Search(() =>
            {
                Walk(folder);

                // The first folder anybody gave is where "Search wider" climbs from; a folder added
                // next to the open model's own does not move the climb.
                if (_reach == null)
                    _reach = folder;
            });
        }

        private void OnChoose(object sender, RoutedEventArgs e)
        {
            var row = _grid.SelectedItems.OfType<LinkUpdateRow>().FirstOrDefault();
            if (row == null || row.Info.Obstacle.Length > 0)
                return;

            var picked = ModelPicker.Server(this, WindowTitle + " — " + row.Name, _linkPreferences, Known)
                .FirstOrDefault();
            if (picked == null)
                return;

            var manual = new LinkTarget(picked.Path, SameName(row, picked.Name), true, int.MaxValue / 2);

            _matching = true;
            row.Targets = new[] { manual }
                .Concat(row.Targets.Where(target => target.Key != manual.Key && !target.IsManual))
                .ToList();
            row.Target = manual;
            _matching = false;

            // Picking a model by hand is the user's word on this link: it is checked, as far as it can be.
            row.IsTouched = true;
            Revalidate();
            row.IsSelected = row.CanUpdate;
        }

        /// <summary>The server models already chosen for some link — the tree greys them out.</summary>
        private HashSet<string> Known()
        {
            return new HashSet<string>(
                _rows.Where(row => row.Target != null).Select(row => row.Target.Key),
                StringComparer.Ordinal);
        }

        private static bool SameName(LinkUpdateRow row, string name)
        {
            return string.Equals(ServerModelIndex.Bare(row.Name), ServerModelIndex.Bare(name),
                StringComparison.CurrentCultureIgnoreCase);
        }

        private void ShowSearch()
        {
            _widerButton.IsEnabled = _reach != null && RevitServerClient.ParentFolder(_reach.Path) != null;

            if (_searched.Count == 0)
            {
                _searchText.Foreground = _host?.Entry != null && _host.Entry.Origin == LinkOrigin.Server
                    ? SystemColors.GrayTextBrush
                    : Brushes.Firebrick;
                _searchText.Text = _host?.Entry != null && _host.Entry.Origin == LinkOrigin.Server
                    ? "Revit Server has not been read yet."
                    : "The open model is not on Revit Server, so there is nowhere to start from: " +
                      "choose the folder to search in with \"Another folder…\".";
                return;
            }

            var text = "Searched: " + string.Join(", ", _searched.Select(folder => folder.Display)) +
                       " — " + _index.FolderCount + " " + (_index.FolderCount == 1 ? "folder" : "folders") +
                       ", " + _index.ModelCount + " " + (_index.ModelCount == 1 ? "model" : "models") + ".";

            if (_index.LimitReached)
                text += " The search stopped at " + ServerModelIndex.FolderLimit + " folders — a model further away may be missed.";

            if (_index.Failures.Count > 0)
                text += " Could not be read: " + string.Join("; ", _index.Failures.Take(3)) +
                        (_index.Failures.Count > 3 ? " and " + (_index.Failures.Count - 3) + " more" : string.Empty) + ".";

            _searchText.Foreground = _index.LimitReached || _index.Failures.Count > 0
                ? Brushes.Firebrick
                : SystemColors.GrayTextBrush;
            _searchText.Text = text;
        }

        // ───────────────────────────── matching ─────────────────────────────

        /// <summary>
        /// Gives every row its drop-down out of what has been read: the models of the same name, the
        /// nearest first; failing those, the ones whose name differs only in punctuation or a version
        /// suffix; a model picked by hand always stays on top. The choice already made survives a wider
        /// search wherever it still can.
        /// </summary>
        private void Match()
        {
            _matching = true;

            foreach (var row in _rows)
            {
                var previous = row.Target?.Key;
                var manual = row.Targets.Where(target => target.IsManual).ToList();

                var exact = _index.Named(row.Name);
                var models = exact.Count > 0 ? exact : _index.Resembling(row.Name);

                var found = models
                    .Select(model => new LinkTarget(model.RsnPath, exact.Count > 0, false, ServerModelIndex.Distance(_origin, model)))
                    .OrderBy(target => target.Distance)
                    .ThenBy(target => target.RsnPath, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                var targets = manual
                    .Concat(found.Where(target => manual.All(chosen => chosen.Key != target.Key)))
                    .ToList();

                row.Targets = targets;
                row.Target = Choose(row, targets, found, previous);
            }

            _matching = false;
        }

        private static LinkTarget Choose(LinkUpdateRow row, List<LinkTarget> targets, List<LinkTarget> found, string previous)
        {
            var kept = targets.FirstOrDefault(target => target.Key == previous);
            if (kept != null)
                return kept;

            // A link already pointing at a model that is here needs nothing — show it as it stands.
            var current = targets.FirstOrDefault(target => target.Key == row.Info.Current.Key);
            if (current != null)
                return current;

            var manual = targets.FirstOrDefault(target => target.IsManual);
            if (manual != null)
                return manual;

            if (found.Count == 1)
                return found[0];

            // Several: the nearest is preselected only when it is strictly nearer than the next one.
            // A tie means nothing is chosen — guessing between two equally likely models is the user's call.
            if (found.Count > 1 && found[0].Distance < found[1].Distance)
                return found[0];

            return null;
        }

        // ───────────────────────────── check boxes ─────────────────────────────

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(_rows, row => value && row.CanUpdate);
        }

        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<LinkUpdateRow>().Where(row => row.CanUpdate).ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);

            SetMany(rows, row => value);
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || InsideComboBox(e.OriginalSource))
                return;

            ToggleSelectedRows();
            e.Handled = true;
        }

        /// <summary>Setting check boxes in bulk: the table is revalidated once, at the end.</summary>
        private void SetMany(IEnumerable<LinkUpdateRow> rows, Func<LinkUpdateRow, bool> value)
        {
            _settingMany = true;

            foreach (var row in rows)
            {
                row.IsSelected = value(row);
                row.IsTouched = true;
            }

            _settingMany = false;

            Revalidate();
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_settingMany || _matching)
                return;

            var row = (LinkUpdateRow)sender;

            if (e.PropertyName == nameof(LinkUpdateRow.IsSelected))
            {
                row.IsTouched = true;
                Revalidate();
            }
            else if (e.PropertyName == nameof(LinkUpdateRow.Target))
            {
                // A model picked from the drop-down is a decision about this link: it is checked with it.
                row.IsTouched = true;
                Revalidate();

                _settingMany = true;
                row.IsSelected = row.CanUpdate;
                _settingMany = false;

                Revalidate();
            }
        }

        private void UpdateChooseButton()
        {
            var row = _grid.SelectedItems.OfType<LinkUpdateRow>().FirstOrDefault();
            _chooseButton.IsEnabled = _grid.SelectedItems.Count == 1 && row != null && row.Info.Obstacle.Length == 0;
        }

        // ───────────────────────────── validation ─────────────────────────────

        /// <summary>
        /// Brings the window in line with the rows: what each can do, the check marks the window still
        /// owns, every "State", the summary and the button — in one place, so the table can never promise
        /// one thing while the button does another.
        /// </summary>
        private void Revalidate()
        {
            // Every link's current place, to catch a server model that is linked here already.
            var linkedAs = new Dictionary<string, LinkUpdateRow>(StringComparer.Ordinal);
            foreach (var row in _rows)
            {
                if (!linkedAs.ContainsKey(row.Info.Current.Key))
                    linkedAs[row.Info.Current.Key] = row;
            }

            _settingMany = true;

            foreach (var row in _rows)
            {
                LinkUpdateRow other;
                var twin = row.Target != null && linkedAs.TryGetValue(row.Target.Key, out other) && other != row;

                row.CanUpdate = row.Info.Obstacle.Length == 0 &&
                                row.Target != null &&
                                row.Target.Key != row.Info.Current.Key &&
                                !twin;

                if (!row.CanUpdate)
                    row.IsSelected = false;
                else if (!row.IsTouched)
                    row.IsSelected = Certain(row);
            }

            _settingMany = false;

            var chosen = _rows
                .Where(row => row.IsSelected && row.CanUpdate)
                .GroupBy(row => row.Target.Key)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group)
                .ToList();

            foreach (var row in _rows)
                Describe(row, linkedAs, chosen);

            UpdateSummary(chosen.Count > 0);
        }

        /// <summary>
        /// Whether the window checks a row by itself: exactly one model of exactly the link's name, and a
        /// link from BIM360 (linked from the cloud or through Desktop Connector) or another Revit Server
        /// path — a plain file link may be a deliberate local model, and is moved to the server only when
        /// the user says so.
        /// </summary>
        private static bool Certain(LinkUpdateRow row)
        {
            var found = row.Targets.Where(target => !target.IsManual).ToList();

            return !PlainFile(row) &&
                   row.Target != null && !row.Target.IsManual && row.Target.SameName &&
                   found.Count == 1;
        }

        /// <summary>A file link that is not a BIM360 model seen through Desktop Connector.</summary>
        private static bool PlainFile(LinkUpdateRow row)
        {
            return row.Info.Current.Origin == LinkOrigin.File && !row.Info.IsCloud;
        }

        /// <summary>Fills in one row's "State": what pointing it will do, or why it cannot be pointed.</summary>
        private void Describe(LinkUpdateRow row, Dictionary<string, LinkUpdateRow> linkedAs, List<LinkUpdateRow> clashing)
        {
            if (row.Info.Obstacle.Length > 0)
            {
                Say(row, true, row.Info.Obstacle);
                return;
            }

            if (row.Target == null)
            {
                if (_searched.Count == 0)
                    Say(row, false, string.Empty);
                else if (row.Targets.Count == 0 && PlainFile(row))
                    Say(row, false, "A file link; no model of this name on the server");
                else if (row.Targets.Count == 0)
                    Say(row, true, "Not found on the server" +
                                   (_index.LimitReached ? " — the search stopped early, try \"Choose on server…\"" : " — \"Search wider\" or \"Choose on server…\""));
                else
                    Say(row, true, row.Targets.Count + " models with this name, equally near — choose one");
                return;
            }

            if (row.Target.Key == row.Info.Current.Key)
            {
                Say(row, false, "Already points here");
                return;
            }

            LinkUpdateRow other;
            if (linkedAs.TryGetValue(row.Target.Key, out other) && other != row)
            {
                Say(row, true, "This model is already linked here as \"" + other.Name +
                               "\" — remove one of the two links in Manage Links first");
                return;
            }

            if (clashing.Contains(row))
            {
                var names = clashing.Where(item => item != row && item.Target.Key == row.Target.Key).Select(item => "\"" + item.Name + "\"");
                Say(row, true, "The same server model is chosen for " + string.Join(", ", names) + " too");
                return;
            }

            if (!row.Target.SameName)
            {
                Say(row, true, "The name differs: " + row.Target.Name + " — check it is the same model");
                return;
            }

            var sameName = row.Targets.Count(target => !target.IsManual && target.SameName);
            if (!row.Target.IsManual && sameName > 1)
            {
                Say(row, true, sameName + " models with this name — the nearest is chosen, check it");
                return;
            }

            if (PlainFile(row) && !row.IsSelected)
            {
                Say(row, false, "A file link — check it to point it at the server");
                return;
            }

            Say(row, false, row.IsSelected
                ? (row.Info.IsLoaded ? "Will be reloaded from the server" : "Will be loaded from the server")
                : "Found on the server");
        }

        private static void Say(LinkUpdateRow row, bool warning, string text)
        {
            row.IsWarning = warning;
            row.StatusText = text;
        }

        private void UpdateSummary(bool clash)
        {
            var updatable = _rows.Count(row => row.CanUpdate);
            var marked = _rows.Where(row => row.IsSelected && row.CanUpdate).ToList();

            _syncingSelectAll = true;
            _selectAll.IsChecked = updatable == 0 || marked.Count == 0
                ? false
                : marked.Count == updatable ? true : (bool?)null;
            _syncingSelectAll = false;

            _updateButton.Content = marked.Count == 0 ? "Update paths" : "Update paths (" + marked.Count + ")";

            var nested = _nested > 0
                ? " Nested links (" + _nested + ") are not listed: they follow the models they are nested in."
                : string.Empty;

            if (clash)
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "One server model is chosen for two links — Revit holds one link per model. " +
                               "Choose another model for one of them, or clear its check box.";
                _updateButton.IsEnabled = false;
                return;
            }

            if (_searched.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Links in the model: " + _rows.Count + "." + nested;
                _updateButton.IsEnabled = false;
                return;
            }

            var already = _rows.Count(row => row.Target != null && row.Target.Key == row.Info.Current.Key);
            var missing = _rows.Count(row => row.Info.Obstacle.Length == 0 && row.Target == null);

            var text = "Links: " + _rows.Count + ", checked: " + marked.Count +
                       (already > 0 ? ", already on the server: " + already : string.Empty) +
                       (missing > 0 ? ", no server model chosen: " + missing : string.Empty) + "." + nested;

            _status.Foreground = SystemColors.GrayTextBrush;
            _status.Text = text;
            _updateButton.IsEnabled = marked.Count > 0;
        }

        // ───────────────────────────── actions ─────────────────────────────

        private void OnUpdate(object sender, RoutedEventArgs e)
        {
            var marked = _rows.Where(row => row.IsSelected && row.CanUpdate).ToList();
            if (marked.Count == 0)
                return;

            if (!Confirm(marked))
                return;

            Selected = marked;
            DialogResult = true;
        }

        /// <summary>
        /// The confirmation names every link and its new path, and says the one thing that cannot be
        /// taken back: reloading a link clears Revit's undo history.
        /// </summary>
        private bool Confirm(IReadOnlyList<LinkUpdateRow> marked)
        {
            const int limit = 15;

            var text = "Point " + marked.Count + " " + (marked.Count == 1 ? "link" : "links") + " at Revit Server?\n\n" +
                       string.Join("\n", marked.Take(limit).Select(row => "• " + row.Name + " → " + row.Target.RsnPath)) +
                       (marked.Count > limit ? "\n… and " + (marked.Count - limit) + " more" : string.Empty) +
                       "\n\nEach link is loaded from its new place, which takes about as long as opening it. " +
                       "The links stay the same elements — placement, pins, the project workset, view settings " +
                       "and Copy/Monitor are kept; the worksets open inside each link are kept as they were.\n\n" +
                       "Reloading a link clears Revit's undo history: nothing done before it can be undone with Ctrl+Z. " +
                       "Save the model first.";

            var answer = MessageBox.Show(this, text, WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.Yes);

            return answer == MessageBoxResult.Yes;
        }
    }
}
