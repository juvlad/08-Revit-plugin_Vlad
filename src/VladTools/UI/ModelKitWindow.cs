using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// The "Building Kit" window: the add-in offers the consultants' models it thinks should be
    /// linked to the open model on its own.
    ///
    /// The job it exists for: every model of every building has the same disciplines linked into
    /// it — AR, KR, ES, PS, PT, OV, VK — and every time they are picked by hand from the folders,
    /// taking care not to grab the wrong building. Meanwhile everything in the project is already
    /// named so there is nothing to pick: discipline folders are called <c>3.0_AR</c>,
    /// <c>4.2_KR</c>, <c>5.1_ES</c>, and a model's name carries the building number —
    /// <c>MK3-VSC-B01-AR</c>.
    ///
    /// So the window asks nothing it does not have to: the building number comes from the open
    /// model's name, the project folder from where that model lives, and the search runs by
    /// itself when the window opens. All that is left for the user is to look at the table and
    /// press "Add".
    ///
    /// What was found is shown **before** anything is linked, and that is not overcaution: parsing
    /// names is a heuristic (see <see cref="ModelKit"/>), and a discipline that turned up several
    /// models is one the window refuses to check — the choice there belongs to the user.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class ModelKitWindow : Window
    {
        private const string WindowTitle = "Building Kit";

        private readonly ObservableCollection<ModelKitRow> _rows = new ObservableCollection<ModelKitRow>();

        private readonly HostModel _host;
        private readonly LinkPreferences _preferences;
        private readonly Func<HashSet<string>> _known;

        private readonly ComboBox _buildingBox;
        private readonly TextBox _codesBox;
        private readonly CheckBox _deepBox;
        private readonly TextBlock _rootText;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _addButton;

        private ModelFolder _root;

        /// <summary>The result of the last search: the number checked is appended to it.</summary>
        private string _lastSummary = "Press \"Find\" to walk the discipline folders.";

        /// <summary>The models the user agreed to add to the link list.</summary>
        public IReadOnlyList<LinkEntry> Selected { get; private set; } = new List<LinkEntry>();

        /// <param name="host">The open model: the building comes from its name, the search root from its folder.</param>
        /// <param name="known">Keys of the models already gathered in the link table — no point offering those.</param>
        public ModelKitWindow(HostModel host, LinkPreferences preferences, Func<HashSet<string>> known)
        {
            _host = host ?? new HostModel(null, null, string.Empty);
            _preferences = preferences;
            _known = known ?? (() => new HashSet<string>(StringComparer.Ordinal));

            Title = WindowTitle;
            Width = 900;
            Height = 640;
            MinWidth = 680;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _buildingBox = new ComboBox
            {
                Width = 140,
                IsEditable = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "The piece of the model name the building's models are matched by.\n" +
                    "The list holds the pieces of the open model's name; usually the third one is needed:\n" +
                    "MK3-VSC-B01-AR → B01. A custom value can be typed in too."
            };

            foreach (var token in ModelKit.Tokens(_host.Name))
                _buildingBox.Items.Add(token);

            _buildingBox.Text = InitialBuilding();

            _codesBox = new TextBox
            {
                Text = string.Join(", ", _preferences.EffectiveKit),
                MinWidth = 320,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2),
                ToolTip =
                    "The disciplines that need to be linked in. A folder counts as a discipline\n" +
                    "folder if the code stands in its name as a separate word: \"3.0_AR\" — yes, \"Provod\" — no.\n" +
                    "The list is saved in links\\_settings.txt (the KIT lines)."
            };

            _deepBox = new CheckBox
            {
                Content = "and inside nested discipline folders",
                IsChecked = _preferences.KitDeep,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip =
                    "Descend into a discipline folder when the models do not sit right in it (\"3.0_AR\\Models\").\n" +
                    "Every cloud folder is a network request, so the search never goes deeper than two levels."
            };

            _rootText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0)
            };

            _grid = BuildGrid();
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _addButton = new Button
            {
                Content = "Add",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _addButton.Click += OnAdd;

            var cancelButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(cancelButton);

            _root = StartFolder();
            ShowRoot();
            UpdateSummary();

            // The search runs by itself when the window opens: asking "search now?" when both the
            // building and the folder are already known would mean an extra click for exactly what
            // the window was opened to do.
            Loaded += (sender, args) =>
            {
                if (_root != null && Building.Length > 0)
                    Search(false);
            };
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // building
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // disciplines
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // where to search
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "The add-in looks for your building's models in the discipline folders and offers to link them. " +
                       "The building number comes from the open model's name, the folder from where it lives; " +
                       "both can be changed.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var building = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            building.Children.Add(Caption("Building:"));
            building.Children.Add(_buildingBox);
            building.Children.Add(new TextBlock
            {
                Text = _host.Name.Length > 0 ? "from the name: " + _host.Name : "the open model has not been saved yet",
                Foreground = SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            });

            Grid.SetRow(building, 1);
            root.Children.Add(building);

            var codes = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            codes.Children.Add(Caption("Disciplines:"));
            codes.Children.Add(_codesBox);
            codes.Children.Add(_deepBox);

            Grid.SetRow(codes, 2);
            root.Children.Add(codes);

            var where = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            where.Children.Add(Caption("Search in:"));
            where.Children.Add(_rootText);
            where.Children.Add(SourceButton("Model's folder", "Go back to the folder the open model lives in.", OnRootAuto));
            where.Children.Add(SourceButton("On server…", "Choose a folder on Revit Server.", OnRootServer));
            where.Children.Add(SourceButton("In BIM360…", "Choose a folder in BIM360 / Autodesk Docs.", OnRootCloud));
            where.Children.Add(SourceButton("On disk…", "Choose a folder on disk or on the network — by pointing to any model in it.", OnRootFile));
            where.Children.Add(SourceButton("Find", "Walk the discipline folders again.", OnFind));

            Grid.SetRow(where, 3);
            root.Children.Add(where);

            Grid.SetRow(_grid, 4);
            root.Children.Add(_grid);

            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_addButton);
            buttons.Children.Add(cancelButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 5);
            root.Children.Add(bottom);

            return root;
        }

        private static TextBlock Caption(string text)
        {
            return new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
        }

        private static Button SourceButton(string text, string tooltip, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(8, 0, 0, 4),
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
                RowHeaderWidth = 0,
                Margin = new Thickness(0, 4, 0, 8)
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = string.Empty,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate()
            });

            grid.Columns.Add(GridBuilder.TextColumn("Discipline", "Discipline", new DataGridLength(80)));
            grid.Columns.Add(GridBuilder.TextColumn("Model", "Name", new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(GridBuilder.TextColumn("Folder", "Folder", new DataGridLength(180)));
            grid.Columns.Add(GridBuilder.TextColumn("State", "Status", new DataGridLength(200)));

            grid.MouseDoubleClick += (sender, args) => ToggleSelected();
            grid.PreviewKeyDown += OnGridKeyDown;

            return grid;
        }

        // ───────────────────────────── where and what to search ─────────────────────────────

        private string Building => (_buildingBox.Text ?? string.Empty).Trim();

        /// <summary>The disciplines from the input field; empty — the default list, otherwise there would be nothing to search for.</summary>
        private IReadOnlyList<string> Codes
        {
            get
            {
                var codes = (_codesBox.Text ?? string.Empty)
                    .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(code => code.Trim())
                    .Where(code => code.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return codes.Count > 0 ? (IReadOnlyList<string>)codes : DisciplineCatalog.KitDefaults;
            }
        }

        /// <summary>
        /// The building number on opening: from the open model's name, the piece chosen in the
        /// settings. No name, or fewer pieces than that — the number from last time is kept.
        /// </summary>
        private string InitialBuilding()
        {
            var building = ModelKit.Building(_host.Name, _preferences.BuildingToken);

            return building.Length > 0 ? building : _preferences.KitBuilding ?? string.Empty;
        }

        /// <summary>
        /// The folder to start from: the one the open model lives in (climbing up a level from a
        /// discipline folder). The model is not saved — the folder from last time.
        /// </summary>
        private ModelFolder StartFolder()
        {
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                var root = ModelKit.Root(_host.Folder, Codes);

                return root ?? ModelFolder.Parse(_preferences.KitRoot);
            }
            catch (Exception)
            {
                // The folder could not be worked out — it will be given through the buttons nearby.
                return ModelFolder.Parse(_preferences.KitRoot);
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }
        }

        private void ShowRoot()
        {
            _rootText.Text = _root == null ? "not chosen" : _root.Display;
            _rootText.Foreground = _root == null ? Brushes.Firebrick : SystemColors.ControlTextBrush;
            _rootText.ToolTip = _rootText.Text;
        }

        private void OnRootAuto(object sender, RoutedEventArgs e)
        {
            if (_host.Folder == null)
            {
                MessageBox.Show(this,
                    "Could not work out where the open model lives: the project has never been saved " +
                    "or is open as detached. Give the folder through the buttons next to this one.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetRoot(ModelKit.Root(_host.Folder, Codes));
        }

        private void OnRootServer(object sender, RoutedEventArgs e)
        {
            SetRoot(ModelPicker.PickServerFolder(this, WindowTitle, _preferences));
        }

        private void OnRootCloud(object sender, RoutedEventArgs e)
        {
            SetRoot(ModelPicker.PickCloudFolder(this, WindowTitle));
        }

        private void OnRootFile(object sender, RoutedEventArgs e)
        {
            SetRoot(ModelPicker.PickFileFolder(this));
        }

        private void SetRoot(ModelFolder folder)
        {
            if (folder == null)
                return;

            _root = folder;
            ShowRoot();
        }

        // ───────────────────────────── the search ─────────────────────────────

        private void OnFind(object sender, RoutedEventArgs e)
        {
            Search(true);
        }

        /// <param name="loud">Whether to show failures as a dialog: when the window searches on its own, an extra dialog is not needed.</param>
        private void Search(bool loud)
        {
            if (Building.Length == 0)
            {
                if (loud)
                    MessageBox.Show(this, "Type in the building number — \"B01\", say.", WindowTitle,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_root == null)
            {
                if (loud)
                    MessageBox.Show(this, "Give the folder to search in.", WindowTitle,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var codes = Codes;
            var keys = _known();
            if (_host.Key.Length > 0)
                keys.Add(_host.Key);

            ModelKitScan scan;
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                scan = ModelKit.Find(_root, Building, codes, _deepBox.IsChecked == true, key => keys.Contains(key));
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not browse the folders.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }

            // The search may have climbed a level up — we show the folder it actually ran in,
            // otherwise the wrong one would be remembered next time.
            if (scan.Root != null)
            {
                _root = scan.Root;
                ShowRoot();
            }

            Fill(scan, codes);
        }

        /// <summary>
        /// Lays out what was found into rows. Only what can be checked without guessing gets
        /// checked: a discipline with one model. Several models in a discipline — rows with no
        /// check box and a note in "State": the choice belongs to the user, the same as when
        /// guessing a workset.
        /// </summary>
        private void Fill(ModelKitScan scan, IReadOnlyList<string> codes)
        {
            foreach (var row in _rows)
                row.PropertyChanged -= OnRowChanged;

            _rows.Clear();

            var order = codes
                .Select((code, index) => new { code, index })
                .ToDictionary(pair => pair.code, pair => pair.index, StringComparer.OrdinalIgnoreCase);

            var groups = scan.Hits
                .GroupBy(hit => hit.Discipline, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => order.ContainsKey(group.Key) ? order[group.Key] : int.MaxValue)
                .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var group in groups)
            {
                var choices = group.Count(hit => !hit.IsKnown);

                foreach (var hit in group.OrderBy(hit => hit.Entry.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var status = Status(hit, choices);

                    var row = new ModelKitRow(hit, !hit.IsKnown && choices == 1, status);
                    row.PropertyChanged += OnRowChanged;
                    _rows.Add(row);
                }
            }

            UpdateSummary(scan, codes);
            Complain(scan);
        }

        /// <summary>
        /// Why a row is not checked. The open model itself is told apart from someone else's
        /// already-gathered one: "already in the list" about the open project would sound like a
        /// matching mistake, when really it is that model's rightful place in the discipline —
        /// and thanks to it the discipline will not end up among "not found".
        /// </summary>
        private string Status(ModelKitHit hit, int choices)
        {
            if (hit.Entry.Key == _host.Key)
                return "this is the open model";

            if (hit.IsKnown)
                return "already in the link list";

            return choices > 1 ? "several in this discipline — choose the right one" : string.Empty;
        }

        /// <summary>Unread folders are not a small thing: the kit found in them could be incomplete.</summary>
        private void Complain(ModelKitScan scan)
        {
            if (scan.Failures.Count == 0 && !scan.IsTruncated)
                return;

            const int limit = 10;
            var text = string.Empty;

            if (scan.Failures.Count > 0)
            {
                text = "Could not read " + scan.Failures.Count + " folder(s):\n• " +
                       string.Join("\n• ", scan.Failures.Take(limit));

                if (scan.Failures.Count > limit)
                    text += "\n… and " + (scan.Failures.Count - limit) + " more";
            }

            if (scan.IsTruncated)
            {
                if (text.Length > 0)
                    text += "\n\n";

                text += "The browse was stopped: there turned out to be too many folders. " +
                        "The search was probably started from the wrong folder — point it at the one holding the discipline folders.";
            }

            MessageBox.Show(this, text, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ───────────────────────────── the table ─────────────────────────────

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModelKitRow.IsSelected))
                UpdateSummary();
        }

        private void ToggleSelected()
        {
            var rows = _grid.SelectedItems.OfType<ModelKitRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);

            foreach (var row in rows)
                row.IsSelected = value;
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space)
                return;

            ToggleSelected();
            e.Handled = true;
        }

        private void UpdateSummary()
        {
            UpdateSummary(null, null);
        }

        private void UpdateSummary(ModelKitScan scan, IReadOnlyList<string> codes)
        {
            var marked = _rows.Count(row => row.IsSelected);

            if (scan != null)
            {
                var missing = scan.Missing(codes);

                _lastSummary = _rows.Count == 0
                    ? "Nothing was found. Folders read: " + scan.Folders + "."
                    : "Models found: " + _rows.Count +
                      ", disciplines: " + (codes.Count - missing.Count) + " of " + codes.Count +
                      (missing.Count > 0 ? ". Not found: " + string.Join(", ", missing) : string.Empty) + ".";
            }

            _status.Foreground = marked == 0 ? SystemColors.GrayTextBrush : SystemColors.ControlTextBrush;
            _status.Text = marked == 0
                ? _lastSummary
                : _lastSummary + " Checked: " + marked + ".";

            _addButton.IsEnabled = marked > 0;
            _addButton.Content = marked > 0 ? "Add (" + marked + ")" : "Add";
        }

        // ───────────────────────────── actions ─────────────────────────────

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            var marked = _rows.Where(row => row.IsSelected).Select(row => row.Entry).ToList();
            if (marked.Count == 0)
                return;

            Selected = marked;
            DialogResult = true;
        }

        /// <summary>
        /// The settings are remembered whenever the window closes — just like in "Link Manager",
        /// the window this one is opened from: that one writes the file itself, here only the
        /// fields are filled in. That is the whole point: the second time, the window opens already set up.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            _preferences.KitBuilding = Building;
            _preferences.KitDeep = _deepBox.IsChecked == true;

            _preferences.Kit.Clear();
            _preferences.Kit.AddRange(Codes);

            if (_root != null)
                _preferences.KitRoot = _root.Format();

            // The piece number is remembered only if what is chosen is actually that piece:
            // a value typed in by hand has nothing to do with parsing the name, and substituting
            // it into the setting would spoil the guess in the next project.
            var tokens = ModelKit.Tokens(_host.Name);
            for (var index = 0; index < tokens.Count; index++)
            {
                if (!string.Equals(tokens[index], Building, StringComparison.OrdinalIgnoreCase))
                    continue;

                _preferences.BuildingToken = index + 1;
                break;
            }

            base.OnClosed(e);
        }
    }
}
