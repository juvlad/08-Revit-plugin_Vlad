using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /// The "Schedule Library" window: a set of schedules kept in the Windows profile on the left of
    /// the buttons, and what will become of each of them in the open project on the right.
    ///
    /// Two jobs meet here, and they are deliberately not split into two buttons: filling the set
    /// ("take from a model") and emptying it into the project ("insert"). Splitting them would mean a
    /// second window with the same table, and a user who has to remember which button fills and which
    /// pours.
    ///
    /// The window knows nothing about the Revit API: reading a set, taking schedules out of a model
    /// and throwing them out of a set are all handed in as call-backs — the same way
    /// <c>DeleteProjectParametersWindow</c> is given its family scan.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class ScheduleLibraryWindow : Window
    {
        private const string WindowTitle = "Schedule Library";

        private readonly IReadOnlyDictionary<string, int> _existing;
        private readonly Func<string, ScheduleSetScan> _read;
        private readonly Func<string, LinkEntry, ScheduleChooser, ScheduleSetScan> _capture;
        private readonly Func<string, IReadOnlyList<string>, ScheduleSetScan> _remove;

        private readonly SchedulePreferences _preferences;
        private readonly LinkPreferences _linkPreferences;

        private readonly ObservableCollection<ScheduleRow> _rows = new ObservableCollection<ScheduleRow>();
        private readonly List<ActionOption> _actionOptions =
            ScheduleActionText.All.Select(action => new ActionOption(action, ScheduleActionText.Caption(action))).ToList();

        private readonly ComboBox _setBox;
        private readonly ComboBox _defaultActionBox;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _setPath;
        private readonly TextBlock _status;
        private readonly Button _insertButton;

        private bool _loadingSets;
        private bool _syncingSelectAll;
        private bool _settingMany;

        /// <summary>The rows the user confirmed for insertion, with the name clash already settled on each.</summary>
        public IReadOnlyList<ScheduleRow> Selected { get; private set; } = new List<ScheduleRow>();

        /// <summary>The set the schedules are taken from — the command opens its file to copy out of.</summary>
        public string SetName => (_setBox.Text ?? string.Empty).Trim();

        /// <param name="existing">Schedule names already in the open project, and how many sheets each sits on.</param>
        /// <param name="projectName">The open project — the caption above the table.</param>
        /// <param name="read">Reads a set's contents.</param>
        /// <param name="capture">Puts schedules into a set; a null <c>LinkEntry</c> means the open project.</param>
        /// <param name="remove">Throws the named schedules out of a set.</param>
        public ScheduleLibraryWindow(
            IReadOnlyDictionary<string, int> existing,
            string projectName,
            Func<string, ScheduleSetScan> read,
            Func<string, LinkEntry, ScheduleChooser, ScheduleSetScan> capture,
            Func<string, IReadOnlyList<string>, ScheduleSetScan> remove)
        {
            _existing = existing ?? new Dictionary<string, int>();
            _read = read;
            _capture = capture;
            _remove = remove;

            _preferences = SchedulePreferences.Load();
            _linkPreferences = LinkPreferences.Load();

            Title = WindowTitle;
            Width = 980;
            Height = 620;
            MinWidth = 700;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _setBox = new ComboBox
            {
                Width = 220,
                IsEditable = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "A saved set of schedules. It lies in the Windows profile as a small Revit file\n" +
                    "and is offered in every project after — the model the schedules came from\n" +
                    "is no longer needed.\n" +
                    "The name can be chosen from the list or typed in: a new name makes a new set."
            };
            _setBox.SelectionChanged += (s, e) =>
            {
                if (!_loadingSets)
                    ReloadSet();
            };

            _defaultActionBox = new ComboBox
            {
                Width = 170,
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = _actionOptions,
                DisplayMemberPath = nameof(ActionOption.Caption),
                SelectedValuePath = nameof(ActionOption.Value),
                SelectedValue = _preferences.Action,
                ToolTip = "Sets the action on every row where the project already holds a schedule of that name."
            };
            _defaultActionBox.SelectionChanged += (s, e) => ApplyDefaultAction();

            _selectAll = new CheckBox
            {
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every schedule in the set"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();

            _setPath = new TextBlock
            {
                Foreground = SystemColors.GrayTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _insertButton = new Button
            {
                Content = "Insert",
                MinWidth = 140,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _insertButton.Click += OnInsert;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(projectName, closeButton);

            ReloadSetNames();

            // The set is named first and read after: assigning the text of an editable box that
            // matches an item raises SelectionChanged, and the set would otherwise be read twice.
            _loadingSets = true;
            _setBox.Text = _preferences.Set.Length > 0
                ? _preferences.Set
                : ScheduleLibrary.Names().FirstOrDefault() ?? string.Empty;
            _loadingSets = false;

            ReloadSet();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(string projectName, Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // set
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // sources
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // clashes
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "Schedules are taken out of a base model once and kept in the Windows profile; from then on " +
                       "they are inserted into any model with one button, and the base model is not needed for it.\n" +
                       "The checked ones go into " +
                       (string.IsNullOrEmpty(projectName) ? "the open project" : "\"" + projectName + "\"") +
                       " as a single operation — one Ctrl+Z undoes all of it.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var sets = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            sets.Children.Add(Caption("Set:"));
            sets.Children.Add(_setBox);
            sets.Children.Add(Small("Open", "Reads the set named on the left.", (s, e) => ReloadSet()));
            sets.Children.Add(Small("Remove from set", "Throws the checked schedules out of the set itself.", OnRemove));
            sets.Children.Add(Small("Delete set", "Deletes the whole set from the Windows profile.", OnDeleteSet));
            Grid.SetRow(sets, 1);
            root.Children.Add(sets);

            var sources = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            sources.Children.Add(Caption("Add to the set from:"));
            sources.Children.Add(Small("Open project",
                "Takes the schedules out of the model open right now — the fastest way: nothing has to be opened.",
                (s, e) => Capture(null)));
            sources.Children.Add(Small("File…",
                "A model from a disk or a network folder. It is opened in the background — a large model takes a while.",
                OnFromFile));
            sources.Children.Add(Small("Revit Server…",
                "A model from Revit Server. It is opened in the background — a large model takes a while.",
                OnFromServer));
            sources.Children.Add(Small("BIM360…",
                "A model from Autodesk Docs, under the account you are signed in to Revit with.",
                OnFromCloud));
            sources.Children.Add(Small("by GUID…",
                "A cloud model given as a pair of GUIDs — when the cloud cannot be browsed.",
                OnFromGuid));
            Grid.SetRow(sources, 2);
            root.Children.Add(sources);

            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            var clashes = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            clashes.Children.Add(Caption("If the project already holds a schedule of that name:"));
            clashes.Children.Add(_defaultActionBox);
            Grid.SetRow(clashes, 4);
            root.Children.Add(clashes);

            var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(_status);
            left.Children.Add(_setPath);
            Grid.SetColumn(left, 0);
            bottom.Children.Add(left);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_insertButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 5);
            root.Children.Add(bottom);

            return root;
        }

        private static TextBlock Caption(string text)
        {
            return new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
        }

        private static Button Small(string text, string tooltip, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tooltip
            };
            button.Click += handler;

            return button;
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
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = SystemColors.ControlLightBrush,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                SelectionMode = DataGridSelectionMode.Extended,
                RowHeaderWidth = 0,
                Margin = new Thickness(0, 6, 0, 0)
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate()
            });

            grid.Columns.Add(Text("Schedule", nameof(ScheduleRow.Name), new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(Text("Category", nameof(ScheduleRow.Category), new DataGridLength(140)));
            grid.Columns.Add(Text("Kind", nameof(ScheduleRow.Kind), new DataGridLength(110)));
            grid.Columns.Add(Text("Columns", nameof(ScheduleRow.FieldCount), new DataGridLength(70)));

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "If the name is taken",
                Width = new DataGridLength(150),
                CanUserSort = false,
                CellTemplate = ActionTemplate()
            });

            var status = Text("State", nameof(ScheduleRow.StatusText), new DataGridLength(1.3, DataGridLengthUnitType.Star));
            status.MinWidth = 170;
            status.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(ScheduleRow.StatusBrush))));
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

        /// <summary>
        /// The clash column: a live <c>ComboBox</c> in the cell, not a <c>DataGridComboBoxColumn</c> —
        /// that one has three mutually exclusive bindings and silently ignores all but the first, so a
        /// choice never reaches the row (see the same decision in <c>AutoDimensionWindow</c>).
        /// The drop-down is disabled where there is nothing to decide: the name is free.
        /// </summary>
        private DataTemplate ActionTemplate()
        {
            var combo = new FrameworkElementFactory(typeof(ComboBox));
            combo.SetValue(ItemsControl.ItemsSourceProperty, _actionOptions);
            combo.SetValue(ItemsControl.DisplayMemberPathProperty, nameof(ActionOption.Caption));
            combo.SetValue(Selector.SelectedValuePathProperty, nameof(ActionOption.Value));
            combo.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));

            // The list is one for every row, so their collection view is shared too: without an
            // explicit "do not synchronise", a choice in one row would drag the others along.
            combo.SetValue(Selector.IsSynchronizedWithCurrentItemProperty, (bool?)false);
            combo.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(ScheduleRow.HasConflict)));
            combo.SetBinding(Selector.SelectedValueProperty,
                new Binding(nameof(ScheduleRow.Action))
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });

            return new DataTemplate { VisualTree = combo };
        }

        // ───────────────────────────── the set ─────────────────────────────

        private void ReloadSetNames()
        {
            var current = _setBox.Text;

            _loadingSets = true;
            _setBox.Items.Clear();

            foreach (var name in ScheduleLibrary.Names())
                _setBox.Items.Add(name);

            _setBox.Text = current;
            _loadingSets = false;
        }

        /// <summary>Reads the chosen set and rebuilds the table from it.</summary>
        private void ReloadSet()
        {
            if (SetName.Length == 0)
            {
                Fill(null);
                return;
            }

            Fill(Busy(() => _read(SetName)));
        }

        /// <summary>
        /// Rebuilds the table out of a set's contents. Everything is checked from the start, and that
        /// is not the "an empty filter does not select everything" rule being broken: the set is the
        /// user's own list, assembled by hand, and inserting it whole is what the button is for.
        /// Nothing is deleted by a check mark either — the project's own schedule only ever goes on an
        /// explicit "Replace", which is never the default.
        /// </summary>
        private void Fill(ScheduleSetScan scan)
        {
            foreach (var row in _rows)
                row.PropertyChanged -= OnRowChanged;

            _rows.Clear();

            var action = DefaultAction;

            foreach (var info in scan == null ? new List<ScheduleInfo>() : scan.Schedules)
            {
                int sheets;
                var conflict = _existing.TryGetValue(info.Name, out sheets);

                var row = new ScheduleRow(info, conflict ? sheets : 0, conflict, action);
                row.PropertyChanged += OnRowChanged;

                _rows.Add(row);
            }

            _setPath.Text = scan == null || scan.FilePath.Length == 0 ? string.Empty : scan.FilePath;
            _setPath.ToolTip = _setPath.Text;

            Revalidate();

            if (scan != null && scan.Failures.Count > 0)
                Complain("Could not read the set in full.", scan.Failures);
        }

        private ScheduleAction DefaultAction
        {
            get
            {
                var value = _defaultActionBox.SelectedValue;
                return value is ScheduleAction ? (ScheduleAction)value : ScheduleAction.Skip;
            }
        }

        private void ApplyDefaultAction()
        {
            var action = DefaultAction;

            _settingMany = true;

            foreach (var row in _rows.Where(row => row.HasConflict))
                row.Action = action;

            _settingMany = false;

            Revalidate();
        }

        // ───────────────────────────── filling the set ─────────────────────────────

        private void OnFromFile(object sender, RoutedEventArgs e)
        {
            Picked(ModelPicker.Files(this, false).FirstOrDefault());
        }

        private void OnFromServer(object sender, RoutedEventArgs e)
        {
            Picked(ModelPicker.Server(this, WindowTitle, _linkPreferences, Nothing).FirstOrDefault());
        }

        private void OnFromCloud(object sender, RoutedEventArgs e)
        {
            Picked(ModelPicker.Cloud(this, WindowTitle, Nothing).FirstOrDefault());
        }

        private void OnFromGuid(object sender, RoutedEventArgs e)
        {
            var region = _preferences.Model != null ? _preferences.Model.Region : string.Empty;
            Picked(ModelPicker.CloudByGuid(this, region).FirstOrDefault());
        }

        /// <summary>
        /// A model chosen in a picker. The check for null is not belt and braces: a cancelled picker
        /// hands back nothing, and "nothing" means "the open project" to <see cref="Capture"/> — so
        /// without it, closing the file dialog would quietly take the schedules out of the model the
        /// user happens to be standing in.
        /// </summary>
        private void Picked(LinkEntry entry)
        {
            if (entry == null)
                return;

            Capture(entry);
        }

        /// <summary>Nothing is greyed out in the model trees here: a set is not a list of links.</summary>
        private static HashSet<string> Nothing()
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Puts schedules out of a model into the set. A null entry means the open project — the
        /// fastest path, and the one the button is built around.
        /// </summary>
        private void Capture(LinkEntry entry)
        {
            if (SetName.Length == 0)
            {
                MessageBox.Show(this, "Choose a set in the list or type in a name for a new one.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var known = new HashSet<string>(_rows.Select(row => row.Name), StringComparer.CurrentCultureIgnoreCase);

            var scan = Busy(() => _capture(SetName, entry, found => Choose(found, entry, known)));

            // Null means the user backed out of the choice: nothing was done, and nothing is reported.
            if (scan == null)
                return;

            if (entry != null && scan.Added.Count > 0)
                _preferences.Model = entry;

            ReloadSetNames();
            Fill(scan);

            if (scan.Added.Count > 0)
            {
                MessageBox.Show(this,
                    "Put into the set \"" + SetName + "\": " + scan.Added.Count + ".\n\n" +
                    Preview(scan.Added),
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (scan.Failures.Count == 0)
            {
                MessageBox.Show(this, "Nothing was put into the set.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// The choice itself, shown while the source model is already open behind the dialog. The wait
        /// cursor is taken off for the duration: the user is being asked something, not made to wait.
        /// </summary>
        private IReadOnlyList<ScheduleInfo> Choose(IReadOnlyList<ScheduleInfo> found, LinkEntry entry, ISet<string> known)
        {
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = null;

                var window = new ScheduleChooserWindow(found, entry == null ? "the open project" : entry.Name, known)
                {
                    Owner = this
                };

                return window.ShowDialog() == true ? window.Selected : null;
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            var marked = _rows.Where(row => row.IsSelected).Select(row => row.Name).ToList();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "Check the schedules to throw out of the set.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(
                this,
                "Throw " + marked.Count + " schedule(s) out of the set \"" + SetName + "\"?\n\n" +
                Preview(marked) + "\n\n" +
                "The project itself is not touched — only the saved set changes.",
                WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            var scan = Busy(() => _remove(SetName, marked));
            Fill(scan);
        }

        private void OnDeleteSet(object sender, RoutedEventArgs e)
        {
            if (SetName.Length == 0)
                return;

            var answer = MessageBox.Show(this,
                "Delete the whole set \"" + SetName + "\" from the Windows profile?\n\n" +
                "The project itself is not touched.",
                WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            try
            {
                ScheduleLibrary.Delete(SetName);
                ReloadSetNames();

                _setBox.Text = string.Empty;
                Fill(null);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not delete the set.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ───────────────────────────── check marks and state ─────────────────────────────

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            _settingMany = true;

            foreach (var row in _rows)
                row.IsSelected = value;

            _settingMany = false;

            Revalidate();
        }

        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<ScheduleRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);

            _settingMany = true;

            foreach (var row in rows)
                row.IsSelected = value;

            _settingMany = false;

            Revalidate();
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space)
                return;

            ToggleSelectedRows();
            e.Handled = true;
        }

        private void OnRowChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_settingMany)
                return;

            if (e.PropertyName == nameof(ScheduleRow.IsSelected) || e.PropertyName == nameof(ScheduleRow.Action))
                Revalidate();
        }

        /// <summary>
        /// Works out what will become of every row and writes it into the "State" column.
        ///
        /// The free name for a copy is looked for against the project's names **and** against the
        /// names the rows above have already claimed: two copies inserted in one go must not both aim
        /// for "Doors (2)".
        /// </summary>
        private void Revalidate()
        {
            var claimed = new HashSet<string>(_existing.Keys, StringComparer.CurrentCultureIgnoreCase);

            foreach (var row in _rows)
            {
                row.ResultName = row.Name;
                row.IsWarning = false;

                if (!row.IsSelected)
                {
                    row.StatusText = string.Empty;
                    continue;
                }

                if (!row.HasConflict)
                {
                    claimed.Add(row.Name);
                    row.StatusText = "Will be added";
                    continue;
                }

                switch (row.Action)
                {
                    case ScheduleAction.Replace:
                        row.IsWarning = true;
                        row.StatusText = "The project's own will be deleted and this one put in its place" +
                                         (row.SheetCount > 0
                                             ? " — it is placed on " + row.SheetCount + " sheet(s), and the new one goes back onto them"
                                             : string.Empty);
                        break;

                    case ScheduleAction.AddCopy:
                        row.ResultName = FreeName(row.Name, claimed);
                        claimed.Add(row.ResultName);
                        row.StatusText = "Will be added as \"" + row.ResultName + "\"";
                        break;

                    default:
                        row.StatusText = "Already in the project — will be skipped";
                        break;
                }
            }

            UpdateSummary();
        }

        /// <summary>The first free "name (2)", "name (3)" — the way Revit itself numbers a duplicated view.</summary>
        private static string FreeName(string name, ICollection<string> claimed)
        {
            for (var index = 2; index < 1000; index++)
            {
                var candidate = name + " (" + index + ")";
                if (!claimed.Contains(candidate))
                    return candidate;
            }

            return name + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")";
        }

        /// <summary>The rows that will really reach the project: a checked "Skip" changes nothing.</summary>
        private List<ScheduleRow> Marked()
        {
            return _rows
                .Where(row => row.IsSelected && (!row.HasConflict || row.Action != ScheduleAction.Skip))
                .ToList();
        }

        private void UpdateSummary()
        {
            var marked = Marked();
            var replaced = marked.Count(row => row.HasConflict && row.Action == ScheduleAction.Replace);
            var skipped = _rows.Count(row => row.IsSelected && row.HasConflict && row.Action == ScheduleAction.Skip);

            if (_rows.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = SetName.Length == 0
                    ? "No set is chosen. Type in a name and take the schedules from a model — the open one, say."
                    : "The set \"" + SetName + "\" is empty: take the schedules from a model.";
            }
            else if (marked.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "In the set: " + _rows.Count + ". Nothing will be inserted." +
                               (skipped > 0 ? " Already in the project: " + skipped + "." : string.Empty);
            }
            else
            {
                _status.Foreground = Brushes.DarkGreen;
                _status.Text = "Will be inserted: " + marked.Count + " of " + _rows.Count + "." +
                               (replaced > 0 ? " Of them replacing the project's own: " + replaced + "." : string.Empty) +
                               (skipped > 0 ? " Skipped as already there: " + skipped + "." : string.Empty);
            }

            _insertButton.IsEnabled = marked.Count > 0;
            _insertButton.Content = marked.Count > 0 ? "Insert (" + marked.Count + ")" : "Insert";

            _syncingSelectAll = true;
            _selectAll.IsChecked = _rows.Count == 0 || !_rows.Any(row => row.IsSelected)
                ? false
                : _rows.All(row => row.IsSelected) ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        // ───────────────────────────── actions ─────────────────────────────

        private void OnInsert(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "Nothing will be inserted: every checked schedule is already in the project " +
                                      "and is set to be skipped.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var replaced = marked.Where(row => row.HasConflict && row.Action == ScheduleAction.Replace).ToList();

            if (replaced.Count > 0)
            {
                var sheets = replaced.Sum(row => row.SheetCount);

                var answer = MessageBox.Show(
                    this,
                    "Insert " + marked.Count + " schedule(s)?\n\n" +
                    "Of them, replacing the project's own: " + replaced.Count +
                    (sheets > 0 ? ", placed on " + sheets + " sheet(s) in all" : string.Empty) + ":\n" +
                    Preview(replaced.Select(row => row.Name).ToList()) + "\n\n" +
                    "The project's own schedules are deleted, and the ones out of the set take their place — " +
                    "on the same sheets. Everything they were sorted and filtered by goes with them.\n" +
                    "This can be undone with a single \"Undo\" (Ctrl+Z) in Revit.",
                    WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

                if (answer != MessageBoxResult.Yes)
                    return;
            }

            Selected = marked;
            DialogResult = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            _preferences.Set = SetName;
            _preferences.Action = DefaultAction;
            _preferences.Save();

            // The Revit Server names typed in here are the same ones "Link Manager" asks for —
            // they should not have to be typed twice.
            _linkPreferences.Save();

            base.OnClosed(e);
        }

        // ───────────────────────────── odds and ends ─────────────────────────────

        /// <summary>
        /// Long Revit work under a wait cursor. The dialog is modal and Revit is waiting anyway, so
        /// there is nothing to gain from another thread — and the Revit API may not be touched from one.
        /// </summary>
        private T Busy<T>(Func<T> work)
        {
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                return work();
            }
            catch (Exception exception)
            {
                // The cursor goes back before the dialog: a message box under a wait cursor reads as
                // "still working", and the work has in fact stopped.
                Mouse.OverrideCursor = cursor;

                MessageBox.Show(this, exception.Message, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return default(T);
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }
        }

        private void Complain(string title, IReadOnlyList<string> failures)
        {
            MessageBox.Show(this, title + "\n\n" + Preview(failures), WindowTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static string Preview(IReadOnlyList<string> lines)
        {
            const int limit = 12;
            var shown = string.Join("\n", lines.Take(limit).Select(line => "• " + line));

            return lines.Count > limit ? shown + "\n… and " + (lines.Count - limit) + " more" : shown;
        }

        /// <summary>An item of the clash drop-down: the value itself plus its caption.</summary>
        private sealed class ActionOption
        {
            public ActionOption(ScheduleAction value, string caption)
            {
                Value = value;
                Caption = caption;
            }

            public ScheduleAction Value { get; }
            public string Caption { get; }
        }
    }
}
