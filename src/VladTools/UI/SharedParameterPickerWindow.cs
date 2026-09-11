using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// "Add from shared file…" — picks which definitions of the current shared parameter file join a
    /// "Parameter Sets" set. Definitions already in the set are left out of the list entirely: there is
    /// nothing to re-add, and showing them again as a hidden trap for another checkbox would be
    /// exactly the "only what is shown gets applied" pitfall the delete windows already avoid.
    ///
    /// Reading a shared parameter file needs <c>Autodesk.Revit.ApplicationServices.Application</c>, so
    /// it is a call-back, not something the window does itself — the same "windows know nothing about
    /// Revit" rule as the family/schedule scans elsewhere in this add-in. Browsing to a *different*
    /// file is plain file-system work (<c>Microsoft.Win32.OpenFileDialog</c>) and is done here directly,
    /// the same way <c>ModelPicker</c> already does for choosing a model.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class SharedParameterPickerWindow : Window
    {
        private const string WindowTitle = "Add from Shared Parameter File";

        private readonly Func<string, IReadOnlyList<SharedParameterInfo>> _read;
        private readonly HashSet<Guid> _known;

        private readonly ObservableCollection<Row> _all = new ObservableCollection<Row>();
        private ICollectionView _view;

        private readonly TextBlock _filePath;
        private readonly TextBox _searchBox;
        private readonly CheckBox _selectAll;
        private readonly TextBlock _status;
        private readonly Button _addButton;

        private bool _syncingSelectAll;

        /// <summary>The definitions the user checked; empty when the window was closed with "Cancel".</summary>
        public IReadOnlyList<SharedParameterInfo> Selected { get; private set; } = new List<SharedParameterInfo>();

        /// <summary>The shared parameter file the list was last read from — remembered by the caller.</summary>
        public string FilePath { get; private set; }

        /// <param name="filePath">The shared parameter file already read for <paramref name="found"/>; may be empty.</param>
        /// <param name="found">Every definition of that file, flattened out of its groups.</param>
        /// <param name="known">GUIDs already in the set — those definitions are not shown at all.</param>
        /// <param name="read">Re-reads a (possibly different) shared parameter file; throws on failure.</param>
        public SharedParameterPickerWindow(
            string filePath,
            IReadOnlyList<SharedParameterInfo> found,
            IEnumerable<Guid> known,
            Func<string, IReadOnlyList<SharedParameterInfo>> read)
        {
            _read = read;
            _known = new HashSet<Guid>(known ?? Enumerable.Empty<Guid>());
            FilePath = filePath ?? string.Empty;

            Title = WindowTitle;
            Width = 760;
            Height = 560;
            MinWidth = 560;
            MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _filePath = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };

            var browseButton = new Button { Content = "Browse…", Padding = new Thickness(10, 3, 10, 3) };
            browseButton.Click += OnBrowse;

            _searchBox = new TextBox
            {
                Margin = new Thickness(0, 6, 0, 6),
                Padding = new Thickness(4, 3, 4, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _searchBox.TextChanged += (s, e) => _view.Refresh();

            _selectAll = new CheckBox
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every definition shown"
            };
            _selectAll.Checked += (s, e) => SetVisibleChecked(true);
            _selectAll.Unchecked += (s, e) => SetVisibleChecked(false);

            var grid = BuildGrid();

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _addButton = new Button
            {
                Content = "Add checked",
                MinWidth = 140,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _addButton.Click += OnAdd;

            var cancelButton = new Button
            {
                Content = "Cancel",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(browseButton, grid, cancelButton);

            Fill(found);
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button browseButton, DataGrid grid, Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // file
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // search
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var filePanel = new Grid();
            filePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            filePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var caption = new TextBlock { Text = "Shared parameter file:", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(caption, 0);
            Grid.SetColumn(_filePath, 1);
            filePanel.Children.Add(caption);
            filePanel.Children.Add(_filePath);

            var fileRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(browseButton, Dock.Right);
            fileRow.Children.Add(browseButton);
            fileRow.Children.Add(filePanel);
            Grid.SetRow(fileRow, 0);
            root.Children.Add(fileRow);

            Grid.SetRow(_searchBox, 1);
            root.Children.Add(_searchBox);

            Grid.SetRow(grid, 2);
            root.Children.Add(grid);

            var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_addButton);
            buttons.Children.Add(cancelButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 3);
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
                SelectionMode = DataGridSelectionMode.Extended,
                RowHeaderWidth = 0
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate()
            });

            grid.Columns.Add(GridBuilder.TextColumn("Name", nameof(Row.Name), new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(GridBuilder.TextColumn("Group", nameof(Row.GroupName), new DataGridLength(160)));
            grid.Columns.Add(GridBuilder.TextColumn("Type", nameof(Row.DataType), new DataGridLength(110)));

            _view = CollectionViewSource.GetDefaultView(_all);
            _view.Filter = FilterRow;
            grid.ItemsSource = _view;

            grid.MouseDoubleClick += (s, e) => ToggleSelectedRows(grid);
            grid.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Space)
                    return;

                ToggleSelectedRows(grid);
                e.Handled = true;
            };

            return grid;
        }

        // ───────────────────────────── the list ─────────────────────────────

        private void Fill(IReadOnlyList<SharedParameterInfo> found)
        {
            foreach (var row in _all)
                row.PropertyChanged -= OnRowChanged;

            _all.Clear();

            _filePath.Text = FilePath.Length == 0 ? "(none)" : FilePath;
            _filePath.ToolTip = _filePath.Text;

            var skipped = 0;

            foreach (var info in found ?? new List<SharedParameterInfo>())
            {
                if (_known.Contains(info.Guid))
                {
                    skipped++;
                    continue;
                }

                var row = new Row(info) { IsSelected = false };
                row.PropertyChanged += OnRowChanged;
                _all.Add(row);
            }

            UpdateStatus(skipped);
        }

        private bool FilterRow(object item)
        {
            var row = item as Row;
            if (row == null)
                return false;

            var text = (_searchBox.Text ?? string.Empty).Trim();
            return text.Length == 0
                   || row.Name.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0
                   || row.GroupName.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a shared parameter file",
                Filter = "Shared parameter files (*.txt)|*.txt|All files (*.*)|*.*",
                CheckFileExists = true,
                FileName = FilePath.Length > 0 ? FilePath : string.Empty
            };

            if (dialog.ShowDialog(this) != true)
                return;

            IReadOnlyList<SharedParameterInfo> found;

            try
            {
                found = _read(dialog.FileName);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not read the shared parameter file.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            FilePath = dialog.FileName;
            Fill(found);
        }

        // ───────────────────────────── check marks ─────────────────────────────

        private void SetVisibleChecked(bool value)
        {
            if (_syncingSelectAll)
                return;

            foreach (var row in _view.Cast<Row>().ToList())
                row.IsSelected = value;
        }

        private void ToggleSelectedRows(DataGrid grid)
        {
            var rows = grid.SelectedItems.OfType<Row>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);

            foreach (var row in rows)
                row.IsSelected = value;
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Row.IsSelected))
                UpdateStatus(null);
        }

        private void UpdateStatus(int? skippedThisRead)
        {
            var marked = _all.Count(row => row.IsSelected);

            var note = skippedThisRead.HasValue && skippedThisRead.Value > 0
                ? " Already in the set and not shown: " + skippedThisRead.Value + "."
                : string.Empty;

            _status.Foreground = marked > 0 ? Brushes.DarkGreen : SystemColors.GrayTextBrush;
            _status.Text = (marked > 0
                ? "Will be added: " + marked + " of " + _all.Count + "."
                : "Found: " + _all.Count + ". Nothing is checked.") + note;

            _addButton.IsEnabled = marked > 0;
            _addButton.Content = marked > 0 ? "Add checked (" + marked + ")" : "Add checked";

            _syncingSelectAll = true;
            _selectAll.IsChecked = _all.Count == 0 || marked == 0
                ? false
                : marked == _all.Count ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            var marked = _all.Where(row => row.IsSelected).Select(row => row.Info).ToList();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "No definition is checked.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Selected = marked;
            DialogResult = true;
        }

        private sealed class Row : INotifyPropertyChanged
        {
            private bool _isSelected;

            public Row(SharedParameterInfo info)
            {
                Info = info;
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public SharedParameterInfo Info { get; }

            public string Name => Info.Name;
            public string GroupName => Info.GroupName;
            public string DataType => Info.DataType;

            public bool IsSelected
            {
                get { return _isSelected; }
                set
                {
                    if (_isSelected == value)
                        return;

                    _isSelected = value;
                    var handler = PropertyChanged;
                    if (handler != null)
                        handler(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }
    }
}
