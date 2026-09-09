using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    /// The "Link Manager" window: a list of models to link to the open project, and one setup for
    /// all of them — how to place them and which worksets to close.
    ///
    /// The point of the button is the word "one". In Revit every link is inserted through its own
    /// dialog, and each time "By shared coordinates" is chosen again and "00_Shared levels and
    /// grids" is unchecked again. On twenty links that is twenty identical dialogs. Here the list
    /// is gathered as a whole — from files, from Revit Server, from BIM360, from a saved set — the
    /// placement is chosen once, the worksets are checked once by name, and everything loads as a batch.
    ///
    /// Worksets here come in two different kinds, and they must not be confused.
    /// **The worksets inside a link** (the lower list) are what to open or close inside the link;
    /// they are checked by name, not by id: every model has its own ids, and the only thing
    /// "00_Shared levels and grids" has in common across every link is the name. For the same
    /// reason there is a rule next to the names: one model calls the workset "00_Shared Levels and
    /// Grids", another calls it something else in another language, and a "contains" rule catches
    /// both while a plain name list would not. **The project workset** (the "Project workset"
    /// column) is where to put the link itself inside the open model. It is different for every
    /// link — architecture goes into "01_Link_AR", structure into "01_Link_KR" — so it is edited
    /// right in the row, and the "Set for checked" button only saves clicks when the workset happens
    /// to be shared.
    ///
    /// The model list does not have to be gathered by hand: the "Building Kit…" button offers a
    /// ready-made set of links — the models of every discipline of the same building, found
    /// through the project's folders (see <see cref="ModelKitWindow"/>). From there they land in
    /// the same table and follow the same rules as models added any other way.
    ///
    /// Links already in the project are not hidden from the list: they show up with the "Already
    /// in the project" status, and they can be checked too — then they reload with the new
    /// workset setup, and if the project workset is changed they move into it together with all of
    /// their instances. The placement of an existing link is never changed: Revit will not allow that.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class LinkManagerWindow : Window
    {
        private const string WindowTitle = "Link Manager";

        private readonly ObservableCollection<LinkRow> _all = new ObservableCollection<LinkRow>();
        private readonly ObservableCollection<LinkRow> _visible = new ObservableCollection<LinkRow>();
        private readonly ObservableCollection<WorksetRow> _worksets = new ObservableCollection<WorksetRow>();

        private readonly Func<IReadOnlyList<LinkRow>, LinkWorksetScan> _readWorksets;
        private readonly LinkPreferences _preferences;

        /// <summary>The open model itself — the "Building Kit" takes the building and the folder from it.</summary>
        private readonly HostModel _host;

        /// <summary>The open project's worksets — with "(active)" as the first entry.</summary>
        private readonly List<string> _hostWorksets = new List<string> { LinkRow.ActiveWorkset };

        private readonly ComboBox _hostWorksetBox;
        private readonly CheckBox _matchWorksetBox;
        private readonly ComboBox _scopeBox;
        private readonly ComboBox _setBox;
        private readonly ComboBox _placementBox;
        private readonly ComboBox _attachmentBox;
        private readonly CheckBox _relativeBox;
        private readonly ComboBox _worksetModeBox;
        private readonly ComboBox _ruleBox;
        private readonly TextBox _patternBox;
        private readonly Button _scanButton;
        private readonly TextBlock _worksetCaption;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly DataGrid _worksetGrid;
        private readonly TextBlock _status;
        private readonly Button _loadButton;

        private bool _syncingSelectAll;
        private bool _settingMany;
        private bool _worksetsRead;

        /// <summary>Rows whose project workset was filled in by the name-based guess — so it can be cleared when the guess is turned off.</summary>
        private readonly HashSet<LinkRow> _autoWorkset = new HashSet<LinkRow>();

        /// <summary>The guess is changing a row's workset right now — do not count that as a hand edit.</summary>
        private bool _suggesting;

        /// <summary>Whether the project has per-discipline worksets; computed on first request.</summary>
        private bool? _usesDisciplineWorksets;

        /// <summary>The links the user confirmed for loading.</summary>
        public IReadOnlyList<LinkRow> Selected { get; private set; } = new List<LinkRow>();

        /// <summary>The setup the command will load the whole batch with.</summary>
        public LinkPreferences Preferences => _preferences;

        /// <summary>The project has worksets, that is, it is workshared.</summary>
        private bool HasHostWorksets => _hostWorksets.Count > 1;

        /// <param name="existing">The links already in the project.</param>
        /// <param name="hostWorksets">
        /// The open project's worksets — the ones a link can be put into.
        /// A non-workshared project means an empty list, and the workset column is hidden.
        /// </param>
        /// <param name="readWorksets">
        /// Reads the worksets of the chosen models without opening them.
        /// All the work with Revit is done by the command — the window only calls it and shows the result.
        /// </param>
        /// <param name="host">Where the open project lives and what it is called; may be empty.</param>
        public LinkManagerWindow(
            IReadOnlyList<LinkRow> existing,
            IReadOnlyList<string> hostWorksets,
            Func<IReadOnlyList<LinkRow>, LinkWorksetScan> readWorksets,
            HostModel host)
        {
            _readWorksets = readWorksets;
            _host = host;
            _preferences = LinkPreferences.Load();

            if (hostWorksets != null)
                _hostWorksets.AddRange(hostWorksets);

            Title = WindowTitle;
            Width = 1080;
            Height = 780;
            MinWidth = 760;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            foreach (var row in existing ?? new List<LinkRow>())
            {
                Normalize(row);
                _all.Add(row);
            }

            _scopeBox = new ComboBox { Width = 200, VerticalAlignment = VerticalAlignment.Center };
            _scopeBox.Items.Add("Every link");
            _scopeBox.Items.Add("New only");
            _scopeBox.Items.Add("Already in the project only");
            _scopeBox.SelectedIndex = 0;
            _scopeBox.ToolTip = "Only what is shown in the table is acted on: a hidden row loses its check mark.";
            _scopeBox.SelectionChanged += (s, e) => RebuildVisible();

            _setBox = new ComboBox
            {
                Width = 220,
                IsEditable = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "A saved model list. For BIM360 this is the main way to work:\n" +
                    "gather the list once and it can be offered to every project after,\n" +
                    "even when the cloud cannot be reached.\n" +
                    "The name can be chosen from the list or typed in."
            };
            ReloadSetNames();

            _placementBox = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center };
            _placementBox.Items.Add("By shared coordinates");
            _placementBox.Items.Add("Origin to origin");
            _placementBox.Items.Add("Centre to centre");
            _placementBox.Items.Add("By project site location");
            _placementBox.SelectedIndex = (int)_preferences.Placement;
            _placementBox.ToolTip =
                "One placement for every new link — the whole point of the button.\n" +
                "Links already in the project keep their placement: Revit will not allow it to be changed.";

            _attachmentBox = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center };
            _attachmentBox.Items.Add("Overlay");
            _attachmentBox.Items.Add("Attachment");
            _attachmentBox.SelectedIndex = _preferences.IsAttachment ? 1 : 0;
            _attachmentBox.ToolTip =
                "Overlay — the link will not carry over into a model that links to this one.\n" +
                "Attachment — it will.";

            _hostWorksetBox = new ComboBox
            {
                Width = 220,
                ItemsSource = _hostWorksets,
                SelectedIndex = 0,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = HasHostWorksets,
                ToolTip = HasHostWorksets
                    ? "The open project's workset the links go into.\n" +
                      "The button next to it sets it on every checked row; in the table itself\n" +
                      "each link's workset can still be changed on its own."
                    : "The project is not workshared — it has no worksets."
            };

            _matchWorksetBox = new CheckBox
            {
                Content = "Guess from the model name",
                IsChecked = _preferences.MatchProjectWorkset,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                IsEnabled = HasHostWorksets,
                ToolTip =
                    "For a new link with no workset set, the add-in takes the discipline code from the\n" +
                    "model name (…_OV_R22 → OV) and picks the project workset whose name carries\n" +
                    "that code as a separate word: \"01_Link_OV\", and the like. A hand-made choice in\n" +
                    "the table is never overwritten. The code list is edited in links\\_settings.txt (the DISCIPLINE lines)."
            };
            _matchWorksetBox.Checked += (s, e) => ApplyWorksetSuggestions();
            _matchWorksetBox.Unchecked += (s, e) => ClearWorksetSuggestions();

            _relativeBox = new CheckBox
            {
                Content = "Relative path",
                IsChecked = _preferences.IsRelativePath,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip =
                    "For file links only: the path is remembered relative to the project,\n" +
                    "so the folder holding the models can be moved as a whole.\n" +
                    "For Revit Server and BIM360 the path is always absolute."
            };

            _worksetModeBox = new ComboBox { Width = 230, VerticalAlignment = VerticalAlignment.Center };
            _worksetModeBox.Items.Add("Open all worksets");
            _worksetModeBox.Items.Add("Close all worksets");
            _worksetModeBox.Items.Add("As last opened");
            _worksetModeBox.SelectedIndex = (int)_preferences.WorksetMode;
            _worksetModeBox.SelectionChanged += (s, e) => UpdateWorksetCaption();

            _scanButton = new Button
            {
                Content = "Read Worksets",
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                IsEnabled = _readWorksets != null,
                ToolTip =
                    "Reads the workset names of the checked models without opening them.\n" +
                    "This is only for seeing the name list: on load the worksets are read again\n" +
                    "regardless — the worksets themselves still have to be found by name."
            };
            _scanButton.Click += OnScanWorksets;

            _ruleBox = new ComboBox { Width = 170, VerticalAlignment = VerticalAlignment.Center };
            _ruleBox.Items.Add("starting with");
            _ruleBox.Items.Add("containing");
            _ruleBox.SelectedIndex = _preferences.WorksetPatternContains ? 1 : 0;

            _patternBox = new TextBox
            {
                Text = _preferences.WorksetPattern,
                MinWidth = 170,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2),
                ToolTip =
                    "The rule also applies on load, not only through the \"Check\" button:\n" +
                    "in every link it catches that link's own worksets, even when the exact\n" +
                    "name is not in the list below. That is how \"00_Shared Levels and Grids\"\n" +
                    "and a differently-named equivalent are both caught by one \"00_\" rule."
            };
            _patternBox.TextChanged += (s, e) => UpdateSummary();

            _worksetCaption = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

            _selectAll = new CheckBox
            {
                IsChecked = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every shown link"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildLinkGrid();
            _worksetGrid = BuildWorksetGrid();
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _loadButton = new Button
            {
                Content = "Load",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _loadButton.Click += OnLoad;

            var cancelButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(cancelButton);

            foreach (var row in _all)
                row.PropertyChanged += OnRowChanged;

            // The workset names from last time are visible right away: usually there is no need to change them at all.
            foreach (var name in _preferences.Worksets)
                AddWorkset(name, true);

            foreach (var workset in _worksets)
                workset.IsSelected = true;

            UpdateWorksetCaption();
            RebuildVisible();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // where to add from
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // saved sets
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // link table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // placement
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // project workset
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // worksets inside links
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });                  // workset list
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "Gather a model list, choose the placement and the worksets — " +
                       "and everything checked links in one operation. Only what is shown in the table is acted on.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var sources = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            sources.Children.Add(SourceButton("Files…", "Ordinary .rvt files: a disk or a network folder.", OnAddFiles));
            sources.Children.Add(SourceButton("Revit Server…", "Browse folders and models on Revit Server.", OnBrowseServer));
            sources.Children.Add(SourceButton("BIM360…", "Browse BIM360/ACC accounts, projects and folders.", OnBrowseCloud));
            sources.Children.Add(SourceButton("BIM360 by GUID…", "Enter cloud models as pairs of GUIDs — when browsing is unavailable.", OnAddCloudByGuid));
            sources.Children.Add(SourceButton("Building Kit…",
                "Finds the models of every discipline of your building on its own — by the project folders and the building number in the name.",
                OnAddKit));
            sources.Children.Add(SourceButton("Remove from list", "Removes the checked rows from the table. The links in the project are left untouched.", OnRemove));

            sources.Children.Add(new TextBlock
            {
                Text = "Show:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 8, 0)
            });
            sources.Children.Add(_scopeBox);

            Grid.SetRow(sources, 1);
            root.Children.Add(sources);

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
            sets.Children.Add(SourceButton("Delete set", "Deletes the saved set from the Windows profile.", OnDeleteSet));

            Grid.SetRow(sets, 2);
            root.Children.Add(sets);

            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            var placement = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            placement.Children.Add(new TextBlock
            {
                Text = "Placement for every link:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            placement.Children.Add(_placementBox);
            placement.Children.Add(new TextBlock
            {
                Text = "Link type:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 8, 0)
            });
            placement.Children.Add(_attachmentBox);
            placement.Children.Add(_relativeBox);

            Grid.SetRow(placement, 4);
            root.Children.Add(placement);

            var hostWorkset = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            hostWorkset.Children.Add(new TextBlock
            {
                Text = "Put the links into the project workset:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            hostWorkset.Children.Add(_hostWorksetBox);

            var applyWorkset = SourceButton("Set for checked",
                "Sets the workset chosen on the left on every checked row of the table.", OnApplyHostWorkset);
            applyWorkset.Margin = new Thickness(8, 0, 8, 4);
            applyWorkset.IsEnabled = HasHostWorksets;
            hostWorkset.Children.Add(applyWorkset);

            hostWorkset.Children.Add(_matchWorksetBox);

            hostWorkset.Children.Add(new TextBlock
            {
                Text = HasHostWorksets
                    ? "in the table the workset can also be edited per link"
                    : "the project is not workshared — it has no worksets",
                Foreground = SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            Grid.SetRow(hostWorkset, 5);
            root.Children.Add(hostWorkset);

            var worksets = new WrapPanel();
            worksets.Children.Add(_scanButton);
            worksets.Children.Add(new TextBlock
            {
                Text = "On load:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            worksets.Children.Add(_worksetModeBox);
            worksets.Children.Add(new TextBlock
            {
                Text = "Name rule:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 8, 0)
            });
            worksets.Children.Add(_ruleBox);
            worksets.Children.Add(_patternBox);
            worksets.Children.Add(SourceButton("Check by rule", "Checks the names matching the rule in the list below.", OnMarkWorksets));

            var worksetHeader = new StackPanel();
            worksetHeader.Children.Add(worksets);
            worksetHeader.Children.Add(_worksetCaption);

            Grid.SetRow(worksetHeader, 6);
            root.Children.Add(worksetHeader);

            Grid.SetRow(_worksetGrid, 7);
            root.Children.Add(_worksetGrid);

            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_loadButton);
            buttons.Children.Add(cancelButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 8);
            root.Children.Add(bottom);

            return root;
        }

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
            button.Click += handler;

            return button;
        }

        private DataGrid BuildLinkGrid()
        {
            var grid = new DataGrid
            {
                ItemsSource = _visible,
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
                RowHeaderWidth = 0,
                Margin = new Thickness(0, 4, 0, 8)
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate()
            });

            grid.Columns.Add(GridBuilder.TextColumn("Model", "Name", new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(GridBuilder.TextColumn("Source", "Kind", new DataGridLength(100)));
            grid.Columns.Add(GridBuilder.TextColumn("Location", "Location", new DataGridLength(1.2, DataGridLengthUnitType.Star)));

            // The project workset is the only thing edited right in the row: it is different for
            // every link, and "set for all" does not always help here.
            if (HasHostWorksets)
            {
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = "Project workset",
                    Width = new DataGridLength(190),
                    CanUserSort = true,
                    SortMemberPath = "Workset",
                    CellTemplate = BuildWorksetTemplate(_hostWorksets)
                });
            }

            grid.Columns.Add(GridBuilder.TextColumn("Worksets inside", "Worksets", new DataGridLength(110)));
            grid.Columns.Add(GridBuilder.TextColumn("State", "Status", new DataGridLength(200)));

            grid.MouseDoubleClick += (s, e) => ToggleSelectedRows();
            grid.PreviewKeyDown += OnGridKeyDown;

            return grid;
        }

        private DataGrid BuildWorksetGrid()
        {
            var grid = new DataGrid
            {
                ItemsSource = _worksets,
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
                RowHeaderWidth = 0,
                Margin = new Thickness(0, 6, 0, 8)
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = string.Empty,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate()
            });

            grid.Columns.Add(GridBuilder.TextColumn("Workset", "Name", new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(GridBuilder.TextColumn("Found in", "Where", new DataGridLength(160)));

            grid.MouseDoubleClick += (s, e) => ToggleSelectedWorksets();

            return grid;
        }

        /// <summary>
        /// The drop-down in a cell. It sits in the cell template rather than the editing one: the
        /// table as a whole is read-only, and this way the workset is visible and can be changed
        /// with one click, without the row switching into edit mode.
        /// </summary>
        private static DataTemplate BuildWorksetTemplate(IEnumerable<string> worksets)
        {
            var combo = new FrameworkElementFactory(typeof(ComboBox));
            combo.SetValue(ItemsControl.ItemsSourceProperty, worksets);
            combo.SetBinding(Selector.SelectedItemProperty,
                new Binding("Workset") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            combo.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));
            combo.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            return new DataTemplate { VisualTree = combo };
        }

        // ───────────────────────────── where the links come from ─────────────────────────────

        private void OnAddFiles(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.Files(this, true), "file(s)");
        }

        private void OnBrowseServer(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.Server(this, WindowTitle, _preferences, Keys), "Revit Server model(s)");
        }

        private void OnBrowseCloud(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.Cloud(this, WindowTitle, Keys), "BIM360 model(s)");
        }

        private void OnAddCloudByGuid(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.CloudByGuid(this, DefaultRegion()), "cloud model(s)");
        }

        /// <summary>
        /// A ready-made link kit by building number. What is found arrives here as ordinary
        /// entries and lives like everything else from then on: duplicates are filtered out, and
        /// the project workset is guessed from the same discipline the model was found by.
        /// </summary>
        private void OnAddKit(object sender, RoutedEventArgs e)
        {
            var window = new ModelKitWindow(_host, _preferences, Keys) { Owner = this };

            if (window.ShowDialog() == true)
                Add(window.Selected, "kit model(s)");
        }

        /// <summary>The region the GUID-entry window opens with: the same as the models already gathered.</summary>
        private string DefaultRegion()
        {
            var region = _all
                .Where(row => row.Entry.Origin == LinkOrigin.Cloud)
                .Select(row => row.Entry.Region)
                .FirstOrDefault(value => !string.IsNullOrEmpty(value));

            return region ?? "US";
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
                return;

            foreach (var row in marked)
            {
                row.PropertyChanged -= OnRowChanged;
                _all.Remove(row);
            }

            RebuildVisible();
        }

        /// <summary>Sets the chosen project workset on every checked row.</summary>
        private void OnApplyHostWorkset(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "Check the links to set the workset for.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var workset = (string)_hostWorksetBox.SelectedItem ?? LinkRow.ActiveWorkset;

            foreach (var row in marked)
                row.Workset = workset;
        }

        /// <summary>Adds models to the table, dropping the ones already in it.</summary>
        private void Add(IReadOnlyList<LinkEntry> entries, string what)
        {
            if (entries == null || entries.Count == 0)
                return;

            var known = Keys();
            var added = 0;

            foreach (var entry in entries)
            {
                if (!known.Add(entry.Key))
                    continue;

                var row = new LinkRow(entry) { IsSelected = true };
                Normalize(row);
                SuggestWorkset(row);
                row.PropertyChanged += OnRowChanged;
                _all.Add(row);
                added++;
            }

            RebuildVisible();

            if (added < entries.Count)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Added " + what + ": " + added +
                               ". Already in the list: " + (entries.Count - added) + ".";
            }
        }

        private HashSet<string> Keys()
        {
            return new HashSet<string>(_all.Select(row => row.Key), StringComparer.Ordinal);
        }

        /// <summary>
        /// A workset that arrived from a saved list may not exist in this project — every project
        /// has its own worksets. Such a name cannot be silently kept: it is not in the drop-down,
        /// the cell would look empty, and on load the link would go to the wrong place.
        /// It is reset to the active one, and this is said in the "State" column.
        /// </summary>
        private void Normalize(LinkRow row)
        {
            var chosen = row.Entry.Workset;
            if (chosen.Length == 0)
                return;

            if (_hostWorksets.Any(name => string.Equals(name, chosen, StringComparison.CurrentCultureIgnoreCase)))
                return;

            row.Workset = LinkRow.ActiveWorkset;
            row.Note = "There is no \"" + chosen + "\" workset in the project";
        }

        // ───────────────────────────── guessing the project workset from the name ─────────────────────────────

        /// <summary>
        /// Sets the project workset on a new link from the discipline code in the model name — if
        /// the guess is turned on, the link is new, and no workset has been set on it yet (neither
        /// by hand nor from a saved set). Exactly one matching workset — it is set; several — only
        /// noted in "State", the choice is the user's; none — kept quiet, so as not to clutter the column.
        /// </summary>
        private void SuggestWorkset(LinkRow row)
        {
            if (!HasHostWorksets || _matchWorksetBox?.IsChecked != true)
                return;

            if (row.IsExisting || row.Entry.Workset.Length > 0)
                return;

            var code = DisciplineCatalog.Detect(row.Entry.Name, _preferences.EffectiveDisciplines);
            if (code.Length == 0)
            {
                // No discipline code visible in the name. Where per-discipline worksets exist,
                // that is exactly the answer to "why is it empty" — staying silent would look like
                // a breakage.
                if (UsesDisciplineWorksets)
                    row.Note = "No discipline visible in the model name";

                return;
            }

            var worksets = _hostWorksets.Skip(1).ToList();
            var matches = DisciplineCatalog.MatchingWorksets(code, worksets);

            // Out of several matches, the one named like the model itself is taken ("…_AR_B03" on
            // a model of building B03), or like the other disciplines' worksets ("01_Link_AR" next
            // to "01_Link_ES" and "01_Link_OV", rather than "05_AR_Elevations").
            var chosen = DisciplineCatalog.Preferred(
                code, matches, worksets, _preferences.EffectiveDisciplines, row.Entry.Name);

            _suggesting = true;
            try
            {
                if (chosen != null)
                {
                    row.Workset = chosen;
                    row.Note = "Workset by discipline \"" + code + "\"";
                    _autoWorkset.Add(row);
                }
                else if (matches.Count > 1)
                {
                    // The names in the note are not decoration: without them "several match" says
                    // nothing about what to choose from.
                    row.Note = "Discipline \"" + code + "\": matches " + string.Join(", ", matches.Take(3)) +
                               (matches.Count > 3 ? " and " + (matches.Count - 3) + " more" : string.Empty) + " — choose one";
                }
                else if (UsesDisciplineWorksets)
                {
                    // Staying quiet here is not allowed: the project has per-discipline worksets,
                    // so a missing one is an answer, not "the guess failed".
                    row.Note = "Discipline \"" + code + "\": no workset with this code in the project";
                }
            }
            finally
            {
                _suggesting = false;
            }
        }

        /// <summary>
        /// The project has per-discipline worksets set up. Computed once: the worksets do not
        /// change while the window is open, and the answer decides whether to say something about
        /// every workset that was not found.
        /// </summary>
        private bool UsesDisciplineWorksets
        {
            get
            {
                if (_usesDisciplineWorksets == null)
                    _usesDisciplineWorksets = DisciplineCatalog.HasDisciplineWorksets(
                        _hostWorksets.Skip(1), _preferences.EffectiveDisciplines);

                return _usesDisciplineWorksets.Value;
            }
        }

        /// <summary>Runs the guess over every row — from the button that turns the guess on.</summary>
        private void ApplyWorksetSuggestions()
        {
            foreach (var row in _all)
                SuggestWorkset(row);

            UpdateSummary();
        }

        /// <summary>Clears what the guess set; leaves hand-made choices untouched.</summary>
        private void ClearWorksetSuggestions()
        {
            _suggesting = true;
            try
            {
                foreach (var row in _all)
                {
                    if (_autoWorkset.Contains(row))
                        row.Workset = LinkRow.ActiveWorkset;

                    // Discipline notes are also the guess's doing: turn it off, and there is nothing left to explain.
                    if (row.Note.StartsWith("Workset by discipline", StringComparison.Ordinal) ||
                        row.Note.StartsWith("Discipline", StringComparison.Ordinal))
                        row.Note = string.Empty;
                }
            }
            finally
            {
                _suggesting = false;
            }

            _autoWorkset.Clear();
            UpdateSummary();
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

        private string SetName => (_setBox.Text ?? string.Empty).Trim();

        private void OnLoadSet(object sender, RoutedEventArgs e)
        {
            if (SetName.Length == 0)
            {
                MessageBox.Show(this, "Choose a set in the list or type in its name.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var entries = LinkSetLibrary.Load(SetName);
            if (entries.Count == 0)
            {
                MessageBox.Show(this, "The set \"" + SetName + "\" is empty or does not exist.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Add(entries, "model(s) from the set");
        }

        private void OnSaveSet(object sender, RoutedEventArgs e)
        {
            if (SetName.Length == 0)
            {
                MessageBox.Show(this, "Type in a set name in the field on the left.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_visible.Count == 0)
            {
                MessageBox.Show(this, "There is nothing in the table to save.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // What is shown gets saved — the same rule as on loading.
                LinkSetLibrary.Save(SetName, _visible.Select(row => row.Entry));
                ReloadSetNames();

                MessageBox.Show(this,
                    "The set \"" + SetName + "\" was saved: " + _visible.Count + " models.\n\n" +
                    LinkSetLibrary.FilePathFor(SetName),
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not save the set.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnDeleteSet(object sender, RoutedEventArgs e)
        {
            if (SetName.Length == 0)
                return;

            var answer = MessageBox.Show(this, "Delete the saved set \"" + SetName + "\"?", WindowTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            try
            {
                LinkSetLibrary.Delete(SetName);
                ReloadSetNames();
                _setBox.Text = string.Empty;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not delete the set.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ───────────────────────────── worksets ─────────────────────────────

        private LinkWorksetMode WorksetMode => (LinkWorksetMode)Math.Max(0, _worksetModeBox.SelectedIndex);

        private string Pattern => _patternBox.Text?.Trim() ?? string.Empty;

        private bool PatternContains => _ruleBox.SelectedIndex == 1;

        /// <summary>The caption above the workset list: under "close all" a check mark means exactly the opposite.</summary>
        private void UpdateWorksetCaption()
        {
            var closing = WorksetMode != LinkWorksetMode.CloseAll;

            _worksetCaption.Text = closing
                ? "The checked worksets will be closed in every chosen link. If a model has no workset with that name, the row is simply skipped."
                : "Only the checked worksets will be open, the rest will close.";

            UpdateSummary();
        }

        private void AddWorkset(string name, bool isRemembered)
        {
            var text = (name ?? string.Empty).Trim();
            if (text.Length == 0)
                return;

            // A duplicate is judged by the same rule the workset is later looked up in a link by.
            // Otherwise a name differing only by a trailing space is silently lost: it never
            // appears as a row, and it can never be found on load either.
            if (_worksets.Any(workset => LinkPreferences.SameWorkset(workset.Name, text)))
                return;

            var row = new WorksetRow(text, isRemembered);
            row.PropertyChanged += OnWorksetChanged;
            _worksets.Add(row);
        }

        /// <summary>
        /// Reads the workset names of the checked models. This is not instant — it goes over the
        /// network to every file — so it sits behind a button and shows the result.
        /// </summary>
        private void OnScanWorksets(object sender, RoutedEventArgs e)
        {
            if (_readWorksets == null)
                return;

            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "Check the links to read the worksets of.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LinkWorksetScan scan;
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                scan = _readWorksets(marked);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not read the worksets.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }

            foreach (var name in scan.Names)
                AddWorkset(name, false);

            _worksetsRead = true;
            RecountWorksets();

            var text = "Models read: " + scan.Scanned + " of " + marked.Count +
                       ".\nDistinct worksets: " + scan.Names.Count + ".";

            if (scan.Failures.Count > 0)
            {
                const int limit = 10;
                text += "\n\nCould not be read (" + scan.Failures.Count + "):\n• " +
                        string.Join("\n• ", scan.Failures.Take(limit));

                if (scan.Failures.Count > limit)
                    text += "\n… and " + (scan.Failures.Count - limit) + " more";
            }

            MessageBox.Show(this, text, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Recomputes how many checked models each workset name occurs in.</summary>
        private void RecountWorksets()
        {
            var marked = Marked();

            // There is something to count only if at least one checked link has had its worksets
            // read: otherwise zero means "not looked at" rather than "in none of them", and the
            // two states must not be confused — the second is where "the add-in did not close the
            // workset" comes from.
            var counted = marked.Any(row => row.WorksetNames != null);

            foreach (var workset in _worksets)
            {
                workset.LinkCount = marked.Count(row => row.WorksetNames != null &&
                    row.WorksetNames.Any(name => LinkPreferences.SameWorkset(name, workset.Name)));

                workset.IsCounted = counted;
            }
        }

        private void OnMarkWorksets(object sender, RoutedEventArgs e)
        {
            if (Pattern.Length == 0)
            {
                MessageBox.Show(this, "Type in a rule string — \"00_\", say.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var marked = 0;

            foreach (var workset in _worksets)
            {
                if (!Matches(workset.Name))
                    continue;

                workset.IsSelected = true;
                marked++;
            }

            if (marked == 0)
            {
                MessageBox.Show(this,
                    "No workset in the list matches the rule.\n\n" +
                    "That is not a problem: the rule is applied to each link separately at load time, " +
                    "even if the name list is empty right now.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private bool Matches(string name)
        {
            var pattern = Pattern;
            if (pattern.Length == 0)
                return false;

            // The same trimming as in the command: the rule must check exactly what will end up
            // closed on load.
            var trimmed = LinkPreferences.NormalizeWorkset(name);

            return PatternContains
                ? trimmed.IndexOf(pattern, StringComparison.CurrentCultureIgnoreCase) >= 0
                : trimmed.StartsWith(pattern, StringComparison.CurrentCultureIgnoreCase);
        }

        private void ToggleSelectedWorksets()
        {
            var rows = _worksetGrid.SelectedItems.OfType<WorksetRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);

            foreach (var row in rows)
                row.IsSelected = value;
        }

        private void OnWorksetChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WorksetRow.IsSelected))
                UpdateSummary();
        }

        // ───────────────────────────── the link table ─────────────────────────────

        private bool InScope(LinkRow row)
        {
            switch (_scopeBox.SelectedIndex)
            {
                case 1:
                    return !row.IsExisting;
                case 2:
                    return row.IsExisting;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Rebuilds the table. A hidden row loses its check mark — the same rule as in the delete
        /// windows: only what is visible is acted on.
        /// </summary>
        private void RebuildVisible()
        {
            _visible.Clear();

            foreach (var row in _all)
            {
                if (InScope(row))
                    _visible.Add(row);
            }

            SetMany(row => InScope(row) && row.IsSelected);
        }

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(row => value && InScope(row));
        }

        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<LinkRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);
            var affected = new HashSet<LinkRow>(rows);

            SetMany(row => affected.Contains(row) ? value : row.IsSelected);
        }

        /// <summary>Setting check marks in bulk: the total is recomputed once, at the end.</summary>
        private void SetMany(Func<LinkRow, bool> value)
        {
            _settingMany = true;

            foreach (var row in _all)
                row.IsSelected = value(row);

            _settingMany = false;

            RecountWorksets();
            UpdateSummary();
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
            // A hand-made change of a row's workset in the table clears the "filled in by the
            // guess" flag: once the guess is turned off, such a choice must not be touched.
            if (e.PropertyName == nameof(LinkRow.Workset))
            {
                if (!_suggesting && _autoWorkset.Remove((LinkRow)sender))
                {
                    var row = (LinkRow)sender;
                    if (row.Note.StartsWith("Workset by discipline", StringComparison.Ordinal))
                        row.Note = string.Empty;
                }

                return;
            }

            if (_settingMany || e.PropertyName != nameof(LinkRow.IsSelected))
                return;

            RecountWorksets();
            UpdateSummary();
        }

        private List<LinkRow> Marked()
        {
            return _visible.Where(row => row.IsSelected).ToList();
        }

        private void UpdateSummary()
        {
            var marked = Marked();
            var fresh = marked.Count(row => !row.IsExisting);
            var again = marked.Count - fresh;

            if (marked.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = _all.Count == 0
                    ? "The list is empty: add models with the buttons above or load a saved set."
                    : "Shown links: " + _visible.Count + " of " + _all.Count + ". Nothing is checked.";
            }
            else
            {
                _status.Foreground = SystemColors.ControlTextBrush;
                _status.Text = "To link afresh: " + fresh +
                               (again > 0 ? ", to reload existing: " + again : string.Empty) +
                               ". " + WorksetNote();
            }

            _loadButton.IsEnabled = marked.Count > 0;
            _loadButton.Content = marked.Count > 0 ? "Load (" + marked.Count + ")" : "Load";

            _syncingSelectAll = true;
            _selectAll.IsChecked = _visible.Count == 0 || marked.Count == 0
                ? false
                : marked.Count == _visible.Count ? true : (bool?)null;
            _syncingSelectAll = false;

            _scanButton.Content = _worksetsRead ? "Read worksets again" : "Read Worksets";
        }

        private string WorksetNote()
        {
            var names = MarkedWorksets().Count;

            if (names == 0 && Pattern.Length == 0)
                return WorksetMode == LinkWorksetMode.CloseAll
                    ? "Every link workset will be closed."
                    : "The worksets are left alone.";

            var verb = WorksetMode == LinkWorksetMode.CloseAll ? "Worksets to open: " : "Worksets to close: ";
            var note = verb + names;

            return Pattern.Length > 0 ? note + " plus whatever matches the rule \"" + Pattern + "\"." : note + ".";
        }

        private List<string> MarkedWorksets()
        {
            return _worksets.Where(workset => workset.IsSelected).Select(workset => workset.Name).ToList();
        }

        // ───────────────────────────── actions ─────────────────────────────

        private void OnLoad(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
                return;

            var fresh = marked.Count(row => !row.IsExisting);
            var again = marked.Count - fresh;

            var text = "Models to link: " + fresh + ".\n" +
                       "Placement: " + _placementBox.SelectedItem + ".\n" +
                       WorksetNote() + "\n";

            if (HasHostWorksets)
            {
                var placed = marked.Count(row => row.Entry.Workset.Length > 0);

                text += placed == 0
                    ? "No link has a project workset set — all will go into the active one.\n"
                    : "A project workset is set on " + placed + " of " + marked.Count +
                      "; the rest will go into the active one.\n";
            }

            text += "\n";

            if (again > 0)
                text += "Existing links to be reloaded: " + again +
                        ". The reload runs first and is not undoable — it wipes the whole document's " +
                        "undo history. Ctrl+Z after it will only bring back the new links, not whatever " +
                        "you did in the project before pressing this.\n\n";

            text += "Revit will stop responding while loading. Continue?";

            var answer = MessageBox.Show(this, text, WindowTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

            if (answer != MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }

        /// <summary>
        /// The settings are saved whenever the window closes — on "Load", on "Close", and on Esc.
        /// That is the whole point of it: the "00_Shared levels and grids" workset is checked once
        /// and offers itself in the next project.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            _preferences.Placement = (LinkPlacement)Math.Max(0, _placementBox.SelectedIndex);
            _preferences.IsAttachment = _attachmentBox.SelectedIndex == 1;
            _preferences.IsRelativePath = _relativeBox.IsChecked == true;
            _preferences.WorksetMode = WorksetMode;
            _preferences.WorksetPattern = Pattern;
            _preferences.WorksetPatternContains = PatternContains;
            _preferences.MatchProjectWorkset = _matchWorksetBox.IsChecked == true;

            _preferences.Worksets.Clear();
            _preferences.Worksets.AddRange(MarkedWorksets());

            _preferences.Save();

            base.OnClosed(e);
        }
    }
}
