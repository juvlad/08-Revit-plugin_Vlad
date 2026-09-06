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
// Только этот enum, псевдонимом — не using Autodesk.Revit.DB целиком: тогда Grid/Binding/Control
// в этом файле стали бы неоднозначны между WPF и Revit API (см. CLAUDE.md, «WPF без XAML»).
using SpatialElementBoundaryLocation = Autodesk.Revit.DB.SpatialElementBoundaryLocation;

namespace VladTools.UI
{
    /// <summary>
    /// Окно «Авторазмеры»: список ниток (вид, смещение, тип размера), граница помещения,
    /// направление и работа с шаблонами.
    ///
    /// Окно про Revit API почти не знает — исключение (как и у «Удалить общие параметры» —
    /// см. CLAUDE.md) сделано только для перечисления <c>SpatialElementBoundaryLocation</c>:
    /// заводить для него отдельную копию ради одного enum избыточно.
    ///
    /// <c>Selection.PickObject</c> нельзя вызвать при открытом модальном окне, поэтому «Взять
    /// образец…» окно не читает само: оно закрывается с признаком <see cref="WantsSample"/>,
    /// команда делает выбор в Revit, разбирает образец и открывает окно заново уже заполненным
    /// (тем же приёмом, что «Проверить семейства» в DeleteProjectParametersWindow работает
    /// через лямбду, только здесь пересоздаётся само окно).
    ///
    /// Окно собрано кодом, без XAML — проект не включает WPF-сборку разметки.
    /// </summary>
    internal sealed class AutoDimensionWindow : Window
    {
        private const string WindowTitle = "Авторазмеры";

        private static readonly BoundaryOption[] BoundaryOptions =
        {
            new BoundaryOption(SpatialElementBoundaryLocation.CoreBoundary, "По граням несущего слоя"),
            new BoundaryOption(SpatialElementBoundaryLocation.Finish, "По отделке"),
            new BoundaryOption(SpatialElementBoundaryLocation.CoreCenter, "По центру несущего слоя"),
            new BoundaryOption(SpatialElementBoundaryLocation.Center, "По центру стены")
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

        /// <summary>Пользователь нажал «Взять образец…» — команде нужно выбрать размеры и открыть окно заново.</summary>
        public bool WantsSample { get; private set; }

        /// <summary>Текущее содержимое таблицы — читается независимо от того, чем закрылось окно.</summary>
        public IReadOnlyList<DimensionChainRow> Rows => _rows;

        public SpatialElementBoundaryLocation Boundary =>
            (_boundaryBox.SelectedItem as BoundaryOption)?.Value ?? SpatialElementBoundaryLocation.CoreBoundary;

        public bool Outward => _directionBox.SelectedIndex == 1;

        public bool RemovePrevious => _removePreviousBox.IsChecked == true;

        /// <summary>Крайние засечки нитки захватывают толщину примыкающей стены.</summary>
        public bool IncludeAdjacentThickness => _adjacentThicknessBox.IsChecked == true;

        /// <summary>Подписи, которым не хватает места между засечками, выносятся на полку.</summary>
        public bool MoveSmallText => _moveSmallTextBox.IsChecked == true;

        /// <summary>Имя шаблона, выбранное или введённое последним — для настроек окна.</summary>
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

            // Тип «по умолчанию» — тот, который поставит сам Revit, а не первый по алфавиту:
            // иначе новая строка рождалась бы со случайным типом проекта.
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
            _directionBox.Items.Add("Внутрь помещения");
            _directionBox.Items.Add("Наружу");
            _directionBox.SelectedIndex = outward ? 1 : 0;

            _removePreviousBox = new CheckBox
            {
                Content = "Удалять ранее расставленные этой кнопкой",
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = removePrevious,
                ToolTip = "Размеры, поставленные пользователем вручную, эта галочка не касается никогда — " +
                          "они не помечены и авторазмерам не видны."
            };

            _adjacentThicknessBox = new CheckBox
            {
                Content = "Захватывать толщину примыкающих стен",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 0, 0),
                IsChecked = includeAdjacentThickness,
                ToolTip = "Нитка начинается и кончается не углом помещения, а дальней гранью примыкающей стены: " +
                          "первым и последним звеном становится её толщина (120 | 3775 | 120) — так устроена " +
                          "каждая нитка кладочного плана."
            };

            _moveSmallTextBox = new CheckBox
            {
                Content = "Выносить мелкие подписи на полку",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 0, 0),
                IsChecked = moveSmallText,
                ToolTip = "Подпись, которой не хватает места между засечками, сдвигается с линии, и Revit " +
                          "дорисовывает к ней выноску. Ширина подписи оценивается по высоте шрифта типа " +
                          "размера и масштабу вида — это подбор, не точный расчёт."
            };

            _templateBox = new ComboBox { Width = 220, VerticalAlignment = VerticalAlignment.Center, ItemsSource = _templateNames, IsEditable = false };
            if (!string.IsNullOrEmpty(lastTemplateName) && _templateNames.Contains(lastTemplateName))
                _templateBox.SelectedItem = lastTemplateName;

            var loadTemplateButton = new Button { Content = "Загрузить", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            loadTemplateButton.Click += OnLoadTemplate;

            _templateNameBox = new TextBox { Width = 160, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), Text = lastTemplateName ?? string.Empty };

            var saveTemplateButton = new Button { Content = "Сохранить шаблон", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            saveTemplateButton.Click += OnSaveTemplate;

            var addChainButton = new Button { Content = "Добавить нитку", Padding = new Thickness(8, 2, 8, 2) };
            addChainButton.Click += (s, e) => AddRow();

            var removeChainButton = new Button { Content = "Удалить нитку", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            removeChainButton.Click += (s, e) => RemoveSelectedRows();

            var sampleButton = new Button { Content = "Взять образец…", Padding = new Thickness(10, 4, 10, 4) };
            sampleButton.Click += OnTakeSample;

            _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _placeButton = new Button
            {
                Content = "Расставить",
                MinWidth = 130,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0)
            };
            _placeButton.Click += OnPlace;

            var closeButton = new Button
            {
                Content = "Закрыть",
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

        // ───────────────────────────── разметка ─────────────────────────────

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
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // подсказка
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // помещения + образец
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // граница/направление
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // галочки
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // шаблон
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // таблица
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // добавить/удалить нитку
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // статус + кнопки

            var hint = new TextBlock
            {
                Text = "Нитки размеров ставятся вдоль каждой стороны каждого выбранного помещения — то же самое, " +
                       "что вы один раз расставили руками. Проще всего собрать список кнопкой «Взять образец…»: " +
                       "выделите готовые размеры вдоль одной стены — окно разберёт их вид и смещение само. " +
                       "Разбор — это подбор, не точный расчёт: строку всегда можно поправить руками.",
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
                Text = roomCount == 1 ? "Выбрано помещений: 1." : "Выбрано помещений: " + roomCount + ".",
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
            settingsRow.Children.Add(new TextBlock { Text = "Граница помещения:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            settingsRow.Children.Add(_boundaryBox);
            settingsRow.Children.Add(new TextBlock { Text = "Ставить размеры:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) });
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
            templateRow.Children.Add(new TextBlock { Text = "Шаблон:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            templateRow.Children.Add(_templateBox);
            templateRow.Children.Add(loadTemplateButton);
            templateRow.Children.Add(new TextBlock { Text = "Имя для сохранения:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) });
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
                Header = "Вид нитки",
                Width = new DataGridLength(150),
                CellTemplate = BuildComboTemplate(kindOptions, "Caption", "Kind", "Kind")
            });

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Смещение, мм",
                Width = new DataGridLength(110),
                Binding = new Binding("OffsetMm") { Mode = BindingMode.TwoWay, StringFormat = "0.#" },
                ElementStyle = CellStyle(),
                EditingElementStyle = EditorStyle()
            });

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Тип размера",
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 150,
                CellTemplate = BuildComboTemplate(_dimensionTypes, "Name", "Name", "DimensionTypeName")
            });

            var note = TextColumn("Откуда взято", "Note", new DataGridLength(1.2, DataGridLengthUnitType.Star));
            note.MinWidth = 140;
            grid.Columns.Add(note);

            var status = TextColumn("Состояние", "StatusText", new DataGridLength(1.2, DataGridLengthUnitType.Star));
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
        /// Колонка-список: живой <c>ComboBox</c> прямо в ячейке, а не <c>DataGridComboBoxColumn</c>.
        ///
        /// Так сделано не ради вида, а потому что у <c>DataGridComboBoxColumn</c> три взаимно
        /// исключающие привязки (<c>SelectedItemBinding</c>, <c>SelectedValueBinding</c>,
        /// <c>TextBinding</c>), и задать можно ровно одну: при двух заданных вторая молча
        /// не работает, выбор из списка не доходит до строки, и в ячейке остаётся то значение,
        /// с которым строка родилась. Выглядит это как «нужный тип не выбирается, возвращается
        /// какой-то свой» — ровно то, на что жаловался проектировщик. Живой список в ячейке
        /// снимает вопрос целиком: привязка одна, значение уходит в строку сразу по выбору
        /// (<c>UpdateSourceTrigger.PropertyChanged</c>), а не по выходу из режима правки.
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

            // Список один на все строки, а значит и представление коллекции у них общее: без
            // явного «не синхронизировать» выбор в одной строке потянул бы за собой остальные.
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

        /// <summary>Номер строки — из порядка в таблице (AlternationIndex), не хранится в самой строке.</summary>
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

        // ───────────────────────────── строки ─────────────────────────────

        private void AddRow()
        {
            var row = new DimensionChainRow { DimensionTypeName = _defaultTypeName };
            _rows.Add(row);
            Adopt(row);
        }

        /// <summary>
        /// Берёт строку под присмотр окна и приводит её к тому, что есть в проекте: тип размера
        /// из чужого шаблона или из образца другого проекта здесь может отсутствовать, а выбрать
        /// из списка то, чего в нём нет, нельзя. Тот же приём, что с рабочими наборами проекта
        /// в «Link Manager» (см. CLAUDE.md): не хранить недостижимое значение молча, а подставить
        /// рабочее и сказать об этом в «Состоянии».
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
                row.SetStatus("Тип размера «" + missing + "» в проекте не найден — подставлен «" +
                              row.DimensionTypeName + "»", false);
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
            // Значение попадает в свойство строки уже после этого события — проверяем на следующем такте.
            var row = e.Row.Item as DimensionChainRow;
            if (row != null)
                Dispatcher.BeginInvoke((Action)(() => Validate(row)));
        }

        private void Validate(DimensionChainRow row)
        {
            if (row.OffsetMm <= 0)
            {
                row.SetStatus("Смещение должно быть положительным числом (мм)", true);
                return;
            }

            if (row.DimensionTypeName.Length > 0 && !_dimensionTypeNames.Contains(row.DimensionTypeName))
            {
                row.SetStatus("Тип размера «" + row.DimensionTypeName + "» не найден — будет использован текущий по умолчанию", false);
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
                _summary.Text = "Ниток пока нет — возьмите образец или добавьте нитку кнопкой ниже.";
            }
            else if (errors > 0)
            {
                _summary.Foreground = Brushes.Firebrick;
                _summary.Text = "Ошибок в отмеченных нитках: " + errors + ". Исправьте смещение перед расстановкой.";
            }
            else
            {
                _summary.Foreground = SystemColors.GrayTextBrush;
                _summary.Text = "Отмечено ниток: " + enabled + " из " + _rows.Count + ".";
            }

            _placeButton.IsEnabled = enabled > 0 && errors == 0;
            _placeButton.Content = enabled > 0 ? "Расставить (" + enabled + ")" : "Расставить";
        }

        // ───────────────────────────── шаблоны ─────────────────────────────

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
                MessageBox.Show(this, "Введите имя шаблона.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_rows.Count == 0)
            {
                MessageBox.Show(this, "В таблице нет ни одной нитки — сохранять нечего.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show(this, "Не удалось сохранить шаблон: " + exception.Message, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_templateNames.Contains(name))
                _templateNames.Add(name);
            _templateBox.SelectedItem = name;
        }

        // ───────────────────────────── действия ─────────────────────────────

        private void OnTakeSample(object sender, RoutedEventArgs e)
        {
            WantsSample = true;
            DialogResult = true;
        }

        private void OnPlace(object sender, RoutedEventArgs e)
        {
            if (_rows.Count(r => r.IsEnabled) == 0)
            {
                MessageBox.Show(this, "Не отмечено ни одной нитки.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_rows.Any(r => r.IsEnabled && r.HasError))
            {
                MessageBox.Show(this, "Есть отмеченные нитки с ошибкой — исправьте смещение.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            WantsSample = false;
            DialogResult = true;
        }
    }
}
