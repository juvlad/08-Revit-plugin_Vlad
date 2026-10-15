using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// The "Copy Parameters" window: a category, the parameter copied from on the left, the one copied
    /// into on the right, and a preview of every element below them.
    ///
    /// The preview is the point of the window. Two parameters of different kinds — a length into a text,
    /// a text into a number — are copied by a rule, and a rule is only trusted once its result is seen:
    /// every line shows the source, the target now and the target after, and a value that cannot be
    /// converted says so in red before anything is changed. The preview comes from the command through a
    /// call-back, the way the "Excel" import gets its own: the window knows nothing about the Revit API,
    /// it is handed categories, parameters and previews as plain snapshots, and it hands back the preview
    /// the user confirmed.
    ///
    /// The scope (selection, active view, whole model) is the selection of elements; there are no check
    /// boxes on the lines. "Find" above each list and "Show" above the preview only find — the copy covers
    /// every line of the preview, shown or not, and the summary says so.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class CopyParametersWindow : Window
    {
        private const string WindowTitle = "Copy Parameter Values";

        private readonly Func<CopyScope, IReadOnlyList<CopyCategoryInfo>> _categories;
        private readonly Func<CopyScope, string, IReadOnlyList<CopyParameterInfo>> _parameters;
        private readonly Func<CopyParameterRequest, CopyParameterPreview> _preview;
        private readonly CopyParameterPreferences _preferences;

        private IReadOnlyList<CopyParameterInfo> _all = new List<CopyParameterInfo>();
        private CopyParameterPreview _current;

        // The last parameter picked on each side. Kept apart from the lists' own selection so that a
        // category with the same parameter, or a "Find" cleared again, brings the choice back.
        private string _sourceKey;
        private string _targetKey;
        private string _categoryName;

        private readonly RadioButton _selectionOption;
        private readonly RadioButton _viewOption;
        private readonly RadioButton _modelOption;
        private readonly ComboBox _categoryBox;
        private readonly TextBlock _categoryNote;
        private readonly TextBox _sourceFind;
        private readonly TextBox _targetFind;
        private readonly DataGrid _sourceGrid;
        private readonly DataGrid _targetGrid;
        private readonly CheckBox _showReadOnly;
        private readonly CheckBox _onlyEmpty;
        private readonly CheckBox _emptyClears;
        private readonly CheckBox _noSymbol;
        private readonly CheckBox _numberFromText;
        private readonly TextBlock _conversion;
        private readonly ComboBox _showBox;
        private readonly TextBlock _summary;
        private readonly DataGrid _previewGrid;
        private readonly TextBlock _status;
        private readonly Button _copyButton;

        private bool _loading;

        /// <summary>The preview the user confirmed with "Copy"; the command writes exactly what it lists.</summary>
        public CopyParameterPreview Confirmed { get; private set; }

        /// <param name="selectionCount">Elements selected in Revit when the button was pressed; 0 disables that scope.</param>
        /// <param name="viewName">The active view, or <c>null</c> when its elements cannot be listed.</param>
        /// <param name="categories">The categories with elements in a scope.</param>
        /// <param name="parameters">The parameters of a category in a scope.</param>
        /// <param name="preview">What the copy would do — called whenever the choice changes.</param>
        public CopyParametersWindow(
            int selectionCount,
            string viewName,
            Func<CopyScope, IReadOnlyList<CopyCategoryInfo>> categories,
            Func<CopyScope, string, IReadOnlyList<CopyParameterInfo>> parameters,
            Func<CopyParameterRequest, CopyParameterPreview> preview,
            CopyParameterPreferences preferences,
            string projectName)
        {
            _categories = categories;
            _parameters = parameters;
            _preview = preview;
            _preferences = preferences ?? new CopyParameterPreferences();

            _sourceKey = _preferences.Source;
            _targetKey = _preferences.Target;
            _categoryName = _preferences.Category;

            Title = WindowTitle;
            Width = 1180;
            Height = 860;
            MinWidth = 900;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _selectionOption = new RadioButton
            {
                Content = selectionCount > 0 ? "Selected in the model (" + selectionCount + ")" : "Selected in the model (nothing is selected)",
                GroupName = "Scope",
                IsEnabled = selectionCount > 0,
                IsChecked = selectionCount > 0,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Only the elements selected in Revit before the button was pressed."
            };

            _viewOption = new RadioButton
            {
                Content = viewName == null ? "In the active view (not available here)" : "In the active view (\"" + viewName + "\")",
                GroupName = "Scope",
                IsEnabled = viewName != null,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "The elements the active view shows. On a schedule — the schedule's own elements."
            };

            _modelOption = new RadioButton
            {
                Content = "The whole model",
                GroupName = "Scope",
                IsChecked = selectionCount == 0,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Every element of the open project. Closed worksets and links are not looked into."
            };

            _selectionOption.Checked += (s, e) => LoadScope();
            _viewOption.Checked += (s, e) => LoadScope();
            _modelOption.Checked += (s, e) => LoadScope();

            _categoryBox = new ComboBox
            {
                Width = 380,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "The categories with elements in the chosen scope, with how many. Only categories that take project parameters are listed."
            };
            _categoryBox.SelectionChanged += (s, e) =>
            {
                if (_loading)
                    return;

                _categoryName = (_categoryBox.SelectedItem as CopyCategoryInfo)?.Name ?? _categoryName;
                LoadCategory();
            };

            _categoryNote = new TextBlock
            {
                Foreground = SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 0, 0, 0)
            };

            _sourceFind = FindBox();
            _targetFind = FindBox();
            _sourceGrid = ParameterGrid(false);
            _targetGrid = ParameterGrid(true);

            _sourceGrid.SelectionChanged += (s, e) => OnParameterPicked(_sourceGrid, true);
            _targetGrid.SelectionChanged += (s, e) => OnParameterPicked(_targetGrid, false);

            _showReadOnly = new CheckBox
            {
                Content = "Show read-only parameters too",
                IsChecked = _preferences.ShowReadOnly,
                Margin = new Thickness(0, 6, 0, 0),
                ToolTip =
                    "Parameters Revit computes (area, volume, length), and ones a formula or a reporting parameter sets in the family.\n" +
                    "They can be copied from, never into — shown greyed, for reference."
            };
            _showReadOnly.Checked += (s, e) => RebuildLists();
            _showReadOnly.Unchecked += (s, e) => RebuildLists();

            _onlyEmpty = Option(
                "Only where the target is empty",
                "A target that already holds a value is left as it is: the copy only fills gaps.",
                _preferences.OnlyEmptyTargets);

            _emptyClears = Option(
                "An empty source empties a text target",
                "Off: an element whose source is empty keeps its target value.\n" +
                "On: a text target is emptied to match. A number cannot be emptied — such a target is always left as it is.",
                _preferences.EmptyClears);

            _noSymbol = Option(
                "Numbers into text without the unit symbol (\"1200\", not \"1200 mm\")",
                "A number goes into a text as Revit shows it — the project's units and rounding. This drops only the symbol.",
                _preferences.NoUnitSymbol);

            _numberFromText = Option(
                "A text that is not a number gives the first number in it (\"DN 50\" → 50)",
                "Off: a text Revit cannot read as a number is not written, and the line says so.\n" +
                "On: the first number in it is taken — check the preview, \"Floor 2 - 1200\" gives 2.",
                _preferences.NumberFromText);

            _conversion = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

            _showBox = new ComboBox
            {
                Width = 220,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = ShowOption.All,
                SelectedIndex = 0,
                ToolTip = "Only narrows what the table shows: \"Copy\" writes every line that will be written, shown or not."
            };
            _showBox.SelectionChanged += (s, e) => ShowRows();

            _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

            _previewGrid = PreviewGrid();

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _copyButton = new Button
            {
                Content = "Copy",
                MinWidth = 120,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _copyButton.Click += OnCopy;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(projectName, closeButton);

            Closing += OnClosing;

            LoadScope();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(string projectName, Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // project
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // scope
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // category
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // the two lists
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // options
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // preview header
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // preview
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // status + buttons

            var hint = new TextBlock
            {
                Text =
                    "Pick a category, then on the left the parameter whose value is copied, and on the right the parameter it is " +
                    "copied into — for every element of the category in the chosen scope. The two do not have to hold the same kind " +
                    "of value: a number goes into a text as Revit shows it, a text into a number the way Revit reads a typed value, " +
                    "anything into Yes/No as yes or no. The preview shows every element before anything is changed; \"Copy\" writes " +
                    "them all in one operation — one Ctrl+Z.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Place(root, hint, 0);

            var project = new TextBlock
            {
                Text = "Project: " + (string.IsNullOrEmpty(projectName) ? "(unsaved)" : projectName),
                Foreground = SystemColors.GrayTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Place(root, project, 1);

            var scope = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            scope.Children.Add(new TextBlock { Text = "Elements:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            scope.Children.Add(_selectionOption);
            scope.Children.Add(_viewOption);
            scope.Children.Add(_modelOption);
            Place(root, scope, 2);

            var category = new DockPanel { LastChildFill = true };
            var categoryLabel = new TextBlock { Text = "Category:", VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(categoryLabel, Dock.Left);
            category.Children.Add(categoryLabel);
            DockPanel.SetDock(_categoryBox, Dock.Left);
            category.Children.Add(_categoryBox);
            category.Children.Add(_categoryNote);
            Place(root, category, 3);

            var lists = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            lists.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var from = Side("Copy from", _sourceFind, _sourceGrid, null);
            Grid.SetColumn(from, 0);
            lists.Children.Add(from);

            var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            middle.Children.Add(new TextBlock { Text = "→", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center });
            var swap = new Button
            {
                Content = "⇄",
                Width = 32,
                Margin = new Thickness(0, 8, 0, 0),
                ToolTip = "Swap the two parameters"
            };
            swap.Click += (s, e) => Swap();
            middle.Children.Add(swap);
            Grid.SetColumn(middle, 1);
            lists.Children.Add(middle);

            var into = Side("Copy into", _targetFind, _targetGrid, _showReadOnly);
            Grid.SetColumn(into, 2);
            lists.Children.Add(into);

            Place(root, lists, 4);

            var options = new Grid();
            options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            options.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            options.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            options.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddOption(options, _onlyEmpty, 0, 0);
            AddOption(options, _emptyClears, 0, 1);
            AddOption(options, _noSymbol, 1, 0);
            AddOption(options, _numberFromText, 1, 1);
            Grid.SetRow(_conversion, 2);
            Grid.SetColumnSpan(_conversion, 2);
            options.Children.Add(_conversion);

            var optionsBox = new GroupBox
            {
                Header = "How",
                Padding = new Thickness(10, 6, 10, 8),
                Margin = new Thickness(0, 10, 0, 0),
                Content = options
            };
            Place(root, optionsBox, 5);

            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 0) };
            var showLabel = new TextBlock { Text = "Preview — show:", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(showLabel, Dock.Left);
            header.Children.Add(showLabel);
            DockPanel.SetDock(_showBox, Dock.Left);
            header.Children.Add(_showBox);
            header.Children.Add(_summary);
            Place(root, header, 6);

            Place(root, _previewGrid, 7);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_copyButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Place(root, bottom, 8);

            return root;
        }

        private static void Place(Grid grid, UIElement element, int row)
        {
            Grid.SetRow(element, row);
            grid.Children.Add(element);
        }

        private static void AddOption(Grid grid, CheckBox box, int column, int row)
        {
            Grid.SetColumn(box, column);
            Grid.SetRow(box, row);
            grid.Children.Add(box);
        }

        private static UIElement Side(string header, TextBox find, DataGrid grid, UIElement below)
        {
            var panel = new DockPanel { LastChildFill = true };

            var top = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
            var label = new TextBlock { Text = "Find:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            DockPanel.SetDock(label, Dock.Left);
            top.Children.Add(label);
            top.Children.Add(find);
            DockPanel.SetDock(top, Dock.Top);
            panel.Children.Add(top);

            if (below != null)
            {
                DockPanel.SetDock(below, Dock.Bottom);
                panel.Children.Add(below);
            }

            panel.Children.Add(grid);

            return new GroupBox
            {
                Header = header,
                Padding = new Thickness(8, 6, 8, 8),
                Content = panel
            };
        }

        private TextBox FindBox()
        {
            var box = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Finds a parameter by a part of its name." };
            box.TextChanged += (s, e) => RebuildLists();
            return box;
        }

        private CheckBox Option(string caption, string tooltip, bool value)
        {
            var box = new CheckBox { Content = caption, ToolTip = tooltip, IsChecked = value, Margin = new Thickness(0, 2, 12, 2) };
            box.Checked += (s, e) => UpdatePreview();
            box.Unchecked += (s, e) => UpdatePreview();
            return box;
        }

        private static DataGrid ParameterGrid(bool target)
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                CanUserSortColumns = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = SystemColors.ControlLightBrush,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                SelectionMode = DataGridSelectionMode.Single,
                IsReadOnly = true,
                RowHeaderWidth = 0
            };

            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(CopyParameterInfo.Tooltip))));
            if (target)
                rowStyle.Setters.Add(new Setter(ForegroundProperty, new Binding(nameof(CopyParameterInfo.Foreground))));
            grid.RowStyle = rowStyle;

            grid.Columns.Add(Text("Parameter", nameof(CopyParameterInfo.Name), new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(Text("Of", nameof(CopyParameterInfo.Level), new DataGridLength(66)));
            grid.Columns.Add(Text("Value", nameof(CopyParameterInfo.ValueKind), new DataGridLength(96)));
            grid.Columns.Add(Text("Kind", nameof(CopyParameterInfo.Kind), new DataGridLength(84)));
            grid.Columns.Add(Text("Elements", nameof(CopyParameterInfo.Elements), new DataGridLength(64)));

            return grid;
        }

        private static DataGrid PreviewGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                CanUserSortColumns = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = SystemColors.ControlLightBrush,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                SelectionMode = DataGridSelectionMode.Extended,
                IsReadOnly = true,
                RowHeaderWidth = 0,
                Margin = new Thickness(0, 6, 0, 0)
            };

            grid.Columns.Add(Text("ID", nameof(CopyPreviewRow.Id), new DataGridLength(80)));
            grid.Columns.Add(Text("Element", nameof(CopyPreviewRow.Element), new DataGridLength(1.3, DataGridLengthUnitType.Star)));
            grid.Columns.Add(Text("Elements", nameof(CopyPreviewRow.Elements), new DataGridLength(64)));
            grid.Columns.Add(Text("Source", nameof(CopyPreviewRow.Source), new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(Text("Target now", nameof(CopyPreviewRow.Before), new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(Text("Target after", nameof(CopyPreviewRow.After), new DataGridLength(1, DataGridLengthUnitType.Star)));

            var state = Text("State", nameof(CopyPreviewRow.StateText), new DataGridLength(1.6, DataGridLengthUnitType.Star));
            state.MinWidth = 200;
            state.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(CopyPreviewRow.StateBrush))));
            grid.Columns.Add(state);

            return grid;
        }

        private static DataGridTextColumn Text(string header, string property, DataGridLength width)
        {
            var column = GridBuilder.TextColumn(header, property, width);
            column.IsReadOnly = true;
            return column;
        }

        // ───────────────────────────── scope and category ─────────────────────────────

        private CopyScope Scope
        {
            get
            {
                if (_selectionOption.IsChecked == true)
                    return CopyScope.Selection;

                return _viewOption.IsChecked == true ? CopyScope.ActiveView : CopyScope.Model;
            }
        }

        private string ScopeWords
        {
            get
            {
                switch (Scope)
                {
                    case CopyScope.Selection:
                        return "among the selected elements";
                    case CopyScope.ActiveView:
                        return "in the active view";
                    default:
                        return "in the whole model";
                }
            }
        }

        /// <summary>
        /// Fills the category list for the scope. The category stays the one picked before when this
        /// scope has it, then the one remembered from last time, then the largest.
        /// </summary>
        private void LoadScope()
        {
            if (_categoryBox == null)
                return;

            IReadOnlyList<CopyCategoryInfo> categories;
            string error = null;

            var previous = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                categories = _categories(Scope) ?? new List<CopyCategoryInfo>();
            }
            catch (Exception exception)
            {
                categories = new List<CopyCategoryInfo>();
                error = exception.Message;
            }
            finally
            {
                Mouse.OverrideCursor = previous;
            }

            _loading = true;
            _categoryBox.ItemsSource = categories;
            _categoryBox.SelectedItem =
                categories.FirstOrDefault(category => string.Equals(category.Name, _categoryName, StringComparison.CurrentCultureIgnoreCase)) ??
                categories.OrderByDescending(category => category.Elements).FirstOrDefault();
            _loading = false;

            _categoryNote.Foreground = error == null ? SystemColors.GrayTextBrush : Brushes.Firebrick;
            _categoryNote.Text = error != null
                ? "The elements could not be read: " + error
                : categories.Count == 0
                    ? "No element " + ScopeWords + " belongs to a category that takes parameters."
                    : categories.Count + (categories.Count == 1 ? " category " : " categories ") + ScopeWords + ".";

            LoadCategory();
        }

        private void LoadCategory()
        {
            var category = _categoryBox.SelectedItem as CopyCategoryInfo;
            string error = null;

            var previous = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                _all = category == null ? new List<CopyParameterInfo>() : _parameters(Scope, category.Key) ?? new List<CopyParameterInfo>();
            }
            catch (Exception exception)
            {
                _all = new List<CopyParameterInfo>();
                error = exception.Message;
            }
            finally
            {
                Mouse.OverrideCursor = previous;
            }

            if (error != null)
            {
                _categoryNote.Foreground = Brushes.Firebrick;
                _categoryNote.Text = "The parameters could not be read: " + error;
            }

            RebuildLists();
        }

        // ───────────────────────────── the two lists ─────────────────────────────

        /// <summary>
        /// Refills both lists from the category's parameters, "Find" and "Show read-only", and puts back
        /// the last choice on each side where it is still in the list. A choice "Find" hides is not
        /// dropped — it comes back with the text — but while hidden it is not the one copied.
        /// </summary>
        private void RebuildLists()
        {
            if (_previewGrid == null)
                return;

            _loading = true;

            var sources = _all.Where(info => Matches(info, _sourceFind.Text)).ToList();
            var showReadOnly = _showReadOnly.IsChecked == true;
            var targets = _all.Where(info => (showReadOnly || !info.IsReadOnly) && Matches(info, _targetFind.Text)).ToList();

            _sourceGrid.ItemsSource = sources;
            _targetGrid.ItemsSource = targets;

            _sourceGrid.SelectedItem = sources.FirstOrDefault(info => info.Key == _sourceKey);
            _targetGrid.SelectedItem = targets.FirstOrDefault(info => info.Key == _targetKey);

            _loading = false;

            if (_sourceGrid.SelectedItem != null)
                _sourceGrid.ScrollIntoView(_sourceGrid.SelectedItem);
            if (_targetGrid.SelectedItem != null)
                _targetGrid.ScrollIntoView(_targetGrid.SelectedItem);

            UpdatePreview();
        }

        private static bool Matches(CopyParameterInfo info, string text)
        {
            var find = (text ?? string.Empty).Trim();
            return find.Length == 0 || info.Name.IndexOf(find, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void OnParameterPicked(DataGrid grid, bool source)
        {
            if (_loading)
                return;

            var info = grid.SelectedItem as CopyParameterInfo;
            if (info != null)
            {
                if (source)
                    _sourceKey = info.Key;
                else
                    _targetKey = info.Key;
            }

            UpdatePreview();
        }

        private void Swap()
        {
            var source = _sourceKey;
            _sourceKey = _targetKey;
            _targetKey = source;

            RebuildLists();
        }

        // ───────────────────────────── the preview ─────────────────────────────

        private void UpdatePreview()
        {
            if (_previewGrid == null || _loading)
                return;

            var category = _categoryBox.SelectedItem as CopyCategoryInfo;
            var source = _sourceGrid.SelectedItem as CopyParameterInfo;
            var target = _targetGrid.SelectedItem as CopyParameterInfo;

            _current = null;

            if (category == null || source == null || target == null)
            {
                Clear(category == null
                    ? "Pick a category."
                    : "Pick the parameter to copy from on the left, and the one to copy into on the right.");
                return;
            }

            if (source.Key == target.Key)
            {
                Clear("The same parameter is picked on both sides — pick another one on the right.");
                return;
            }

            var request = new CopyParameterRequest
            {
                Scope = Scope,
                Category = category.Key,
                Source = source.Key,
                Target = target.Key,
                SourceName = source.Name,
                TargetName = target.Name,
                OnlyEmptyTargets = _onlyEmpty.IsChecked == true,
                EmptyClears = _emptyClears.IsChecked == true,
                NoUnitSymbol = _noSymbol.IsChecked == true,
                NumberFromText = _numberFromText.IsChecked == true
            };

            var previous = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                _current = _preview(request);
            }
            catch (Exception exception)
            {
                Clear("The preview could not be made: " + exception.Message, true);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = previous;
            }

            _conversion.Text = _current.Conversion;
            ShowRows();
        }

        private void Clear(string message, bool error = false)
        {
            _current = null;
            _conversion.Text = string.Empty;
            _previewGrid.ItemsSource = null;
            _summary.Text = string.Empty;
            _status.Foreground = error ? Brushes.Firebrick : SystemColors.GrayTextBrush;
            _status.Text = message;
            _copyButton.IsEnabled = false;
            _copyButton.Content = "Copy";
        }

        /// <summary>Fills the preview table from the "Show" choice, and the summary and the button from the whole preview.</summary>
        private void ShowRows()
        {
            if (_current == null || _previewGrid == null)
                return;

            var option = _showBox.SelectedItem as ShowOption ?? ShowOption.All[0];
            var shown = _current.Rows.Where(option.Matches).ToList();
            _previewGrid.ItemsSource = shown;

            var unit = _current.TargetIsType ? "type" : "element";
            var parts = new List<string>();
            if (_current.Write > 0)
                parts.Add(_current.Write + " will be written");
            if (_current.Same > 0)
                parts.Add(_current.Same + " already hold the value");
            if (_current.Skipped > 0)
                parts.Add(_current.Skipped + " left as they are");
            if (_current.Blocked > 0)
                parts.Add(_current.Blocked + " cannot be written");

            var total = _current.Rows.Count;
            var head = total + " " + (total == 1 ? unit : unit + "s") +
                       (_current.TargetIsType ? " (" + _current.Elements + " elements " + ScopeWords + ")" : " " + ScopeWords);

            _summary.Foreground = _current.Blocked > 0 ? Brushes.Firebrick : SystemColors.ControlTextBrush;
            _summary.Text = total == 0 ? "Nothing " + ScopeWords + "." : head + ": " + string.Join(", ", parts) + "." +
                            (shown.Count < total ? " Shown: " + shown.Count + "." : string.Empty);

            var notes = _current.Notes.ToList();
            var hidden = _current.Rows.Count(row => row.State == CopyRowState.Write) - shown.Count(row => row.State == CopyRowState.Write);
            if (hidden > 0)
                notes.Add(hidden + " of the lines to be written are out of sight under \"Show\" — \"Copy\" writes them too.");

            _status.Foreground = SystemColors.GrayTextBrush;
            _status.Text = string.Join(" ", notes);

            _copyButton.IsEnabled = _current.Write > 0;
            _copyButton.Content = _current.Write > 0 ? "Copy (" + _current.Write + ")" : "Copy";
        }

        // ───────────────────────────── actions ─────────────────────────────

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            if (_current == null || _current.Write == 0)
                return;

            if (_current.TargetIsType)
            {
                var target = (_targetGrid.SelectedItem as CopyParameterInfo)?.Name ?? "the parameter";
                var answer = MessageBox.Show(
                    this,
                    "\"" + target + "\" is a type parameter: " + _current.Write + (_current.Write == 1 ? " type changes" : " types change") +
                    ", and with it every element of that type — wherever the element stands, not only " + ScopeWords + ".\n\nCopy?",
                    WindowTitle,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes)
                    return;
            }

            Confirmed = _current;
            DialogResult = true;
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            _preferences.Category = _categoryName ?? string.Empty;
            _preferences.Source = _sourceKey ?? string.Empty;
            _preferences.Target = _targetKey ?? string.Empty;
            _preferences.OnlyEmptyTargets = _onlyEmpty.IsChecked == true;
            _preferences.EmptyClears = _emptyClears.IsChecked == true;
            _preferences.NoUnitSymbol = _noSymbol.IsChecked == true;
            _preferences.NumberFromText = _numberFromText.IsChecked == true;
            _preferences.ShowReadOnly = _showReadOnly.IsChecked == true;
            _preferences.Save();
        }

        // ───────────────────────────── list items ─────────────────────────────

        /// <summary>An item of the "Show" list above the preview.</summary>
        private sealed class ShowOption
        {
            public static readonly IReadOnlyList<ShowOption> All = new List<ShowOption>
            {
                new ShowOption("Every line", row => true),
                new ShowOption("Will be written", row => row.State == CopyRowState.Write),
                new ShowOption("Cannot be written", row => row.State == CopyRowState.Blocked),
                new ShowOption("Left as they are", row => row.State == CopyRowState.Same || row.State == CopyRowState.Skipped)
            };

            private ShowOption(string caption, Func<CopyPreviewRow, bool> matches)
            {
                Caption = caption;
                Matches = matches;
            }

            public string Caption { get; }

            public Func<CopyPreviewRow, bool> Matches { get; }

            public override string ToString()
            {
                return Caption;
            }
        }
    }
}
