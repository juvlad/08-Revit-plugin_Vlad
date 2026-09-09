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
    /// The "Delete Project Shared Parameters" window: a table of every shared parameter in the
    /// open project, with a check box on the left of each row. The checked parameters are deleted.
    ///
    /// Check boxes are set the same way as in the family "Delete Parameters" window: by hand
    /// (a click, a double click on the row, the space bar, the header check box for all at once)
    /// and by a "starts with" / "contains" rule that works like a search: matching names stay in
    /// the table and get checked, the rest leave it, and "Invert the search" swaps the sides.
    /// On top of that there is a "Show" filter: besides project parameters, the file also holds
    /// shared parameters that arrived with loaded families, and by eye there is no telling them apart.
    ///
    /// Only what is shown in the table gets deleted: a row that is hidden loses its check mark.
    ///
    /// Separately — a guard for families: a parameter that labels a dimension inside a family
    /// holds its geometry together. This is invisible from the project; the only way to find out
    /// is to open every family, so the check does not run by itself — it is started by the
    /// "Scan Families" button.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class DeleteProjectParametersWindow : Window
    {
        private const string WindowTitle = "Delete Project Shared Parameters";

        private readonly IReadOnlyList<ProjectParameterRow> _all;
        private readonly ObservableCollection<ProjectParameterRow> _visible = new ObservableCollection<ProjectParameterRow>();

        private readonly int _familyCount;
        private readonly Func<bool, FamilyDimensionScan> _scanFamilies;

        private readonly ComboBox _scopeBox;
        private readonly ComboBox _ruleBox;
        private readonly TextBox _patternBox;
        private readonly CheckBox _caseBox;
        private readonly CheckBox _invertBox;
        private readonly CheckBox _skipDimensionsBox;
        private readonly Button _scanButton;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _deleteButton;

        private bool _syncingSelectAll;
        private bool _settingMany;
        private int _pendingFamilies;
        private DateTime? _checkedAt;

        /// <summary>The parameters the user confirmed for deletion.</summary>
        public IReadOnlyList<ProjectParameterRow> Selected { get; private set; } = new List<ProjectParameterRow>();

        /// <param name="familyCount">How many families the project has in total — for the "Scan again" button.</param>
        /// <param name="pendingFamilies">How many of them the saved scan does not cover.</param>
        /// <param name="checkedAt">When the scan was saved; null if there never was one.</param>
        /// <param name="scanFamilies">
        /// The family scan for dimension labels; the argument is "scan again, ignore the cache".
        /// All the work with Revit is done by the command.
        /// </param>
        public DeleteProjectParametersWindow(
            IReadOnlyList<ProjectParameterRow> parameters,
            int familyCount,
            int pendingFamilies,
            DateTime? checkedAt,
            Func<bool, FamilyDimensionScan> scanFamilies)
        {
            _all = parameters ?? new List<ProjectParameterRow>();
            _familyCount = familyCount;
            _pendingFamilies = pendingFamilies;
            _checkedAt = checkedAt;
            _scanFamilies = scanFamilies;

            Title = WindowTitle;
            Width = 1000;
            Height = 620;
            MinWidth = 640;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _scopeBox = new ComboBox { Width = 230, VerticalAlignment = VerticalAlignment.Center };
            _scopeBox.Items.Add("Every shared parameter");
            _scopeBox.Items.Add("Project parameters only");
            _scopeBox.Items.Add("Unbound only");
            _scopeBox.SelectedIndex = 0;
            _scopeBox.ToolTip =
                "Only what is shown in the table gets deleted.\n" +
                "\"Project parameters\" — bound to categories, the ones visible in\n" +
                "\"Manage → Project Parameters\".\n" +
                "\"Unbound\" — shared parameters left in the file from loaded families\n" +
                "and from bindings that were removed.";
            _scopeBox.SelectionChanged += (s, e) => RebuildVisible();

            // Some scan already exists — so there is something for the check box to hide.
            var known = _familyCount == 0 || _pendingFamilies < _familyCount;

            _scanButton = new Button
            {
                Content = ScanButtonText(),
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = _scanFamilies != null && _familyCount > 0,
                ToolTip =
                    "Opens a loaded family and looks at which shared parameter labels its\n" +
                    "dimensions. The result is saved in the Windows profile, so next time\n" +
                    "only new and changed families are opened.\n\n" +
                    "When there is nothing left to open, the button scans every family again:\n" +
                    "Revit only marks a family changed on save, so a family reloaded during\n" +
                    "this session will not be noticed by the saved scan."
            };
            _scanButton.Click += OnScanFamilies;

            _skipDimensionsBox = new CheckBox
            {
                Content = "Hide parameters used on dimensions",
                IsChecked = known,
                IsEnabled = known,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip =
                    "A parameter that labels a dimension holds the family geometry together.\n" +
                    "Deleting it from the project means breaking the parametrics.\n" +
                    "The check box turns on once the family scan has covered at least part of them."
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
                ToolTip = "Check or clear every shown parameter"
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
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // family guard
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // rule
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "The checked shared parameters are deleted — together with their values on every project element. " +
                       "The rule below leaves only the names that match in the table and checks them right away; " +
                       "the check marks can then be edited by hand.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var scopePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            scopePanel.Children.Add(new TextBlock
            {
                Text = "Show:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            scopePanel.Children.Add(_scopeBox);
            Grid.SetRow(scopePanel, 1);
            root.Children.Add(scopePanel);

            var dimensionPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            dimensionPanel.Children.Add(_scanButton);
            dimensionPanel.Children.Add(_skipDimensionsBox);
            Grid.SetRow(dimensionPanel, 2);
            root.Children.Add(dimensionPanel);

            var rulePanel = new StackPanel { Orientation = Orientation.Horizontal };
            rulePanel.Children.Add(_ruleBox);
            rulePanel.Children.Add(_patternBox);
            rulePanel.Children.Add(_caseBox);
            rulePanel.Children.Add(_invertBox);
            Grid.SetRow(rulePanel, 3);
            root.Children.Add(rulePanel);

            Grid.SetRow(_grid, 4);
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
            grid.Columns.Add(TextColumn("Instance/Type", "Binding", new DataGridLength(110)));
            grid.Columns.Add(TextColumn("Dimensions", "DimensionUse", new DataGridLength(110)));
            grid.Columns.Add(TextColumn("Categories", "Categories", new DataGridLength(230)));
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
        private bool InScope(ProjectParameterRow row)
        {
            return MatchesScope(row) && !HiddenByDimensions(row) && MatchesPattern(row);
        }

        /// <summary>
        /// An empty rule hides nothing: an empty string matches any name — including under
        /// inversion, otherwise one check box would clear the whole table at once.
        /// </summary>
        private bool MatchesPattern(ProjectParameterRow row)
        {
            var pattern = Pattern;
            if (pattern.Length == 0)
                return true;

            var found = Rule == NameRule.StartsWith
                ? row.Name.StartsWith(pattern, Comparison)
                : row.Name.IndexOf(pattern, Comparison) >= 0;

            return _invertBox.IsChecked == true ? !found : found;
        }

        private bool MatchesScope(ProjectParameterRow row)
        {
            switch (_scopeBox.SelectedIndex)
            {
                case 1:
                    return row.IsBound;
                case 2:
                    return !row.IsBound;
                default:
                    return true;
            }
        }

        /// <summary>The parameter labels a dimension in a family, and the user asked not to show such ones.</summary>
        private bool HiddenByDimensions(ProjectParameterRow row)
        {
            return _skipDimensionsBox.IsChecked == true && row.UsedInDimensions;
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
            var rows = _grid.SelectedItems.OfType<ProjectParameterRow>().ToList();
            if (rows.Count == 0)
                return;

            // A mix is brought to one state: if not all are checked, we check all of them.
            var value = !rows.All(row => row.IsSelected);
            var affected = new HashSet<ProjectParameterRow>(rows);

            SetMany(row => affected.Contains(row) ? value : row.IsSelected);
        }

        /// <summary>
        /// Setting check marks in bulk: the totals are recomputed once, at the end,
        /// rather than for every row.
        /// </summary>
        private void SetMany(Func<ProjectParameterRow, bool> value)
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
            if (_settingMany || e.PropertyName != nameof(ProjectParameterRow.IsSelected))
                return;

            UpdateSummary();
        }

        private List<ProjectParameterRow> Marked()
        {
            return _visible.Where(row => row.IsSelected).ToList();
        }

        private void UpdateSummary()
        {
            var marked = Marked().Count;

            if (marked == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Shown: " + _visible.Count + " of " + _all.Count +
                               " shared parameters in the project. Nothing is checked." + DimensionNote();
            }
            else
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "Will be deleted: " + marked + " of " + _visible.Count +
                               " shown." + DimensionNote();
            }

            _deleteButton.IsEnabled = marked > 0;
            _deleteButton.Content = marked > 0 ? "Delete (" + marked + ")" : "Delete";

            _syncingSelectAll = true;
            _selectAll.IsChecked = _visible.Count == 0 || marked == 0
                ? false
                : marked == _visible.Count ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        /// <summary>A note for the status line: how the scan stands and what it hides.</summary>
        private string DimensionNote()
        {
            if (_familyCount == 0)
                return string.Empty;

            var hidden = _all.Count(row => MatchesScope(row) && HiddenByDimensions(row));
            var note = hidden > 0 ? " Hidden as dimension labels: " + hidden + "." : string.Empty;

            if (_pendingFamilies >= _familyCount)
                return " The families have not been scanned for dimension labels.";

            if (_pendingFamilies > 0)
                return note + " Families not scanned: " + _pendingFamilies + ".";

            return note + (_checkedAt.HasValue
                ? " Family scan from " + _checkedAt.Value.ToString("dd.MM.yyyy HH:mm") + "."
                : string.Empty);
        }

        // ───────────────────────────── the family scan ─────────────────────────────

        /// <summary>Nothing left to scan — so the button offers to run everything again, bypassing the saved scan.</summary>
        private bool ForcesRescan => _pendingFamilies == 0;

        private string ScanButtonText()
        {
            return ForcesRescan
                ? "Scan again (" + _familyCount + ")"
                : "Scan families (" + _pendingFamilies + ")";
        }

        /// <summary>
        /// Opens the loaded families and marks the parameters that label dimensions.
        /// This is a long, blocking job, so we ask first — it never starts on its own.
        /// </summary>
        private void OnScanFamilies(object sender, RoutedEventArgs e)
        {
            if (_scanFamilies == null)
                return;

            var force = ForcesRescan;

            if (!Confirm(force))
                return;

            FamilyDimensionScan scan;
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                scan = _scanFamilies(force);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not scan the families.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }

            foreach (var row in _all)
                row.UsedInDimensions = scan.ParameterGuids.Contains(row.Guid);

            // What is left unscanned is exactly what could not be opened.
            _pendingFamilies = scan.Failures.Count;
            _checkedAt = DateTime.Now;
            _scanButton.Content = ScanButtonText();
            _skipDimensionsBox.IsEnabled = true;

            // Checking the box will trigger a rebuild by itself; if it is already checked, no event fires.
            if (_skipDimensionsBox.IsChecked == true)
                RebuildVisible();
            else
                _skipDimensionsBox.IsChecked = true;

            ReportScan(scan);
        }

        private bool Confirm(bool force)
        {
            var text = force
                ? "The scan will run again over every family (" + _familyCount + "), " +
                  "replacing the saved result.\n\n" +
                  "This is needed after reloading families during the current session: Revit only marks " +
                  "a family changed on save, and the saved scan will not notice such an edit."
                : "The project cannot show which parameter labels a dimension inside a family — " +
                  "finding out requires opening the family.\n\n" +
                  "Left to open: " + _pendingFamilies + " of " + _familyCount +
                  "; the rest will come from the saved scan.";

            var answer = MessageBox.Show(
                this,
                text + "\n\nRevit will stop responding while this runs. Continue?",
                WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            return answer == MessageBoxResult.Yes;
        }

        private void ReportScan(FamilyDimensionScan scan)
        {
            var used = _all.Count(row => row.UsedInDimensions);

            var text = "Families opened: " + scan.OpenedFamilies +
                       ", taken from the saved scan: " + scan.ReusedFamilies +
                       " (" + _familyCount + " in the project in total).\n" +
                       "Shared parameters that label dimensions: " + used + ".\n\n" +
                       "The result is saved — next time the window opens already scanned.";

            if (scan.Failures.Count > 0)
            {
                const int limit = 10;
                text += "\n\nCould not be opened (" + scan.Failures.Count + "):\n• " +
                        string.Join("\n• ", scan.Failures.Take(limit));

                if (scan.Failures.Count > limit)
                    text += "\n… and " + (scan.Failures.Count - limit) + " more";

                text += "\n\nThe parameters of these families were not checked — delete them with caution.";
            }

            MessageBox.Show(this, text, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
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
                "Delete " + marked.Count + " shared parameters from the project?\n\n" +
                Preview(marked) + "\n\nAlong with the parameter its values on every project element will " +
                "disappear too, together with the schedule fields and filters that referred to it.\n" +
                (_pendingFamilies == 0
                    ? string.Empty
                    : "Families not scanned: " + _pendingFamilies + " — a parameter that holds a family's " +
                      "geometry together may be among the checked ones.\n") +
                "This can only be undone through \"Undo\" (Ctrl+Z) in Revit.",
                WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }

        private static string Preview(IReadOnlyList<ProjectParameterRow> rows)
        {
            const int limit = 12;
            var shown = string.Join("\n", rows.Take(limit).Select(row => "• " + row.Name));

            return rows.Count > limit
                ? shown + "\n… and " + (rows.Count - limit) + " more"
                : shown;
        }
    }
}
