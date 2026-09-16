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
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// The "Parameter Sets" window: a saved bundle of shared parameters on the left of the buttons —
    /// name, GUID, instance/type, categories, parameter group, "varies across groups" — and what will
    /// happen to each of them in the open project on the right.
    ///
    /// Two jobs meet here, deliberately not split into two buttons, the same reasoning as
    /// <c>ScheduleLibraryWindow</c>: building the set (from a shared parameter file) and applying it
    /// (to the open project). A set is built once and carried into every project after.
    ///
    /// The window knows nothing about the Revit API: reading the shared parameter file and comparing a
    /// row against the open project are both handed in as call-backs — the same pattern as
    /// <c>DeleteProjectParametersWindow</c>'s family scan and <c>ScheduleLibraryWindow</c>'s capture.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class ParameterSetWindow : Window
    {
        private const string WindowTitle = "Parameter Sets";

        private readonly IReadOnlyList<CategoryInfo> _categories;
        private readonly IReadOnlyDictionary<string, string> _categoryLabels;
        private readonly IReadOnlyList<ParameterGroupInfo> _groups;

        private readonly Func<IReadOnlyList<ParameterEntry>, IReadOnlyList<ParameterStatusInfo>> _describeStatus;
        private readonly Func<string, IReadOnlyList<SharedParameterInfo>> _readSharedFile;
        private readonly Func<string> _currentSharedFile;

        private readonly ParameterSetPreferences _preferences;

        private readonly ObservableCollection<ParameterSetRow> _rows = new ObservableCollection<ParameterSetRow>();
        private readonly List<BindingOption> _bindingOptions;

        private readonly ComboBox _setBox;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _applyButton;
        private readonly Button _copySettingsButton;
        private readonly Button _pasteSettingsButton;

        private bool _loadingSets;
        private bool _syncingSelectAll;
        private bool _settingMany;

        /// <summary>
        /// What "Copy settings" last captured — binding, categories, group and "vary by group", but
        /// never the name or the GUID: those stay each row's own identity. <c>null</c> until the
        /// button is used once.
        /// </summary>
        private RowSettings _clipboard;

        /// <summary>The rows the user confirmed for applying, each with a non-empty set of categories.</summary>
        public IReadOnlyList<ParameterSetRow> Selected { get; private set; } = new List<ParameterSetRow>();

        /// <summary>The set the parameters are taken from — the command saves and reports against this name.</summary>
        public string SetName => (_setBox.Text ?? string.Empty).Trim();

        /// <summary>
        /// The shared parameter file this set was built from. The command needs it to create a
        /// parameter the project does not have yet: the definitions live only in that file, and it
        /// need not be the one Revit itself is currently pointed at.
        /// </summary>
        public string SharedParameterFile => _preferences.SharedParameterFile ?? string.Empty;

        /// <param name="categories">Every bindable category of the open project.</param>
        /// <param name="groups">Every parameter group Revit offers, for the "Group" column.</param>
        /// <param name="describeStatus">Compares a batch of entries against the open project.</param>
        /// <param name="readSharedFile">Reads a shared parameter file's definitions; throws on failure.</param>
        /// <param name="currentSharedFile">The shared parameter file Revit is already configured with.</param>
        /// <param name="projectName">The open project — the caption above the table.</param>
        public ParameterSetWindow(
            IReadOnlyList<CategoryInfo> categories,
            IReadOnlyList<ParameterGroupInfo> groups,
            Func<IReadOnlyList<ParameterEntry>, IReadOnlyList<ParameterStatusInfo>> describeStatus,
            Func<string, IReadOnlyList<SharedParameterInfo>> readSharedFile,
            Func<string> currentSharedFile,
            string projectName)
        {
            _categories = categories ?? new List<CategoryInfo>();
            _categoryLabels = _categories.ToDictionary(c => c.BuiltInName, c => c.DisplayName, StringComparer.OrdinalIgnoreCase);
            _groups = groups ?? new List<ParameterGroupInfo>();
            _describeStatus = describeStatus;
            _readSharedFile = readSharedFile;
            _currentSharedFile = currentSharedFile;

            _bindingOptions = new List<BindingOption>
            {
                new BindingOption(ParameterBindingKind.Instance, "Instance"),
                new BindingOption(ParameterBindingKind.Type, "Type")
            };

            _preferences = ParameterSetPreferences.Load();

            Title = WindowTitle;
            Width = 1040;
            Height = 640;
            MinWidth = 760;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _setBox = new ComboBox
            {
                Width = 220,
                IsEditable = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "A saved bundle of shared parameters. It lies in the Windows profile and is offered in\n" +
                    "every project after — build it once, apply it everywhere.\n" +
                    "The name can be chosen from the list or typed in: a new name makes a new set."
            };
            _setBox.SelectionChanged += (s, e) => { if (!_loadingSets) ReloadSet(); };

            _selectAll = new CheckBox
            {
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every parameter in the set"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _applyButton = new Button
            {
                Content = "Apply to Project",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _applyButton.Click += OnApply;

            _copySettingsButton = Small("Copy settings",
                "Copies binding, categories, group and \"vary by group\" from the one row highlighted below.",
                OnCopySettings);
            _copySettingsButton.IsEnabled = false;

            _pasteSettingsButton = Small("Paste settings",
                "Applies the copied settings to every row highlighted below (click, Ctrl+click, Shift+click)." +
                "\nThe name and the GUID of each row are left untouched.",
                OnPasteSettings);
            _pasteSettingsButton.IsEnabled = false;

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
                : ParameterSetLibrary.Names().FirstOrDefault() ?? string.Empty;
            _loadingSets = false;

            ReloadSet();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(string projectName, Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // set
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "Parameters are gathered once into a named set and carried into every project after. The\n" +
                       "checked ones go into " +
                       (string.IsNullOrEmpty(projectName) ? "the open project" : "\"" + projectName + "\"") +
                       " as a single operation — one Ctrl+Z undoes all of it. A parameter already bound in the\n" +
                       "project is checked against the set and brought in line with it, rather than added twice.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var sets = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            sets.Children.Add(Caption("Set:"));
            sets.Children.Add(_setBox);
            sets.Children.Add(Small("Open", "Reads the set named on the left.", (s, e) => ReloadSet()));
            sets.Children.Add(Small("Save set", "Writes the table as it stands now to the Windows profile.", OnSaveSet));
            sets.Children.Add(Small("Delete set", "Deletes the whole set from the Windows profile.", OnDeleteSet));
            sets.Children.Add(Small("Add from shared file…",
                "Picks definitions out of a shared parameter file and adds them to the working set below.",
                OnAddFromSharedFile));
            sets.Children.Add(Small("Remove from set", "Throws the checked parameters out of the working set.", OnRemoveFromSet));
            sets.Children.Add(_copySettingsButton);
            sets.Children.Add(_pasteSettingsButton);
            Grid.SetRow(sets, 1);
            root.Children.Add(sets);

            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);

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

            Grid.SetRow(bottom, 3);
            root.Children.Add(bottom);

            return root;
        }

        private static TextBlock Caption(string text)
        {
            return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
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

            grid.Columns.Add(ReadOnlyText("Name", nameof(ParameterSetRow.Name), new DataGridLength(1.2, DataGridLengthUnitType.Star)));
            grid.Columns.Add(ReadOnlyText("GUID", nameof(ParameterSetRow.GuidText), new DataGridLength(230)));

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Binding",
                Width = new DataGridLength(100),
                CellTemplate = GridBuilder.ComboTemplate(_bindingOptions, nameof(BindingOption.Caption), nameof(BindingOption.Value), nameof(ParameterSetRow.Binding))
            });

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Vary by group",
                Width = new DataGridLength(90),
                CellTemplate = GridBuilder.CheckBoxTemplate(nameof(ParameterSetRow.VariesAcrossGroups), nameof(ParameterSetRow.CanVary))
            });

            grid.Columns.Add(CategoriesColumn());

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Group",
                Width = new DataGridLength(160),
                CellTemplate = GridBuilder.ComboTemplate(_groups, nameof(ParameterGroupInfo.Label), nameof(ParameterGroupInfo.TypeId), nameof(ParameterSetRow.GroupTypeId))
            });

            var status = ReadOnlyText("State", nameof(ParameterSetRow.StatusText), new DataGridLength(1.4, DataGridLengthUnitType.Star));
            status.MinWidth = 180;
            status.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(ParameterSetRow.StatusBrush))));
            grid.Columns.Add(status);

            grid.MouseDoubleClick += OnGridDoubleClick;
            grid.PreviewKeyDown += OnGridKeyDown;
            grid.SelectionChanged += (s, e) => UpdateCopyPasteButtons();

            return grid;
        }

        /// <summary>
        /// A double click toggles the row's check mark — except on the cells that are controls in
        /// their own right. Half this table is live: a double click on the categories button, a
        /// drop-down or the "vary by group" box means the control, not the check mark, and toggling
        /// on top of it would be the row quietly changing under the user's hand.
        /// </summary>
        private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;

            while (source != null)
            {
                if (source is ButtonBase || source is ComboBox)
                    return;

                if (source is DataGrid)
                    break;

                // Not every click starts on a Visual (the text inside a cell is content, not a
                // visual), and VisualTreeHelper throws on those — the logical tree walks both.
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }

            ToggleSelectedRows();
        }

        private static DataGridTextColumn ReadOnlyText(string header, string property, DataGridLength width)
        {
            var column = GridBuilder.TextColumn(header, property, width);
            column.IsReadOnly = true;

            return column;
        }

        /// <summary>
        /// The "Categories" cell: a button showing how many are chosen, opening
        /// <see cref="CategoryPickerWindow"/> on click — a checklist does not fit in a grid cell, the
        /// same reasoning that gave "Rename Nested" and "Delete Parameters" their own small dialogs.
        /// </summary>
        private DataGridTemplateColumn CategoriesColumn()
        {
            var button = new FrameworkElementFactory(typeof(Button));
            button.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ParameterSetRow.CategoriesSummary)));
            button.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));
            button.SetValue(Control.PaddingProperty, new Thickness(6, 1, 6, 1));
            button.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left);
            button.AddHandler(ButtonBase.ClickEvent, (RoutedEventHandler)OnChooseCategories);

            return new DataGridTemplateColumn
            {
                Header = "Categories",
                Width = new DataGridLength(180),
                CanUserSort = false,
                CellTemplate = new DataTemplate { VisualTree = button }
            };
        }

        private void OnChooseCategories(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var row = element?.DataContext as ParameterSetRow;
            if (row == null)
                return;

            var picker = new CategoryPickerWindow(_categories, row.Categories) { Owner = this };
            if (picker.ShowDialog() == true)
                row.Categories = picker.Selected;
        }

        // ───────────────────────────── the set ─────────────────────────────

        private void ReloadSetNames()
        {
            var current = _setBox.Text;

            _loadingSets = true;
            _setBox.Items.Clear();

            foreach (var name in ParameterSetLibrary.Names())
                _setBox.Items.Add(name);

            _setBox.Text = current;
            _loadingSets = false;
        }

        private void ReloadSet()
        {
            Fill(SetName.Length == 0 ? new List<ParameterEntry>() : ParameterSetLibrary.Load(SetName));
        }

        private void Fill(IReadOnlyList<ParameterEntry> entries)
        {
            foreach (var row in _rows)
                row.PropertyChanged -= OnRowChanged;

            _rows.Clear();

            foreach (var entry in entries)
                AddRow(entry);

            Revalidate();
        }

        private void AddRow(ParameterEntry entry)
        {
            var row = new ParameterSetRow(entry, _categoryLabels) { IsSelected = true };
            row.PropertyChanged += OnRowChanged;
            _rows.Add(row);
        }

        private void OnSaveSet(object sender, RoutedEventArgs e)
        {
            if (SetName.Length == 0)
            {
                MessageBox.Show(this, "Type in a name for the set.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                ParameterSetLibrary.Save(SetName, _rows.Select(row => row.Entry));
                ReloadSetNames();

                MessageBox.Show(this, "Saved: \"" + SetName + "\" (" + _rows.Count + " parameter(s)).",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not save the set.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnDeleteSet(object sender, RoutedEventArgs e)
        {
            if (SetName.Length == 0)
                return;

            var answer = MessageBox.Show(this,
                "Delete the whole set \"" + SetName + "\" from the Windows profile?\n\nThe project itself is not touched.",
                WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            try
            {
                ParameterSetLibrary.Delete(SetName);
                ReloadSetNames();

                _setBox.Text = string.Empty;
                Fill(new List<ParameterEntry>());
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not delete the set.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnRemoveFromSet(object sender, RoutedEventArgs e)
        {
            var marked = _rows.Where(row => row.IsSelected).ToList();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "Check the parameters to remove from the working set.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                "Remove " + marked.Count + " parameter(s) from the working set?\n\n" +
                "The project itself is not touched — only the table changes; \"Save set\" writes it to disk.",
                WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            foreach (var row in marked)
            {
                row.PropertyChanged -= OnRowChanged;
                _rows.Remove(row);
            }

            UpdateSummary();
        }

        // ───────────────────────────── adding from the shared file ─────────────────────────────

        private void OnAddFromSharedFile(object sender, RoutedEventArgs e)
        {
            var path = _preferences.SharedParameterFile.Length > 0
                ? _preferences.SharedParameterFile
                : SafeCurrentSharedFile();

            IReadOnlyList<SharedParameterInfo> found;

            try
            {
                found = path.Length == 0 ? new List<SharedParameterInfo>() : Busy(() => _readSharedFile(path));
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not read the shared parameter file.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                found = new List<SharedParameterInfo>();
            }

            var known = _rows.Select(row => row.Guid).ToList();
            var picker = new SharedParameterPickerWindow(path, found, known, p => Busy(() => _readSharedFile(p))) { Owner = this };

            if (picker.ShowDialog() != true)
                return;

            _preferences.SharedParameterFile = picker.FilePath;

            var defaultGroup = _groups
                .OrderBy(group => group.Label, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => group.TypeId)
                .FirstOrDefault() ?? string.Empty;

            foreach (var info in picker.Selected)
                AddRow(new ParameterEntry(info.Guid, info.Name, ParameterBindingKind.Instance, false, new List<string>(), defaultGroup));

            Revalidate();

            if (picker.Selected.Count > 0)
            {
                MessageBox.Show(this,
                    "Added to the working set: " + picker.Selected.Count + ".\n\n" +
                    "Choose categories for each before applying, and \"Save set\" to keep them for next time.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private string SafeCurrentSharedFile()
        {
            try
            {
                return _currentSharedFile() ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
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

            UpdateSummary();
        }

        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<ParameterSetRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);

            _settingMany = true;

            foreach (var row in rows)
                row.IsSelected = value;

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

        // ───────────────────────────── copying settings between rows ─────────────────────────────

        /// <summary>
        /// Deliberately separate from the "apply" check boxes: those mean "will be applied", and by
        /// default every row starts checked (<see cref="AddRow"/>), so they cannot double as "which
        /// rows to copy into" without emptying the table first. The grid's own row highlight
        /// (<see cref="DataGrid.SelectedItems"/>, already live for <see cref="ToggleSelectedRows"/>)
        /// picks out a source and, separately, targets without touching what will be applied.
        /// </summary>
        private void UpdateCopyPasteButtons()
        {
            _copySettingsButton.IsEnabled = _grid.SelectedItems.Count == 1;
            _pasteSettingsButton.IsEnabled = _clipboard != null && _grid.SelectedItems.Count > 0;
        }

        private void OnCopySettings(object sender, RoutedEventArgs e)
        {
            var row = _grid.SelectedItem as ParameterSetRow;
            if (row == null)
                return;

            _clipboard = new RowSettings(row.Binding, row.VariesAcrossGroups, row.Categories.ToList(), row.GroupTypeId);
            UpdateCopyPasteButtons();
        }

        /// <summary>
        /// Applies the clipboard to every highlighted row in one pass. The four properties are set
        /// through <c>_settingMany</c> — the same guard <see cref="SetAllSelected"/> uses for the
        /// check boxes — so a paste onto many rows revalidates the whole table once, not once per
        /// property per row: an unguarded loop here would call <see cref="_describeStatus"/> (a
        /// round trip into the open project) up to four times for every row pasted into.
        /// </summary>
        private void OnPasteSettings(object sender, RoutedEventArgs e)
        {
            if (_clipboard == null)
                return;

            var targets = _grid.SelectedItems.OfType<ParameterSetRow>().ToList();
            if (targets.Count == 0)
                return;

            _settingMany = true;

            foreach (var row in targets)
            {
                row.Binding = _clipboard.Binding;
                row.VariesAcrossGroups = _clipboard.VariesAcrossGroups;
                row.Categories = _clipboard.Categories;
                row.GroupTypeId = _clipboard.GroupTypeId;
            }

            _settingMany = false;

            Revalidate();
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_settingMany)
                return;

            if (e.PropertyName == nameof(ParameterSetRow.IsSelected))
            {
                UpdateSummary();
                return;
            }

            if (e.PropertyName == nameof(ParameterSetRow.Binding)
                || e.PropertyName == nameof(ParameterSetRow.VariesAcrossGroups)
                || e.PropertyName == nameof(ParameterSetRow.Categories)
                || e.PropertyName == nameof(ParameterSetRow.GroupTypeId))
            {
                Revalidate();
            }
        }

        /// <summary>
        /// Works out what will become of every row: a row with no category chosen cannot be applied at
        /// all (there is nothing to bind it to) and is flagged without asking the command — the rest
        /// are compared against the open project through <see cref="_describeStatus"/>, the one place
        /// this window reaches into Revit, and only through a call-back.
        /// </summary>
        private void Revalidate()
        {
            var toCheck = new List<ParameterSetRow>();

            foreach (var row in _rows)
            {
                if (row.Categories.Count == 0)
                {
                    row.StatusText = "Choose at least one category";
                    row.IsWarning = true;
                }
                else
                {
                    toCheck.Add(row);
                }
            }

            if (toCheck.Count > 0 && _describeStatus != null)
            {
                IReadOnlyList<ParameterStatusInfo> statuses;

                try
                {
                    statuses = _describeStatus(toCheck.Select(row => row.Entry).ToList());
                }
                catch (Exception exception)
                {
                    statuses = toCheck.Select(row => new ParameterStatusInfo("Could not be checked: " + exception.Message, true)).ToList();
                }

                for (var i = 0; i < toCheck.Count; i++)
                {
                    var status = i < statuses.Count ? statuses[i] : new ParameterStatusInfo(string.Empty, false);
                    toCheck[i].StatusText = status.Text;
                    toCheck[i].IsWarning = status.IsWarning;
                }
            }

            UpdateSummary();
        }

        private List<ParameterSetRow> Marked()
        {
            return _rows.Where(row => row.IsSelected && row.Categories.Count > 0).ToList();
        }

        private void UpdateSummary()
        {
            var marked = Marked();
            var warnings = marked.Count(row => row.IsWarning);

            if (_rows.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = SetName.Length == 0
                    ? "No set is chosen. Type in a name and add parameters from a shared parameter file."
                    : "The set \"" + SetName + "\" is empty: add parameters from a shared parameter file.";
            }
            else if (marked.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "In the set: " + _rows.Count + ". Nothing will be applied.";
            }
            else
            {
                _status.Foreground = warnings > 0 ? Brushes.Firebrick : Brushes.DarkGreen;
                _status.Text = "Will be applied: " + marked.Count + " of " + _rows.Count + "." +
                               (warnings > 0 ? " Of them needing attention: " + warnings + "." : string.Empty);
            }

            _applyButton.IsEnabled = marked.Count > 0;
            _applyButton.Content = marked.Count > 0 ? "Apply to Project (" + marked.Count + ")" : "Apply to Project";

            _syncingSelectAll = true;
            _selectAll.IsChecked = _rows.Count == 0 || !_rows.Any(row => row.IsSelected)
                ? false
                : _rows.All(row => row.IsSelected) ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        // ───────────────────────────── applying ─────────────────────────────

        private void OnApply(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "Nothing will be applied: check at least one parameter with categories chosen.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var warnings = marked.Where(row => row.IsWarning).ToList();

            if (warnings.Count > 0)
            {
                var answer = MessageBox.Show(
                    this,
                    "Apply " + marked.Count + " parameter(s)?\n\n" +
                    "Of them, " + warnings.Count + " need attention:\n" +
                    Preview(warnings.Select(row => row.Name + " — " + row.StatusText).ToList()) + "\n\n" +
                    "Where a category is being removed from an already-bound parameter, its values on\n" +
                    "elements of that category are lost. This can be undone with a single \"Undo\" (Ctrl+Z) in Revit.",
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
            _preferences.Save();

            base.OnClosed(e);
        }

        private static string Preview(IReadOnlyList<string> lines)
        {
            const int limit = 12;
            var shown = string.Join("\n", lines.Take(limit).Select(line => "• " + line));

            return lines.Count > limit ? shown + "\n… and " + (lines.Count - limit) + " more" : shown;
        }

        /// <summary>
        /// Long Revit work under a wait cursor. The dialog is modal and Revit is waiting anyway, so
        /// there is nothing to gain from another thread — and the Revit API may not be touched from one.
        /// Exceptions are deliberately not caught here: the callers each decide how to report them.
        /// </summary>
        private T Busy<T>(Func<T> work)
        {
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                return work();
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }
        }

        /// <summary>An item of the "Binding" drop-down: the value itself plus its caption.</summary>
        private sealed class BindingOption
        {
            public BindingOption(ParameterBindingKind value, string caption)
            {
                Value = value;
                Caption = caption;
            }

            public ParameterBindingKind Value { get; }
            public string Caption { get; }
        }

        /// <summary>What "Copy settings" carries between rows — never the name or the GUID, those stay each row's own identity.</summary>
        private sealed class RowSettings
        {
            public RowSettings(ParameterBindingKind binding, bool variesAcrossGroups, IReadOnlyList<string> categories, string groupTypeId)
            {
                Binding = binding;
                VariesAcrossGroups = variesAcrossGroups;
                Categories = categories;
                GroupTypeId = groupTypeId;
            }

            public ParameterBindingKind Binding { get; }
            public bool VariesAcrossGroups { get; }
            public IReadOnlyList<string> Categories { get; }
            public string GroupTypeId { get; }
        }
    }
}
