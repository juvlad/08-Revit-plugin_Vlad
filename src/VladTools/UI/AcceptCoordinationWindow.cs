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
    /// read-only rows — new link elements, and differences that cannot be expressed as a move:
    /// there is nothing the API can do about them, but the user still needs to know.
    ///
    /// An element gone from the coordination file sits between the two: the button can delete it,
    /// but a deleted level takes everything standing on it along, so such a row is checkable only
    /// while the "Delete…" box is on and is never checked for the user — see
    /// <see cref="AllowRemoval"/>.
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
        private readonly CheckBox _removeBox;
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

        /// <summary>
        /// The user asked for the batch over other models instead of the open project.
        ///
        /// The window closes on this rather than opening the batch one on top of itself: the two
        /// decide different things, and a check mark set here means nothing there — the same reason
        /// "Take a sample…" closes the "Auto Dimensions" window instead of working through it.
        /// </summary>
        public bool WantsBatch { get; private set; }

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
            _scopeBox.Items.Add("Gone from the coordination file");
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

            _removeBox = new CheckBox
            {
                Content = "Delete grids and levels that are gone from the coordination file",
                IsChecked = false,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Firebrick,
                ToolTip =
                    "Off — such rows are shown but their check box is disabled.\n\n" +
                    "Deleting a level takes everything standing on it with it, and the\n" +
                    "\"State\" column says how many elements that is for each row. That is why\n" +
                    "the deletion is a switch of its own and nothing here is ever checked by\n" +
                    "default: every other change in this window only moves or renames something."
            };
            _removeBox.Checked += (s, e) => AllowRemoval(true);
            _removeBox.Unchecked += (s, e) => AllowRemoval(false);

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
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // switches
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "The project's grids and levels that monitor the coordination file have been compared " +
                       "against it. The checked ones will be put in line with the file as a single operation — " +
                       "it can be undone with one Ctrl+Z. Elements gone from the file can be deleted along with " +
                       "the rest, but only once the box below is on, and they are never checked by default. Rows " +
                       "with no check box are ones the Revit API does not let this button apply: they are shown " +
                       "so they can be sorted out by hand.",
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

            var switches = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            switches.Children.Add(_removeBox);
            _openReviewBox.Margin = new Thickness(0, 4, 0, 0);
            switches.Children.Add(_openReviewBox);
            Grid.SetRow(switches, 4);
            root.Children.Add(switches);

            var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var batchButton = new Button
            {
                Content = "Other models…",
                MinWidth = 140,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip =
                    "The same thing, but on a batch of models nobody has open: each one is opened in\n" +
                    "turn with only the grid and base-file worksets in it, its grids are put in line\n" +
                    "with the coordination file linked into it, and the model is synchronised back.\n\n" +
                    "This window closes — what is checked in it applies to the open project alone."
            };
            batchButton.Click += OnBatch;
            buttons.Children.Add(batchButton);

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

            var style = new Style(typeof(DataGridRow));

            // A row that deletes is the one thing here that cannot be judged from the model
            // afterwards, so it is coloured apart from the rows that merely move something.
            var removal = new DataTrigger
            {
                Binding = new Binding(nameof(CoordinationChangeRow.IsRemoval)),
                Value = true
            };
            removal.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Firebrick));
            style.Triggers.Add(removal);

            // Rows the button cannot apply are dimmed by colour: they have no check box anyway,
            // and there is no reason to confuse them with actionable ones. This trigger comes
            // second on purpose — while deletions are switched off, grey wins over red.
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
                    return row.IsRemoval;
                case 4:
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
            {
                row.PropertyChanged += OnRowChanged;
                row.RemovalAllowed = _removeBox.IsChecked == true;
            }

            SetMany(row => row.CanApply && !row.IsRemoval);
            RebuildVisible();
        }

        /// <summary>
        /// Turns deletions on or off for the rows of the current link.
        ///
        /// Switching the box on checks nothing by itself: the user said "deleting is allowed here",
        /// not "delete everything that is gone from the file" — that is the one rule this add-in
        /// keeps wherever a button deletes. Switching it off clears what was checked, through
        /// <see cref="RebuildVisible"/>: a check mark on a row that can no longer be applied must
        /// never reach the transaction.
        /// </summary>
        private void AllowRemoval(bool value)
        {
            foreach (var row in All)
                row.RemovalAllowed = value;

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
                var removals = marked.Where(row => row.IsRemoval).ToList();

                _status.Foreground = removals.Count > 0 ? Brushes.Firebrick : Brushes.DarkGreen;
                _status.Text = "Will be accepted: " + marked.Count + " of " + applicable +
                               " applicable." + Removing(removals) + Unsupported();
            }

            _applyButton.IsEnabled = marked.Count > 0;
            _applyButton.Content = marked.Count > 0 ? "Accept (" + marked.Count + ")" : "Accept";

            _syncingSelectAll = true;
            _selectAll.IsChecked = applicable == 0 || marked.Count == 0
                ? false
                : marked.Count == applicable ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        /// <summary>
        /// The deletion part of the status line, kept separate from the rest of the count: this is
        /// the only thing in the window that removes something from the project, and a number in a
        /// line that also says "accepted" would read as one more move.
        ///
        /// The element count is "up to": an element bound to two deleted levels at once is counted
        /// by both of them, and overstating the price of a deletion is the safe direction to err in.
        /// </summary>
        private static string Removing(IReadOnlyList<CoordinationChangeRow> removals)
        {
            if (removals.Count == 0)
                return string.Empty;

            var dependents = removals.Sum(row => row.Dependents);
            var text = " Will be deleted: " + removals.Count;

            return dependents > 0
                ? text + ", and up to " + dependents + " element(s) tied to them."
                : text + ".";
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

        /// <summary>
        /// Hands the work over to the batch window. Closed with <c>DialogResult = false</c>: nothing
        /// is applied to the open project here, and the command tells the two apart by
        /// <see cref="WantsBatch"/> rather than by the dialog result.
        /// </summary>
        private void OnBatch(object sender, RoutedEventArgs e)
        {
            WantsBatch = true;
            DialogResult = false;
        }

        private void OnApply(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "No change is checked.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var removals = marked.Where(row => row.IsRemoval).ToList();
            var moves = marked.Count(row => row.Kind == CoordinationChangeKind.Position);
            var names = marked.Count(row => row.Kind == CoordinationChangeKind.Name);

            var text = "Accept " + marked.Count + " change(s) (position — " + moves + ", names — " + names +
                       (removals.Count > 0 ? ", deletions — " + removals.Count : string.Empty) + ")?\n\n";

            var edits = marked.Where(row => !row.IsRemoval).ToList();
            if (edits.Count > 0)
                text += Preview(edits) + "\n\n";

            // The deletions are listed apart from the moves, by name and with their price: in the
            // line above they are one number among three, and that is not enough to decide on.
            if (removals.Count > 0)
                text += Danger(removals) + "\n\n";

            text += "The grids and levels will be put in line with the coordination file; anything tied to " +
                    "them moves along with them.\n" +
                    "This can be undone with a single \"Undo\" (Ctrl+Z) in Revit.";

            var answer = MessageBox.Show(
                this,
                text,
                WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                removals.Count > 0 ? MessageBoxResult.No : MessageBoxResult.Yes);

            if (answer != MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }

        /// <summary>
        /// What exactly will be deleted and at what price. Everything else in this window can be
        /// checked by eye on the plan afterwards; a deleted level cannot, so the list is spelled
        /// out here in full rather than summed up into a count.
        /// </summary>
        private static string Danger(IReadOnlyList<CoordinationChangeRow> removals)
        {
            const int limit = 12;

            var text = "These " + removals.Count + " will be DELETED from the project — they are no longer " +
                       "in the coordination file:\n" +
                       string.Join("\n", removals.Take(limit).Select(row => "• " + row.Title +
                           (row.Dependents > 0
                               ? " — and " + row.Dependents + " element(s) tied to it"
                               : string.Empty)));

            if (removals.Count > limit)
                text += "\n… and " + (removals.Count - limit) + " more";

            var dependents = removals.Sum(row => row.Dependents);
            if (dependents > 0)
            {
                text += "\n\nEverything standing on a deleted level is deleted by Revit together with it — " +
                        "walls, rooms, views, whatever is hosted there.";
            }

            return text;
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
