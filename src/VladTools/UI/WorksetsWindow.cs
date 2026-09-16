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
    /// The "Worksets" window: every user workset of the open project, with what it holds, who owns it
    /// and a check box to remove it.
    ///
    /// The window opens on the plain list — nothing is checked, nothing is asked. That is the shape
    /// the button was asked for ("first the user sees the whole list of the project's worksets"), and
    /// it is also the deletion rule this add-in keeps everywhere: where a button deletes, the
    /// selection is the user's alone.
    ///
    /// The question Revit's own dialog asks one workset at a time — "move the elements somewhere, or
    /// delete them with the workset?" — is asked here once for the whole batch, under the table. It
    /// is on screen from the start rather than sprung as a dialog at the end: a choice that decides
    /// whether geometry survives belongs in view while the check boxes are being ticked. It is then
    /// repeated in the confirmation, because it is the one irreversible thing this button does.
    ///
    /// The window knows nothing about the Revit API: it is handed a list of <see cref="WorksetInfo"/>
    /// snapshots and hands back rows and an answer.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class WorksetsWindow : Window
    {
        private const string WindowTitle = "Worksets";

        private readonly IReadOnlyList<ProjectWorksetRow> _rows;

        private readonly ObservableCollection<string> _destinations = new ObservableCollection<string>();

        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly RadioButton _moveOption;
        private readonly RadioButton _deleteOption;
        private readonly ComboBox _destinationBox;
        private readonly TextBlock _status;
        private readonly Button _removeButton;

        private bool _syncingSelectAll;
        private bool _settingMany;
        private bool _rebuildingDestinations;

        /// <summary>The worksets the user confirmed for removal.</summary>
        public IReadOnlyList<ProjectWorksetRow> Selected { get; private set; } = new List<ProjectWorksetRow>();

        /// <summary>What to do with whatever stands in them.</summary>
        public WorksetElementAction ElementAction { get; private set; } = WorksetElementAction.Move;

        /// <summary>Where the elements go when <see cref="ElementAction"/> is <c>Move</c>; empty otherwise.</summary>
        public string Destination { get; private set; } = string.Empty;

        /// <summary>
        /// The destination workset's stable identity — what the command actually resolves against the
        /// project. The name is carried alongside it only for the report: the window picks by name,
        /// but a name is not what a workset is (see <see cref="WorksetInfo.UniqueId"/>).
        /// </summary>
        public Guid DestinationId { get; private set; } = Guid.Empty;

        /// <param name="worksets">Every user workset of the open project, as read when the command started.</param>
        /// <param name="projectName">The open project — the caption above the table.</param>
        public WorksetsWindow(IReadOnlyList<WorksetInfo> worksets, string projectName)
        {
            _rows = (worksets ?? new List<WorksetInfo>())
                .Select(info => new ProjectWorksetRow(info) { CanRemove = Removable(info) })
                .ToList();

            Title = WindowTitle;
            Width = 880;
            Height = 620;
            MinWidth = 640;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _selectAll = new CheckBox
            {
                IsChecked = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every workset that can be removed"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();

            _moveOption = new RadioButton
            {
                Content = "Move them to workset:",
                GroupName = "ElementAction",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "The worksets go, everything standing in them stays — in the workset chosen on the right.\n" +
                    "Nothing is lost this way, which is why the window starts on this answer."
            };
            _moveOption.Checked += (s, e) => Revalidate();

            _destinationBox = new ComboBox
            {
                Width = 240,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = _destinations,
                ToolTip = "Only worksets that are staying are offered: elements cannot be moved into a workset that is going too."
            };
            _destinationBox.SelectionChanged += (s, e) =>
            {
                if (!_rebuildingDestinations)
                    Revalidate();
            };

            _deleteOption = new RadioButton
            {
                Content = "Delete them together with the worksets",
                GroupName = "ElementAction",
                Margin = new Thickness(0, 8, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "The elements go with the workset — walls, links, views, whatever stands in it.\n" +
                    "Inside the Revit session this undoes with Ctrl+Z; once the model is synchronised it does not."
            };
            _deleteOption.Checked += (s, e) => Revalidate();

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _removeButton = new Button
            {
                Content = "Remove worksets",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _removeButton.Click += OnRemove;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(projectName, closeButton);

            foreach (var row in _rows)
                row.PropertyChanged += OnRowChanged;

            _grid.ItemsSource = _rows;

            Loaded += (s, e) => _grid.Focus();

            Revalidate();
        }

        /// <summary>
        /// Whether the row gets a check box at all. Somebody else's workset is not something to argue
        /// with: <c>CanDeleteWorkset</c> will refuse it, so a check box here would lead only to a
        /// failure line in the report. The reason goes into "State" instead.
        /// </summary>
        private static bool Removable(WorksetInfo info)
        {
            return string.IsNullOrEmpty(info.Owner) || info.IsEditable;
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(string projectName, Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // project
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // the question
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text =
                    "Every workset of the open project. Check the ones to remove — they go in one operation, " +
                    "so they undo with a single Ctrl+Z while the session lasts. The worksets you own are checked " +
                    "out automatically; one owned by another user cannot be removed until they relinquish it.",
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
                Margin = new Thickness(0, 0, 0, 2)
            };
            Grid.SetRow(project, 1);
            root.Children.Add(project);

            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);

            var question = BuildQuestion();
            Grid.SetRow(question, 3);
            root.Children.Add(question);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_removeButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 4);
            root.Children.Add(bottom);

            return root;
        }

        /// <summary>The question under the table: what becomes of the elements standing in the removed worksets.</summary>
        private UIElement BuildQuestion()
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(_moveOption);
            line.Children.Add(_destinationBox);

            var inner = new StackPanel();
            inner.Children.Add(line);
            inner.Children.Add(_deleteOption);

            return new GroupBox
            {
                Header = "Elements standing in the removed worksets",
                Padding = new Thickness(10, 8, 10, 10),
                Margin = new Thickness(0, 10, 0, 0),
                Content = inner
            };
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
                CellTemplate = GridBuilder.CheckBoxTemplate(
                    nameof(ProjectWorksetRow.IsSelected),
                    nameof(ProjectWorksetRow.CanRemove))
            });

            grid.Columns.Add(Text("Workset", nameof(ProjectWorksetRow.Name), new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(Text("Elements", nameof(ProjectWorksetRow.Contents), new DataGridLength(90)));

            var status = Text("State", nameof(ProjectWorksetRow.StatusText), new DataGridLength(1.4, DataGridLengthUnitType.Star));
            status.MinWidth = 200;
            status.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(ProjectWorksetRow.StatusBrush))));
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

        // ───────────────────────────── check boxes ─────────────────────────────

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(_rows, row => value && row.CanRemove);
        }

        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<ProjectWorksetRow>().Where(row => row.CanRemove).ToList();
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
        private void SetMany(IEnumerable<ProjectWorksetRow> rows, Func<ProjectWorksetRow, bool> value)
        {
            _settingMany = true;

            foreach (var row in rows)
                row.IsSelected = value(row);

            _settingMany = false;

            Revalidate();
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_settingMany || e.PropertyName != nameof(ProjectWorksetRow.IsSelected))
                return;

            Revalidate();
        }

        // ───────────────────────────── validation ─────────────────────────────

        private List<ProjectWorksetRow> Marked()
        {
            return _rows.Where(row => row.IsSelected && row.CanRemove).ToList();
        }

        /// <summary>
        /// Brings the whole window in line with the check boxes: the list of destinations, every
        /// row's "State", the summary and the button. One method rather than a handler per control,
        /// so the table can never promise one thing while the button does another — the same rule
        /// <c>ParameterSetCommand.Compare</c> keeps for its own table.
        /// </summary>
        private void Revalidate()
        {
            var marked = Marked();
            var doomed = new HashSet<Guid>(marked.Select(row => row.Info.UniqueId));

            RebuildDestinations(doomed);

            var moving = _moveOption.IsChecked == true;
            var destination = moving ? (_destinationBox.SelectedItem as string ?? string.Empty) : string.Empty;

            foreach (var row in _rows)
                Describe(row, doomed.Contains(row.Info.UniqueId), moving, destination);

            _destinationBox.IsEnabled = moving && _destinations.Count > 0;

            UpdateSummary(marked, moving, destination);
        }

        /// <summary>
        /// The destinations are the worksets that are staying: moving elements into one that is about
        /// to be deleted itself would only delete them a moment later — exactly the answer the user
        /// did not pick. The current choice survives a rebuild wherever it still can.
        /// </summary>
        private void RebuildDestinations(HashSet<Guid> doomed)
        {
            var survivors = _rows
                .Where(row => !doomed.Contains(row.Info.UniqueId))
                .Select(row => row.Name)
                .ToList();

            if (survivors.SequenceEqual(_destinations))
                return;

            var chosen = _destinationBox.SelectedItem as string;

            _rebuildingDestinations = true;

            _destinations.Clear();
            foreach (var name in survivors)
                _destinations.Add(name);

            _destinationBox.SelectedItem = chosen != null && survivors.Contains(chosen)
                ? chosen
                : survivors.FirstOrDefault();

            _rebuildingDestinations = false;
        }

        /// <summary>Fills in one row's "State" — why it cannot be removed, or what removing it will cost.</summary>
        private static void Describe(ProjectWorksetRow row, bool marked, bool moving, string destination)
        {
            var info = row.Info;

            if (!row.CanRemove)
            {
                row.IsWarning = true;
                row.StatusText = "Owned by " + info.Owner + " — they have to relinquish it first";
                return;
            }

            if (!marked)
            {
                row.IsWarning = false;
                row.StatusText = info.IsActive
                    ? "Active workset"
                    : !info.IsCounted ? Uncounted(info) + " — its contents cannot be counted" : string.Empty;
                return;
            }

            if (!info.IsCounted)
            {
                // The one case where "empty" and "unknown" must never be merged: a closed workset
                // reads as zero elements through any collector, and on the delete path that zero
                // would quietly take a wall down with it.
                row.IsWarning = true;
                row.StatusText = Uncounted(info) + (moving
                    ? " — contents unknown; whatever is inside moves to \"" + destination + "\""
                    : " — contents unknown; whatever is inside will be DELETED");
                return;
            }

            if (info.ElementCount == 0)
            {
                row.IsWarning = false;
                row.StatusText = "Will be removed — it is empty";
                return;
            }

            if (moving)
            {
                row.IsWarning = string.IsNullOrEmpty(destination);
                row.StatusText = string.IsNullOrEmpty(destination)
                    ? "Nowhere to move " + info.ElementCount + " elements to"
                    : info.ElementCount + " elements move to \"" + destination + "\"";
                return;
            }

            row.IsWarning = true;
            row.StatusText = info.ElementCount + " elements will be DELETED with the workset";
        }

        /// <summary>
        /// Why the contents are unknown. Nearly always because the workset is closed — a collector
        /// cannot see into one — but a collector that threw leaves the same gap, and calling that
        /// "Closed" would send the user looking for a check box that is already ticked.
        /// </summary>
        private static string Uncounted(WorksetInfo info)
        {
            return info.IsOpen ? "Could not be read" : "Closed";
        }

        private void UpdateSummary(IReadOnlyList<ProjectWorksetRow> marked, bool moving, string destination)
        {
            var removable = _rows.Count(row => row.CanRemove);

            _syncingSelectAll = true;
            _selectAll.IsChecked = removable == 0 || marked.Count == 0
                ? false
                : marked.Count == removable ? true : (bool?)null;
            _syncingSelectAll = false;

            _removeButton.Content = "Remove worksets";

            if (marked.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = removable == 0
                    ? "None of the project's worksets can be removed."
                    : "Nothing is checked. Worksets in the project: " + _rows.Count +
                      (removable == _rows.Count ? "." : ", of them removable: " + removable + ".");

                _removeButton.IsEnabled = false;
                return;
            }

            if (marked.Count == _rows.Count)
            {
                // Revit keeps no project without a user workset, and there would be nowhere to move
                // the contents either. Caught here rather than as a row of identical failure lines later.
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "Every workset is checked — at least one has to stay in the project.";
                _removeButton.IsEnabled = false;
                return;
            }

            if (moving && string.IsNullOrEmpty(destination))
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "Choose the workset the elements move to.";
                _removeButton.IsEnabled = false;
                return;
            }

            var counted = marked.Where(row => row.Info.IsCounted).Sum(row => row.Info.ElementCount);
            var unknown = marked.Count(row => !row.Info.IsCounted);

            var text = "Worksets checked: " + marked.Count + " of " + removable + ". Elements in them: " + counted;
            if (unknown > 0)
                text += ", plus the contents of " + unknown + " " +
                        (unknown == 1 ? "workset" : "worksets") + " that could not be counted";

            text += moving ? ". They move to \"" + destination + "\"." : ". They will be deleted.";

            _status.Foreground = !moving || unknown > 0 ? Brushes.Firebrick : SystemColors.GrayTextBrush;
            _status.Text = text;

            _removeButton.IsEnabled = true;
            _removeButton.Content = "Remove worksets (" + marked.Count + ")";
        }

        // ───────────────────────────── actions ─────────────────────────────

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
                return;

            var moving = _moveOption.IsChecked == true;
            var destination = moving ? (_destinationBox.SelectedItem as string ?? string.Empty) : string.Empty;

            if (moving && string.IsNullOrEmpty(destination))
            {
                MessageBox.Show(this, "Choose the workset the elements move to.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!Confirm(marked, moving, destination))
                return;

            Selected = marked;
            ElementAction = moving ? WorksetElementAction.Move : WorksetElementAction.Delete;
            Destination = destination;
            DestinationId = _rows
                .Where(row => row.Name == destination)
                .Select(row => row.Info.UniqueId)
                .FirstOrDefault();
            DialogResult = true;
        }

        /// <summary>
        /// The confirmation names every workset and states the answer to the question in full. The
        /// "delete the elements" path gets a sentence of its own and starts on "No": once the model
        /// is synchronised, Ctrl+Z will not bring the geometry back.
        /// </summary>
        private bool Confirm(IReadOnlyList<ProjectWorksetRow> marked, bool moving, string destination)
        {
            var counted = marked.Where(row => row.Info.IsCounted).Sum(row => row.Info.ElementCount);
            var unknown = marked.Where(row => !row.Info.IsCounted).ToList();

            var text = "Remove " + marked.Count + " " + (marked.Count == 1 ? "workset" : "worksets") + "?\n\n" +
                       string.Join("\n", marked.Select(row => "• " + row.Name + " — " + row.Contents)) + "\n\n";

            if (counted == 0 && unknown.Count == 0)
                text += "They are empty, so nothing but the worksets themselves disappears.";
            else if (moving)
                text += "Elements standing in them: " + counted + ". They move to workset \"" + destination +
                        "\" and stay in the model.";
            else
                text += "Elements standing in them: " + counted + ". THEY WILL BE DELETED together with the " +
                        "worksets — walls, links, views, everything that is in them.";

            if (unknown.Count > 0)
            {
                // Said out loud on both paths, but it is the delete path where it matters: these
                // worksets' contents were never counted, and the number above does not include them.
                text += "\n\nThe contents of " + unknown.Count + " " +
                        (unknown.Count == 1 ? "workset" : "worksets") + " could not be counted — a closed " +
                        "workset is invisible to the add-in — and are not in the number above:\n" +
                        string.Join("\n", unknown.Select(row => "• " + row.Name)) +
                        (moving
                            ? "\nWhatever is inside moves to \"" + destination + "\" as well."
                            : "\nWhatever is inside will be DELETED, sight unseen. Open these worksets in Revit " +
                              "and look before going on.");
            }

            text += "\n\nEverything is carried out as a single operation and undoes with one Ctrl+Z — but only " +
                    "until the model is synchronised. Save or synchronise before removing.";

            var answer = MessageBox.Show(
                this,
                text,
                WindowTitle,
                MessageBoxButton.YesNo,
                moving && unknown.Count == 0 ? MessageBoxImage.Question : MessageBoxImage.Warning,
                MessageBoxResult.No);

            return answer == MessageBoxResult.Yes;
        }
    }
}
