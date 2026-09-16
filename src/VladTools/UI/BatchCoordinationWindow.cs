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
    /// "Accept Changes → Other models…": the list of models whose grids are to be put in line with
    /// the coordination file without opening any of them by hand.
    ///
    /// The main window of the button works on the open project — one model, every difference shown
    /// as a row, each with a check box. Here there is nothing to show before the work starts: what
    /// differs in somebody else's model cannot be known until that model is opened, and opening a
    /// dozen of them to fill a table the user would then look through would cost the whole run twice
    /// over. So this window is about the models, not the differences: what to open, which worksets to
    /// open in it, and what may be changed there. The differences themselves land in the report,
    /// model by model.
    ///
    /// That is also why this is a window of its own rather than a second tab of the main one: the
    /// two decide different things, and the main window's rules ("only what is shown gets applied",
    /// "everything applicable is checked from the start") are about rows that are all in front of the
    /// user at once.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class BatchCoordinationWindow : Window
    {
        private const string WindowTitle = "Accept Coordination Changes — other models";

        /// <summary>What separates the workset prefixes in the text box. A semicolon: a workset name may hold a comma.</summary>
        private const char WorksetSeparator = ';';

        private readonly CoordinationPreferences _preferences;
        private readonly LinkPreferences _linkPreferences;

        private readonly ObservableCollection<BatchModelRow> _rows = new ObservableCollection<BatchModelRow>();

        private readonly DataGrid _grid;
        private readonly ComboBox _setBox;
        private readonly TextBox _worksetBox;
        private readonly CheckBox _selectAll;
        private readonly CheckBox _levelsBox;
        private readonly CheckBox _renameBox;
        private readonly TextBlock _status;
        private readonly Button _runButton;

        private bool _syncingSelectAll;
        private bool _settingMany;

        /// <summary>The models the user confirmed for the run.</summary>
        public IReadOnlyList<BatchModelRow> Selected { get; private set; } = new List<BatchModelRow>();

        /// <summary>The worksets to open in every model — prefixes of names, see <see cref="BatchCoordination.Matches"/>.</summary>
        public IReadOnlyList<string> Worksets => Split(_worksetBox.Text);

        /// <summary>Put levels in line with the file as well, not only grids.</summary>
        public bool WithLevels => _levelsBox.IsChecked == true;

        /// <summary>Rename grids and levels to follow the coordination file.</summary>
        public bool WithRename => _renameBox.IsChecked == true;

        public BatchCoordinationWindow(CoordinationPreferences preferences, LinkPreferences linkPreferences)
        {
            _preferences = preferences ?? new CoordinationPreferences();
            _linkPreferences = linkPreferences ?? new LinkPreferences();

            Title = WindowTitle;
            Width = 980;
            Height = 620;
            MinWidth = 720;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _setBox = new ComboBox
            {
                Width = 220,
                IsEditable = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 4),
                Text = _preferences.Set,
                ToolTip = "The same saved sets as in \"Link Manager\": a list of models gathered once."
            };

            _worksetBox = new TextBox
            {
                Width = 420,
                VerticalAlignment = VerticalAlignment.Center,
                Text = string.Join("; ", _preferences.EffectiveWorksets),
                ToolTip =
                    "Everything else in the model stays closed — so a batch run cannot touch a single\n" +
                    "element it was not asked about.\n\n" +
                    "The start of a name is enough: \"00_Link_BM\" also opens \"00_Link_BM_K3\".\n" +
                    "Separate them with a semicolon.\n\n" +
                    "The grids must be in one of these worksets, and so must the link to the\n" +
                    "coordination file — what a closed workset holds is not in the document at all,\n" +
                    "and a model with neither of them open has nothing to compare against."
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

            _levelsBox = new CheckBox
            {
                Content = "Levels as well, not only grids",
                IsChecked = _preferences.Levels,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "Off by default, and not out of timidity: a level moved in a discipline model takes\n" +
                    "every wall, room and view standing on it along, and here nobody is watching it happen.\n" +
                    "A coordination file usually shifts the grids."
            };

            _renameBox = new CheckBox
            {
                Content = "Rename grids and levels to follow the coordination file",
                IsChecked = _preferences.Rename,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip =
                    "A grid renamed in the base file is renamed in the model too.\n" +
                    "Off by default: a name is what views, schedules and somebody's drawings refer to."
            };

            _grid = BuildGrid();
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _runButton = new Button
            {
                Content = "Run",
                MinWidth = 140,
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

            ReloadSetNames();
            Add(_preferences.Models, "model(s) from last time");
            UpdateSummary();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // sources
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // sets
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // worksets
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // switches
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "Each checked model is opened in turn, its grids are put where the coordination file " +
                       "linked into it has them, and the model is synchronised back. Nothing is deleted here — " +
                       "grids and levels gone from the file are only counted in the report; deleting one is a " +
                       "decision to take with the model open in front of you.\n" +
                       "A local copy is made of every workshared model, so the central files are never edited " +
                       "in place — which also means Revit will be busy for as long as the whole batch takes, " +
                       "and there is no Ctrl+Z afterwards: each model is synchronised as soon as it is done.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var sources = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            sources.Children.Add(SourceButton("Files…", "Ordinary .rvt files: a disk or a network folder.", OnAddFiles));
            sources.Children.Add(SourceButton("Revit Server…", "Browse folders and models on Revit Server.", OnBrowseServer));
            sources.Children.Add(SourceButton("BIM360…", "Browse BIM360/ACC accounts, projects and folders.", OnBrowseCloud));
            sources.Children.Add(SourceButton("BIM360 by GUID…",
                "Enter cloud models as pairs of GUIDs — when browsing is unavailable.", OnAddCloudByGuid));
            sources.Children.Add(SourceButton("Remove from list",
                "Removes the checked rows from the table. The models themselves are left untouched.", OnRemove));
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
            Grid.SetRow(sets, 2);
            root.Children.Add(sets);

            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            var worksets = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            worksets.Children.Add(new TextBlock
            {
                Text = "Open only these worksets:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            worksets.Children.Add(_worksetBox);
            Grid.SetRow(worksets, 4);
            root.Children.Add(worksets);

            var switches = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            switches.Children.Add(_levelsBox);
            switches.Children.Add(_renameBox);
            Grid.SetRow(switches, 5);
            root.Children.Add(switches);

            var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(_runButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 6);
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
            Add(ModelPicker.Files(this, true), "file(s)");
        }

        private void OnBrowseServer(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.Server(this, WindowTitle, _linkPreferences, Keys), "Revit Server model(s)");
        }

        private void OnBrowseCloud(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.Cloud(this, WindowTitle, Keys), "BIM360 model(s)");
        }

        private void OnAddCloudByGuid(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.CloudByGuid(this, DefaultRegion()), "cloud model(s)");
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

                var row = new BatchModelRow(entry);
                row.PropertyChanged += OnRowChanged;
                _rows.Add(row);
                added++;
            }

            UpdateSummary();

            if (added < entries.Count)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Added " + what + ": " + added +
                               ". Already in the list: " + (entries.Count - added) + ".";
            }
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

            if (_rows.Count == 0)
            {
                MessageBox.Show(this, "There is nothing in the table to save.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                LinkSetLibrary.Save(SetName, _rows.Select(row => row.Entry));
                ReloadSetNames();

                MessageBox.Show(this,
                    "The set \"" + SetName + "\" was saved: " + _rows.Count + " models.\n\n" +
                    LinkSetLibrary.FilePathFor(SetName),
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not save the set.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ───────────────────────────── check marks ─────────────────────────────

        private List<BatchModelRow> Marked()
        {
            return _rows.Where(row => row.IsSelected).ToList();
        }

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

        /// <summary>Setting check marks in bulk: the total is recomputed once, at the end.</summary>
        private void SetMany(Func<BatchModelRow, bool> value)
        {
            _settingMany = true;

            foreach (var row in _rows)
                row.IsSelected = value(row);

            _settingMany = false;

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
            if (_settingMany || e.PropertyName != nameof(BatchModelRow.IsSelected))
                return;

            UpdateSummary();
        }

        private void UpdateSummary()
        {
            var marked = Marked();

            if (_rows.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Add the models to work on — or load a saved set.";
            }
            else if (marked.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "In the list: " + _rows.Count + ". Nothing is checked.";
            }
            else
            {
                _status.Foreground = Brushes.DarkGreen;
                _status.Text = "Will be opened and synchronised: " + marked.Count + " of " + _rows.Count + ".";
            }

            _runButton.IsEnabled = marked.Count > 0;
            _runButton.Content = marked.Count > 0 ? "Run (" + marked.Count + ")" : "Run";

            _syncingSelectAll = true;
            _selectAll.IsChecked = _rows.Count == 0 || marked.Count == 0
                ? false
                : marked.Count == _rows.Count ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        // ───────────────────────────── actions ─────────────────────────────

        private void OnRun(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "No model is checked.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // The workset names are the one setting here that can silently cost the whole run: with
            // none of them matching, a model opens with nothing in it and the report says "nothing
            // to compare against" for every single line.
            if (Worksets.Count == 0)
            {
                MessageBox.Show(this,
                    "Name at least one workset to open.\n\n" +
                    "The grids live in \"" + BaseFilePreferences.SharedLevelsWorkset + "\", and the " +
                    "coordination file is linked into \"00_Link_BM\" — with every workset closed there " +
                    "would be nothing in the opened model to work on.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            const int limit = 12;
            var names = string.Join("\n", marked.Take(limit).Select(row => "• " + row.Name));
            if (marked.Count > limit)
                names += "\n… and " + (marked.Count - limit) + " more";

            var text = "Open " + marked.Count + " model(s) and put their grids" +
                       (WithLevels ? " and levels" : string.Empty) +
                       " in line with the coordination file?\n\n" + names + "\n\n" +
                       "Only these worksets will be opened in each: " + string.Join("; ", Worksets) + ".\n" +
                       "Every model that differs is synchronised with its central file straight away — " +
                       "this cannot be undone with Ctrl+Z afterwards.\n\n" +
                       "Revit will be busy until the whole batch is done.";

            var answer = MessageBox.Show(this, text, WindowTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }

        /// <summary>The workset prefixes as typed: split, trimmed, and without the empty pieces.</summary>
        private static IReadOnlyList<string> Split(string text)
        {
            return (text ?? string.Empty)
                .Split(WorksetSeparator)
                .Select(LinkPreferences.NormalizeWorkset)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        protected override void OnClosed(EventArgs e)
        {
            _preferences.Levels = WithLevels;
            _preferences.Rename = WithRename;
            _preferences.Set = SetName;

            _preferences.Worksets.Clear();
            _preferences.Worksets.AddRange(Worksets);

            // The list is remembered as it stands, check marks aside: the models are the same next
            // time, and which of them to run is a decision made anew on each run.
            _preferences.Models.Clear();
            _preferences.Models.AddRange(_rows.Select(row => row.Entry));

            _preferences.Save();

            // The server names the user typed in here belong to the whole add-in, not to this window.
            _linkPreferences.Save();

            base.OnClosed(e);
        }
    }
}
