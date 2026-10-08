using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// The "Export to Excel" window: the project's schedules, a check box on each, and how the sheet
    /// should look. Every checked schedule becomes a sheet of one workbook.
    ///
    /// The search box only *finds* — the same exception to the "only what is shown gets applied" rule
    /// as the category picker (see CLAUDE.md, "Conventions"): typing "Door" to reach one schedule must
    /// not take the check mark off the one picked a moment ago, and nothing here deletes anything.
    ///
    /// The window knows nothing about the Revit API: it is handed <see cref="ScheduleInfo"/> snapshots
    /// and hands back schedule names, a file path and the chosen layout.
    /// </summary>
    internal sealed class ExcelExportWindow : Window
    {
        private const string WindowTitle = "Export to Excel";

        private readonly IReadOnlyList<ExcelScheduleRow> _rows;
        private readonly ObservableCollection<ExcelScheduleRow> _visible = new ObservableCollection<ExcelScheduleRow>();
        private readonly ExcelPreferences _preferences;
        private readonly string _projectName;

        private readonly TextBox _searchBox;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly RadioButton _asInRevit;
        private readonly RadioButton _plainTable;
        private readonly CheckBox _openAfter;
        private readonly TextBlock _status;
        private readonly Button _exportButton;

        private bool _syncingSelectAll;

        /// <summary>The checked schedules, in the order of the list — the order of the sheets.</summary>
        public IReadOnlyList<string> Selected { get; private set; } = new List<string>();

        public string FilePath { get; private set; } = string.Empty;

        public ScheduleExportLayout Layout => _plainTable.IsChecked == true ? ScheduleExportLayout.PlainTable : ScheduleExportLayout.AsInRevit;

        public bool OpenAfter => _openAfter.IsChecked == true;

        /// <param name="schedules">Every schedule of the project that can be exported.</param>
        /// <param name="activeSchedule">The schedule open on screen, checked from the start; null when the active view is not a schedule.</param>
        /// <param name="projectName">The project's title — part of the suggested file name.</param>
        public ExcelExportWindow(IReadOnlyList<ScheduleInfo> schedules, string activeSchedule, string projectName, ExcelPreferences preferences)
        {
            _preferences = preferences;
            _projectName = projectName ?? string.Empty;
            _rows = schedules
                .Select(info => new ExcelScheduleRow(info, string.Equals(info.Name, activeSchedule, StringComparison.Ordinal)))
                .ToList();

            Title = WindowTitle;
            Width = 760;
            Height = 620;
            MinWidth = 560;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _searchBox = new TextBox
            {
                Width = 260,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Finds a schedule by its name, kind or category.\nOnly finds: schedules out of sight keep their check marks."
            };
            _searchBox.TextChanged += (s, e) => RebuildVisible();

            _selectAll = new CheckBox
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every schedule shown in the list"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();

            _asInRevit = new RadioButton
            {
                Content = "As in Revit — the title, headings, group lines, subtotals and the grand total",
                GroupName = "layout",
                Margin = new Thickness(0, 2, 0, 2),
                ToolTip = "The sheet looks like the schedule. Lines that stand for elements carry a hidden key,\n" +
                          "so the file can still be imported back; headings, group lines and totals are skipped then."
            };
            _plainTable = new RadioButton
            {
                Content = "Plain table — one heading line and one line per schedule row, with filter buttons",
                GroupName = "layout",
                Margin = new Thickness(0, 2, 0, 2),
                ToolTip = "The form for editing in Excel and importing back: sorting and filtering in Excel\n" +
                          "keep every line tied to its elements."
            };

            if (_preferences.Layout == ScheduleExportLayout.PlainTable)
                _plainTable.IsChecked = true;
            else
                _asInRevit.IsChecked = true;

            _openAfter = new CheckBox
            {
                Content = "Open the file in Excel when it is written",
                IsChecked = _preferences.OpenAfter,
                Margin = new Thickness(0, 8, 0, 0)
            };

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = SystemColors.GrayTextBrush, TextWrapping = TextWrapping.Wrap };

            _exportButton = new Button
            {
                Content = "Export…",
                MinWidth = 130,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0)
            };
            _exportButton.Click += OnExport;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 100,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(closeButton);

            foreach (var row in _rows)
                row.PropertyChanged += (s, e) => UpdateStatus();

            _grid.ItemsSource = _visible;
            RebuildVisible();

            Loaded += (s, e) =>
            {
                var first = _rows.FirstOrDefault(row => row.IsSelected);
                if (first != null)
                    _grid.ScrollIntoView(first);
                _searchBox.Focus();
            };
            Closing += OnClosing;
        }

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var hint = new TextBlock
            {
                Text = "Check the schedules to export. Each one becomes a sheet of one Excel workbook, with every value exactly as the " +
                       "schedule shows it. Edit the values in Excel and bring them back with \"Import from Excel\" — every line remembers " +
                       "which elements it stands for.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            tools.Children.Add(new TextBlock { Text = "Find:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            tools.Children.Add(_searchBox);
            Grid.SetRow(tools, 1);
            root.Children.Add(tools);

            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);

            var options = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            options.Children.Add(new TextBlock { Text = "Layout of the sheet:", Margin = new Thickness(0, 0, 0, 2) });
            options.Children.Add(_asInRevit);
            options.Children.Add(_plainTable);
            options.Children.Add(_openAfter);
            Grid.SetRow(options, 3);
            root.Children.Add(options);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(_exportButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 4);
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
                IsReadOnly = false,
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
                CellTemplate = GridBuilder.CheckBoxTemplate(nameof(ExcelScheduleRow.IsSelected))
            });

            var name = GridBuilder.TextColumn("Schedule", nameof(ExcelScheduleRow.Name), new DataGridLength(1, DataGridLengthUnitType.Star));
            name.IsReadOnly = true;
            grid.Columns.Add(name);

            var kind = GridBuilder.TextColumn("Kind", nameof(ExcelScheduleRow.Kind), new DataGridLength(130));
            kind.IsReadOnly = true;
            grid.Columns.Add(kind);

            var category = GridBuilder.TextColumn("Category", nameof(ExcelScheduleRow.Category), new DataGridLength(170));
            category.IsReadOnly = true;
            grid.Columns.Add(category);

            grid.PreviewKeyDown += OnGridKeyDown;

            return grid;
        }

        private void RebuildVisible()
        {
            var query = (_searchBox?.Text ?? string.Empty).Trim();

            _visible.Clear();
            foreach (var row in _rows)
            {
                if (query.Length == 0 || Contains(row.Name, query) || Contains(row.Kind, query) || Contains(row.Category, query))
                    _visible.Add(row);
            }

            UpdateStatus();
        }

        private static bool Contains(string text, string query)
        {
            return (text ?? string.Empty).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            foreach (var row in _visible)
                row.IsSelected = value;
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space)
                return;

            var rows = _grid.SelectedItems.OfType<ExcelScheduleRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);
            foreach (var row in rows)
                row.IsSelected = value;

            e.Handled = true;
        }

        private void UpdateStatus()
        {
            var selected = _rows.Count(row => row.IsSelected);
            var hidden = _rows.Count(row => row.IsSelected && !_visible.Contains(row));

            _status.Text = "Schedules: " + _rows.Count + ". Checked: " + selected +
                           (hidden > 0 ? " (" + hidden + " of them not shown by the search)" : string.Empty) + ".";
            _exportButton.IsEnabled = selected > 0;
            _exportButton.Content = selected > 1 ? "Export " + selected + "…" : "Export…";

            var shownSelected = _visible.Count(row => row.IsSelected);
            _syncingSelectAll = true;
            _selectAll.IsChecked = shownSelected == 0 ? false : shownSelected == _visible.Count ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        private void OnExport(object sender, RoutedEventArgs e)
        {
            var selected = _rows.Where(row => row.IsSelected).ToList();
            if (selected.Count == 0)
                return;

            var suggested = selected.Count == 1
                ? selected[0].Name
                : (_projectName.Length == 0 ? "Project" : Path.GetFileNameWithoutExtension(_projectName)) + " - schedules";

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = WindowTitle,
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = SafeFileName(suggested) + ".xlsx"
            };

            var folder = _preferences.ExistingFolder();
            if (folder != null)
                dialog.InitialDirectory = folder;

            if (dialog.ShowDialog(this) != true)
                return;

            Selected = selected.Select(row => row.Name).ToList();
            FilePath = dialog.FileName;
            _preferences.Folder = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            DialogResult = true;
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
            return clean.Length == 0 ? "Schedule" : clean;
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            _preferences.Layout = Layout;
            _preferences.OpenAfter = OpenAfter;
            _preferences.Save();
        }
    }
}
