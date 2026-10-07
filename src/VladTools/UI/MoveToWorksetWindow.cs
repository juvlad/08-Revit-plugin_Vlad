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
    /// The "Move to Workset" window: the elements of the scope, one row per workset and category,
    /// and the one workset the checked rows go into.
    ///
    /// The rows are workset × category rather than single elements: what has to be fixed is nearly
    /// always "everything the AI modelled ended up in the wrong place" — hundreds of ducts and
    /// fittings — and a table of hundreds of element rows is no help in deciding that. A category is
    /// the finest grain the decision is ever made at ("ducts go to OV, pipes to VK"), and running the
    /// button twice covers that.
    ///
    /// Rows in Revit's own worksets — project standards, a view, a family — come first and are
    /// checked from the start: a model element standing there is the anomaly the button was asked
    /// for. Rows in user worksets start unchecked on the whole model and checked on a selection — the
    /// user selected those elements on purpose.
    ///
    /// The window knows nothing about the Revit API beyond carrying element ids: it is handed scans
    /// and the user worksets, and hands back rows, a target and what to do with them.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class MoveToWorksetWindow : Window
    {
        private const string WindowTitle = "Move to Workset";

        private readonly WorksetCategoryScan _selectionScan;
        private readonly Func<WorksetCategoryScan> _scanModel;
        private WorksetCategoryScan _modelScan;
        private string _modelError = string.Empty;

        private readonly IReadOnlyList<TargetOption> _targets;

        private List<WorksetCategoryRow> _rows = new List<WorksetCategoryRow>();
        private readonly ObservableCollection<WorksetCategoryRow> _visible = new ObservableCollection<WorksetCategoryRow>();
        private readonly ObservableCollection<ShowOption> _showOptions = new ObservableCollection<ShowOption>();

        private readonly RadioButton _selectionOption;
        private readonly RadioButton _modelOption;
        private readonly ComboBox _showBox;
        private readonly TextBlock _scopeNote;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly ComboBox _targetBox;
        private readonly TextBlock _status;
        private readonly Button _selectButton;
        private readonly Button _moveButton;

        private bool _syncingSelectAll;
        private bool _settingMany;
        private bool _rebuildingShow;

        /// <summary>The checked rows, as the window was closed.</summary>
        public IReadOnlyList<WorksetCategoryRow> Selected { get; private set; } = new List<WorksetCategoryRow>();

        /// <summary>
        /// The window was closed with "Select in model": the command selects the elements of
        /// <see cref="Selected"/> in Revit and moves nothing. Selecting from inside the window would be
        /// pointless: the point is to look at the elements, and the model cannot be looked at while a
        /// modal window sits on top of it.
        /// </summary>
        public bool WantsSelection { get; private set; }

        /// <summary>The target workset's GUID — what the command resolves against the project.</summary>
        public Guid TargetId { get; private set; } = Guid.Empty;

        /// <summary>The target workset's name, for the report only.</summary>
        public string Target { get; private set; } = string.Empty;

        /// <param name="selectionScan">What is selected in Revit right now; <c>null</c> when nothing is.</param>
        /// <param name="scanModel">Reads the whole model — called only when that scope is shown, since on a large model it takes a while.</param>
        /// <param name="targets">The project's user worksets: the only kind elements can be moved into.</param>
        /// <param name="projectName">The open project — the caption above the table.</param>
        public MoveToWorksetWindow(
            WorksetCategoryScan selectionScan,
            Func<WorksetCategoryScan> scanModel,
            IReadOnlyList<WorksetInfo> targets,
            string projectName)
        {
            _selectionScan = selectionScan;
            _scanModel = scanModel;
            _targets = (targets ?? new List<WorksetInfo>()).Select(info => new TargetOption(info)).ToList();

            Title = WindowTitle;
            Width = 980;
            Height = 640;
            MinWidth = 720;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _selectionOption = new RadioButton
            {
                Content = selectionScan == null
                    ? "Selected in the model (nothing is selected)"
                    : "Selected in the model (" + selectionScan.ElementCount + ")",
                GroupName = "Scope",
                IsEnabled = selectionScan != null,
                IsChecked = selectionScan != null,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Only the elements selected in Revit before the button was pressed."
            };

            _modelOption = new RadioButton
            {
                Content = "The whole model",
                GroupName = "Scope",
                IsChecked = selectionScan == null,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "Every element of the open project whose workset can be changed.\n" +
                    "Closed worksets are not looked into — open them first if they matter."
            };

            _selectionOption.Checked += (s, e) => LoadScope();
            _modelOption.Checked += (s, e) => LoadScope();

            _showBox = new ComboBox
            {
                Width = 300,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = _showOptions,
                ToolTip = "Which rows the table shows. A row that leaves the table loses its check mark: only what is shown gets moved."
            };
            _showBox.SelectionChanged += (s, e) =>
            {
                if (!_rebuildingShow)
                    RebuildVisible();
            };

            _scopeNote = new TextBlock
            {
                Foreground = SystemColors.GrayTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };

            _selectAll = new CheckBox
            {
                IsChecked = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every row in the table"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();

            _targetBox = new ComboBox
            {
                Width = 360,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = _targets,
                ToolTip = "Only user worksets are offered: they are the only kind Revit lets elements stand in."
            };
            _targetBox.SelectedItem = _targets.FirstOrDefault(target => target.Info.IsActive) ?? _targets.FirstOrDefault();
            _targetBox.SelectionChanged += (s, e) => Revalidate();

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _selectButton = new Button
            {
                Content = "Select in model",
                MinWidth = 140,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false,
                ToolTip =
                    "Closes the window and selects the elements of the checked rows in Revit — every one of them,\n" +
                    "including the ones that will not move — to look at them before moving. Nothing is changed."
            };
            _selectButton.Click += OnSelect;

            _moveButton = new Button
            {
                Content = "Move",
                MinWidth = 120,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _moveButton.Click += OnMove;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(projectName, closeButton);

            _grid.ItemsSource = _visible;

            Loaded += (s, e) => _grid.Focus();

            LoadScope();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(string projectName, Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // project
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // scope + show
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // scope note
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // target
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text =
                    "The elements by the workset they stand in and their category. Check the rows to move, choose " +
                    "the workset they belong in, and press \"Move\" — it is one operation, one Ctrl+Z. Rows in " +
                    "Revit's own worksets (project standards, views, families) are model elements that ended up " +
                    "where none belong; they come first and are checked from the start. \"Select in model\" selects " +
                    "the checked elements in Revit instead, to look at them first.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var project = new TextBlock
            {
                Text = "Project: " + (string.IsNullOrEmpty(projectName) ? "(unsaved)" : projectName),
                Foreground = SystemColors.GrayTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(project, 1);
            root.Children.Add(project);

            var scope = new DockPanel { LastChildFill = false };

            var scopeLabel = new TextBlock { Text = "Elements:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(scopeLabel, Dock.Left);
            scope.Children.Add(scopeLabel);

            DockPanel.SetDock(_selectionOption, Dock.Left);
            scope.Children.Add(_selectionOption);

            DockPanel.SetDock(_modelOption, Dock.Left);
            scope.Children.Add(_modelOption);

            DockPanel.SetDock(_showBox, Dock.Right);
            scope.Children.Add(_showBox);

            var showLabel = new TextBlock { Text = "Show:", VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(showLabel, Dock.Right);
            scope.Children.Add(showLabel);

            Grid.SetRow(scope, 2);
            root.Children.Add(scope);

            Grid.SetRow(_scopeNote, 3);
            root.Children.Add(_scopeNote);

            Grid.SetRow(_grid, 4);
            root.Children.Add(_grid);

            var target = new StackPanel { Orientation = Orientation.Horizontal };
            target.Children.Add(new TextBlock { Text = "Move to workset:", VerticalAlignment = VerticalAlignment.Center });
            target.Children.Add(_targetBox);

            var targetBox = new GroupBox
            {
                Header = "Where the checked elements go",
                Padding = new Thickness(10, 8, 10, 10),
                Margin = new Thickness(0, 10, 0, 0),
                Content = target
            };
            Grid.SetRow(targetBox, 5);
            root.Children.Add(targetBox);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_selectButton);
            buttons.Children.Add(_moveButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 6);
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
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = SystemColors.ControlLightBrush,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                SelectionMode = DataGridSelectionMode.Extended,
                IsReadOnly = true,
                RowHeaderWidth = 0,
                Margin = new Thickness(0, 6, 0, 0)
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate(nameof(WorksetCategoryRow.IsSelected))
            });

            grid.Columns.Add(Text("Workset", nameof(WorksetCategoryRow.Workset), new DataGridLength(1.2, DataGridLengthUnitType.Star)));
            grid.Columns.Add(Text("Kind", nameof(WorksetCategoryRow.Kind), new DataGridLength(120)));
            grid.Columns.Add(Text("Category", nameof(WorksetCategoryRow.Category), new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(Text("Elements", nameof(WorksetCategoryRow.Elements), new DataGridLength(80)));

            var status = Text("State", nameof(WorksetCategoryRow.StatusText), new DataGridLength(1.6, DataGridLengthUnitType.Star));
            status.MinWidth = 220;
            status.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(WorksetCategoryRow.StatusBrush))));
            grid.Columns.Add(status);

            grid.MouseDoubleClick += (s, e) => ToggleSelectedRows();
            grid.PreviewKeyDown += OnGridKeyDown;

            return grid;
        }

        private static DataGridTextColumn Text(string header, string property, DataGridLength width)
        {
            var column = GridBuilder.TextColumn(header, property, width);
            column.IsReadOnly = true;

            return column;
        }

        // ───────────────────────────── scope ─────────────────────────────

        /// <summary>
        /// Rebuilds the table for the scope chosen above it. Check marks start over with the scope's
        /// own defaults: a row of the selection and a row of the whole model are different rows, even
        /// when they name the same workset and category.
        /// </summary>
        private void LoadScope()
        {
            var selection = _selectionOption.IsChecked == true;
            var scan = selection ? _selectionScan : ModelScan();

            foreach (var row in _rows)
                row.PropertyChanged -= OnRowChanged;

            _rows = (scan?.Rows ?? new List<WorksetCategoryInfo>())
                .Select(info => new WorksetCategoryRow(info) { IsSelected = selection || !info.IsUserWorkset })
                .ToList();

            foreach (var row in _rows)
                row.PropertyChanged += OnRowChanged;

            RebuildShowOptions();

            _scopeNote.Foreground = scan == null ? Brushes.Firebrick : SystemColors.GrayTextBrush;
            _scopeNote.Text = ScopeNote(scan, selection);

            RebuildVisible();
        }

        /// <summary>The whole model is read once, the first time it is asked for, under a wait cursor.</summary>
        private WorksetCategoryScan ModelScan()
        {
            if (_modelScan != null || _scanModel == null)
                return _modelScan;

            var previous = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                _modelScan = _scanModel();
                _modelError = string.Empty;
            }
            catch (Exception exception)
            {
                _modelError = exception.Message;
            }
            finally
            {
                Mouse.OverrideCursor = previous;
            }

            return _modelScan;
        }

        private string ScopeNote(WorksetCategoryScan scan, bool selection)
        {
            if (scan == null)
                return "The model could not be read: " + _modelError;

            var outside = scan.Rows.Where(row => !row.IsUserWorkset).Sum(row => row.Total);

            string text;

            if (selection)
            {
                text = "Selected in the model: " + scan.ElementCount + ".";
                if (scan.LeftOut > 0)
                    text += " Not in the table: " + scan.LeftOut + " — their workset is Revit's to decide (types, views, " +
                            "annotation belonging to a view, parts of another element).";
            }
            else
            {
                text = outside == 0
                    ? "No element of the model stands outside the user worksets."
                    : "Elements standing outside the user worksets: " + outside + ".";
            }

            if (scan.ClosedWorksets.Count > 0)
                text += " Closed worksets are not looked into — whatever stands in them is in no row: " +
                        string.Join(", ", scan.ClosedWorksets) + ".";

            return text;
        }

        /// <summary>
        /// The "Show" list: Revit's own worksets together (offered first when there are any — that is
        /// what the button is for), everything, or one workset at a time.
        /// </summary>
        private void RebuildShowOptions()
        {
            _rebuildingShow = true;

            _showOptions.Clear();

            if (_rows.Any(row => !row.Info.IsUserWorkset))
                _showOptions.Add(new ShowOption("Outside user worksets", row => !row.Info.IsUserWorkset));

            _showOptions.Add(new ShowOption("All worksets", row => true));

            foreach (var workset in _rows.Select(row => row.Info).GroupBy(info => info.WorksetId))
            {
                var id = workset.Key;
                _showOptions.Add(new ShowOption("Workset: " + workset.First().WorksetName, row => row.Info.WorksetId == id));
            }

            _showBox.SelectedIndex = 0;

            _rebuildingShow = false;
        }

        /// <summary>
        /// Fills the table from the "Show" choice. A row that leaves the table loses its check mark —
        /// only what is shown gets moved, the rule every filter in this add-in keeps where the filter
        /// is the selection.
        /// </summary>
        private void RebuildVisible()
        {
            var option = _showBox.SelectedItem as ShowOption;

            _settingMany = true;

            _visible.Clear();

            foreach (var row in _rows)
            {
                if (option == null || option.Matches(row))
                    _visible.Add(row);
                else
                    row.IsSelected = false;
            }

            _settingMany = false;

            Revalidate();
        }

        // ───────────────────────────── check boxes ─────────────────────────────

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(_visible, row => value);
        }

        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<WorksetCategoryRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);

            SetMany(rows, row => value);
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space)
                return;

            ToggleSelectedRows();
            e.Handled = true;
        }

        /// <summary>Setting check boxes in bulk: the table is revalidated once, at the end.</summary>
        private void SetMany(IEnumerable<WorksetCategoryRow> rows, Func<WorksetCategoryRow, bool> value)
        {
            _settingMany = true;

            foreach (var row in rows.ToList())
                row.IsSelected = value(row);

            _settingMany = false;

            Revalidate();
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_settingMany || e.PropertyName != nameof(WorksetCategoryRow.IsSelected))
                return;

            Revalidate();
        }

        // ───────────────────────────── validation ─────────────────────────────

        private List<WorksetCategoryRow> Marked()
        {
            return _visible.Where(row => row.IsSelected).ToList();
        }

        /// <summary>
        /// Brings the whole window in line with the check boxes and the target: every row's "State",
        /// the summary and both buttons, in one method — so the table can never promise one thing
        /// while "Move" does another.
        /// </summary>
        private void Revalidate()
        {
            var target = _targetBox.SelectedItem as TargetOption;

            foreach (var row in _visible)
                Describe(row, target);

            UpdateSummary(target);
        }

        /// <summary>Fills in one row's "State": what moving it will do, and what in it will not move.</summary>
        private static void Describe(WorksetCategoryRow row, TargetOption target)
        {
            var info = row.Info;
            var parts = new List<string>();
            var warning = false;

            if (target != null && info.WorksetId == target.Info.UniqueId)
            {
                parts.Add("Already in this workset");
            }
            else if (row.IsSelected && target != null && info.Movable.Count > 0)
            {
                parts.Add(info.Movable.Count + " move to \"" + target.Info.Name + "\"");
            }
            else if (!info.IsUserWorkset)
            {
                parts.Add("Revit's own workset — model elements do not belong in it");
                warning = true;
            }

            // In a user workset left unchecked these are nobody's concern; anywhere else they are the
            // elements that will stay behind, and that has to be visible before "Move" is pressed.
            if (row.IsSelected || !info.IsUserWorkset)
            {
                if (info.InGroups.Count > 0)
                {
                    parts.Add(info.InGroups.Count + " inside model groups stay — edit or ungroup the group");
                    warning = true;
                }

                if (info.Locked.Count > 0)
                {
                    parts.Add("Revit does not let " + info.Locked.Count + " of them change workset");
                    warning = true;
                }
            }

            row.StatusText = string.Join("; ", parts);
            row.IsWarning = warning;
        }

        private void UpdateSummary(TargetOption target)
        {
            var marked = Marked();

            _syncingSelectAll = true;
            _selectAll.IsChecked = _visible.Count == 0 || marked.Count == 0
                ? false
                : marked.Count == _visible.Count ? true : (bool?)null;
            _syncingSelectAll = false;

            var moving = Moving(marked, target);
            var staying = marked.Sum(row => row.Info.InGroups.Count + row.Info.Locked.Count);
            var selectable = marked.Sum(row => row.Info.Total);

            _selectButton.IsEnabled = selectable > 0;
            _selectButton.Content = selectable > 0 ? "Select in model (" + selectable + ")" : "Select in model";

            _moveButton.IsEnabled = moving > 0;
            _moveButton.Content = moving > 0 ? "Move (" + moving + ")" : "Move";

            if (_rows.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "There is nothing here whose workset can be changed.";
                return;
            }

            if (marked.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Nothing is checked. Rows in the table: " + _visible.Count + ", elements in them: " +
                               _visible.Sum(row => row.Info.Total) + ".";
                return;
            }

            if (target == null)
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "The project has no user workset to move the elements into.";
                return;
            }

            var text = "Checked rows: " + marked.Count + ". " + (moving > 0
                ? moving + " elements move to \"" + target.Info.Name + "\""
                : "Nothing to move — the checked elements are already in \"" + target.Info.Name + "\" or cannot be moved");

            if (staying > 0)
                text += ", " + staying + " stay where they are (inside model groups, or Revit does not let them go)";

            text += ".";

            var warning = false;

            if (moving > 0 && !target.Info.IsOpen)
            {
                text += " \"" + target.Info.Name + "\" is closed — the elements will disappear from view.";
                warning = true;
            }

            if (moving > 0 && target.IsOwnedByOther)
            {
                text += " \"" + target.Info.Name + "\" is owned by " + target.Info.Owner +
                        " — Revit may refuse to put elements into it until they relinquish it.";
                warning = true;
            }

            _status.Foreground = warning ? Brushes.Firebrick : SystemColors.GrayTextBrush;
            _status.Text = text;
        }

        /// <summary>How many elements the checked rows actually send anywhere: a row already in the target sends none.</summary>
        private static int Moving(IEnumerable<WorksetCategoryRow> marked, TargetOption target)
        {
            if (target == null)
                return 0;

            return marked
                .Where(row => row.Info.WorksetId != target.Info.UniqueId)
                .Sum(row => row.Info.Movable.Count);
        }

        // ───────────────────────────── actions ─────────────────────────────

        private void OnMove(object sender, RoutedEventArgs e)
        {
            var target = _targetBox.SelectedItem as TargetOption;
            var marked = Marked();

            if (target == null || Moving(marked, target) == 0)
                return;

            Selected = marked;
            WantsSelection = false;
            TargetId = target.Info.UniqueId;
            Target = target.Info.Name;
            DialogResult = true;
        }

        private void OnSelect(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Sum(row => row.Info.Total) == 0)
                return;

            Selected = marked;
            WantsSelection = true;
            DialogResult = true;
        }

        // ───────────────────────────── list items ─────────────────────────────

        /// <summary>An item of the "Show" list: a caption and which rows it keeps.</summary>
        private sealed class ShowOption
        {
            public ShowOption(string caption, Func<WorksetCategoryRow, bool> matches)
            {
                Caption = caption;
                Matches = matches;
            }

            public string Caption { get; }

            public Func<WorksetCategoryRow, bool> Matches { get; }

            public override string ToString()
            {
                return Caption;
            }
        }

        /// <summary>
        /// An item of the target list. What would make the move go wrong — a closed workset, one owned
        /// by somebody else — is written into the caption, so it is seen while choosing, not after.
        /// </summary>
        private sealed class TargetOption
        {
            public TargetOption(WorksetInfo info)
            {
                Info = info;

                var notes = new List<string>();
                if (info.IsActive)
                    notes.Add("active");
                if (!info.IsOpen)
                    notes.Add("closed");
                if (IsOwnedByOther)
                    notes.Add("owned by " + info.Owner);

                Caption = notes.Count == 0 ? info.Name : info.Name + " (" + string.Join(", ", notes) + ")";
            }

            public WorksetInfo Info { get; }

            public string Caption { get; }

            /// <summary>Somebody else has the workset checked out: <c>Owner</c> is set, and it is not the current user's.</summary>
            public bool IsOwnedByOther => !string.IsNullOrEmpty(Info.Owner) && !Info.IsEditable;

            public override string ToString()
            {
                return Caption;
            }
        }
    }
}
