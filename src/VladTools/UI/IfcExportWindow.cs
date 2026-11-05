using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// The "Export IFC" window: the models (<see cref="ModelListPanel"/> — a folder in any store, files,
    /// Revit Server, BIM360, a saved set), the folder the .ifc files go into, and the choices Revit's own
    /// IFC export dialog asks for, made once for the whole list.
    ///
    /// Nothing in any model is changed, so the only confirmation is about the folder: the files already
    /// there under the names this run will write are named before they are replaced.
    ///
    /// The window knows nothing about the Revit API: it hands back the checked models and an
    /// <see cref="IfcExportRequest"/>.
    /// </summary>
    internal sealed class IfcExportWindow : Window
    {
        private const string WindowTitle = "Export IFC";

        /// <summary>What stands in the file dialog's name box when the folder is being chosen — not a file anybody has.</summary>
        private const string FolderPlaceholder = "Choose this folder";

        private readonly IfcExportPreferences _preferences;
        private readonly LinkPreferences _linkPreferences;

        private readonly ModelListPanel _models;
        private readonly TextBox _folderBox;
        private readonly ComboBox _schemaBox;
        private readonly CheckBox _byViewBox;
        private readonly TextBox _viewBox;
        private readonly CheckBox _revitSetsBox;
        private readonly CheckBox _materialSetsBox;
        private readonly CheckBox _quantitiesBox;
        private readonly CheckBox _splitBox;
        private readonly TextBlock _status;
        private readonly Button _runButton;

        /// <summary>The models the user ran the export on, in the table's order.</summary>
        public IReadOnlyList<BatchModelRow> Selected { get; private set; } = new List<BatchModelRow>();

        /// <summary>What every model is exported with.</summary>
        public IfcExportRequest Request { get; private set; } = new IfcExportRequest();

        public IfcExportWindow(IfcExportPreferences preferences, LinkPreferences linkPreferences)
        {
            _preferences = preferences ?? new IfcExportPreferences();
            _linkPreferences = linkPreferences ?? new LinkPreferences();

            Title = WindowTitle;
            Width = 980;
            Height = 680;
            MinWidth = 720;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _models = new ModelListPanel(WindowTitle, _linkPreferences, _preferences.Set);

            _folderBox = new TextBox
            {
                Text = _preferences.Folder ?? string.Empty,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "The folder the .ifc files go into, one per model, named after the model.\n" +
                          "A folder that does not exist yet is made."
            };
            _folderBox.TextChanged += (s, e) => UpdateSummary();

            _schemaBox = new ComboBox { MinWidth = 260, VerticalAlignment = VerticalAlignment.Center };
            foreach (var schema in IfcSchemaText.All)
                _schemaBox.Items.Add(IfcSchemaText.Caption(schema));
            _schemaBox.SelectedIndex = Math.Max(0, Array.IndexOf(IfcSchemaText.All, _preferences.Schema));

            _byViewBox = new CheckBox
            {
                Content = "Export only what this 3D view shows:",
                IsChecked = _preferences.ByView,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Revit's \"Export only elements visible in view\": the view's visibility settings decide what goes into the\n" +
                          "file — hidden categories, worksets, filters, the section box. A model without a 3D view of this name\n" +
                          "is not exported at all (the report names its 3D views), rather than exported whole."
            };
            _byViewBox.Checked += (s, e) => UpdateSummary();
            _byViewBox.Unchecked += (s, e) => UpdateSummary();

            _viewBox = new TextBox
            {
                Text = _preferences.ViewName ?? string.Empty,
                MinWidth = 220,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "The 3D view's name, the same in every model; case does not matter."
            };
            _viewBox.TextChanged += (s, e) => UpdateSummary();

            _revitSetsBox = new CheckBox
            {
                Content = "Export Revit property sets",
                IsChecked = _preferences.RevitPropertySets,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip = "Every Revit parameter of an element as property sets of their own, besides the IFC common ones.\n" +
                          "Bigger files; off in Revit's own setups."
            };

            // Revit's IFC export setup words it exactly so (Property Sets tab), and the exporter is passed
            // the same option its own dialog passes — see IfcExport.Options.
            _materialSetsBox = new CheckBox
            {
                Content = "Export material property sets",
                IsChecked = _preferences.MaterialPropertySets,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip = "Property sets on the IFC materials themselves — common, thermal, mechanical and the like —\n" +
                          "filled from the Revit materials. Off in Revit's own setups."
            };

            _quantitiesBox = new CheckBox
            {
                Content = "Export base quantities",
                IsChecked = _preferences.BaseQuantities,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip = "IFC's standard quantities — lengths, areas, volumes — for the elements Revit supports them for."
            };

            _splitBox = new CheckBox
            {
                Content = "Split walls and columns by level",
                IsChecked = _preferences.SplitWallsAndColumns,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip = "A wall or a column through several levels is exported as one piece per level."
            };

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _runButton = new Button
            {
                Content = "Export…",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false,
                ToolTip = "Opens every checked model in turn and writes its .ifc file into the folder above."
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
            UpdateSummary();
        }

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // folder
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // models
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // options
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "Each checked model is opened in turn — detached from its central file, with every workset open, so " +
                       "nothing is locked and nothing is left out — exported to IFC with the settings below, and closed " +
                       "without saving: the models themselves are never changed. A model already open in Revit is exported " +
                       "as it stands. The files are named after the models. Revit is busy until the whole list is done; " +
                       "the full report is written as it goes.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var folder = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            folder.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            folder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            folder.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = "Into the folder:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(label, 0);
            folder.Children.Add(label);

            Grid.SetColumn(_folderBox, 1);
            folder.Children.Add(_folderBox);

            var browse = new Button
            {
                Content = "Browse…",
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Pick the folder the .ifc files go into."
            };
            browse.Click += OnBrowse;
            Grid.SetColumn(browse, 2);
            folder.Children.Add(browse);

            Grid.SetRow(folder, 1);
            root.Children.Add(folder);

            Grid.SetRow(_models, 2);
            root.Children.Add(_models);

            var options = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

            var version = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            version.Children.Add(new TextBlock { Text = "IFC version:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            version.Children.Add(_schemaBox);
            options.Children.Add(version);

            var view = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            view.Children.Add(_byViewBox);
            view.Children.Add(_viewBox);
            options.Children.Add(view);

            options.Children.Add(_revitSetsBox);
            options.Children.Add(_materialSetsBox);
            options.Children.Add(_quantitiesBox);
            options.Children.Add(_splitBox);

            Grid.SetRow(options, 3);
            root.Children.Add(options);

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

            Grid.SetRow(bottom, 4);
            root.Children.Add(bottom);

            return root;
        }

        private string Folder => (_folderBox.Text ?? string.Empty).Trim().Trim('"');

        private bool ByView => _byViewBox.IsChecked == true;

        private string ViewName => (_viewBox.Text ?? string.Empty).Trim();

        private IfcSchema Schema => _schemaBox.SelectedIndex >= 0 && _schemaBox.SelectedIndex < IfcSchemaText.All.Length
            ? IfcSchemaText.All[_schemaBox.SelectedIndex]
            : IfcSchema.Ifc2x3CoordinationView2;

        private void UpdateSummary()
        {
            _viewBox.IsEnabled = ByView;

            var total = _models.Rows.Count;
            var marked = _models.Marked().Count;
            var problem = string.Empty;

            if (total == 0)
                problem = "Add the models to export — a whole folder with \"Folder…\", files, or a saved set.";
            else if (marked == 0)
                problem = "In the list: " + total + ". Nothing is checked.";
            else if (Folder.Length == 0)
                problem = "Choose the folder the .ifc files go into.";
            else if (!IsPath(Folder))
                problem = "\"" + Folder + "\" is not a folder path.";
            else if (ByView && ViewName.Length == 0)
                problem = "Type the name of the 3D view to export, or clear \"Export only what this 3D view shows\".";

            if (problem.Length > 0)
            {
                _status.Foreground = total > 0 && marked > 0 ? Brushes.Firebrick : SystemColors.GrayTextBrush;
                _status.Text = problem;
            }
            else
            {
                _status.Foreground = Brushes.DarkGreen;
                _status.Text = "Will be exported: " + marked + " of " + total + ".";
            }

            _runButton.IsEnabled = problem.Length == 0;
            _runButton.Content = marked > 0 ? "Export (" + marked + ")…" : "Export…";
        }

        private static bool IsPath(string folder)
        {
            try
            {
                return Path.IsPathRooted(folder) && Path.GetFullPath(folder).Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// WPF has no folder dialog in the .NET Framework the 2022 and 2024 builds run on, so the file dialog
        /// answers with a folder — the same trick as <see cref="ModelPicker.PickFileFolder"/>, with the .ifc
        /// files already in the folder in view.
        /// </summary>
        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open the folder the IFC files go into and press \"Open\"",
                Filter = "IFC files (*.ifc)|*.ifc|All files (*.*)|*.*",
                FileName = FolderPlaceholder,
                CheckFileExists = false,
                ValidateNames = false
            };

            if (Directory.Exists(Folder))
                dialog.InitialDirectory = Folder;

            if (dialog.ShowDialog(this) != true)
                return;

            var folder = Directory.Exists(dialog.FileName) ? dialog.FileName : Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                _folderBox.Text = folder;
        }

        private void OnRun(object sender, RoutedEventArgs e)
        {
            var marked = _models.Marked();
            if (marked.Count == 0)
                return;

            var folder = Path.GetFullPath(Folder);

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "The folder could not be made:\n\n" + folder + "\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // The names the run will write — worked out the same way the run works them out.
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existing = marked
                .Select(row => IfcExport.FileName(row.Entry.Name, taken))
                .Where(name => File.Exists(Path.Combine(folder, name)))
                .ToList();

            if (existing.Count > 0)
            {
                const int limit = 12;
                var names = string.Join("\n", existing.Take(limit).Select(name => "• " + name)) +
                            (existing.Count > limit ? "\n… and " + (existing.Count - limit) + " more" : string.Empty);

                var answer = MessageBox.Show(this,
                    existing.Count + " of the files this export writes are already in the folder, and will be replaced:\n\n" + names +
                    "\n\nA model that fails to export leaves its old file as it is.\n\nExport?",
                    WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

                if (answer != MessageBoxResult.Yes)
                    return;
            }

            Selected = marked;
            Request = new IfcExportRequest
            {
                Folder = folder,
                Schema = Schema,
                ViewName = ByView ? ViewName : string.Empty,
                RevitPropertySets = _revitSetsBox.IsChecked == true,
                MaterialPropertySets = _materialSetsBox.IsChecked == true,
                BaseQuantities = _quantitiesBox.IsChecked == true,
                SplitWallsAndColumns = _splitBox.IsChecked == true
            };

            DialogResult = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            _preferences.Folder = Folder;
            _preferences.Schema = Schema;
            _preferences.ByView = ByView;
            _preferences.ViewName = ViewName;
            _preferences.RevitPropertySets = _revitSetsBox.IsChecked == true;
            _preferences.MaterialPropertySets = _materialSetsBox.IsChecked == true;
            _preferences.BaseQuantities = _quantitiesBox.IsChecked == true;
            _preferences.SplitWallsAndColumns = _splitBox.IsChecked == true;
            _preferences.Set = _models.SetName;

            // The list is remembered as it stands, check marks aside — the same rule as in the other batches:
            // the models are the same next time, which of them to export is not.
            _preferences.Models.Clear();
            _preferences.Models.AddRange(_models.Rows.Select(row => row.Entry));

            _preferences.Save();

            // The server names typed in here belong to the whole add-in, not to this window.
            _linkPreferences.Save();

            base.OnClosed(e);
        }
    }
}
