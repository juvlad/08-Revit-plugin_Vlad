using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// "Which of this model's schedules go into the set" — the second half of the "take from a model"
    /// step. The window opens while the source model is already open in the background, so nothing
    /// here may be slow: the user is waiting with a document held open behind the dialog.
    ///
    /// Nothing is checked to begin with, unlike the main window. A base model holds dozens of
    /// schedules and three of them are wanted; "everything at once" here would mean a set nobody asked
    /// for and a cache file several times larger.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class ScheduleChooserWindow : Window
    {
        private const string WindowTitle = "Schedules of the model";

        private readonly ObservableCollection<ScheduleRow> _rows = new ObservableCollection<ScheduleRow>();
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _takeButton;

        private bool _syncingSelectAll;

        /// <summary>The schedules the user chose; empty when the window was closed with "Cancel".</summary>
        public IReadOnlyList<ScheduleInfo> Selected { get; private set; } = new List<ScheduleInfo>();

        /// <param name="found">Every schedule in the source model that can be copied.</param>
        /// <param name="modelName">The model the schedules came from — the caption above the table.</param>
        /// <param name="known">The names already in the set: taking one again refreshes it.</param>
        public ScheduleChooserWindow(IReadOnlyList<ScheduleInfo> found, string modelName, ISet<string> known)
        {
            Title = WindowTitle;
            Width = 760;
            Height = 520;
            MinWidth = 560;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            foreach (var info in found ?? new List<ScheduleInfo>())
            {
                var inSet = known != null && known.Contains(info.Name);

                var row = new ScheduleRow(info, 0, false, ScheduleAction.Skip)
                {
                    IsSelected = false,
                    StatusText = inSet ? "Already in the set — will be refreshed" : string.Empty
                };

                row.PropertyChanged += (s, e) => UpdateSummary();
                _rows.Add(row);
            }

            _selectAll = new CheckBox
            {
                IsChecked = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every schedule"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _takeButton = new Button
            {
                Content = "Add to the set",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _takeButton.Click += OnTake;

            var cancelButton = new Button
            {
                Content = "Cancel",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(modelName, cancelButton);

            UpdateSummary();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(string modelName, Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // caption
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var name = string.IsNullOrEmpty(modelName) ? "the model" : "\"" + modelName + "\"";

            var hint = new TextBlock
            {
                Text = "The schedules of " + name + ". The checked ones are copied into the set and stay in the " +
                       "Windows profile — the model itself will not be needed to insert them.\n" +
                       "A schedule already in the set is refreshed rather than doubled.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            Grid.SetRow(_grid, 1);
            root.Children.Add(_grid);

            var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_takeButton);
            buttons.Children.Add(cancelButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            return root;
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
                CellTemplate = GridBuilder.CheckBoxTemplate()
            });

            grid.Columns.Add(GridBuilder.TextColumn("Schedule", nameof(ScheduleRow.Name),
                new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(GridBuilder.TextColumn("Category", nameof(ScheduleRow.Category), new DataGridLength(150)));
            grid.Columns.Add(GridBuilder.TextColumn("Kind", nameof(ScheduleRow.Kind), new DataGridLength(120)));
            grid.Columns.Add(GridBuilder.TextColumn("Columns", nameof(ScheduleRow.FieldCount), new DataGridLength(70)));
            grid.Columns.Add(GridBuilder.TextColumn("State", nameof(ScheduleRow.StatusText), new DataGridLength(210)));

            grid.MouseDoubleClick += (s, e) => ToggleSelectedRows();
            grid.PreviewKeyDown += OnGridKeyDown;

            return grid;
        }

        // ───────────────────────────── check marks ─────────────────────────────

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            foreach (var row in _rows)
                row.IsSelected = value;
        }

        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<ScheduleRow>().ToList();
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

            ToggleSelectedRows();
            e.Handled = true;
        }

        private void UpdateSummary()
        {
            var marked = _rows.Count(row => row.IsSelected);
            var keys = _rows.Count(row => row.IsSelected && row.Info.IsKeySchedule);

            if (marked == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Found: " + _rows.Count + ". Nothing is checked.";
            }
            else
            {
                _status.Foreground = Brushes.DarkGreen;
                _status.Text = "Will go into the set: " + marked + " of " + _rows.Count + "." +
                               (keys > 0
                                   ? " Among them key schedules: " + keys + " — their key values travel with them."
                                   : string.Empty);
            }

            _takeButton.IsEnabled = marked > 0;
            _takeButton.Content = marked > 0 ? "Add to the set (" + marked + ")" : "Add to the set";

            _syncingSelectAll = true;
            _selectAll.IsChecked = marked == 0
                ? false
                : marked == _rows.Count ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        private void OnTake(object sender, RoutedEventArgs e)
        {
            var marked = _rows.Where(row => row.IsSelected).Select(row => row.Info).ToList();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "No schedule is checked.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Selected = marked;
            DialogResult = true;
        }
    }
}
