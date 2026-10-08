using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
    /// The "Import from Excel" window: a sheet of the workbook on one side, a schedule of the project
    /// on the other, and between them a preview of exactly what would change — every changed cell
    /// coloured, with the old and the new value in its tooltip — before anything is written.
    ///
    /// The pairing is the user's to steer: by the elements a line was exported with (only a file this
    /// add-in wrote has them), by a key column such as a mark or a room number, or simply by order, as
    /// ModPlus does it. Which file column feeds which schedule column is a drop-down under every heading.
    ///
    /// The "Show" list and the search box only *find* rows, like in "Translate": looking at the rows
    /// that cannot be written must not take the check marks off the ones that can. The confirmation
    /// says how many of the rows going in are out of sight.
    ///
    /// The window knows nothing about the Revit API: every preview comes from a call-back the command
    /// hands in, and what the window hands back is the preview the user confirmed.
    /// </summary>
    internal sealed class ExcelImportWindow : Window
    {
        private const string WindowTitle = "Import from Excel";

        private static readonly string[] ShowOptions =
        {
            "Rows with changes",
            "All rows",
            "Cannot be written",
            "Not paired"
        };

        private static readonly string[] ModeOptions =
        {
            "By element (from the export)",
            "By a key column",
            "By row order"
        };

        private readonly Func<ExcelImportRequest, ExcelImportPreview> _plan;
        private readonly IReadOnlyDictionary<string, string> _guesses;
        private readonly IReadOnlyList<ScheduleInfo> _schedules;
        private readonly ExcelPreferences _preferences;
        private readonly ObservableCollection<ExcelPreviewRow> _visible = new ObservableCollection<ExcelPreviewRow>();

        private readonly ComboBox _sheetBox;
        private readonly ComboBox _scheduleBox;
        private readonly ComboBox _modeBox;
        private readonly TextBlock _keyLabel;
        private readonly ComboBox _keyBox;
        private readonly TextBox _firstRowBox;
        private readonly CheckBox _emptyClearsBox;
        private readonly Border _notesBorder;
        private readonly TextBlock _notes;
        private readonly ComboBox _showBox;
        private readonly TextBox _searchBox;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly CheckBox _selectAfterBox;
        private readonly Button _applyButton;

        private ExcelImportRequest _request = new ExcelImportRequest();
        private ExcelImportPreview _preview;
        private bool _updating;
        private bool _syncingSelectAll;
        private bool _settingMany;

        /// <summary>The preview the user confirmed; its checked rows are what gets written.</summary>
        public ExcelImportPreview Result { get; private set; }

        public bool SelectAfter => _selectAfterBox.IsChecked == true;

        /// <param name="fileName">The workbook, for the caption.</param>
        /// <param name="sheets">The workbook's sheets that hold anything.</param>
        /// <param name="guesses">For each sheet, the schedule it most likely belongs to.</param>
        /// <param name="schedules">The project's schedules.</param>
        /// <param name="plan">Works out the preview for a request — the Revit side of the window.</param>
        public ExcelImportWindow(
            string fileName,
            IReadOnlyList<string> sheets,
            IReadOnlyDictionary<string, string> guesses,
            IReadOnlyList<ScheduleInfo> schedules,
            ExcelPreferences preferences,
            Func<ExcelImportRequest, ExcelImportPreview> plan)
        {
            _plan = plan;
            _guesses = guesses;
            _schedules = schedules;
            _preferences = preferences;

            Title = WindowTitle + " — " + fileName;
            Width = 1280;
            Height = 780;
            MinWidth = 900;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _sheetBox = new ComboBox { Width = 220, ItemsSource = sheets };
            _sheetBox.SelectionChanged += (s, e) => OnSheetChanged();

            _scheduleBox = new ComboBox
            {
                Width = 320,
                ItemsSource = schedules,
                DisplayMemberPath = nameof(ScheduleInfo.Caption),
                SelectedValuePath = nameof(ScheduleInfo.Name)
            };
            _scheduleBox.SelectionChanged += (s, e) => OnScheduleChanged();

            _modeBox = new ComboBox { Width = 200 };
            foreach (var option in ModeOptions)
                _modeBox.Items.Add(new ComboBoxItem { Content = option });
            _modeBox.SelectionChanged += (s, e) => OnModeChanged();

            _keyLabel = Label("Key:", 12);
            _keyBox = new ComboBox { Width = 200, DisplayMemberPath = nameof(ExcelSourceColumn.Caption) };
            _keyBox.SelectionChanged += (s, e) => OnKeyChanged();

            _firstRowBox = new TextBox
            {
                Width = 50,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "The first line of data, as Excel numbers its rows. The line above it is read as the headings.\n" +
                          "Clear the box to have it found again from the headings."
            };
            _firstRowBox.LostFocus += (s, e) => OnFirstRowChanged();
            _firstRowBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                    OnFirstRowChanged();
            };

            _emptyClearsBox = new CheckBox
            {
                Content = "Empty cells clear values",
                IsChecked = preferences.EmptyClears,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
                ToolTip = "Off: an empty cell in the sheet leaves the value in Revit as it is.\n" +
                          "On: it clears the value (text parameters only — a number cannot be emptied)."
            };
            _emptyClearsBox.Checked += (s, e) => OnEmptyClearsChanged();
            _emptyClearsBox.Unchecked += (s, e) => OnEmptyClearsChanged();

            _notes = new TextBlock { TextWrapping = TextWrapping.Wrap };
            _notesBorder = new Border
            {
                Child = _notes,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xE1)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xC8, 0x6E)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 6),
                Visibility = Visibility.Collapsed
            };

            _showBox = new ComboBox { Width = 170, ItemsSource = ShowOptions, SelectedIndex = 0 };
            _showBox.SelectionChanged += (s, e) => RebuildVisible();

            _searchBox = new TextBox
            {
                Width = 220,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Finds a row by any of its values.\nOnly finds: rows out of sight keep their check marks and are imported too."
            };
            _searchBox.TextChanged += (s, e) => RebuildVisible();

            _selectAll = new CheckBox
            {
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every row shown in the table"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = SystemColors.GrayTextBrush };

            _selectAfterBox = new CheckBox
            {
                Content = "Then open the schedule and select the changed rows",
                IsChecked = preferences.SelectAfter,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip = "After the import the schedule becomes the active view, and the elements whose values changed\n" +
                          "are selected — Revit highlights their rows."
            };

            _applyButton = new Button
            {
                Content = "Import",
                MinWidth = 140,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _applyButton.Click += OnApply;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 100,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(closeButton);
            _grid.ItemsSource = _visible;

            Closing += OnClosing;

            // The first sheet is picked once the window is up, so the wait cursor of the first preview
            // shows over a window rather than over nothing.
            Loaded += (s, e) =>
            {
                if (sheets.Count > 0)
                    _sheetBox.SelectedIndex = 0;
            };
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // sheet → schedule
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // pairing
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // notes
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // show / find / legend
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "Pick the sheet of the workbook and the schedule it goes into. The table shows the schedule as it is, with " +
                       "every value the sheet changes highlighted — yellow: it will be written; red: it cannot be (hover a cell for " +
                       "the reason and the old value). Nothing is written until \"Import\" is pressed, and all of it undoes with one Ctrl+Z.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var source = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            source.Children.Add(Label("Sheet:"));
            source.Children.Add(_sheetBox);
            source.Children.Add(Label("→   Schedule:", 12));
            source.Children.Add(_scheduleBox);
            Grid.SetRow(source, 1);
            root.Children.Add(source);

            var pairing = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            pairing.Children.Add(Label("Pair rows:"));
            pairing.Children.Add(_modeBox);
            pairing.Children.Add(_keyLabel);
            pairing.Children.Add(_keyBox);
            pairing.Children.Add(Label("First data row:", 12));
            pairing.Children.Add(_firstRowBox);
            pairing.Children.Add(_emptyClearsBox);
            Grid.SetRow(pairing, 2);
            root.Children.Add(pairing);

            Grid.SetRow(_notesBorder, 3);
            root.Children.Add(_notesBorder);

            var tools = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
            tools.Children.Add(Label("Show:"));
            tools.Children.Add(_showBox);
            tools.Children.Add(Label("Find:", 12));
            tools.Children.Add(_searchBox);

            var legend = new StackPanel { Orientation = Orientation.Horizontal };
            legend.Children.Add(Swatch(ExcelPreviewCell.ChangedBrush, "will be written"));
            legend.Children.Add(Swatch(ExcelPreviewCell.BlockedBrush, "cannot be written"));
            legend.Children.Add(Swatch(ExcelPreviewCell.KeyBrush, "pairing key"));
            DockPanel.SetDock(legend, Dock.Right);
            tools.Children.Add(legend);

            Grid.SetRow(tools, 4);
            root.Children.Add(tools);

            Grid.SetRow(_grid, 5);
            root.Children.Add(_grid);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_selectAfterBox);
            buttons.Children.Add(_applyButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 6);
            root.Children.Add(bottom);

            return root;
        }

        private static TextBlock Label(string text, double left = 0)
        {
            return new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(left, 0, 6, 0)
            };
        }

        private static UIElement Swatch(Brush brush, string text)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
            panel.Children.Add(new Border
            {
                Width = 14,
                Height = 14,
                Background = brush,
                BorderBrush = SystemColors.ControlDarkBrush,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        private DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                CanUserSortColumns = false,
                CanUserReorderColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                HorizontalGridLinesBrush = SystemColors.ControlLightBrush,
                VerticalGridLinesBrush = SystemColors.ControlLightBrush,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                SelectionMode = DataGridSelectionMode.Extended,
                RowHeaderWidth = 0,
                FrozenColumnCount = 5,
                EnableRowVirtualization = true,
                EnableColumnVirtualization = true
            };

            // A selected row keeps its colours readable: the highlight is a light tint behind the cell
            // colours rather than the system's dark blue over them.
            var cellStyle = new Style(typeof(DataGridCell));
            var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xCC, 0xE4, 0xF7))));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.ControlTextBrush));
            selected.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            cellStyle.Triggers.Add(selected);
            grid.CellStyle = cellStyle;

            // The row's own columns are built once; only the schedule's columns change with the preview.
            // Re-adding the header check box to a new column would make WPF refuse it as already parented.
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CellTemplate = GridBuilder.CheckBoxTemplate(nameof(ExcelPreviewRow.IsSelected), nameof(ExcelPreviewRow.CanApply))
            });

            grid.Columns.Add(Fixed("Row", nameof(ExcelPreviewRow.ScheduleRowText), 46, "The row of the schedule, counted among its element rows"));
            grid.Columns.Add(Fixed("Sheet line", nameof(ExcelPreviewRow.FileRowText), 70, "The line of the sheet, as Excel numbers it"));
            grid.Columns.Add(Fixed("Elements", nameof(ExcelPreviewRow.ElementCountText), 66, "How many elements the schedule row stands for"));

            var state = (DataGridTextColumn)Fixed("State", nameof(ExcelPreviewRow.StatusText), 200, null);
            state.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(ExcelPreviewRow.StatusBrush))));
            state.ElementStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(ExcelPreviewRow.StatusText))));
            grid.Columns.Add(state);

            grid.PreviewKeyDown += OnGridKeyDown;
            return grid;
        }

        /// <summary>The schedule's columns follow the schedule, so they are built again for every preview.</summary>
        private void BuildColumns()
        {
            while (_grid.Columns.Count > _grid.FrozenColumnCount)
                _grid.Columns.RemoveAt(_grid.Columns.Count - 1);

            if (_preview == null)
                return;

            for (var i = 0; i < _preview.Columns.Count; i++)
            {
                _grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = ColumnHeader(i),
                    Width = new DataGridLength(150),
                    MinWidth = 70,
                    CellTemplate = CellTemplate(i)
                });
            }
        }

        private static DataGridColumn Fixed(string header, string property, double width, string tooltip)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));

            return new DataGridTextColumn
            {
                Header = new TextBlock { Text = header, ToolTip = tooltip, FontWeight = FontWeights.SemiBold },
                Width = new DataGridLength(width),
                Binding = new Binding(property),
                IsReadOnly = true,
                ElementStyle = style
            };
        }

        /// <summary>The heading of a schedule column, with the drop-down that picks the sheet column feeding it.</summary>
        private UIElement ColumnHeader(int index)
        {
            var column = _preview.Columns[index];

            var title = new TextBlock
            {
                Text = column.Heading + (column.IsKey ? "  (key)" : string.Empty),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = column.ReadOnlyReason != null ? SystemColors.GrayTextBrush : SystemColors.ControlTextBrush,
                ToolTip = column.ReadOnlyReason != null ? "Read-only: " + column.ReadOnlyReason : column.Heading
            };

            var combo = new ComboBox
            {
                ItemsSource = _preview.SourceColumns,
                DisplayMemberPath = nameof(ExcelSourceColumn.Caption),
                Margin = new Thickness(0, 3, 0, 0),
                MinWidth = 110,
                ToolTip = "The column of the sheet that feeds this one"
            };
            combo.SelectedItem = _preview.SourceColumns.FirstOrDefault(source => source.Index == column.SourceColumn);
            combo.SelectionChanged += (s, e) =>
            {
                if (_updating || _request.ColumnMap == null || index >= _request.ColumnMap.Length)
                    return;

                var map = (int[])_request.ColumnMap.Clone();
                map[index] = (combo.SelectedItem as ExcelSourceColumn)?.Index ?? -1;
                _request.ColumnMap = map;
                Refresh();
            };

            var panel = new StackPanel { MinWidth = 110 };
            panel.Children.Add(title);
            panel.Children.Add(combo);
            return panel;
        }

        private static DataTemplate CellTemplate(int index)
        {
            var cell = nameof(ExcelPreviewRow.Cells) + "[" + index.ToString(CultureInfo.InvariantCulture) + "].";

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new Binding(cell + nameof(ExcelPreviewCell.Background)));
            border.SetBinding(FrameworkElement.ToolTipProperty, new Binding(cell + nameof(ExcelPreviewCell.Tooltip)));

            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(cell + nameof(ExcelPreviewCell.Text)));
            text.SetBinding(TextBlock.ForegroundProperty, new Binding(cell + nameof(ExcelPreviewCell.Foreground)));
            text.SetBinding(TextBlock.FontStyleProperty, new Binding(cell + nameof(ExcelPreviewCell.FontStyle)));
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            text.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 1, 4, 1));
            text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            border.AppendChild(text);
            return new DataTemplate { VisualTree = border };
        }

        // ───────────────────────────── choices ─────────────────────────────

        private void OnSheetChanged()
        {
            if (_updating || !(_sheetBox.SelectedItem is string sheet))
                return;

            // A new sheet starts over: its headings, its columns and its IDs are its own.
            _request = new ExcelImportRequest
            {
                SheetName = sheet,
                ScheduleName = _request.ScheduleName,
                EmptyClears = _emptyClearsBox.IsChecked == true
            };

            if (_guesses.TryGetValue(sheet, out var guess) && _schedules.Any(info => info.Name == guess))
                _request.ScheduleName = guess;
            else if (_schedules.All(info => info.Name != _request.ScheduleName))
                _request.ScheduleName = _schedules.Count > 0 ? _schedules[0].Name : string.Empty;

            _updating = true;
            _scheduleBox.SelectedValue = _request.ScheduleName;
            _updating = false;

            Refresh();
        }

        private void OnScheduleChanged()
        {
            if (_updating || !(_scheduleBox.SelectedValue is string schedule))
                return;

            _request.ScheduleName = schedule;
            _request.ColumnMap = null;
            _request.KeyColumn = -1;
            _request.FirstDataRow = 0;
            Refresh();
        }

        private void OnModeChanged()
        {
            if (_updating || _modeBox.SelectedIndex < 0)
                return;

            _request.MatchMode = (ExcelMatchMode)_modeBox.SelectedIndex;
            Refresh();
        }

        private void OnKeyChanged()
        {
            if (_updating || !(_keyBox.SelectedItem is ExcelSourceColumn key))
                return;

            _request.KeyColumn = key.Index;
            Refresh();
        }

        private void OnFirstRowChanged()
        {
            if (_updating)
                return;

            var text = _firstRowBox.Text.Trim();
            var value = 0;
            if (text.Length > 0 && (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value) || value < 1))
            {
                _firstRowBox.Text = _request.FirstDataRow.ToString(CultureInfo.CurrentCulture);
                return;
            }

            if (value == _request.FirstDataRow && text.Length > 0)
                return;

            _request.FirstDataRow = value;
            _request.ColumnMap = null;
            Refresh();
        }

        private void OnEmptyClearsChanged()
        {
            if (_updating)
                return;

            _request.EmptyClears = _emptyClearsBox.IsChecked == true;
            Refresh();
        }

        // ───────────────────────────── the preview ─────────────────────────────

        private void Refresh()
        {
            if (string.IsNullOrEmpty(_request.SheetName) || string.IsNullOrEmpty(_request.ScheduleName))
                return;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                _preview = _plan(_request.Copy());
            }
            catch (Exception exception)
            {
                _preview = ExcelImportPreview.Failed(_request.Copy(), exception.Message);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            // From here on the request is the one the planner worked out — the columns, the key and
            // the first row it settled on are what the next change starts from.
            _request = _preview.Request.Copy();
            ShowPreview();
        }

        private void ShowPreview()
        {
            _updating = true;

            var mode = _request.MatchMode ?? ExcelMatchMode.RowOrder;
            ((ComboBoxItem)_modeBox.Items[(int)ExcelMatchMode.ElementIds]).IsEnabled = _preview.FileHasIds;
            _modeBox.SelectedIndex = (int)mode;

            var keys = _preview.KeyCandidates
                .Where(index => index < _preview.Columns.Count)
                .Select(index => new ExcelSourceColumn(index, _preview.Columns[index].Heading))
                .ToList();
            _keyBox.ItemsSource = keys;
            _keyBox.SelectedItem = keys.FirstOrDefault(key => key.Index == _request.KeyColumn);

            var keyVisibility = mode == ExcelMatchMode.KeyColumn ? Visibility.Visible : Visibility.Collapsed;
            _keyLabel.Visibility = keyVisibility;
            _keyBox.Visibility = keyVisibility;

            _firstRowBox.Text = _request.FirstDataRow > 0 ? _request.FirstDataRow.ToString(CultureInfo.CurrentCulture) : string.Empty;

            if (_preview.Error != null)
            {
                _notes.Text = _preview.Error;
                _notes.Foreground = Brushes.Firebrick;
                _notesBorder.Visibility = Visibility.Visible;
            }
            else if (_preview.Notes.Count > 0)
            {
                _notes.Text = "• " + string.Join("\n• ", _preview.Notes);
                _notes.Foreground = SystemColors.ControlTextBrush;
                _notesBorder.Visibility = Visibility.Visible;
            }
            else
            {
                _notesBorder.Visibility = Visibility.Collapsed;
            }

            BuildColumns();

            foreach (var row in _preview.Rows)
                row.PropertyChanged += OnRowChanged;

            // Opening on the changes is the point of the window; with none, there is nothing to hide.
            _showBox.SelectedIndex = _preview.Rows.Any(row => row.HasChanges) ? 0 : 1;

            _updating = false;

            RebuildVisible();
        }

        private void RebuildVisible()
        {
            if (_updating)
                return;

            var query = (_searchBox?.Text ?? string.Empty).Trim();

            _visible.Clear();
            if (_preview != null)
            {
                foreach (var row in _preview.Rows)
                {
                    if (Shows(row) && Matches(row, query))
                        _visible.Add(row);
                }
            }

            UpdateSummary();
        }

        private bool Shows(ExcelPreviewRow row)
        {
            switch (_showBox.SelectedIndex)
            {
                case 0:
                    return row.HasChanges;
                case 2:
                    return row.BlockedCount > 0;
                case 3:
                    return row.Kind != ExcelRowKind.Matched;
                default:
                    return true;
            }
        }

        private static bool Matches(ExcelPreviewRow row, string query)
        {
            if (query.Length == 0)
                return true;

            return row.ScheduleRowText == query || row.FileRowText == query ||
                   row.Cells.Any(cell => cell.Text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0) ||
                   row.StatusText.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        // ───────────────────────────── check boxes ─────────────────────────────

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(_visible.Where(row => row.CanApply).ToList(), value);
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || Keyboard.FocusedElement is ComboBox)
                return;

            var rows = _grid.SelectedItems.OfType<ExcelPreviewRow>().Where(row => row.CanApply).ToList();
            if (rows.Count == 0)
                return;

            SetMany(rows, !rows.All(row => row.IsSelected));
            e.Handled = true;
        }

        /// <summary>Many check boxes at once: the summary is worked out once, at the end, not once per row.</summary>
        private void SetMany(IEnumerable<ExcelPreviewRow> rows, bool value)
        {
            _settingMany = true;

            foreach (var row in rows)
                row.IsSelected = value;

            _settingMany = false;
            UpdateSummary();
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!_settingMany && e.PropertyName == nameof(ExcelPreviewRow.IsSelected))
                UpdateSummary();
        }

        private void UpdateSummary()
        {
            var rows = _preview?.Rows ?? new List<ExcelPreviewRow>();

            var paired = rows.Count(row => row.Kind == ExcelRowKind.Matched);
            var scheduleRows = rows.Count(row => row.Kind != ExcelRowKind.FileOnly);
            var fileOnly = rows.Count(row => row.Kind == ExcelRowKind.FileOnly);
            var changes = rows.Sum(row => row.ChangeCount);
            var going = rows.Where(row => row.IsSelected).ToList();
            var goingChanges = going.Sum(row => row.ChangeCount);
            var blocked = rows.Sum(row => row.BlockedCount);

            if (_preview == null || _preview.Error != null)
            {
                _status.Text = string.Empty;
            }
            else
            {
                _status.Text = "Paired: " + paired + " of " + scheduleRows + " schedule rows" +
                               (fileOnly > 0 ? "; sheet lines left unpaired: " + fileOnly : string.Empty) +
                               ". Changes: " + changes + (changes == 1 ? " value" : " values") +
                               (goingChanges != changes ? ", checked: " + goingChanges : string.Empty) +
                               (blocked > 0 ? ". Cannot be written: " + blocked : string.Empty) + ".";
            }

            _status.Foreground = blocked > 0 || fileOnly > 0 ? Brushes.Firebrick : SystemColors.GrayTextBrush;

            _applyButton.IsEnabled = goingChanges > 0;
            _applyButton.Content = goingChanges > 0 ? "Import (" + goingChanges + ")" : "Import";

            var candidates = _visible.Where(row => row.CanApply).ToList();
            var selected = candidates.Count(row => row.IsSelected);

            _syncingSelectAll = true;
            _selectAll.IsChecked = candidates.Count == 0 || selected == 0 ? false : selected == candidates.Count ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        // ───────────────────────────── applying and closing ─────────────────────────────

        private void OnApply(object sender, RoutedEventArgs e)
        {
            if (_preview == null || _preview.Error != null)
                return;

            var going = _preview.Rows.Where(row => row.IsSelected).ToList();
            var values = going.Sum(row => row.ChangeCount);
            if (values == 0)
                return;

            var hidden = going.Count(row => !_visible.Contains(row));
            var blocked = _preview.Rows.Sum(row => row.BlockedCount);

            var text = "Write " + values + (values == 1 ? " value" : " values") + " from " + going.Count +
                       (going.Count == 1 ? " row" : " rows") + " into \"" + _request.ScheduleName + "\"?\n\n" +
                       "Everything goes in as one operation, so it undoes with a single Ctrl+Z.";

            if (hidden > 0)
                text += "\n\n" + hidden + " of these rows " + (hidden == 1 ? "is" : "are") + " not shown in the table right now " +
                        "(the \"Show\" list or the search hides them) — they are imported too.";

            if (blocked > 0)
                text += "\n\n" + blocked + (blocked == 1 ? " value" : " values") + " of the sheet cannot be written and " +
                        (blocked == 1 ? "is" : "are") + " left out — the red cells say why.";

            if (MessageBox.Show(this, text, WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            Result = _preview;
            DialogResult = true;
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            _preferences.EmptyClears = _emptyClearsBox.IsChecked == true;
            _preferences.SelectAfter = _selectAfterBox.IsChecked == true;
            _preferences.Save();
        }
    }
}
