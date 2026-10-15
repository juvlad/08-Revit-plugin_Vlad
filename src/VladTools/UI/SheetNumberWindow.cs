using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// "Sheets → Number": which parameter is a sheet's number — Revit's own Sheet Number, or another one:
    /// a shared parameter out of a shared parameter file (any file: "Browse…" loads another one), or any
    /// parameter the open project's sheets and titleblocks already carry, shared or not. Beside every one
    /// the window says on how many sheets it stands and how many get a value from it, and the ones that
    /// hold values come first — so the parameter the numbers were really typed in is found by looking,
    /// not by trying a name from the file that turns out to be empty.
    ///
    /// The window knows nothing about Revit: the file is read and the project counted through
    /// <see cref="SheetNumberSource"/>. It is built in code, without XAML, like every window here.
    /// </summary>
    internal sealed class SheetNumberWindow : Window
    {
        private const string WindowTitle = "Sheet number";

        private const string SharedProjectGroup = "(on this project's sheets, not in this file)";
        private const string NamedProjectGroup = "(on this project's sheets, not shared)";
        private const string PickedGroup = "(picked, not in this file)";

        private readonly SheetNumberSource _source;
        private readonly SheetNumberParameter _current;
        private readonly ObservableCollection<Row> _rows = new ObservableCollection<Row>();
        private readonly ICollectionView _view;

        private readonly RadioButton _revitOption;
        private readonly RadioButton _sharedOption;
        private readonly TextBlock _filePath;
        private readonly TextBlock _fileProblem;
        private readonly TextBox _searchBox;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _okButton;

        /// <summary>The parameter picked; set only when the window closes with "OK".</summary>
        public SheetNumberParameter Chosen { get; private set; } = SheetNumberParameter.Revit;

        /// <summary>The shared parameter file the list was last read from.</summary>
        public string FilePath { get; private set; }

        /// <summary>
        /// Shows the window over <paramref name="owner"/> and keeps the choice in
        /// <paramref name="preferences"/> (saved when the owner closes). True when the number shown has
        /// to be read again.
        /// </summary>
        public static bool Choose(Window owner, SheetPreferences preferences, SheetNumberSource source)
        {
            var window = new SheetNumberWindow(preferences.Number, preferences.NumberFile, source) { Owner = owner };
            if (window.ShowDialog() != true)
                return false;

            var before = preferences.Number;
            preferences.Number = window.Chosen;
            preferences.NumberFile = window.FilePath;

            return !window.Chosen.Same(before) || !string.Equals(window.Chosen.Name, before.Name, StringComparison.Ordinal);
        }

        /// <summary>
        /// What the "Number" buttons show. A TextBlock rather than the string: a string in a button is
        /// read as access-key text, and the underscore of "SH_SheetNumber" vanished as a shortcut marker.
        /// </summary>
        public static object ButtonContent(SheetNumberParameter parameter)
        {
            return new TextBlock { Text = (parameter ?? SheetNumberParameter.Revit).Label + "…" };
        }

        private SheetNumberWindow(SheetNumberParameter current, string filePath, SheetNumberSource source)
        {
            _source = source;
            _current = current ?? SheetNumberParameter.Revit;
            FilePath = string.Empty;

            Title = WindowTitle;
            Width = 860;
            Height = 580;
            MinWidth = 600;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _revitOption = new RadioButton { Content = "Revit's own Sheet Number", GroupName = "number", Margin = new Thickness(0, 0, 0, 4) };
            _sharedOption = new RadioButton
            {
                Content = new TextBlock { Text = "Another parameter — one the sheets already carry, or one from a shared parameter file:" },
                GroupName = "number",
                Margin = new Thickness(0, 0, 0, 6)
            };

            _filePath = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            _fileProblem = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };

            var browseButton = new Button { Content = "Browse…", Padding = new Thickness(10, 3, 10, 3), ToolTip = "Load another shared parameter file" };
            browseButton.Click += OnBrowse;

            _searchBox = new TextBox
            {
                Margin = new Thickness(0, 6, 0, 6),
                Padding = new Thickness(4, 3, 4, 3),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Finds a parameter by its name or its group in the file"
            };

            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = Matches;
            _searchBox.TextChanged += (s, e) => _view.Refresh();

            _grid = BuildGrid();

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _okButton = new Button { Content = "OK", MinWidth = 100, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
            _okButton.Click += (s, e) => Accept();

            var cancelButton = new Button { Content = "Cancel", MinWidth = 100, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

            Content = BuildLayout(browseButton, cancelButton);

            // Revit's own is picked by clearing the list's selection; a row picked switches to the
            // shared parameter — the two never disagree about what "OK" will keep.
            _revitOption.Checked += (s, e) =>
            {
                _grid.SelectedItem = null;
                UpdateStatus();
            };
            _sharedOption.Checked += (s, e) => UpdateStatus();

            var path = !string.IsNullOrEmpty(filePath) && File.Exists(filePath) ? filePath : _source.CurrentFile;
            Load(path, true);

            if (_current.IsRevit)
                _revitOption.IsChecked = true;
            else
                _sharedOption.IsChecked = true;

            UpdateStatus();

            Loaded += (s, e) =>
            {
                if (_grid.SelectedItem != null)
                    _grid.ScrollIntoView(_grid.SelectedItem);

                _searchBox.Focus();
            };
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button browseButton, Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // Revit's own
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // a shared parameter
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // the file
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // the file's problem
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // search
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // the list
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "The number the \"Sheets\" table shows for a sheet — in \"A101 - Name\", in \"Export to Excel…\" and in the " +
                       "reports of \"Several models…\". The parameter is read off the sheet, or off its titleblock (the instance, " +
                       "then its type) when the sheet gives it no value; the table and the export are then headed by its name, and " +
                       "Revit's own Sheet Number is left out. The list puts first what this project's sheets actually hold values in. A shared parameter is " +
                       "found by its GUID; one that is not shared, by its name. A model that has no such parameter is shown with " +
                       "Revit's own numbers, and the window says so. The choice is remembered.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            Grid.SetRow(_revitOption, 1);
            root.Children.Add(_revitOption);

            Grid.SetRow(_sharedOption, 2);
            root.Children.Add(_sharedOption);

            var fileRow = new DockPanel { Margin = new Thickness(20, 0, 0, 4) };
            var caption = new TextBlock { Text = "File:", VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(caption, Dock.Left);
            DockPanel.SetDock(browseButton, Dock.Right);
            fileRow.Children.Add(caption);
            fileRow.Children.Add(browseButton);
            fileRow.Children.Add(_filePath);
            Grid.SetRow(fileRow, 3);
            root.Children.Add(fileRow);

            _fileProblem.Margin = new Thickness(20, 0, 0, 4);
            Grid.SetRow(_fileProblem, 4);
            root.Children.Add(_fileProblem);

            var search = new DockPanel { Margin = new Thickness(20, 0, 0, 0) };
            var find = new TextBlock { Text = "Find:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            DockPanel.SetDock(find, Dock.Left);
            search.Children.Add(find);
            search.Children.Add(_searchBox);
            Grid.SetRow(search, 5);
            root.Children.Add(search);

            _grid.Margin = new Thickness(20, 0, 0, 0);
            Grid.SetRow(_grid, 6);
            root.Children.Add(_grid);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_okButton);
            buttons.Children.Add(cancelButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 7);
            root.Children.Add(bottom);

            return root;
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
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = SystemColors.ControlLightBrush,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                SelectionMode = DataGridSelectionMode.Single,
                RowHeaderWidth = 0,
                ItemsSource = _view
            };

            grid.Columns.Add(GridBuilder.TextColumn("Parameter", nameof(Row.Name), new DataGridLength(2, DataGridLengthUnitType.Star)));
            grid.Columns.Add(GridBuilder.TextColumn("Group in the file", nameof(Row.GroupName), new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(GridBuilder.TextColumn("Type", nameof(Row.DataType), new DataGridLength(90)));

            if (_source.HasProject)
                grid.Columns.Add(GridBuilder.TextColumn("In this project", nameof(Row.Use), new DataGridLength(2, DataGridLengthUnitType.Star)));

            grid.SelectionChanged += (s, e) =>
            {
                if (grid.SelectedItem != null)
                    _sharedOption.IsChecked = true;

                UpdateStatus();
            };

            grid.MouseDoubleClick += (s, e) =>
            {
                if (ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) is DataGridRow)
                    Accept();
            };

            return grid;
        }

        // ───────────────────────────── the list ─────────────────────────────

        /// <param name="quiet">
        /// The opening read: a file that cannot be read is a red line in the window, not a dialog — the
        /// window has only just opened. "Browse…" speaks up in full instead, and keeps the old list.
        /// </param>
        private bool Load(string path, bool quiet)
        {
            IReadOnlyList<SharedParameterInfo> found = new List<SharedParameterInfo>();
            var problem = string.Empty;

            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    found = Busy(() => _source.Read(path));
                }
                catch (Exception exception)
                {
                    if (!quiet)
                    {
                        MessageBox.Show(this, "Could not read the shared parameter file.\n\n" + exception.Message,
                            WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }

                    problem = "The file could not be read: " + exception.Message;
                }
            }

            FilePath = path ?? string.Empty;
            _filePath.Text = FilePath.Length == 0 ? "(none — Revit is pointed at no shared parameter file; \"Browse…\" for one)" : FilePath;
            _filePath.ToolTip = _filePath.Text;
            _fileProblem.Text = problem;
            _fileProblem.Visibility = problem.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            Fill(found);
            return true;
        }

        /// <summary>
        /// The file's definitions, then the parameters the project's sheets and titleblocks carry that the
        /// file does not hold — shared or not — then the parameter picked if neither holds it, so it can
        /// always be kept. What holds values comes first, the most filled at the top: the numbers were typed
        /// into one parameter, and a name from the file may well be another one, standing there empty.
        /// </summary>
        private void Fill(IReadOnlyList<SharedParameterInfo> found)
        {
            // A file read again keeps the row picked; with Revit's own picked it must not pick one for
            // the user. On the opening read neither is picked yet, and the choice from before stands.
            var keep = (_grid.SelectedItem as Row)?.Parameter
                       ?? (_revitOption.IsChecked == true || _current.IsRevit ? null : _current);

            var uses = _source.HasProject
                ? _source.Uses.GroupBy(use => use.Parameter.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
                : new Dictionary<string, SheetParameterUse>(StringComparer.Ordinal);

            var rows = new List<Row>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var info in found)
            {
                var parameter = new SheetNumberParameter(info.Guid, info.Name);
                if (!seen.Add(parameter.Key))
                    continue;

                uses.TryGetValue(parameter.Key, out var use);
                rows.Add(new Row(parameter, info.GroupName, info.DataType, use));
            }

            foreach (var use in uses.Values.Where(use => seen.Add(use.Parameter.Key)).OrderBy(use => use.Parameter.Name, StringComparer.CurrentCultureIgnoreCase))
                rows.Add(new Row(use.Parameter, use.Parameter.IsByName ? NamedProjectGroup : SharedProjectGroup, use.DataType, use));

            if (keep != null && seen.Add(keep.Key))
                rows.Add(new Row(keep, PickedGroup, string.Empty, null));

            _rows.Clear();
            foreach (var row in rows.OrderBy(row => row.Filled > 0 ? 0 : row.Use.Length > 0 ? 1 : 2).ThenByDescending(row => row.Filled))
                _rows.Add(row);

            _grid.SelectedItem = keep == null ? null : _rows.FirstOrDefault(row => row.Parameter.Same(keep));
            UpdateStatus();
        }

        private bool Matches(object item)
        {
            if (!(item is Row row))
                return false;

            var query = (_searchBox?.Text ?? string.Empty).Trim();
            return query.Length == 0
                   || row.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0
                   || row.GroupName.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a shared parameter file",
                Filter = "Shared parameter files (*.txt)|*.txt|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (FilePath.Length > 0)
            {
                try
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(FilePath);
                    dialog.FileName = Path.GetFileName(FilePath);
                }
                catch (Exception)
                {
                    // A path that will not split is only a starting point lost.
                }
            }

            if (dialog.ShowDialog(this) == true)
                Load(dialog.FileName, false);
        }

        // ───────────────────────────── the choice ─────────────────────────────

        private void UpdateStatus()
        {
            if (_status == null || _okButton == null)
                return;

            var row = _grid?.SelectedItem as Row;

            if (_revitOption.IsChecked == true)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "The table shows Revit's own Sheet Number.";
                _okButton.IsEnabled = true;
            }
            else if (row == null)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = _rows.Count == 0
                    ? "The list is empty — \"Browse…\" for the shared parameter file that holds the sheet number."
                    : "Pick the parameter in the list.";
                _okButton.IsEnabled = false;
            }
            else if (_source.HasProject && row.Use.Length == 0)
            {
                _status.Foreground = Brushes.DarkRed;
                _status.Text = "\"" + row.Name + "\" is on no sheet and no titleblock of this project: here the table would show Revit's own numbers. " +
                               "It is remembered for the models that have it.";
                _okButton.IsEnabled = true;
            }
            else if (_source.HasProject && row.Filled == 0)
            {
                _status.Foreground = Brushes.DarkRed;
                _status.Text = "\"" + row.Name + "\" is on this project's sheets, but not one gives it a value — the table would show the names alone. " +
                               "The parameters at the top of the list are the ones holding values.";
                _okButton.IsEnabled = true;
            }
            else
            {
                _status.Foreground = Brushes.DarkGreen;
                _status.Text = "The table will show \"" + row.Name + "\" as the sheet number" + (row.Use.Length > 0 ? " — " + row.Use + "." : ".") +
                               (row.Parameter.IsByName ? " It is not shared, so it is found by its name." : string.Empty);
                _okButton.IsEnabled = true;
            }
        }

        private void Accept()
        {
            if (_revitOption.IsChecked == true)
            {
                Chosen = SheetNumberParameter.Revit;
            }
            else
            {
                if (!(_grid.SelectedItem is Row row))
                    return;

                Chosen = row.Parameter;
            }

            DialogResult = true;
        }

        private static T Busy<T>(Func<T> work)
        {
            var previous = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                return work();
            }
            finally
            {
                Mouse.OverrideCursor = previous;
            }
        }

        private sealed class Row
        {
            public Row(SheetNumberParameter parameter, string groupName, string dataType, SheetParameterUse use)
            {
                Parameter = parameter;
                GroupName = groupName ?? string.Empty;
                DataType = string.IsNullOrEmpty(dataType) ? use?.DataType ?? string.Empty : dataType;
                Use = use?.Text ?? string.Empty;
                Filled = use?.Filled ?? 0;
            }

            public SheetNumberParameter Parameter { get; }

            public string Name => Parameter.Name;

            public string GroupName { get; }

            public string DataType { get; }

            /// <summary>Where the open project has it, and how filled; empty when nowhere.</summary>
            public string Use { get; }

            /// <summary>Sheets of the open project that get a value from it.</summary>
            public int Filled { get; }
        }
    }
}
