using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// The "Sheet Scale" window: the sheets of the project in the folders and the order of the Project
    /// Browser, every view standing on each, and one new scale for the checked ones.
    ///
    /// It opens with the sheets selected in the Project Browser — or the sheet that is open — already
    /// checked, so the usual run is: select the sheets, press the button, pick the scale. "Change the
    /// checked views at" narrows what can be checked to one scale, so a 1:20 detail on a 1:100 sheet is
    /// not swept along; a view that stops being checkable loses its check mark.
    ///
    /// "Find" only finds, the same as in "Views to Sheets": a view the search hides keeps its check
    /// mark, and the summary and the confirmation say how many checked views are out of sight.
    ///
    /// The window knows nothing about the Revit API: it is handed snapshots and hands back a
    /// <see cref="SheetScaleRequest"/>.
    /// </summary>
    internal sealed class SheetScaleWindow : Window
    {
        private const string WindowTitle = "Sheet Scale";

        /// <summary>The first item of "Change the checked views at": no scale narrows anything.</summary>
        private const string AnyScale = "any scale";

        /// <summary>Half a millimetre: an estimate this close to the edge of the drawing area counts as fitting.</summary>
        private const double FitTolerance = 0.5;

        /// <summary>Revit's own metric scales; any other is typed in, and Revit makes it a custom scale.</summary>
        private static readonly string[] StandardScales =
            new[] { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000 }.Select(ViewPlacementRow.ScaleCaption).ToArray();

        private static readonly Brush FolderBackground = Frozen(new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFB)));
        private static readonly Brush SheetBackground = Frozen(new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6)));

        private readonly SheetScalePreferences _preferences;

        private readonly List<SheetScaleRow> _all = new List<SheetScaleRow>();
        private readonly ObservableCollection<SheetScaleRow> _visible = new ObservableCollection<SheetScaleRow>();

        /// <summary>Every line of a view, by the view: a legend has one per sheet it stands on, and one check box for all of them.</summary>
        private readonly Dictionary<string, List<SheetScaleRow>> _byView = new Dictionary<string, List<SheetScaleRow>>(StringComparer.Ordinal);

        /// <summary>The view lines the search leaves, folding aside.</summary>
        private HashSet<SheetScaleRow> _shown = new HashSet<SheetScaleRow>();

        private readonly ComboBox _fromBox;
        private readonly ComboBox _scaleBox;
        private readonly ComboBox _templateBox;
        private readonly TextBlock _templateNote;
        private readonly TextBox _searchBox;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _applyButton;

        private bool _syncing;
        private bool _syncingSelectAll;
        private bool _ready;

        /// <summary>What to do, as the window was closed with "Change scale".</summary>
        public SheetScaleRequest Request { get; private set; }

        /// <param name="sheets">Every sheet with a view on it, in the order the Project Browser lists them.</param>
        /// <param name="organization">The name of the browser organization the folders come from; empty when unknown.</param>
        /// <param name="checkedSheets">The UniqueIds of the sheets whose views start checked — legends excepted.</param>
        /// <param name="checkedViews">The UniqueIds of the views that start checked, on whichever sheet they stand.</param>
        public SheetScaleWindow(
            IReadOnlyList<ScaleSheetInfo> sheets,
            string organization,
            ICollection<string> checkedSheets,
            ICollection<string> checkedViews,
            SheetScalePreferences preferences)
        {
            _preferences = preferences;

            BuildTree(sheets, checkedSheets, checkedViews);

            Title = WindowTitle;
            Width = 1120;
            Height = 840;
            MinWidth = 820;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            var scales = _all
                .Where(row => row.IsView && row.View.Scale > 0)
                .Select(row => row.View.Scale)
                .Distinct()
                .OrderBy(scale => scale)
                .Select(ViewPlacementRow.ScaleCaption);

            _fromBox = new ComboBox
            {
                Width = 110,
                ItemsSource = new[] { AnyScale }.Concat(scales).ToList(),
                SelectedIndex = 0,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Only the views at this scale can be checked — a 1:20 detail on a 1:100 sheet is not swept along.\n" +
                          "A view that stops being checkable loses its check mark."
            };
            _fromBox.SelectionChanged += (s, e) => RebuildVisible();

            _scaleBox = new ComboBox
            {
                Width = 110,
                IsEditable = true,
                ItemsSource = StandardScales,
                Text = _preferences.Scale ?? string.Empty,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "A scale from the list, or typed: 1:250 or simply 250. Revit makes a scale not in its list a custom one."
            };
            // The box's own text box raises this both for typing and for a pick from the list.
            _scaleBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => Revalidate()));

            var anyTemplate = _all.Any(row => row.IsView && row.View.TemplateSetsScale);
            var mode = Array.IndexOf(ScaleTemplateModeText.All, _preferences.Templates);

            _templateBox = new ComboBox
            {
                Width = 300,
                ItemsSource = ScaleTemplateModeText.All.Select(ScaleTemplateModeText.Caption).ToList(),
                SelectedIndex = mode < 0 ? 0 : mode,
                IsEnabled = anyTemplate,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = anyTemplate
                    ? "A view template that sets the scale decides it — the view's own scale cannot be changed past it."
                    : "No view on these sheets has a view template that sets its scale."
            };
            _templateBox.SelectionChanged += (s, e) =>
            {
                UpdateTemplateNote();
                RebuildVisible();
            };

            _templateNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(0, 4, 0, 0) };

            _searchBox = new TextBox
            {
                Width = 240,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Finds sheets by number or name, views by name or kind, and folders. A view the search hides keeps its check mark —\n" +
                          "find one lot, tick it, then find the next."
            };
            _searchBox.TextChanged += OnSearchChanged;

            var collapseAll = new Button { Content = "Collapse all", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(10, 0, 0, 0) };
            collapseAll.Click += (s, e) => SetAllExpanded(false);

            var expandAll = new Button { Content = "Expand all", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            expandAll.Click += (s, e) => SetAllExpanded(true);

            _selectAll = new CheckBox
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every view the search leaves in the table"
            };
            _selectAll.Checked += (s, e) => SetAllChecked(true);
            _selectAll.Unchecked += (s, e) => SetAllChecked(false);

            _grid = BuildGrid();

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _applyButton = new Button
            {
                Content = "Change scale",
                MinWidth = 140,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false,
                ToolTip = "Gives every checked view the new scale and keeps the middle of each viewport where it stands — one operation, one Ctrl+Z."
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

            Content = BuildLayout(organization, collapseAll, expandAll, closeButton);

            _grid.ItemsSource = _visible;

            _ready = true;
            UpdateTemplateNote();
            RebuildVisible();

            Loaded += (s, e) =>
            {
                var first = _visible.FirstOrDefault(row => row.IsView && row.IsChecked == true);
                if (first != null)
                    _grid.ScrollIntoView(first);

                if (TargetScale == 0)
                    _scaleBox.Focus();
                else
                    _searchBox.Focus();
            };
            Closing += OnClosing;
        }

        // ───────────────────────────── the tree ─────────────────────────────

        /// <summary>
        /// Lays the sheets out under their folder lines, and every view under its sheet. The sheets
        /// arrive sorted by their paths, so a folder's sheets are consecutive, and a new folder line is
        /// needed only where a sheet's path parts from the one before it. A sheet starts folded unless
        /// something on it starts checked: the whole set one line a sheet, the sheets in hand open.
        /// </summary>
        private void BuildTree(IReadOnlyList<ScaleSheetInfo> sheets, ICollection<string> checkedSheets, ICollection<string> checkedViews)
        {
            var open = new List<SheetScaleRow>();

            foreach (var sheet in sheets)
            {
                var path = sheet.Folders;

                var common = 0;
                while (common < open.Count && common < path.Count && string.Equals(open[common].FolderName, path[common], StringComparison.Ordinal))
                    common++;

                open.RemoveRange(common, open.Count - common);

                for (var level = common; level < path.Count; level++)
                {
                    var folder = SheetScaleRow.Folder(level == 0 ? null : open[level - 1], level, path[level]);
                    folder.Toggled += OnToggled;
                    open.Add(folder);
                    _all.Add(folder);
                }

                var sheetRow = SheetScaleRow.ForSheet(open.Count == 0 ? null : open[open.Count - 1], path.Count, sheet);
                sheetRow.Toggled += OnToggled;
                _all.Add(sheetRow);

                var sheetChecked = checkedSheets.Contains(sheet.UniqueId);

                foreach (var viewport in sheet.Viewports)
                {
                    var row = SheetScaleRow.ForView(sheetRow, path.Count + 1, sheet, viewport);
                    row.Toggled += OnToggled;
                    _all.Add(row);

                    sheetRow.Views.Add(row);
                    foreach (var folder in open)
                        folder.Views.Add(row);

                    if (!_byView.TryGetValue(viewport.View.UniqueId, out var lines))
                        _byView[viewport.View.UniqueId] = lines = new List<SheetScaleRow>();
                    lines.Add(row);

                    // A legend stands on many sheets and is rarely what a sheet's new scale is meant for: checked by hand only.
                    if (sheetChecked && !viewport.View.IsLegend || checkedViews.Contains(viewport.View.UniqueId))
                        row.SetChecked(true);
                }
            }

            foreach (var lines in _byView.Values.Where(lines => lines.Exists(row => row.IsChecked == true)))
            {
                foreach (var row in lines)
                    row.SetChecked(true);
            }

            foreach (var row in _all.Where(row => row.IsSheet))
                row.IsExpanded = row.Views.Exists(view => view.IsChecked == true);
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(string organization, Button collapseAll, Button expandAll, Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // the scale
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // find
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "Check the views whose scale is to change: a sheet's box takes every view on it, a folder's every view in it. " +
                       "Each view keeps its place — the middle of its viewport stays where it stands on the sheet. The sheets are in the " +
                       "folders and the order of the Project Browser" +
                       (string.IsNullOrEmpty(organization) ? "" : " (sheet organization \"" + organization + "\")") +
                       "; the ones selected there when the button was pressed start checked. Everything goes in one operation — one Ctrl+Z.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var scale = new StackPanel { Orientation = Orientation.Horizontal };
            scale.Children.Add(Label("Change the checked views at", 0));
            scale.Children.Add(_fromBox);
            scale.Children.Add(Label("to", 8));
            scale.Children.Add(_scaleBox);

            var templates = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            templates.Children.Add(Label("Views whose view template sets the scale:", 0));
            templates.Children.Add(_templateBox);

            var settings = new StackPanel();
            settings.Children.Add(scale);
            settings.Children.Add(templates);
            settings.Children.Add(_templateNote);

            var group = new GroupBox
            {
                Header = "Scale",
                Padding = new Thickness(10, 8, 10, 8),
                Content = settings
            };
            Grid.SetRow(group, 1);
            root.Children.Add(group);

            var find = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
            find.Children.Add(Label("Find:", 0));
            find.Children.Add(_searchBox);
            find.Children.Add(collapseAll);
            find.Children.Add(expandAll);
            Grid.SetRow(find, 2);
            root.Children.Add(find);

            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            buttons.Children.Add(_applyButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 4);
            root.Children.Add(bottom);

            return root;
        }

        private static TextBlock Label(string text, double left)
        {
            return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 6, 0) };
        }

        private DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                // The order is the Project Browser's; a click on a heading would scatter the sheets out of their folders.
                CanUserSortColumns = false,
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
                CellTemplate = GridBuilder.CheckBoxTemplate(nameof(SheetScaleRow.IsChecked), nameof(SheetScaleRow.IsCheckable))
            });

            // Not GridBuilder.TextColumn: that one sets a fixed margin, and here the margin is the tree's indent.
            var nameStyle = new Style(typeof(TextBlock));
            nameStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            nameStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            nameStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(SheetScaleRow.Text))));
            nameStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Binding(nameof(SheetScaleRow.Indent))));
            nameStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, new Binding(nameof(SheetScaleRow.Weight))));
            nameStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(SheetScaleRow.NameBrush))));

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Sheet / view",
                Width = new DataGridLength(2.4, DataGridLengthUnitType.Star),
                Binding = new Binding(nameof(SheetScaleRow.Text)),
                ElementStyle = nameStyle
            });

            grid.Columns.Add(GridBuilder.TextColumn("Scale", nameof(SheetScaleRow.ScaleText), new DataGridLength(110)));

            var newScale = GridBuilder.TextColumn("New scale", nameof(SheetScaleRow.NewScaleText), new DataGridLength(110));
            newScale.ElementStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            grid.Columns.Add(newScale);

            var status = GridBuilder.TextColumn("State", nameof(SheetScaleRow.StatusText), new DataGridLength(2.2, DataGridLengthUnitType.Star));
            status.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(SheetScaleRow.StatusBrush))));
            grid.Columns.Add(status);

            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 26.0));

            var folder = new DataTrigger { Binding = new Binding(nameof(SheetScaleRow.IsFolder)), Value = true };
            folder.Setters.Add(new Setter(Control.BackgroundProperty, FolderBackground));
            folder.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            rowStyle.Triggers.Add(folder);

            var sheet = new DataTrigger { Binding = new Binding(nameof(SheetScaleRow.IsSheet)), Value = true };
            sheet.Setters.Add(new Setter(Control.BackgroundProperty, SheetBackground));
            sheet.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            rowStyle.Triggers.Add(sheet);

            grid.RowStyle = rowStyle;

            grid.PreviewMouseLeftButtonDown += OnGridMouseDown;
            grid.MouseDoubleClick += OnGridDoubleClick;
            grid.PreviewKeyDown += OnGridKeyDown;

            return grid;
        }

        private static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        // ───────────────────────────── the settings ─────────────────────────────

        /// <summary>The scale to change from, 100 for 1:100; 0 for any.</summary>
        private int FromScale => _fromBox.SelectedItem is string text && text != AnyScale ? ViewPlacementRow.ParseScale(text) : 0;

        private string ScaleInput => (_scaleBox?.Text ?? string.Empty).Trim();

        /// <summary>The new scale, 100 for 1:100; 0 when the box holds no scale.</summary>
        private int TargetScale => ViewPlacementRow.ParseScale(ScaleInput);

        private ScaleTemplateMode Mode => _templateBox.SelectedIndex >= 0 ? ScaleTemplateModeText.All[_templateBox.SelectedIndex] : ScaleTemplateMode.Release;

        private void UpdateTemplateNote()
        {
            var count = _all.Where(row => row.IsView && row.View.TemplateSetsScale).Select(row => row.View.TemplateUniqueId).Distinct().Count();

            _templateNote.Text = count == 0
                ? "No view on these sheets has a view template that sets its scale."
                : ScaleTemplateModeText.Description(Mode) + " " + (count == 1 ? "1 view template" : count + " view templates") +
                  " on these sheets set the scale.";
        }

        // ───────────────────────────── what can be checked ─────────────────────────────

        /// <summary>Why a view cannot be checked with the settings chosen; null when it can.</summary>
        private string LockReason(ScaleViewInfo view)
        {
            if (view.Problem != null)
                return view.Problem;

            var from = FromScale;
            if (from > 0 && view.Scale != from)
                return "At " + ViewPlacementRow.ScaleCaption(view.Scale) + " — only the views at " + ViewPlacementRow.ScaleCaption(from) + " are changed";

            if (view.TemplateSetsScale && Mode == ScaleTemplateMode.Leave)
                return "Its view template \"" + view.TemplateName + "\" sets the scale";

            return null;
        }

        /// <summary>
        /// Whether a line can be checked with the settings chosen. A line that stops being checkable
        /// loses its check mark, so a change of settings cannot leave behind a change nobody sees.
        /// </summary>
        private void UpdateCheckable()
        {
            _syncing = true;

            foreach (var row in _all.Where(row => row.IsView))
            {
                row.IsCheckable = LockReason(row.View) == null;
                if (!row.IsCheckable && row.IsChecked == true)
                    row.SetChecked(false);
            }

            foreach (var group in _all.Where(row => row.IsGroup))
                group.IsCheckable = group.Views.Exists(row => row.IsCheckable);

            _syncing = false;
        }

        // ───────────────────────────── what is shown ─────────────────────────────

        private string Query => (_searchBox?.Text ?? string.Empty).Trim();

        private void RebuildVisible()
        {
            if (!_ready)
                return;

            UpdateCheckable();

            var query = Query;
            _shown = new HashSet<SheetScaleRow>(_all.Where(row => row.IsView && Matches(row, query)));

            var target = _all
                .Where(row => row.IsGroup ? row.Views.Exists(_shown.Contains) : _shown.Contains(row))
                .Where(Unfolded)
                .ToList();

            FlatTree.Sync(_visible, target);
            Revalidate();
        }

        private static bool Matches(SheetScaleRow row, string query)
        {
            if (query.Length == 0)
                return true;

            if (Contains(row.Text, query) || Contains(row.Sheet.Title, query))
                return true;

            for (var folder = row.Parent; folder != null; folder = folder.Parent)
            {
                if (folder.IsFolder && Contains(folder.FolderName, query))
                    return true;
            }

            return false;
        }

        private static bool Contains(string text, string query)
        {
            return (text ?? string.Empty).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private static bool Unfolded(SheetScaleRow row)
        {
            for (var folder = row.Parent; folder != null; folder = folder.Parent)
            {
                if (!folder.IsExpanded)
                    return false;
            }

            return true;
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            // A match inside a folded folder or sheet would otherwise be found and still not shown.
            if (Query.Length > 0)
            {
                foreach (var row in _all.Where(row => row.IsGroup))
                    row.IsExpanded = true;
            }

            RebuildVisible();
        }

        private void SetAllExpanded(bool value)
        {
            foreach (var row in _all.Where(row => row.IsGroup))
                row.IsExpanded = value;

            RebuildVisible();
        }

        private void ToggleFolding(SheetScaleRow group)
        {
            group.IsExpanded = !group.IsExpanded;
            RebuildVisible();
        }

        // ───────────────────────────── check boxes ─────────────────────────────

        /// <summary>
        /// A click on a check box. A folder's or a sheet's click goes to every view under it that the
        /// search shows and that can be checked — its own value is only a summary of theirs and is
        /// worked out again afterwards. A view's click goes to every line of that view: a legend on
        /// three sheets has one scale, not three.
        /// </summary>
        private void OnToggled(SheetScaleRow row, bool? before)
        {
            if (_syncing)
                return;

            _syncing = true;

            if (row.IsGroup)
            {
                var value = row.IsChecked == true;
                foreach (var view in row.Views.Where(view => view.IsCheckable && _shown.Contains(view)).ToList())
                    SetView(view.View, value);
            }
            else if (!row.IsCheckable)
            {
                row.SetChecked(false);
            }
            else
            {
                SetView(row.View, row.IsChecked == true);
            }

            _syncing = false;

            Revalidate();
        }

        private void SetView(ScaleViewInfo view, bool value)
        {
            foreach (var line in _byView[view.UniqueId])
                line.SetChecked(value);
        }

        private void SetAllChecked(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(_shown, value);
        }

        private void SetMany(IEnumerable<SheetScaleRow> rows, bool value)
        {
            _syncing = true;

            foreach (var row in rows.Where(row => row.IsView && row.IsCheckable).ToList())
                SetView(row.View, value);

            _syncing = false;

            Revalidate();
        }

        /// <summary>Space or a double click: the highlighted lines flip together — folders and sheets standing for their shown views.</summary>
        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<SheetScaleRow>()
                .SelectMany(row => row.IsGroup ? row.Views.Where(_shown.Contains) : new[] { row })
                .Where(row => row.IsCheckable)
                .Distinct()
                .ToList();

            if (rows.Count == 0)
                return;

            SetMany(rows, !rows.All(row => row.IsChecked == true));
        }

        // ───────────────────────────── the mouse and the keys ─────────────────────────────

        private void OnGridMouseDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (!(ItemsControl.ContainerFromElement(_grid, source) is DataGridRow container) ||
                !(container.Item is SheetScaleRow row) || !row.IsGroup || IsInCheckBox(source))
                return;

            // The second click of a double click is swallowed, so a double click folds once, not twice.
            if (e.ClickCount == 1)
                ToggleFolding(row);

            e.Handled = true;
        }

        /// <summary>The click landed on a check box, which handles it itself.</summary>
        private static bool IsInCheckBox(DependencyObject element)
        {
            for (var current = element; current != null && !(current is DataGridRow); current = VisualTreeHelper.GetParent(current))
            {
                if (current is CheckBox)
                    return true;
            }

            return false;
        }

        private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (ItemsControl.ContainerFromElement(_grid, source) is DataGridRow container &&
                container.Item is SheetScaleRow row && row.IsView && !IsInCheckBox(source) && row.IsCheckable)
                row.IsChecked = row.IsChecked != true;
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            var row = _grid.SelectedItem as SheetScaleRow;

            switch (e.Key)
            {
                case Key.Space:
                    ToggleSelectedRows();
                    e.Handled = true;
                    break;
                case Key.Enter when row != null && row.IsGroup:
                case Key.Left when row != null && row.IsGroup && row.IsExpanded:
                case Key.Right when row != null && row.IsGroup && !row.IsExpanded:
                    ToggleFolding(row);
                    e.Handled = true;
                    break;
            }
        }

        // ───────────────────────────── validation ─────────────────────────────

        /// <summary>The checked view lines, in the order of the table.</summary>
        private List<SheetScaleRow> Marked()
        {
            return _all.Where(row => row.IsView && row.IsChecked == true && row.IsCheckable).ToList();
        }

        /// <summary>What "Change scale" would do with the settings and the check boxes as they are.</summary>
        private sealed class Plan
        {
            public int Scale;
            public ScaleTemplateMode Mode;

            /// <summary>The checked view lines — a legend on two sheets is two lines.</summary>
            public List<SheetScaleRow> Marked;

            /// <summary>The checked views not at the new scale yet, each once.</summary>
            public List<ScaleViewInfo> Changing = new List<ScaleViewInfo>();

            public HashSet<ScaleViewInfo> ChangingSet = new HashSet<ScaleViewInfo>();

            /// <summary>Views nobody checked that change with a checked one: they share its scale, or its template gets the new one.</summary>
            public HashSet<ScaleViewInfo> Along = new HashSet<ScaleViewInfo>();

            /// <summary>The checked views already at the new scale.</summary>
            public int Already;

            public HashSet<string> Groups = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>The templates the run edits: stops their setting the scale, or gives them the new one.</summary>
            public HashSet<string> Templates = new HashSet<string>(StringComparer.Ordinal);

            public bool Changes(ScaleViewInfo view) => ChangingSet.Contains(view) || Along.Contains(view);
        }

        private Plan MakePlan()
        {
            var plan = new Plan { Scale = TargetScale, Mode = Mode, Marked = Marked() };
            if (plan.Scale <= 0)
                return plan;

            var views = plan.Marked.Select(row => row.View).Distinct().ToList();
            plan.Changing = views.Where(view => view.Scale != plan.Scale).ToList();
            plan.ChangingSet = new HashSet<ScaleViewInfo>(plan.Changing);
            plan.Already = views.Count - plan.Changing.Count;

            foreach (var view in plan.Changing)
            {
                if (view.ScaleGroup != null)
                    plan.Groups.Add(view.ScaleGroup);

                if (view.TemplateSetsScale && plan.Mode != ScaleTemplateMode.Leave)
                    plan.Templates.Add(view.TemplateUniqueId);
            }

            foreach (var view in _byView.Values.Select(lines => lines[0].View))
            {
                if (!plan.ChangingSet.Contains(view) && view.Scale > 0 && view.Scale != plan.Scale && AlongReason(view, plan) != null)
                    plan.Along.Add(view);
            }

            return plan;
        }

        /// <summary>Why a view nobody checked changes anyway; null when it does not.</summary>
        private static string AlongReason(ScaleViewInfo view, Plan plan)
        {
            if (view.ScaleGroup != null && plan.Groups.Contains(view.ScaleGroup))
                return "a primary view and its dependents share one scale";

            if (plan.Mode == ScaleTemplateMode.ChangeTemplate && view.TemplateSetsScale && plan.Templates.Contains(view.TemplateUniqueId))
                return "its view template \"" + view.TemplateName + "\" gets " + ViewPlacementRow.ScaleCaption(plan.Scale);

            return null;
        }

        /// <summary>
        /// Brings the whole window in line with the settings and the check boxes: every line's "New
        /// scale" and "State", the folders' and sheets' check boxes, the summary — in one method, so the
        /// table can never promise one thing while "Change scale" does another.
        /// </summary>
        private void Revalidate()
        {
            if (!_ready)
                return;

            var plan = MakePlan();
            var tooLarge = 0;

            foreach (var row in _all.Where(row => row.IsView))
            {
                if (Describe(row, plan))
                    tooLarge++;
            }

            UpdateGroups(plan);
            UpdateSummary(plan, tooLarge);
        }

        /// <summary>Fills in one view line's "New scale" and "State"; true when it may stick out of the drawing area.</summary>
        private bool Describe(SheetScaleRow row, Plan plan)
        {
            var view = row.View;
            var changes = plan.Scale > 0 && plan.Changes(view);
            var isChecked = row.IsChecked == true && row.IsCheckable;

            row.NewScaleText = changes ? ViewPlacementRow.ScaleCaption(plan.Scale) : string.Empty;

            var parts = new List<string>();
            var warning = false;

            if (changes && !isChecked)
            {
                parts.Add("changes too — " + AlongReason(view, plan));
                warning = true;
            }
            else if (!row.IsCheckable)
            {
                row.StatusText = LockReason(view) ?? string.Empty;
                row.IsWarning = false;
                return false;
            }
            else if (isChecked && !changes && plan.Scale > 0)
            {
                parts.Add("already " + ViewPlacementRow.ScaleCaption(plan.Scale));
            }
            else if (changes)
            {
                if (view.TemplateSetsScale)
                {
                    var others = view.TemplateUsers - 1;
                    parts.Add(plan.Mode == ScaleTemplateMode.Release
                        ? "its view template \"" + view.TemplateName + "\" stops setting the scale" + (others > 0 ? "; its other " + Views(others) + " keep theirs" : string.Empty)
                        : "through its view template \"" + view.TemplateName + "\"" + (others > 0 ? " — all " + Views(view.TemplateUsers) + " using it change" : string.Empty));
                }

                if (view.ScaleGroup != null)
                    parts.Add(view.IsDependent ? "its primary view and the other dependents change too" : "its " + Views(view.GroupSize - 1) + " dependent on it change too");

                if (view.IsLegend && view.SheetCount > 1)
                    parts.Add("a legend — it changes on all " + view.SheetCount + " sheets it stands on");
            }

            var tooLarge = false;
            if (changes)
            {
                var overhang = row.Viewport.OverhangAt(row.Sheet, plan.Scale);
                if (overhang > FitTolerance)
                {
                    parts.Insert(0, "May stick out of the drawing area, by ≈ " + overhang.ToString("0", CultureInfo.CurrentCulture) + " mm");
                    warning = true;
                    tooLarge = true;
                }
            }

            row.StatusText = Capitalised(string.Join("; ", parts));
            row.IsWarning = warning;
            return tooLarge;
        }

        /// <summary>A folder's or a sheet's check box sums up the views under it that the search shows; its "State" counts them.</summary>
        private void UpdateGroups(Plan plan)
        {
            _syncing = true;

            foreach (var group in _all.Where(row => row.IsGroup))
            {
                var checkable = group.Views.Where(row => row.IsCheckable && _shown.Contains(row)).ToList();
                var checkedCount = checkable.Count(row => row.IsChecked == true);

                group.SetChecked(checkable.Count == 0 || checkedCount == 0 ? false : checkedCount == checkable.Count ? true : (bool?)null);

                var changing = group.Views.Count(row => row.NewScaleText.Length > 0);
                var checkedText = checkedCount > 0 ? ", " + checkedCount + " checked" : string.Empty;

                if (group.IsSheet)
                {
                    group.NewScaleText = changing == 0 ? string.Empty
                        : ViewPlacementRow.ScaleCaption(plan.Scale) + (changing < group.Views.Count ? " (" + changing + " of " + group.Views.Count + ")" : string.Empty);

                    // A folded sheet hides its views' warnings; the sheet's line carries them.
                    var warned = group.Views.Exists(row => row.IsWarning);
                    group.StatusText = Views(group.Views.Count) + checkedText + (warned ? " — see the views on it" : string.Empty);
                    group.IsWarning = warned;
                }
                else
                {
                    var sheets = group.Views.Where(_shown.Contains).Select(row => row.Sheet).Distinct().Count();
                    group.StatusText = (sheets == 1 ? "1 sheet" : sheets + " sheets") + checkedText;
                }
            }

            _syncing = false;
        }

        private void UpdateSummary(Plan plan, int tooLarge)
        {
            var checkableShown = _shown.Where(row => row.IsCheckable).ToList();
            var checkedShown = checkableShown.Count(row => row.IsChecked == true);

            _syncingSelectAll = true;
            _selectAll.IsChecked = checkableShown.Count == 0 || checkedShown == 0
                ? false
                : checkedShown == checkableShown.Count ? true : (bool?)null;
            _syncingSelectAll = false;

            var changingCount = plan.Changing.Count + plan.Along.Count;
            var input = ScaleInput;
            var badScale = input.Length > 0 && plan.Scale == 0;

            _applyButton.IsEnabled = !badScale && plan.Scale > 0 && plan.Changing.Count > 0;
            _applyButton.Content = _applyButton.IsEnabled ? "Change scale (" + changingCount + ")" : "Change scale";

            if (badScale)
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "\"" + input + "\" is not a scale — pick one from the list, or type 1:200 or simply 200.";
                return;
            }

            _status.Foreground = SystemColors.GrayTextBrush;

            if (plan.Marked.Count == 0)
            {
                _status.Text = "Nothing is checked. Select the sheets in the Project Browser before pressing the button, or check them here — " +
                               "a sheet's box takes every view on it.";
                return;
            }

            var views = plan.Marked.Select(row => row.View).Distinct().Count();
            var sheets = plan.Marked.Select(row => row.Sheet).Distinct().Count();
            var text = "Checked: " + Views(views) + " on " + (sheets == 1 ? "1 sheet" : sheets + " sheets");

            if (plan.Scale == 0)
            {
                _status.Text = text + ". Pick the new scale above.";
                return;
            }

            text += " → " + ViewPlacementRow.ScaleCaption(plan.Scale) + ".";

            if (plan.Already > 0)
                text += " Already at " + ViewPlacementRow.ScaleCaption(plan.Scale) + ": " + plan.Already + ".";

            if (plan.Along.Count > 0)
                text += " Changing along with them: " + Views(plan.Along.Count) + ".";

            if (plan.Templates.Count > 0)
                text += " " + (plan.Templates.Count == 1 ? "1 view template" : plan.Templates.Count + " view templates") +
                        (plan.Mode == ScaleTemplateMode.Release ? " stop setting the scale." : " get " + ViewPlacementRow.ScaleCaption(plan.Scale) + ".");

            var hidden = plan.Marked.Count(row => !_shown.Contains(row));
            if (hidden > 0)
                text += " " + hidden + " of them out of sight — the search hides them.";

            if (tooLarge > 0)
                text += " " + (tooLarge == 1 ? "1 may" : tooLarge + " may") + " stick out of the drawing area.";

            _status.Foreground = tooLarge > 0 ? Brushes.Firebrick : SystemColors.ControlTextBrush;
            _status.Text = text;
        }

        private static string Views(int count)
        {
            return count == 1 ? "1 view" : count + " views";
        }

        private static string Capitalised(string text)
        {
            return text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text.Substring(1);
        }

        // ───────────────────────────── closing ─────────────────────────────

        /// <summary>
        /// Asks first when the run reaches beyond the checked views — a view template edited, views on
        /// other sheets changing along, checked views out of sight, views that may stick out — and hands
        /// the plan back.
        /// </summary>
        private void OnApply(object sender, RoutedEventArgs e)
        {
            Revalidate();
            if (!_applyButton.IsEnabled)
                return;

            var plan = MakePlan();
            var scale = ViewPlacementRow.ScaleCaption(plan.Scale);
            var notes = new List<string>();

            if (plan.Templates.Count > 0)
            {
                var templates = plan.Changing
                    .Where(view => view.TemplateSetsScale)
                    .GroupBy(view => view.TemplateUniqueId)
                    .Select(group => "• " + group.First().TemplateName + " — " + Views(group.First().TemplateUsers) + " use it")
                    .ToList();

                var list = string.Join("\n", templates.Take(10)) + (templates.Count > 10 ? "\n… and " + (templates.Count - 10) + " more" : string.Empty);

                notes.Add(plan.Mode == ScaleTemplateMode.Release
                    ? "These view templates stop setting the scale. Every view using them keeps the scale it has, the checked ones get " + scale +
                      "; everything else the templates set stays as it is:\n" + list
                    : "These view templates get " + scale + ", and every view using them follows — on sheets that are not checked, " +
                      "and on no sheet, too:\n" + list);
            }

            var shared = plan.Along.Count(view => view.ScaleGroup != null && plan.Groups.Contains(view.ScaleGroup));
            if (shared > 0)
                notes.Add((shared == 1 ? "1 more view shares" : shared + " more views share") +
                          " one scale with a checked view — a primary view and its dependents — and " + (shared == 1 ? "changes" : "change") + " too.");

            var hidden = plan.Marked.Count(row => !_shown.Contains(row));
            if (hidden > 0)
                notes.Add(hidden + " of the checked views are out of sight — the search hides them. They change too.");

            var tooLarge = _all.Count(row => row.IsView && row.NewScaleText.Length > 0 && row.Viewport.OverhangAt(row.Sheet, plan.Scale) > FitTolerance);
            if (tooLarge > 0)
                notes.Add((tooLarge == 1 ? "1 view" : tooLarge + " views") + " may stick out of the drawing area at " + scale + " — the report lists the sheets to check.");

            if (notes.Count > 0)
            {
                var count = plan.Changing.Count + plan.Along.Count;
                var answer = MessageBox.Show(this,
                    "Change the scale of " + Views(count) + " to " + scale + "?\n\n" + string.Join("\n\n", notes),
                    WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes)
                    return;
            }

            Request = new SheetScaleRequest(plan.Scale, plan.Mode, plan.Changing);
            DialogResult = true;
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            _preferences.Scale = ScaleInput;
            _preferences.Templates = Mode;
            _preferences.Save();
        }
    }
}
