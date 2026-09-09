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
using System.Windows.Media;
using VladTools.Infrastructure;
// Only this enum, aliased — not a whole using of Autodesk.Revit.DB: that would make Grid/Binding/
// Control in this file ambiguous between WPF and the Revit API (see CLAUDE.md, "WPF without XAML").
using SpatialElementBoundaryLocation = Autodesk.Revit.DB.SpatialElementBoundaryLocation;

namespace VladTools.UI
{
    /// <summary>
    /// The "Auto Dimensions" window: the chain list (kind, offset, dimension type), the room
    /// boundary, the direction, and template handling.
    ///
    /// The window knows almost nothing about the Revit API — an exception (like "Delete Project
    /// Shared Parameters", see CLAUDE.md) is made only for the <c>SpatialElementBoundaryLocation</c>
    /// enum: setting up a separate copy of it just for one enum would be overkill.
    ///
    /// <c>Selection.PickObject</c> cannot be called while a modal window is open, so "Take a
    /// sample…" is not read by the window itself: it closes with the <see cref="WantsSample"/>
    /// flag, the command does the picking in Revit, parses the sample and reopens the window
    /// already filled in (the same trick "Scan Families" uses in DeleteProjectParametersWindow via
    /// a lambda, except here the window itself is recreated).
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class AutoDimensionWindow : Window
    {
        private const string WindowTitle = "Auto Dimensions";

        private static readonly BoundaryOption[] BoundaryOptions =
        {
            new BoundaryOption(SpatialElementBoundaryLocation.CoreBoundary, "By core layer faces"),
            new BoundaryOption(SpatialElementBoundaryLocation.Finish, "By finish"),
            new BoundaryOption(SpatialElementBoundaryLocation.CoreCenter, "By core layer centre"),
            new BoundaryOption(SpatialElementBoundaryLocation.Center, "By wall centre")
        };

        private readonly IReadOnlyList<DimensionTypeInfo> _dimensionTypes;
        private readonly HashSet<string> _dimensionTypeNames;
        private readonly string _defaultTypeName;
        private readonly ObservableCollection<DimensionChainRow> _rows = new ObservableCollection<DimensionChainRow>();
        private readonly ObservableCollection<string> _templateNames;
        private readonly Func<string, DimensionTemplate> _loadTemplate;
        private readonly Action<string, DimensionTemplate> _saveTemplate;

        private readonly ComboBox _boundaryBox;
        private readonly ComboBox _directionBox;
        private readonly CheckBox _removePreviousBox;
        private readonly CheckBox _adjacentThicknessBox;
        private readonly CheckBox _moveSmallTextBox;
        private readonly ComboBox _templateBox;
        private readonly TextBox _templateNameBox;
        private readonly DataGrid _grid;
        private readonly TextBlock _summary;
        private readonly Button _placeButton;

        /// <summary>The user pressed "Take a sample…" — the command needs to pick dimensions and reopen the window.</summary>
        public bool WantsSample { get; private set; }

        /// <summary>The table's current contents — read regardless of how the window was closed.</summary>
        public IReadOnlyList<DimensionChainRow> Rows => _rows;

        public SpatialElementBoundaryLocation Boundary =>
            (_boundaryBox.SelectedItem as BoundaryOption)?.Value ?? SpatialElementBoundaryLocation.CoreBoundary;

        public bool Outward => _directionBox.SelectedIndex == 1;

        public bool RemovePrevious => _removePreviousBox.IsChecked == true;

        /// <summary>The end ticks of a chain pick up the thickness of the adjoining wall.</summary>
        public bool IncludeAdjacentThickness => _adjacentThicknessBox.IsChecked == true;

        /// <summary>Labels that do not fit between their ticks are pulled out onto a leader.</summary>
        public bool MoveSmallText => _moveSmallTextBox.IsChecked == true;

        /// <summary>The template name last chosen or typed in — for the window settings.</summary>
        public string TemplateName => (_templateBox.SelectedItem as string) ?? (_templateNameBox.Text ?? string.Empty).Trim();

        public AutoDimensionWindow(
            int roomCount,
            IReadOnlyList<DimensionTypeInfo> dimensionTypes,
            string defaultDimensionTypeName,
            IReadOnlyList<string> templateNames,
            IReadOnlyList<DimensionChainRow> initialRows,
            SpatialElementBoundaryLocation boundary,
            bool outward,
            bool removePrevious,
            bool includeAdjacentThickness,
            bool moveSmallText,
            string lastTemplateName,
            Func<string, DimensionTemplate> loadTemplate,
            Action<string, DimensionTemplate> saveTemplate)
        {
            _dimensionTypes = dimensionTypes ?? new List<DimensionTypeInfo>();
            _dimensionTypeNames = new HashSet<string>(_dimensionTypes.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            _templateNames = new ObservableCollection<string>(templateNames ?? new List<string>());
            _loadTemplate = loadTemplate;
            _saveTemplate = saveTemplate;

            // The "default" type is the one Revit itself would pick, not the alphabetically first
            // one — otherwise a new row would be born with a random project type.
            _defaultTypeName = Known(defaultDimensionTypeName)
                ? defaultDimensionTypeName
                : (_dimensionTypes.Count > 0 ? _dimensionTypes[0].Name : string.Empty);

            Title = WindowTitle;
            Width = 1080;
            Height = 700;
            MinWidth = 860;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _boundaryBox = new ComboBox { Width = 210, VerticalAlignment = VerticalAlignment.Center, ItemsSource = BoundaryOptions, DisplayMemberPath = "Caption" };
            _boundaryBox.SelectedItem = BoundaryOptions.FirstOrDefault(o => o.Value == boundary) ?? BoundaryOptions[0];

            _directionBox = new ComboBox { Width = 160, VerticalAlignment = VerticalAlignment.Center };
            _directionBox.Items.Add("Into the room");
            _directionBox.Items.Add("Outward");
            _directionBox.SelectedIndex = outward ? 1 : 0;

            _removePreviousBox = new CheckBox
            {
                Content = "Remove what this button placed before",
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = removePrevious,
                ToolTip = "This check box never touches dimensions the user placed by hand — " +
                          "they carry no mark and are invisible to auto dimensioning."
            };

            _adjacentThicknessBox = new CheckBox
            {
                Content = "Capture the thickness of adjoining walls",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 0, 0),
                IsChecked = includeAdjacentThickness,
                ToolTip = "A chain starts and ends not at the room corner but at the far face of the adjoining wall: " +
                          "the first and last link becomes its thickness (120 | 3775 | 120) — that is how every " +
                          "chain on a masonry plan is built."
            };

            _moveSmallTextBox = new CheckBox
            {
                Content = "Pull small labels out onto a leader",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 0, 0),
                IsChecked = moveSmallText,
                ToolTip = "A label that does not fit between its ticks is moved off the line, and Revit draws " +
                          "a leader to it. The label width is estimated from the dimension type's font height " +
                          "and the view scale — this is a guess, not an exact calculation."
            };

            _templateBox = new ComboBox { Width = 220, VerticalAlignment = VerticalAlignment.Center, ItemsSource = _templateNames, IsEditable = false };
            if (!string.IsNullOrEmpty(lastTemplateName) && _templateNames.Contains(lastTemplateName))
                _templateBox.SelectedItem = lastTemplateName;

            var loadTemplateButton = new Button { Content = "Load", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            loadTemplateButton.Click += OnLoadTemplate;

            _templateNameBox = new TextBox { Width = 160, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), Text = lastTemplateName ?? string.Empty };

            var saveTemplateButton = new Button { Content = "Save template", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            saveTemplateButton.Click += OnSaveTemplate;

            var addChainButton = new Button { Content = "Add chain", Padding = new Thickness(8, 2, 8, 2) };
            addChainButton.Click += (s, e) => AddRow();

            var removeChainButton = new Button { Content = "Remove chain", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            removeChainButton.Click += (s, e) => RemoveSelectedRows();

            var sampleButton = new Button { Content = "Take a sample…", Padding = new Thickness(10, 4, 10, 4) };
            sampleButton.Click += OnTakeSample;

            _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _placeButton = new Button
            {
                Content = "Place",
                MinWidth = 130,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0)
            };
            _placeButton.Click += OnPlace;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            _grid = BuildGrid();

            Content = BuildLayout(
                roomCount, sampleButton, loadTemplateButton, saveTemplateButton,
                addChainButton, removeChainButton, closeButton);

            _rows.CollectionChanged += (s, e) => UpdateSummary();

            foreach (var row in initialRows ?? new List<DimensionChainRow>())
            {
                _rows.Add(row);
                Adopt(row);
            }

            UpdateSummary();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(
            int roomCount,
            Button sampleButton,
            Button loadTemplateButton,
            Button saveTemplateButton,
            Button addChainButton,
            Button removeChainButton,
            Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // rooms + sample
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // boundary/direction
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // check boxes
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // template
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // add/remove chain
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status + buttons

            var hint = new TextBlock
            {
                Text = "Dimension chains are placed along every side of every selected room — the same thing you " +
                       "once placed by hand. The easiest way to build the list is the \"Take a sample…\" button: " +
                       "select the finished dimensions along one wall and the window will work out their kind and " +
                       "offset by itself. That is a guess, not an exact calculation: a row can always be fixed by hand.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var roomsText = new TextBlock
            {
                Text = roomCount == 1 ? "Rooms selected: 1." : "Rooms selected: " + roomCount + ".",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(roomsText, 0);
            topRow.Children.Add(roomsText);

            Grid.SetColumn(sampleButton, 1);
            topRow.Children.Add(sampleButton);

            Grid.SetRow(topRow, 1);
            root.Children.Add(topRow);

            var settingsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            settingsRow.Children.Add(new TextBlock { Text = "Room boundary:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            settingsRow.Children.Add(_boundaryBox);
            settingsRow.Children.Add(new TextBlock { Text = "Place dimensions:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) });
            settingsRow.Children.Add(_directionBox);
            Grid.SetRow(settingsRow, 2);
            root.Children.Add(settingsRow);

            var checkRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            checkRow.Children.Add(_removePreviousBox);
            checkRow.Children.Add(_adjacentThicknessBox);
            checkRow.Children.Add(_moveSmallTextBox);
            Grid.SetRow(checkRow, 3);
            root.Children.Add(checkRow);

            var templateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            templateRow.Children.Add(new TextBlock { Text = "Template:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            templateRow.Children.Add(_templateBox);
            templateRow.Children.Add(loadTemplateButton);
            templateRow.Children.Add(new TextBlock { Text = "Name to save as:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) });
            templateRow.Children.Add(_templateNameBox);
            templateRow.Children.Add(saveTemplateButton);
            Grid.SetRow(templateRow, 4);
            root.Children.Add(templateRow);

            Grid.SetRow(_grid, 5);
            _grid.Margin = new Thickness(0, 10, 0, 8);
            root.Children.Add(_grid);

            var chainButtons = new StackPanel { Orientation = Orientation.Horizontal };
            chainButtons.Children.Add(addChainButton);
            chainButtons.Children.Add(removeChainButton);
            Grid.SetRow(chainButtons, 6);
            root.Children.Add(chainButtons);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_summary, 0);
            bottom.Children.Add(_summary);

            var actionButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actionButtons.Children.Add(_placeButton);
            actionButtons.Children.Add(closeButton);
            Grid.SetColumn(actionButtons, 1);
            bottom.Children.Add(actionButtons);

            Grid.SetRow(bottom, 7);
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
                CanUserSortColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = SystemColors.ControlLightBrush,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                SelectionMode = DataGridSelectionMode.Extended,
                RowHeaderWidth = 0,
                AlternationCount = int.MaxValue
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "✓",
                Width = new DataGridLength(30),
                CanUserResize = false,
                CellTemplate = BuildCheckBoxTemplate()
            });

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "№",
                Width = new DataGridLength(32),
                CanUserResize = false,
                CellTemplate = BuildIndexTemplate()
            });

            var kindOptions = DimensionChainKindText.All
                .Select(kind => new KindOption(kind, DimensionChainKindText.Caption(kind)))
                .ToList();

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Chain kind",
                Width = new DataGridLength(150),
                CellTemplate = BuildComboTemplate(kindOptions, "Caption", "Kind", "Kind")
            });

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Offset, mm",
                Width = new DataGridLength(110),
                Binding = new Binding("OffsetMm") { Mode = BindingMode.TwoWay, StringFormat = "0.#" },
                ElementStyle = CellStyle(),
                EditingElementStyle = EditorStyle()
            });

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Dimension type",
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 150,
                CellTemplate = BuildComboTemplate(_dimensionTypes, "Name", "Name", "DimensionTypeName")
            });

            var note = TextColumn("Where from", "Note", new DataGridLength(1.2, DataGridLengthUnitType.Star));
            note.MinWidth = 140;
            grid.Columns.Add(note);

            var status = TextColumn("State", "StatusText", new DataGridLength(1.2, DataGridLengthUnitType.Star));
            status.MinWidth = 140;
            status.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding("StatusBrush")));
            grid.Columns.Add(status);

            grid.CellEditEnding += OnCellEditEnding;

            return grid;
        }

        private static DataGridTextColumn TextColumn(string header, string property, DataGridLength width)
        {
            var style = CellStyle();
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(property)));

            return new DataGridTextColumn
            {
                Header = header,
                Width = width,
                Binding = new Binding(property),
                IsReadOnly = true,
                ElementStyle = style
            };
        }

        /// <summary>
        /// A drop-down column: a live <c>ComboBox</c> right in the cell, rather than a
        /// <c>DataGridComboBoxColumn</c>.
        ///
        /// This is not for looks — <c>DataGridComboBoxColumn</c> has three mutually exclusive
        /// bindings (<c>SelectedItemBinding</c>, <c>SelectedValueBinding</c>, <c>TextBinding</c>),
        /// and exactly one can be set: with two set, the second silently does nothing, the choice
        /// from the list never reaches the row, and the cell keeps whatever value the row was born
        /// with. That looks like "the type I want will not select, something else comes back" —
        /// exactly what the designer complained about. A live drop-down in the cell removes the
        /// question entirely: there is one binding, and the value goes into the row the moment it
        /// is picked (<c>UpdateSourceTrigger.PropertyChanged</c>), not when editing mode ends.
        /// </summary>
        private static DataTemplate BuildComboTemplate(
            System.Collections.IEnumerable itemsSource,
            string displayMemberPath,
            string selectedValuePath,
            string bindingPath)
        {
            var combo = new FrameworkElementFactory(typeof(ComboBox));
            combo.SetValue(ItemsControl.ItemsSourceProperty, itemsSource);
            combo.SetValue(ItemsControl.DisplayMemberPathProperty, displayMemberPath);
            combo.SetValue(Selector.SelectedValuePathProperty, selectedValuePath);
            combo.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));

            // The list is one for every row, so their collection view is shared too: without an
            // explicit "do not synchronise", a choice in one row would drag the others along.
            combo.SetValue(Selector.IsSynchronizedWithCurrentItemProperty, (bool?)false);
            combo.SetBinding(Selector.SelectedValueProperty,
                new Binding(bindingPath) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

            return new DataTemplate { VisualTree = combo };
        }

        private static DataTemplate BuildCheckBoxTemplate()
        {
            var checkBox = new FrameworkElementFactory(typeof(CheckBox));
            checkBox.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding("IsEnabled") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            checkBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            return new DataTemplate { VisualTree = checkBox };
        }

        /// <summary>A row's number, from its position in the table (AlternationIndex), not stored in the row itself.</summary>
        private static DataTemplate BuildIndexTemplate()
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1),
                Path = new PropertyPath("(ItemsControl.AlternationIndex)"),
                Converter = new IndexToOneBasedConverter()
            });
            text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetValue(TextBlock.ForegroundProperty, SystemColors.GrayTextBrush);

            return new DataTemplate { VisualTree = text };
        }

        private sealed class IndexToOneBasedConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                var index = value is int ? (int)value : 0;
                return (index + 1).ToString(CultureInfo.InvariantCulture);
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                throw new NotSupportedException();
            }
        }

        private static Style CellStyle()
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
            return style;
        }

        private static Style EditorStyle()
        {
            var style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0)));
            return style;
        }

        private sealed class BoundaryOption
        {
            public BoundaryOption(SpatialElementBoundaryLocation value, string caption)
            {
                Value = value;
                Caption = caption;
            }

            public SpatialElementBoundaryLocation Value { get; }
            public string Caption { get; }
        }

        private sealed class KindOption
        {
            public KindOption(DimensionChainKind kind, string caption)
            {
                Kind = kind;
                Caption = caption;
            }

            public DimensionChainKind Kind { get; }
            public string Caption { get; }
        }

        // ───────────────────────────── rows ─────────────────────────────

        private void AddRow()
        {
            var row = new DimensionChainRow { DimensionTypeName = _defaultTypeName };
            _rows.Add(row);
            Adopt(row);
        }

        /// <summary>
        /// Takes a row under the window's watch and brings it in line with the project: a
        /// dimension type from someone else's template, or from another project's sample, may be
        /// missing here, and there is no way to pick from a list something that is not in it. The
        /// same trick as with project worksets in "Link Manager" (see CLAUDE.md): do not silently
        /// keep an unreachable value, substitute a working one and say so in "State".
        /// </summary>
        private void Adopt(DimensionChainRow row)
        {
            Watch(row);

            var missing = string.Empty;
            if (row.DimensionTypeName.Length == 0)
            {
                row.DimensionTypeName = _defaultTypeName;
            }
            else if (!Known(row.DimensionTypeName))
            {
                missing = row.DimensionTypeName;
                row.DimensionTypeName = _defaultTypeName;
            }

            Validate(row);

            if (missing.Length > 0)
            {
                row.SetStatus("Dimension type \"" + missing + "\" was not found in the project — substituted \"" +
                              row.DimensionTypeName + "\"", false);
            }
        }

        private bool Known(string typeName)
        {
            return !string.IsNullOrEmpty(typeName) && _dimensionTypeNames.Contains(typeName);
        }

        private void RemoveSelectedRows()
        {
            foreach (var row in _grid.SelectedItems.Cast<DimensionChainRow>().ToList())
                _rows.Remove(row);
        }

        private void Watch(DimensionChainRow row)
        {
            row.PropertyChanged += OnRowChanged;
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DimensionChainRow.OffsetMm) || e.PropertyName == nameof(DimensionChainRow.DimensionTypeName))
                Validate((DimensionChainRow)sender);

            if (e.PropertyName == nameof(DimensionChainRow.IsEnabled))
                UpdateSummary();
        }

        private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            // The value lands in the row's property only after this event — validated on the next tick.
            var row = e.Row.Item as DimensionChainRow;
            if (row != null)
                Dispatcher.BeginInvoke((Action)(() => Validate(row)));
        }

        private void Validate(DimensionChainRow row)
        {
            if (row.OffsetMm <= 0)
            {
                row.SetStatus("The offset must be a positive number (mm)", true);
                return;
            }

            if (row.DimensionTypeName.Length > 0 && !_dimensionTypeNames.Contains(row.DimensionTypeName))
            {
                row.SetStatus("Dimension type \"" + row.DimensionTypeName + "\" was not found — the current default will be used", false);
                return;
            }

            row.SetStatus(string.Empty, false);
        }

        private void UpdateSummary()
        {
            var enabled = _rows.Count(r => r.IsEnabled);
            var errors = _rows.Count(r => r.IsEnabled && r.HasError);

            if (_rows.Count == 0)
            {
                _summary.Foreground = SystemColors.GrayTextBrush;
                _summary.Text = "No chain yet — take a sample or add one with the button below.";
            }
            else if (errors > 0)
            {
                _summary.Foreground = Brushes.Firebrick;
                _summary.Text = "Errors in the checked chains: " + errors + ". Fix the offset before placing.";
            }
            else
            {
                _summary.Foreground = SystemColors.GrayTextBrush;
                _summary.Text = "Chains checked: " + enabled + " of " + _rows.Count + ".";
            }

            _placeButton.IsEnabled = enabled > 0 && errors == 0;
            _placeButton.Content = enabled > 0 ? "Place (" + enabled + ")" : "Place";
        }

        // ───────────────────────────── templates ─────────────────────────────

        private void OnLoadTemplate(object sender, RoutedEventArgs e)
        {
            var name = _templateBox.SelectedItem as string;
            if (string.IsNullOrEmpty(name) || _loadTemplate == null)
                return;

            var template = _loadTemplate(name);
            if (template == null)
                return;

            _boundaryBox.SelectedItem = BoundaryOptions.FirstOrDefault(o => o.Value == template.Boundary) ?? BoundaryOptions[0];
            _directionBox.SelectedIndex = template.Outward ? 1 : 0;
            _adjacentThicknessBox.IsChecked = template.IncludeAdjacentWallThickness;
            _moveSmallTextBox.IsChecked = template.MoveSmallText;
            _templateNameBox.Text = name;

            foreach (var row in _rows.ToList())
            {
                row.PropertyChanged -= OnRowChanged;
                _rows.Remove(row);
            }

            foreach (var chain in template.Chains)
            {
                var row = new DimensionChainRow
                {
                    Kind = chain.Kind,
                    OffsetMm = chain.OffsetMm,
                    DimensionTypeName = chain.DimensionTypeName
                };
                _rows.Add(row);
                Adopt(row);
            }

            UpdateSummary();
        }

        private void OnSaveTemplate(object sender, RoutedEventArgs e)
        {
            var name = (_templateNameBox.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "Enter a template name.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_rows.Count == 0)
            {
                MessageBox.Show(this, "The table has no chain at all — there is nothing to save.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var template = new DimensionTemplate
            {
                Boundary = Boundary,
                Outward = Outward,
                IncludeAdjacentWallThickness = IncludeAdjacentThickness,
                MoveSmallText = MoveSmallText
            };
            foreach (var row in _rows)
            {
                template.Chains.Add(new DimensionTemplateChain
                {
                    Kind = row.Kind,
                    OffsetMm = row.OffsetMm,
                    DimensionTypeName = row.DimensionTypeName
                });
            }

            try
            {
                _saveTemplate?.Invoke(name, template);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not save the template: " + exception.Message, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_templateNames.Contains(name))
                _templateNames.Add(name);
            _templateBox.SelectedItem = name;
        }

        // ───────────────────────────── actions ─────────────────────────────

        private void OnTakeSample(object sender, RoutedEventArgs e)
        {
            WantsSample = true;
            DialogResult = true;
        }

        private void OnPlace(object sender, RoutedEventArgs e)
        {
            if (_rows.Count(r => r.IsEnabled) == 0)
            {
                MessageBox.Show(this, "No chain is checked.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_rows.Any(r => r.IsEnabled && r.HasError))
            {
                MessageBox.Show(this, "Some checked chains have an error — fix the offset.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            WantsSample = false;
            DialogResult = true;
        }
    }
}
