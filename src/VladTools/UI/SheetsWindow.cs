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
    /// <summary>What the "Sheets" window was closed for.</summary>
    internal enum SheetsWindowAction
    {
        None,
        Open,
        Export
    }

    /// <summary>
    /// The "Sheets" window: every sheet of the project with what is placed on it, in the folders and
    /// the order of the Project Browser. A click on a folder line folds it; a double click on a sheet
    /// opens it — which needs the window closed, so the window hands the sheet back rather than
    /// opening it itself.
    ///
    /// The search box narrows the table, and the export writes the table as it is — the rule "only what
    /// is shown gets applied" (see CLAUDE.md, "Conventions"). Folding is not narrowing: a folded folder
    /// is exported whole, the same as a folded group in Excel still holds its rows.
    ///
    /// The window knows nothing about the Revit API: it is handed <see cref="SheetInfo"/> snapshots,
    /// already in the browser's order, and lays their folders out itself.
    /// </summary>
    internal sealed class SheetsWindow : Window
    {
        private const string WindowTitle = "Sheets";

        private static readonly Brush FolderBackground = Frozen(new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFB)));

        private readonly List<SheetListRow> _all = new List<SheetListRow>();
        private readonly ObservableCollection<SheetListRow> _visible = new ObservableCollection<SheetListRow>();
        private readonly SheetPreferences _preferences;
        private readonly string _projectName;
        private readonly int _sheetCount;

        private readonly TextBox _searchBox;
        private readonly DataGrid _grid;
        private readonly CheckBox _openAfter;
        private readonly TextBlock _status;
        private readonly Button _openButton;
        private readonly Button _exportButton;

        public SheetsWindowAction Action { get; private set; }

        /// <summary>The sheet to open, when <see cref="Action"/> is <see cref="SheetsWindowAction.Open"/>.</summary>
        public SheetInfo SheetToOpen { get; private set; }

        /// <summary>The lines to write, folders included, in the table's order — when <see cref="Action"/> is <see cref="SheetsWindowAction.Export"/>.</summary>
        public IReadOnlyList<SheetListRow> ExportRows { get; private set; } = new List<SheetListRow>();

        public string FilePath { get; private set; } = string.Empty;

        public bool OpenAfter => _openAfter.IsChecked == true;

        /// <param name="sheets">Every sheet of the project, in the order the Project Browser lists them.</param>
        /// <param name="organization">The name of the browser organization the folders come from; empty when unknown.</param>
        /// <param name="activeSheet">The UniqueId of the sheet open on screen, selected from the start; null when the active view is not a sheet.</param>
        /// <param name="projectName">The project's title — part of the suggested file name.</param>
        public SheetsWindow(IReadOnlyList<SheetInfo> sheets, string organization, string activeSheet, string projectName, SheetPreferences preferences)
        {
            _preferences = preferences;
            _projectName = projectName ?? string.Empty;
            _sheetCount = sheets.Count;

            BuildTree(sheets);

            Title = WindowTitle;
            Width = 960;
            Height = 720;
            MinWidth = 620;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _searchBox = new TextBox
            {
                Width = 260,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Finds sheets by number, name, folder or by what is placed on them — a view, a schedule, a legend.\n" +
                          "The export writes what the search leaves in the table."
            };
            _searchBox.TextChanged += OnSearchChanged;

            var collapseAll = new Button { Content = "Collapse all", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(12, 0, 0, 0) };
            collapseAll.Click += (s, e) => SetAllExpanded(false);

            var expandAll = new Button { Content = "Expand all", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            expandAll.Click += (s, e) => SetAllExpanded(true);

            _grid = BuildGrid();

            _openAfter = new CheckBox
            {
                Content = "Open the file in Excel when it is written",
                IsChecked = _preferences.OpenAfter,
                Margin = new Thickness(0, 8, 0, 0)
            };

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = SystemColors.GrayTextBrush, TextWrapping = TextWrapping.Wrap };

            _openButton = new Button
            {
                Content = "Open sheet",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Closes the window and opens the selected sheet (or double-click it)"
            };
            _openButton.Click += (s, e) => OpenSelected();

            _exportButton = new Button
            {
                Content = "Export to Excel…",
                MinWidth = 140,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Writes the table into an .xlsx: the same folders, as lines Excel can fold, and every sheet the search leaves shown —\n" +
                          "folded folders included."
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

            Content = BuildLayout(organization, collapseAll, expandAll, closeButton);

            _grid.ItemsSource = _visible;
            RebuildVisible();

            Loaded += (s, e) =>
            {
                var active = _all.FirstOrDefault(row => !row.IsFolder && string.Equals(row.Sheet.UniqueId, activeSheet, StringComparison.Ordinal));
                if (active != null && _visible.Contains(active))
                {
                    _grid.SelectedItem = active;
                    _grid.ScrollIntoView(active);
                }

                _searchBox.Focus();
            };
            Closing += OnClosing;
        }

        /// <summary>
        /// Lays the sheets' folder paths out as folder lines. The sheets arrive sorted by their paths,
        /// so a folder's sheets are consecutive, and a new folder line is needed only where a sheet's
        /// path parts from the one before it.
        /// </summary>
        private void BuildTree(IReadOnlyList<SheetInfo> sheets)
        {
            var open = new List<SheetListRow>();

            foreach (var sheet in sheets)
            {
                var path = sheet.Folders;

                var common = 0;
                while (common < open.Count && common < path.Count && string.Equals(open[common].FolderName, path[common], StringComparison.Ordinal))
                    common++;

                open.RemoveRange(common, open.Count - common);

                for (var level = common; level < path.Count; level++)
                {
                    var folder = SheetListRow.Folder(level == 0 ? null : open[level - 1], level, path[level]);
                    open.Add(folder);
                    _all.Add(folder);
                }

                _all.Add(SheetListRow.ForSheet(open.Count == 0 ? null : open[open.Count - 1], path.Count, sheet));
            }
        }

        private UIElement BuildLayout(string organization, Button collapseAll, Button expandAll, Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var hint = new TextBlock
            {
                Text = "Every sheet of the project and what is placed on it, in the folders and the order of the Project Browser" +
                       (string.IsNullOrEmpty(organization) ? "." : " (sheet organization \"" + organization + "\").") +
                       " Click a folder to fold it; double-click a sheet to open it.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            tools.Children.Add(new TextBlock { Text = "Find:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            tools.Children.Add(_searchBox);
            tools.Children.Add(collapseAll);
            tools.Children.Add(expandAll);
            Grid.SetRow(tools, 1);
            root.Children.Add(tools);

            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);

            Grid.SetRow(_openAfter, 3);
            root.Children.Add(_openAfter);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(_openButton);
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
                // The order is the Project Browser's; a click on a heading would scatter the folders.
                CanUserSortColumns = false,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = SystemColors.ControlLightBrush,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                SelectionMode = DataGridSelectionMode.Single,
                RowHeaderWidth = 0
            };

            var sheetStyle = CellStyle();
            sheetStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Binding(nameof(SheetListRow.Indent))));
            sheetStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, new Binding(nameof(SheetListRow.Weight))));

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Sheet",
                Width = new DataGridLength(2, DataGridLengthUnitType.Star),
                Binding = new Binding(nameof(SheetListRow.SheetText)),
                ElementStyle = sheetStyle
            });

            var contentsStyle = CellStyle();
            contentsStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 3, 4, 3)));
            var muted = new DataTrigger { Binding = new Binding(nameof(SheetListRow.IsMuted)), Value = true };
            muted.Setters.Add(new Setter(TextBlock.ForegroundProperty, SystemColors.GrayTextBrush));
            contentsStyle.Triggers.Add(muted);

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "On the sheet",
                Width = new DataGridLength(3, DataGridLengthUnitType.Star),
                Binding = new Binding(nameof(SheetListRow.ContentsText)),
                ElementStyle = contentsStyle
            });

            var rowStyle = new Style(typeof(DataGridRow));
            var folder = new DataTrigger { Binding = new Binding(nameof(SheetListRow.IsFolder)), Value = true };
            folder.Setters.Add(new Setter(Control.BackgroundProperty, FolderBackground));
            folder.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            rowStyle.Triggers.Add(folder);
            grid.RowStyle = rowStyle;

            grid.PreviewMouseLeftButtonDown += OnGridMouseDown;
            grid.MouseDoubleClick += OnGridDoubleClick;
            grid.PreviewKeyDown += OnGridKeyDown;
            grid.SelectionChanged += (s, e) => UpdateStatus();

            return grid;
        }

        /// <summary>A text cell that grows with its text: a sheet's contents are one line per thing placed.</summary>
        private static Style CellStyle()
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            return style;
        }

        private static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        // ───────────────────────────── what is shown ─────────────────────────────

        private string Query => (_searchBox?.Text ?? string.Empty).Trim();

        /// <summary>
        /// The lines the search leaves, in order: every matching sheet and the folders above it. Folded
        /// folders hide their contents unless <paramref name="ignoreFolding"/> — the export's view.
        /// Also refreshes each folder's count of shown sheets.
        /// </summary>
        private List<SheetListRow> Shown(bool ignoreFolding)
        {
            var query = Query;
            var matching = new HashSet<SheetListRow>();

            foreach (var row in _all.Where(row => row.IsFolder))
                row.ShownSheets = 0;

            foreach (var row in _all.Where(row => !row.IsFolder && Matches(row, query)))
            {
                matching.Add(row);
                for (var folder = row.Parent; folder != null; folder = folder.Parent)
                    folder.ShownSheets++;
            }

            return _all
                .Where(row => row.IsFolder ? row.ShownSheets > 0 : matching.Contains(row))
                .Where(row => ignoreFolding || Unfolded(row))
                .ToList();
        }

        private static bool Matches(SheetListRow row, string query)
        {
            if (query.Length == 0)
                return true;

            if (Contains(row.Sheet.Title, query) || row.Sheet.Contents.Any(line => Contains(line, query)))
                return true;

            for (var folder = row.Parent; folder != null; folder = folder.Parent)
            {
                if (Contains(folder.FolderName, query))
                    return true;
            }

            return false;
        }

        private static bool Contains(string text, string query)
        {
            return (text ?? string.Empty).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private static bool Unfolded(SheetListRow row)
        {
            for (var folder = row.Parent; folder != null; folder = folder.Parent)
            {
                if (!folder.IsExpanded)
                    return false;
            }

            return true;
        }

        private void RebuildVisible()
        {
            FlatTree.Sync(_visible, Shown(false));
            UpdateStatus();
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            // A match inside a folded folder would otherwise be found and still not shown.
            if (Query.Length > 0)
            {
                foreach (var row in _all.Where(row => row.IsFolder))
                    row.IsExpanded = true;
            }

            RebuildVisible();
        }

        private void SetAllExpanded(bool value)
        {
            foreach (var row in _all.Where(row => row.IsFolder))
                row.IsExpanded = value;

            RebuildVisible();
        }

        private void Toggle(SheetListRow folder)
        {
            folder.IsExpanded = !folder.IsExpanded;
            RebuildVisible();
        }

        private void UpdateStatus()
        {
            var shown = Shown(true).Count(row => !row.IsFolder);
            var empty = _all.Count(row => !row.IsFolder && row.Sheet.Contents.Count == 0);

            _status.Text = "Sheets: " + _sheetCount +
                           (Query.Length > 0 ? ". Found: " + shown : string.Empty) +
                           (empty > 0 ? ". With nothing placed: " + empty : string.Empty) + ".";

            _exportButton.IsEnabled = shown > 0;
            _exportButton.Content = Query.Length > 0 && shown < _sheetCount ? "Export " + shown + " to Excel…" : "Export to Excel…";

            var selected = _grid.SelectedItem as SheetListRow;
            _openButton.IsEnabled = selected != null && !selected.IsFolder && selected.Sheet.CanOpen;
        }

        // ───────────────────────────── the mouse and the keys ─────────────────────────────

        private void OnGridMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(ItemsControl.ContainerFromElement(_grid, e.OriginalSource as DependencyObject) is DataGridRow container) ||
                !(container.Item is SheetListRow row) || !row.IsFolder)
                return;

            // The second click of a double click is swallowed, so a double click folds once, not twice.
            if (e.ClickCount == 1)
                Toggle(row);

            e.Handled = true;
        }

        private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(_grid, e.OriginalSource as DependencyObject) is DataGridRow container &&
                container.Item is SheetListRow row && !row.IsFolder)
                Open(row);
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (!(_grid.SelectedItem is SheetListRow row))
                return;

            switch (e.Key)
            {
                case Key.Enter:
                    if (row.IsFolder)
                        Toggle(row);
                    else
                        Open(row);
                    e.Handled = true;
                    break;
                case Key.Left when row.IsFolder && row.IsExpanded:
                case Key.Right when row.IsFolder && !row.IsExpanded:
                    Toggle(row);
                    e.Handled = true;
                    break;
            }
        }

        // ───────────────────────────── closing ─────────────────────────────

        private void OpenSelected()
        {
            if (_grid.SelectedItem is SheetListRow row && !row.IsFolder)
                Open(row);
        }

        private void Open(SheetListRow row)
        {
            if (!row.Sheet.CanOpen)
            {
                MessageBox.Show(this,
                    "\"" + row.Sheet.Title + "\" is a placeholder sheet: it exists only as a line in sheet lists, and there is nothing to open.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SheetToOpen = row.Sheet;
            Action = SheetsWindowAction.Open;
            DialogResult = true;
        }

        private void OnExport(object sender, RoutedEventArgs e)
        {
            var rows = Shown(true);
            if (!rows.Any(row => !row.IsFolder))
                return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export to Excel",
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = SafeFileName((_projectName.Length == 0 ? "Project" : Path.GetFileNameWithoutExtension(_projectName)) + " - sheets") + ".xlsx"
            };

            var folder = _preferences.ExistingFolder();
            if (folder != null)
                dialog.InitialDirectory = folder;

            if (dialog.ShowDialog(this) != true)
                return;

            ExportRows = rows;
            FilePath = dialog.FileName;
            _preferences.Folder = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            Action = SheetsWindowAction.Export;
            DialogResult = true;
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
            return clean.Length == 0 ? "Sheets" : clean;
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            _preferences.OpenAfter = OpenAfter;
            _preferences.Save();
        }
    }
}
