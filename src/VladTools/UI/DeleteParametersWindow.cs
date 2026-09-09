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
    /// <summary>The name-matching rule for filtering parameters.</summary>
    internal enum NameRule
    {
        StartsWith,
        Contains
    }

    /// <summary>
    /// The "Delete Parameters" window: a table of every shared parameter in the family, with a
    /// check box on the left of each row. The checked parameters are deleted.
    ///
    /// Check boxes are set two ways: by hand (clicking a box, or the one in the header — all at
    /// once) and by a "starts with" / "contains" rule. The rule works like a search: names that
    /// match stay in the table and get checked, the rest leave it; "Invert the search" swaps the sides.
    ///
    /// Parameters that label dimensions are left out of the list by default: deleting one means
    /// dropping the label and breaking the family parametrics. Only what is shown in the table
    /// gets deleted, so a row that is hidden loses its check mark.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class DeleteParametersWindow : Window
    {
        private const string WindowTitle = "Delete Parameters";

        private readonly IReadOnlyList<SharedParameterRow> _all;
        private readonly ObservableCollection<SharedParameterRow> _visible = new ObservableCollection<SharedParameterRow>();

        private readonly ComboBox _ruleBox;
        private readonly TextBox _patternBox;
        private readonly CheckBox _caseBox;
        private readonly CheckBox _invertBox;
        private readonly CheckBox _skipDimensionsBox;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _deleteButton;

        private bool _syncingSelectAll;
        private bool _settingMany;

        /// <summary>The parameters the user confirmed for deletion.</summary>
        public IReadOnlyList<SharedParameterRow> Selected { get; private set; } = new List<SharedParameterRow>();

        public DeleteParametersWindow(IReadOnlyList<SharedParameterRow> parameters)
        {
            _all = parameters ?? new List<SharedParameterRow>();

            Title = WindowTitle;
            Width = 940;
            Height = 580;
            MinWidth = 620;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _skipDimensionsBox = new CheckBox
            {
                Content = "Hide parameters used on dimensions",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "A parameter that labels a dimension holds the family geometry together.\n" +
                    "Deleting it drops the label from the dimension and breaks the parametrics,\n" +
                    "so such parameters are hidden from the list by default.\n" +
                    "Clear the box to see them (marked in the \"Dimensions\" column)."
            };
            _skipDimensionsBox.Checked += (s, e) => RebuildVisible();
            _skipDimensionsBox.Unchecked += (s, e) => RebuildVisible();

            _ruleBox = new ComboBox { Width = 250, VerticalAlignment = VerticalAlignment.Center };
            _ruleBox.Items.Add("Match parameters starting with");
            _ruleBox.Items.Add("Match parameters containing");
            _ruleBox.SelectedIndex = 0;
            _ruleBox.SelectionChanged += (s, e) => RebuildVisible();

            _patternBox = new TextBox
            {
                MinWidth = 180,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2)
            };
            _patternBox.TextChanged += (s, e) => RebuildVisible();

            _caseBox = new CheckBox
            {
                Content = "Match case",
                VerticalAlignment = VerticalAlignment.Center
            };
            _caseBox.Checked += (s, e) => RebuildVisible();
            _caseBox.Unchecked += (s, e) => RebuildVisible();

            _invertBox = new CheckBox
            {
                Content = "Invert the search",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip =
                    "The rule works in reverse: the table keeps the parameters that do NOT match it.\n" +
                    "For example, \"containing\" + \"ADSK\" + invert leaves every parameter except the ADSK ones.\n" +
                    "An empty rule still shows the whole list and checks nothing."
            };
            _invertBox.Checked += (s, e) => RebuildVisible();
            _invertBox.Unchecked += (s, e) => RebuildVisible();

            _selectAll = new CheckBox
            {
                IsChecked = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every parameter"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _deleteButton = new Button
            {
                Content = "Delete",
                MinWidth = 130,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _deleteButton.Click += OnDelete;

            var cancelButton = new Button
            {
                Content = "Cancel",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(cancelButton);

            foreach (var row in _all)
                row.PropertyChanged += OnRowChanged;

            Loaded += (s, e) => _patternBox.Focus();

            RebuildVisible();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // filter
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // rule
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "The checked parameters are deleted. The rule below leaves only the names that " +
                       "match in the table and checks them right away; the check marks can then be edited by hand.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var scopePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            scopePanel.Children.Add(_skipDimensionsBox);
            Grid.SetRow(scopePanel, 1);
            root.Children.Add(scopePanel);

            var rulePanel = new StackPanel { Orientation = Orientation.Horizontal };
            rulePanel.Children.Add(_ruleBox);
            rulePanel.Children.Add(_patternBox);
            rulePanel.Children.Add(_caseBox);
            rulePanel.Children.Add(_invertBox);
            Grid.SetRow(rulePanel, 2);
            root.Children.Add(rulePanel);

            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_deleteButton);
            buttons.Children.Add(cancelButton);
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
                Margin = new Thickness(0, 10, 0, 8)
            };

            // The check box sits to the left of the parameter — the first column, with an
            // "all" check box in the header.
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = BuildCheckBoxTemplate()
            });

            grid.Columns.Add(TextColumn("Name", "Name", new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(TextColumn("Instance/Type", "Binding", new DataGridLength(105)));
            grid.Columns.Add(TextColumn("Dimensions", "DimensionUse", new DataGridLength(110)));
            grid.Columns.Add(TextColumn("Group", "Group", new DataGridLength(150)));
            grid.Columns.Add(TextColumn("GUID", "Guid", new DataGridLength(240)));

            // A double click on a row and the space bar also toggle the check box —
            // hitting the small square is not the only way.
            grid.MouseDoubleClick += (s, e) => ToggleSelectedRows();
            grid.PreviewKeyDown += OnGridKeyDown;

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

        /// <summary>A check box in a cell: with its own template it reacts to the first click.</summary>
        private static DataTemplate BuildCheckBoxTemplate()
        {
            var checkBox = new FrameworkElementFactory(typeof(CheckBox));
            checkBox.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding("IsSelected") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            checkBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            return new DataTemplate { VisualTree = checkBox };
        }

        // ───────────────────────────── filtering ─────────────────────────────

        private NameRule Rule => _ruleBox.SelectedIndex == 1 ? NameRule.Contains : NameRule.StartsWith;

        private string Pattern => _patternBox.Text?.Trim() ?? string.Empty;

        private StringComparison Comparison =>
            _caseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        /// <summary>
        /// Whether a row belongs in the table. A hidden row is not merely invisible: it cannot be
        /// deleted, so it loses its check mark.
        /// </summary>
        private bool InScope(SharedParameterRow row)
        {
            return !HiddenByDimensions(row) && MatchesPattern(row);
        }

        /// <summary>The parameter labels a dimension, and the user asked not to show such ones.</summary>
        private bool HiddenByDimensions(SharedParameterRow row)
        {
            return _skipDimensionsBox.IsChecked == true && row.UsedInDimensions;
        }

        /// <summary>
        /// An empty rule hides nothing: an empty string matches any name — including under
        /// inversion, otherwise one check box would clear the whole table at once.
        /// </summary>
        private bool MatchesPattern(SharedParameterRow row)
        {
            var pattern = Pattern;
            if (pattern.Length == 0)
                return true;

            var found = Rule == NameRule.StartsWith
                ? row.Name.StartsWith(pattern, Comparison)
                : row.Name.IndexOf(pattern, Comparison) >= 0;

            return _invertBox.IsChecked == true ? !found : found;
        }

        /// <summary>
        /// Rebuilds the table. The rule works like a search: it leaves only the matching names in
        /// the list and checks them right away, the rest leave the table and lose their check mark —
        /// only what is visible gets deleted. An empty rule shows everything and checks nothing, so
        /// that "typed nothing in" never means "delete everything".
        /// </summary>
        private void RebuildVisible()
        {
            _visible.Clear();

            foreach (var row in _all)
            {
                if (InScope(row))
                    _visible.Add(row);
            }

            var byRule = Pattern.Length > 0;

            SetMany(row => InScope(row) && (byRule || row.IsSelected));
        }

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(row => value && InScope(row));
        }

        /// <summary>Toggles the check marks of the rows selected in the table — by double click or the space bar.</summary>
        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<SharedParameterRow>().ToList();
            if (rows.Count == 0)
                return;

            // A mix is brought to one state: if not all are checked, we check all of them.
            var value = !rows.All(row => row.IsSelected);
            var affected = new HashSet<SharedParameterRow>(rows);

            SetMany(row => affected.Contains(row) ? value : row.IsSelected);
        }

        /// <summary>
        /// Setting check marks in bulk: the totals are recomputed once, at the end,
        /// rather than for every row.
        /// </summary>
        private void SetMany(Func<SharedParameterRow, bool> value)
        {
            _settingMany = true;

            foreach (var row in _all)
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
            if (_settingMany || e.PropertyName != nameof(SharedParameterRow.IsSelected))
                return;

            UpdateSummary();
        }

        private List<SharedParameterRow> Marked()
        {
            return _visible.Where(row => row.IsSelected).ToList();
        }

        private void UpdateSummary()
        {
            var marked = Marked().Count;
            var hidden = _all.Count(HiddenByDimensions);
            var hiddenText = hidden > 0 ? " Hidden as dimension labels: " + hidden + "." : string.Empty;

            if (marked == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Shown: " + _visible.Count + " of " + _all.Count +
                               " shared parameters in the family." + hiddenText + " Nothing is checked.";
            }
            else
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "Will be deleted: " + marked + " of " + _visible.Count + " shown." + hiddenText;
            }

            _deleteButton.IsEnabled = marked > 0;
            _deleteButton.Content = marked > 0 ? "Delete (" + marked + ")" : "Delete";

            _syncingSelectAll = true;
            _selectAll.IsChecked = _visible.Count == 0 || marked == 0
                ? false
                : marked == _visible.Count ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        // ───────────────────────────── actions ─────────────────────────────

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "No parameter is checked.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(
                this,
                "Delete " + marked.Count + " shared parameters from the family?\n\n" +
                Preview(marked) + "\n\nThis can only be undone through \"Undo\" (Ctrl+Z) in Revit.",
                WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }

        private static string Preview(IReadOnlyList<SharedParameterRow> rows)
        {
            const int limit = 12;
            var shown = string.Join("\n", rows.Take(limit).Select(row => "• " + row.Name));

            return rows.Count > limit
                ? shown + "\n… and " + (rows.Count - limit) + " more"
                : shown;
        }
    }
}
