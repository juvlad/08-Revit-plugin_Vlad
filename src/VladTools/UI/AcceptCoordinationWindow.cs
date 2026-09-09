using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// The "Accept Changes" window: the differences between the project's grids and levels and
    /// the coordination file, with a check box on each. The checked ones are applied to the
    /// project as a single operation.
    ///
    /// Showing the list before editing is mandatory, not "accept everything silently": in
    /// "Coordination Review" the user sees every change, and the button must not know less about
    /// the model than they do. Besides what the button is able to apply, the table also has
    /// read-only rows — link elements that vanished or are new: there is nothing the API can do
    /// about them, but the user still needs to know.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class AcceptCoordinationWindow : Window
    {
        private const string WindowTitle = "Accept Coordination Changes";

        private readonly IReadOnlyList<CoordinationScan> _scans;
        private readonly ObservableCollection<CoordinationChangeRow> _visible =
            new ObservableCollection<CoordinationChangeRow>();

        private readonly ComboBox _linkBox;
        private readonly ComboBox _scopeBox;
        private readonly CheckBox _selectAll;
        private readonly CheckBox _openReviewBox;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _applyButton;

        private bool _syncingSelectAll;
        private bool _settingMany;

        /// <summary>The changes the user confirmed for application.</summary>
        public IReadOnlyList<CoordinationChangeRow> Selected { get; private set; } =
            new List<CoordinationChangeRow>();

        /// <summary>The link the project is being adjusted against — it also goes into the report.</summary>
        public CoordinationScan Chosen => Current;

        /// <summary>Open "Coordination Review" after applying — to check that the list is empty.</summary>
        public bool OpenReview => _openReviewBox.IsChecked == true;

        public AcceptCoordinationWindow(IReadOnlyList<CoordinationScan> scans)
        {
            _scans = scans ?? new List<CoordinationScan>();

            Title = WindowTitle;
            Width = 980;
            Height = 600;
            MinWidth = 640;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _linkBox = new ComboBox
            {
                Width = 460,
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = _scans,
                DisplayMemberPath = nameof(CoordinationScan.Caption),
                SelectedIndex = _scans.Count > 0 ? 0 : -1,
                IsEnabled = _scans.Count > 1,
                ToolTip =
                    "The links that the project's grids and levels monitor.\n" +
                    "The one with the most differences comes first."
            };
            _linkBox.SelectionChanged += (s, e) => Reload();

            _scopeBox = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center };
            _scopeBox.Items.Add("Every difference");
            _scopeBox.Items.Add("Position only");
            _scopeBox.Items.Add("Names only");
            _scopeBox.Items.Add("Only what the button cannot apply");
            _scopeBox.SelectedIndex = 0;
            _scopeBox.ToolTip =
                "Only what is shown in the table gets applied:\n" +
                "a row that leaves it loses its check mark.";
            _scopeBox.SelectionChanged += (s, e) => RebuildVisible();

            _selectAll = new CheckBox
            {
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every shown change"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _openReviewBox = new CheckBox
            {
                Content = "Open \"Coordination Review\" after applying",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "The button edits the model itself, not Revit's own list: pressing \"Accept\" inside\n" +
                    "\"Coordination Review\" cannot be done through the API — it is simply not exposed.\n" +
                    "Once an element is back in place, Revit stops counting it as a difference on its own,\n" +
                    "and the list empties itself; opening it is worth doing at least to confirm that."
            };

            _grid = BuildGrid();
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _applyButton = new Button
            {
                Content = "Accept",
                MinWidth = 140,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _applyButton.Click += OnApply;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(closeButton);

            Reload();
        }

        private CoordinationScan Current => _linkBox.SelectedItem as CoordinationScan;

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // link
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // filter
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // "Coordination Review"
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "The project's grids and levels that monitor the coordination file have been compared " +
                       "against it. The checked ones will be put in line with the file as a single operation — " +
                       "it can be undone with one Ctrl+Z. Rows with no check box are ones the Revit API does not " +
                       "let this button apply: they are shown so they can be sorted out by hand.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var linkPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            linkPanel.Children.Add(new TextBlock
            {
                Text = "Coordination file:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            linkPanel.Children.Add(_linkBox);
            Grid.SetRow(linkPanel, 1);
            root.Children.Add(linkPanel);

            var scopePanel = new StackPanel { Orientation = Orientation.Horizontal };
            scopePanel.Children.Add(new TextBlock
            {
                Text = "Show:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            scopePanel.Children.Add(_scopeBox);
            Grid.SetRow(scopePanel, 2);
            root.Children.Add(scopePanel);

            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            Grid.SetRow(_openReviewBox, 4);
            root.Children.Add(_openReviewBox);

            var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_applyButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 5);
            root.Children.Add(bottom);

            return root;
        }

        private DataGrid BuildGrid()
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
                Margin = new Thickness(0, 10, 0, 0)
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = BuildCheckBoxTemplate()
            });

            grid.Columns.Add(TextColumn("Type", "Type", new DataGridLength(80)));
            grid.Columns.Add(TextColumn("Name", "Name", new DataGridLength(150)));
            grid.Columns.Add(TextColumn("What changed", "What", new DataGridLength(210)));
            grid.Columns.Add(TextColumn("Before → after", "Detail", new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(TextColumn("State", "Note", new DataGridLength(260)));

            grid.MouseDoubleClick += (s, e) => ToggleSelectedRows();
            grid.PreviewKeyDown += OnGridKeyDown;

            // Rows the button cannot apply are dimmed by colour: they have no check box anyway,
            // and there is no reason to confuse them with actionable ones.
            var style = new Style(typeof(DataGridRow));
            var trigger = new DataTrigger
            {
                Binding = new Binding(nameof(CoordinationChangeRow.CanApply)),
                Value = false
            };
            trigger.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.GrayTextBrush));
            style.Triggers.Add(trigger);
            grid.RowStyle = style;

            return grid;
        }

        private static DataGridTextColumn TextColumn(string header, string property, DataGridLength width)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(property)));

            return new DataGridTextColumn
            {
                Header = header,
                Width = width,
                Binding = new Binding(property),
                ElementStyle = style
            };
        }

        /// <summary>
        /// A check box in a cell. Whether it is enabled is bound to the row itself: a vanished or
        /// new link element has nothing to apply, and the check box must not suggest otherwise.
        /// </summary>
        private static DataTemplate BuildCheckBoxTemplate()
        {
            var checkBox = new FrameworkElementFactory(typeof(CheckBox));
            checkBox.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding(nameof(CoordinationChangeRow.IsSelected))
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });
            checkBox.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(CoordinationChangeRow.CanApply)));
            checkBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            return new DataTemplate { VisualTree = checkBox };
        }

        // ───────────────────────────── filtering ─────────────────────────────

        private IReadOnlyList<CoordinationChangeRow> All =>
            Current != null ? Current.Rows : (IReadOnlyList<CoordinationChangeRow>)new List<CoordinationChangeRow>();

        private bool InScope(CoordinationChangeRow row)
        {
            switch (_scopeBox.SelectedIndex)
            {
                case 1:
                    return row.Kind == CoordinationChangeKind.Position;
                case 2:
                    return row.Kind == CoordinationChangeKind.Name;
                case 3:
                    return !row.CanApply;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Switching the link: the table is rebuilt from scratch and everything applicable is
        /// checked right away.
        ///
        /// "Shown means checked" here is not an oversight but the whole point of the button: it is
        /// asked to accept the coordination-file changes all at once. This is not an exception to
        /// the "an empty filter does not mean select everything" rule — that rule guards against
        /// accidental deletion, and here nothing is deleted and everything rolls back with one Ctrl+Z.
        /// </summary>
        private void Reload()
        {
            foreach (var scan in _scans)
            {
                foreach (var row in scan.Rows)
                    row.PropertyChanged -= OnRowChanged;
            }

            foreach (var row in All)
                row.PropertyChanged += OnRowChanged;

            SetMany(row => row.CanApply);
            RebuildVisible();
        }

        /// <summary>
        /// Rebuilds the table. A row that leaves it loses its check mark — only what is visible
        /// gets applied; the same rule as in the delete windows.
        /// </summary>
        private void RebuildVisible()
        {
            _visible.Clear();

            foreach (var row in All)
            {
                if (InScope(row))
                    _visible.Add(row);
            }

            SetMany(row => InScope(row) && row.CanApply && row.IsSelected);
        }

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(row => value && InScope(row) && row.CanApply);
        }

        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<CoordinationChangeRow>().Where(row => row.CanApply).ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);
            var affected = new HashSet<CoordinationChangeRow>(rows);

            SetMany(row => affected.Contains(row) ? value : row.IsSelected);
        }

        /// <summary>Setting check marks in bulk: the total is recomputed once, at the end.</summary>
        private void SetMany(Func<CoordinationChangeRow, bool> value)
        {
            _settingMany = true;

            foreach (var row in All)
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
            if (_settingMany || e.PropertyName != nameof(CoordinationChangeRow.IsSelected))
                return;

            UpdateSummary();
        }

        private List<CoordinationChangeRow> Marked()
        {
            return _visible.Where(row => row.IsSelected && row.CanApply).ToList();
        }

        private void UpdateSummary()
        {
            var scan = Current;
            var marked = Marked();
            var applicable = _visible.Count(row => row.CanApply);

            if (scan == null)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "The project has no link monitored by any grid or level.";
            }
            else if (!scan.IsLoaded)
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "The link is not loaded — there is nothing to compare against. Load it in \"Link Manager\".";
            }
            else if (scan.Rows.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "No differences: all " + scan.MonitoredCount +
                               " grids and levels are in line with the coordination file.";
            }
            else if (marked.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Shown: " + _visible.Count + " of " + scan.Rows.Count +
                               " differences. Nothing is checked." + Unsupported();
            }
            else
            {
                _status.Foreground = Brushes.DarkGreen;
                _status.Text = "Will be accepted: " + marked.Count + " of " + applicable +
                               " applicable." + Unsupported();
            }

            _applyButton.IsEnabled = marked.Count > 0;
            _applyButton.Content = marked.Count > 0 ? "Accept (" + marked.Count + ")" : "Accept";

            _syncingSelectAll = true;
            _selectAll.IsChecked = applicable == 0 || marked.Count == 0
                ? false
                : marked.Count == applicable ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        /// <summary>A note about what the button is not able to do: this must not stay unmentioned.</summary>
        private string Unsupported()
        {
            var scan = Current;
            if (scan == null)
                return string.Empty;

            var count = scan.Rows.Count(row => !row.CanApply);
            return count == 0 ? string.Empty : " Will have to be sorted out by hand: " + count + ".";
        }

        // ───────────────────────────── actions ─────────────────────────────

        private void OnApply(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "No change is checked.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var moves = marked.Count(row => row.Kind == CoordinationChangeKind.Position);
            var names = marked.Count - moves;

            var answer = MessageBox.Show(
                this,
                "Accept " + marked.Count + " change(s) (position — " + moves + ", names — " + names + ")?\n\n" +
                Preview(marked) + "\n\n" +
                "The grids and levels will be put in line with the coordination file; anything tied to " +
                "them moves along with them.\n" +
                "This can be undone with a single \"Undo\" (Ctrl+Z) in Revit.",
                WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);

            if (answer != MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }

        private static string Preview(IReadOnlyList<CoordinationChangeRow> rows)
        {
            const int limit = 12;
            var shown = string.Join("\n", rows.Take(limit).Select(row => "• " + row.Title + " — " + row.Detail));

            return rows.Count > limit
                ? shown + "\n… and " + (rows.Count - limit) + " more"
                : shown;
        }
    }
}
