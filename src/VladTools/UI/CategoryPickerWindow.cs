using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace VladTools.UI
{
    /// <summary>
    /// "Choose categories…" — a small modal opened from a "Parameter Sets" row: every bindable
    /// category of the open project, with a check box on the left of each, and a search box to find
    /// one by name.
    ///
    /// Unlike the two "delete" windows, nothing here is destructive: an empty search still shows
    /// (and lets you check) the whole list — the "an empty filter must not select everything" guard
    /// is for deletion, not for choosing what to bind a parameter to (see CLAUDE.md, "Conventions").
    ///
    /// The search box is a way of **finding** a category, not of narrowing what counts: a category
    /// checked and then typed out of sight stays checked. That is the one deliberate exception to
    /// "only what is shown gets applied" — check "Walls", type "door" to find the next one, and
    /// losing "Walls" for having scrolled it away would be indefensible. Every other window's filter
    /// *is* the selection, so there the rule stands.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class CategoryPickerWindow : Window
    {
        private const string WindowTitle = "Choose Categories";

        private readonly ObservableCollection<Row> _all = new ObservableCollection<Row>();
        private readonly ICollectionView _view;

        private readonly TextBox _searchBox;
        private readonly TextBlock _status;

        /// <summary>The <see cref="Autodesk.Revit.DB.BuiltInCategory"/> names checked when "OK" was pressed.</summary>
        public IReadOnlyList<string> Selected { get; private set; } = new List<string>();

        public CategoryPickerWindow(IReadOnlyList<CategoryInfo> categories, IEnumerable<string> selected)
        {
            var chosen = new HashSet<string>(selected ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var category in (categories ?? new List<CategoryInfo>())
                     .OrderBy(category => category.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new Row(category, chosen.Contains(category.BuiltInName));
                row.PropertyChanged += (s, e) => UpdateStatus();
                _all.Add(row);
            }

            Title = WindowTitle;
            Width = 420;
            Height = 560;
            MinWidth = 320;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;
            ShowInTaskbar = false;

            _searchBox = new TextBox
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(4, 3, 4, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _searchBox.TextChanged += (s, e) => _view.Refresh();

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                HeadersVisibility = DataGridHeadersVisibility.None,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                RowHeaderWidth = 0,

                // Nothing in this table is typed into, and without this a double click — which is
                // meant to toggle the row — puts the name cell into edit mode instead.
                IsReadOnly = true
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Width = new DataGridLength(28),
                CanUserResize = false,

                // The path is named explicitly: this row's flag is IsChecked, not the "IsSelected"
                // the helper defaults to, and a binding to a property that does not exist fails
                // silently — the box ticks on screen and the tick never reaches the row.
                CellTemplate = GridBuilder.CheckBoxTemplate(nameof(Row.IsChecked))
            });

            grid.Columns.Add(GridBuilder.TextColumn(string.Empty, nameof(Row.DisplayName), new DataGridLength(1, DataGridLengthUnitType.Star)));

            grid.MouseDoubleClick += (s, e) => ToggleSelectedRows(grid);
            grid.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Space)
                    return;

                ToggleSelectedRows(grid);
                e.Handled = true;
            };

            _view = CollectionViewSource.GetDefaultView(_all);
            _view.Filter = FilterRow;
            grid.ItemsSource = _view;

            var selectAllButton = new Button { Content = "Check shown", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
            selectAllButton.Click += (s, e) => SetVisibleChecked(true);

            var selectNoneButton = new Button { Content = "Uncheck shown", Padding = new Thickness(8, 3, 8, 3) };
            selectNoneButton.Click += (s, e) => SetVisibleChecked(false);

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            toolbar.Children.Add(selectAllButton);
            toolbar.Children.Add(selectNoneButton);

            _status = new TextBlock { Margin = new Thickness(0, 6, 0, 0), Foreground = SystemColors.GrayTextBrush };

            var okButton = new Button { Content = "OK", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            okButton.Click += OnOk;

            var cancelButton = new Button { Content = "Cancel", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), IsCancel = true };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(_searchBox, 0);
            Grid.SetRow(grid, 1);
            Grid.SetRow(toolbar, 2);
            Grid.SetRow(_status, 3);
            Grid.SetRow(buttons, 4);

            root.Children.Add(_searchBox);
            root.Children.Add(grid);
            root.Children.Add(toolbar);
            root.Children.Add(_status);
            root.Children.Add(buttons);

            Content = root;

            UpdateStatus();
        }

        private bool FilterRow(object item)
        {
            var row = item as Row;
            if (row == null)
                return false;

            var text = (_searchBox.Text ?? string.Empty).Trim();
            return text.Length == 0 || row.DisplayName.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void SetVisibleChecked(bool value)
        {
            foreach (var row in _view.Cast<Row>().ToList())
                row.IsChecked = value;
        }

        private void ToggleSelectedRows(DataGrid grid)
        {
            var rows = grid.SelectedItems.OfType<Row>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsChecked);

            foreach (var row in rows)
                row.IsChecked = value;
        }

        private void UpdateStatus()
        {
            var count = _all.Count(row => row.IsChecked);
            _status.Text = count == 0 ? "No category chosen." : "Chosen: " + count + ".";
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            Selected = _all.Where(row => row.IsChecked).Select(row => row.BuiltInName).ToList();
            DialogResult = true;
        }

        private sealed class Row : INotifyPropertyChanged
        {
            private bool _isChecked;

            public Row(CategoryInfo category, bool isChecked)
            {
                BuiltInName = category.BuiltInName;
                DisplayName = category.DisplayName;
                _isChecked = isChecked;
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public string BuiltInName { get; }

            public string DisplayName { get; }

            public bool IsChecked
            {
                get { return _isChecked; }
                set
                {
                    if (_isChecked == value)
                        return;

                    _isChecked = value;
                    var handler = PropertyChanged;
                    if (handler != null)
                        handler(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }
    }
}
