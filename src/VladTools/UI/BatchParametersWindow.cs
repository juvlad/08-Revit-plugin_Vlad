using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    /// The "Parameters" window: a DiRoots ParaManager Excel file — every parameter in it, checked where it can be
    /// added — and the models they go into (<see cref="ModelListPanel"/>: a folder in any store, files, Revit
    /// Server, BIM360, a saved set).
    ///
    /// The window knows nothing about the Revit API: the file is read through a call-back the command answers,
    /// and the window hands back the checked parameters' GUIDs and the checked models.
    /// </summary>
    internal sealed class BatchParametersWindow : Window
    {
        private const string WindowTitle = "Parameters";

        private readonly BatchParametersPreferences _preferences;
        private readonly LinkPreferences _linkPreferences;
        private readonly Func<string, BatchParameterFileView> _read;

        private readonly ObservableCollection<BatchParameterRow> _rows = new ObservableCollection<BatchParameterRow>();
        private readonly TextBox _fileBox;
        private readonly TextBlock _fileNote;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly ModelListPanel _models;
        private readonly TextBlock _status;
        private readonly Button _runButton;

        private BatchParameterFileView _view;
        private bool _syncingSelectAll;

        /// <summary>The file the checked parameters come from — the one the window last read.</summary>
        public string FilePath { get; private set; } = string.Empty;

        /// <summary>The checked parameters, in the file's order.</summary>
        public IReadOnlyList<Guid> SelectedParameters { get; private set; } = new List<Guid>();

        /// <summary>The checked models, in the table's order.</summary>
        public IReadOnlyList<BatchModelRow> SelectedModels { get; private set; } = new List<BatchModelRow>();

        public BatchParametersWindow(
            BatchParametersPreferences preferences,
            LinkPreferences linkPreferences,
            Func<string, BatchParameterFileView> read)
        {
            _preferences = preferences ?? new BatchParametersPreferences();
            _linkPreferences = linkPreferences ?? new LinkPreferences();
            _read = read;

            Title = WindowTitle;
            Width = 1180;
            Height = 860;
            MinWidth = 820;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _fileBox = new TextBox
            {
                Text = _preferences.File ?? string.Empty,
                IsReadOnly = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "The Excel file DiRoots ParaManager exported the parameters to (\"Export to Excel\")."
            };

            _fileNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };

            _selectAll = new CheckBox
            {
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every parameter that can be added"
            };
            _selectAll.Checked += (s, e) => SetAll(true);
            _selectAll.Unchecked += (s, e) => SetAll(false);

            _grid = BuildGrid();

            _models = new ModelListPanel(WindowTitle, _linkPreferences, _preferences.Set);

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _runButton = new Button
            {
                Content = "Add…",
                MinWidth = 170,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false,
                ToolTip = "Opens every checked model in turn and adds the checked parameters to it."
            };
            _runButton.Click += OnRun;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(closeButton);

            _models.Changed += (s, e) => UpdateSummary();
            _models.Notice += (s, text) =>
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = text;
            };

            _models.Add(_preferences.Models, "model(s) from last time");

            if (_fileBox.Text.Length > 0 && File.Exists(_fileBox.Text))
                Load(_fileBox.Text);
            else
                ShowFileNote("Pick the Excel file DiRoots ParaManager exported the parameters to.", false);

            UpdateSummary();
        }

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // file
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // file note
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // parameters
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // models caption
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // models
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "The parameters of a DiRoots ParaManager Excel file go into every checked model as project parameters — " +
                       "with the file's GUID, type, instance/type, group, categories and \"values can vary by group instance\". " +
                       "Each model is opened in turn with every workset closed: a workshared one as a local copy, synchronised " +
                       "with central at the end; one that is not workshared is saved over. A parameter a model has already gets " +
                       "only what it lacks — nothing is ever unbound or switched. Revit is busy until the whole list is done; " +
                       "the full report is written as it goes.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var file = new Grid();
            file.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            file.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            file.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            file.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = "Parameter file:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(label, 0);
            file.Children.Add(label);

            Grid.SetColumn(_fileBox, 1);
            file.Children.Add(_fileBox);

            var browse = new Button
            {
                Content = "Browse…",
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Pick a ParaManager Excel file (.xlsx, .xlsm)."
            };
            browse.Click += OnBrowse;
            Grid.SetColumn(browse, 2);
            file.Children.Add(browse);

            var reload = new Button
            {
                Content = "Read again",
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Reads the file once more — after it was edited in Excel."
            };
            reload.Click += (s, e) =>
            {
                if (FilePath.Length > 0)
                    Load(FilePath);
            };
            Grid.SetColumn(reload, 3);
            file.Children.Add(reload);

            Grid.SetRow(file, 1);
            root.Children.Add(file);

            Grid.SetRow(_fileNote, 2);
            root.Children.Add(_fileNote);

            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            var modelsCaption = new TextBlock
            {
                Text = "The models they go into:",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 6)
            };
            Grid.SetRow(modelsCaption, 4);
            root.Children.Add(modelsCaption);

            Grid.SetRow(_models, 5);
            root.Children.Add(_models);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_runButton);
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
                ItemsSource = _rows,
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
                RowHeaderWidth = 0
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate(nameof(BatchParameterRow.IsSelected), nameof(BatchParameterRow.CanSelect))
            });

            grid.Columns.Add(GridBuilder.TextColumn("Row", nameof(BatchParameterRow.Row), new DataGridLength(44)));
            grid.Columns.Add(GridBuilder.TextColumn("Parameter", nameof(BatchParameterRow.Name), new DataGridLength(220)));
            grid.Columns.Add(GridBuilder.TextColumn("Type", nameof(BatchParameterRow.TypeText), new DataGridLength(90)));
            grid.Columns.Add(GridBuilder.TextColumn("Instance/Type", nameof(BatchParameterRow.BindingText), new DataGridLength(90)));
            grid.Columns.Add(GridBuilder.TextColumn("Group", nameof(BatchParameterRow.GroupText), new DataGridLength(100)));
            grid.Columns.Add(GridBuilder.TextColumn("Categories", nameof(BatchParameterRow.CategoriesText), new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(GridBuilder.TextColumn("Group instances", nameof(BatchParameterRow.VaryText), new DataGridLength(100)));

            var state = GridBuilder.TextColumn("State", nameof(BatchParameterRow.State), new DataGridLength(300));
            state.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(BatchParameterRow.StateBrush))));
            grid.Columns.Add(state);

            grid.MouseDoubleClick += (s, e) => ToggleSelectedRows();
            grid.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Space)
                    return;

                ToggleSelectedRows();
                e.Handled = true;
            };

            return grid;
        }

        // ───────────────────────────── the file ─────────────────────────────

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "The DiRoots ParaManager Excel file with the parameters",
                Filter = "Excel workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
                CheckFileExists = true
            };

            var folder = SafeFolder(FilePath.Length > 0 ? FilePath : _fileBox.Text);
            if (folder.Length > 0)
                dialog.InitialDirectory = folder;

            if (dialog.ShowDialog(this) == true)
                Load(dialog.FileName);
        }

        private static string SafeFolder(string path)
        {
            try
            {
                var folder = Path.GetDirectoryName(path ?? string.Empty) ?? string.Empty;
                return Directory.Exists(folder) ? folder : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private void Load(string path)
        {
            BatchParameterFileView view;

            var cursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                view = _read(path);
            }
            catch (Exception exception)
            {
                view = new BatchParameterFileView(path) { Error = "the file could not be read: " + exception.Message };
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }

            foreach (var row in _rows)
                row.PropertyChanged -= OnRowChanged;

            _rows.Clear();
            _view = view;
            FilePath = path;
            _fileBox.Text = path;

            foreach (var row in view.Rows)
            {
                row.PropertyChanged += OnRowChanged;
                _rows.Add(row);
            }

            if (view.Error.Length > 0)
            {
                ShowFileNote("Nothing can be taken from this file: " + view.Error, true);
            }
            else
            {
                var blocked = view.Rows.Count(row => !row.CanSelect);
                var text = "In the file: " + view.Rows.Count + " parameter(s)" +
                           (blocked > 0 ? ", " + blocked + " of them cannot be added (red — the row says why)" : string.Empty) + ".";

                if (view.Notes.Count > 0)
                    text += " " + string.Join(" ", view.Notes);

                ShowFileNote(text, false);
            }

            SyncSelectAll();
            UpdateSummary();
        }

        private void ShowFileNote(string text, bool problem)
        {
            _fileNote.Text = text;
            _fileNote.Foreground = problem ? Brushes.Firebrick : SystemColors.GrayTextBrush;
        }

        // ───────────────────────────── check boxes ─────────────────────────────

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(BatchParameterRow.IsSelected))
                return;

            SyncSelectAll();
            UpdateSummary();
        }

        private void SetAll(bool value)
        {
            if (_syncingSelectAll)
                return;

            _syncingSelectAll = true;
            try
            {
                foreach (var row in _rows)
                    row.IsSelected = value;
            }
            finally
            {
                _syncingSelectAll = false;
            }

            SyncSelectAll();
            UpdateSummary();
        }

        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<BatchParameterRow>().Where(row => row.CanSelect).ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);
            foreach (var row in rows)
                row.IsSelected = value;
        }

        private void SyncSelectAll()
        {
            if (_syncingSelectAll)
                return;

            var usable = _rows.Where(row => row.CanSelect).ToList();
            var marked = usable.Count(row => row.IsSelected);

            _syncingSelectAll = true;
            try
            {
                _selectAll.IsChecked = usable.Count == 0 || marked == 0 ? false : marked == usable.Count ? (bool?)true : null;
            }
            finally
            {
                _syncingSelectAll = false;
            }
        }

        // ───────────────────────────── the run ─────────────────────────────

        private List<BatchParameterRow> MarkedParameters()
        {
            return _rows.Where(row => row.CanSelect && row.IsSelected).ToList();
        }

        private void UpdateSummary()
        {
            var parameters = MarkedParameters().Count;
            var total = _models.Rows.Count;
            var models = _models.Marked().Count;
            var problem = string.Empty;

            if (_view == null || FilePath.Length == 0)
                problem = "Pick the ParaManager file with the parameters.";
            else if (_view.Error.Length > 0)
                problem = "The file gave no parameters — pick another one.";
            else if (parameters == 0)
                problem = "No parameter is checked.";
            else if (total == 0)
                problem = "Add the models — a whole folder with \"Folder…\", files, Revit Server, BIM360, or a saved set.";
            else if (models == 0)
                problem = "Models in the list: " + total + ". None is checked.";

            if (problem.Length > 0)
            {
                _status.Foreground = parameters > 0 && models > 0 ? Brushes.Firebrick : SystemColors.GrayTextBrush;
                _status.Text = problem;
            }
            else
            {
                _status.Foreground = Brushes.DarkGreen;
                _status.Text = parameters + " parameter(s) into " + models + " of " + total + " model(s).";
            }

            _runButton.IsEnabled = problem.Length == 0;
            _runButton.Content = problem.Length == 0 ? "Add to " + models + " model(s)…" : "Add…";
        }

        private void OnRun(object sender, RoutedEventArgs e)
        {
            var parameters = MarkedParameters();
            var models = _models.Marked();
            if (parameters.Count == 0 || models.Count == 0)
                return;

            var answer = MessageBox.Show(this,
                "Add " + parameters.Count + " parameter(s) to " + models.Count + " model(s)?\n\n" +
                "Each model is opened in turn with every workset closed — a workshared one as a local copy, synchronised " +
                "with central at the end; one that is not workshared is saved over. A model saved in an older Revit than " +
                "this one is left alone rather than upgraded.\n\n" +
                "A parameter a model has already (by GUID) only gets the categories it lacks and the file's group. Nothing " +
                "is ever unbound, instance and type are never switched, and a parameter whose name the model already uses " +
                "for another parameter is left out of that model.\n\n" +
                "Revit is busy until the whole list is done; the full report is written as it goes.",
                WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

            if (answer != MessageBoxResult.Yes)
                return;

            SelectedParameters = parameters.Select(row => row.Guid).ToList();
            SelectedModels = models;
            DialogResult = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            _preferences.File = FilePath.Length > 0 ? FilePath : _fileBox.Text ?? string.Empty;
            _preferences.Set = _models.SetName;

            // The list is remembered as it stands, check marks aside — the same rule as in the other batches.
            _preferences.Models.Clear();
            _preferences.Models.AddRange(_models.Rows.Select(row => row.Entry));

            _preferences.Save();

            // The server names typed in here belong to the whole add-in, not to this window.
            _linkPreferences.Save();

            base.OnClosed(e);
        }
    }
}
