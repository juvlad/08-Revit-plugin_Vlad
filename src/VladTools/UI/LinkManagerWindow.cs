using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    /// Окно «Link Manager»: список моделей, которые нужно связать с открытым проектом,
    /// и одна на всех настройка — как их размещать и какие рабочие наборы закрывать.
    ///
    /// Смысл кнопки в слове «одна». В Revit каждая связь вставляется своим диалогом,
    /// и в каждом заново выбирается «По общим координатам» и заново снимаются галочки
    /// с «00_Shared levels and grids». На двадцати связях это двадцать одинаковых диалогов.
    /// Здесь список собирается целиком — из файлов, с Revit Server, из BIM360, из сохранённого
    /// набора, — способ размещения выбирается один раз, наборы отмечаются один раз по имени,
    /// и дальше всё грузится пачкой.
    ///
    /// Рабочие наборы здесь двух разных сортов, и путать их нельзя.
    /// **Наборы внутри связи** (нижний список) — то, что у связи открыть или закрыть;
    /// они отмечаются по имени, а не по идентификатору: у каждой модели свои идентификаторы,
    /// и общее у «00_Shared levels and grids» во всех связях — только имя. По той же причине
    /// рядом с именами есть правило: в одной модели набор зовётся «00_Shared Levels and Grids»,
    /// в другой — «00_Общие уровни и оси», и правило «содержит» ловит обе, а список имён — нет.
    /// **Набор проекта** (столбец «Набор проекта») — то, куда положить саму связь в открытой
    /// модели. Он у каждой связи свой: АР в «01_Связи_АР», КР в «01_Связи_КР», — поэтому правится
    /// прямо в строке, а кнопка «Задать отмеченным» лишь избавляет от щелчков, когда набор общий.
    ///
    /// Список моделей не обязательно собирать руками: кнопка «Комплект по корпусу…»
    /// предлагает готовый набор связей — модели всех разделов того же корпуса, найденные
    /// по папкам проекта (см. <see cref="ModelKitWindow"/>). Дальше они попадают в ту же
    /// таблицу и живут по тем же правилам, что и добавленные любым другим способом.
    ///
    /// Связи, уже стоящие в проекте, из списка не прячутся: их видно со статусом
    /// «Уже в проекте», и отметить их можно — тогда они перезагрузятся с новой настройкой
    /// рабочих наборов, а при смене набора проекта ещё и переедут в него вместе со всеми своими
    /// экземплярами. Размещение у существующей связи не меняется: Revit этого не позволяет.
    ///
    /// Окно собрано кодом, без XAML — проект не включает WPF-сборку разметки.
    /// </summary>
    internal sealed class LinkManagerWindow : Window
    {
        private const string WindowTitle = "Link Manager";

        private readonly ObservableCollection<LinkRow> _all = new ObservableCollection<LinkRow>();
        private readonly ObservableCollection<LinkRow> _visible = new ObservableCollection<LinkRow>();
        private readonly ObservableCollection<WorksetRow> _worksets = new ObservableCollection<WorksetRow>();

        private readonly Func<IReadOnlyList<LinkRow>, LinkWorksetScan> _readWorksets;
        private readonly LinkPreferences _preferences;

        /// <summary>Сама открытая модель — из неё «Комплект по корпусу» берёт корпус и папку.</summary>
        private readonly HostModel _host;

        /// <summary>Рабочие наборы открытого проекта — с «(активный)» первой строкой.</summary>
        private readonly List<string> _hostWorksets = new List<string> { LinkRow.ActiveWorkset };

        private readonly ComboBox _hostWorksetBox;
        private readonly CheckBox _matchWorksetBox;
        private readonly ComboBox _scopeBox;
        private readonly ComboBox _setBox;
        private readonly ComboBox _placementBox;
        private readonly ComboBox _attachmentBox;
        private readonly CheckBox _relativeBox;
        private readonly ComboBox _worksetModeBox;
        private readonly ComboBox _ruleBox;
        private readonly TextBox _patternBox;
        private readonly Button _scanButton;
        private readonly TextBlock _worksetCaption;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly DataGrid _worksetGrid;
        private readonly TextBlock _status;
        private readonly Button _loadButton;

        private bool _syncingSelectAll;
        private bool _settingMany;
        private bool _worksetsRead;

        /// <summary>Строки, которым набор проекта подставил подбор по имени, — чтобы снять его при выключении.</summary>
        private readonly HashSet<LinkRow> _autoWorkset = new HashSet<LinkRow>();

        /// <summary>Подбор сейчас сам меняет набор строки — не считать это ручной правкой.</summary>
        private bool _suggesting;

        /// <summary>Заведены ли в проекте наборы под разделы; считается по первому запросу.</summary>
        private bool? _usesDisciplineWorksets;

        /// <summary>Связи, которые пользователь подтвердил к загрузке.</summary>
        public IReadOnlyList<LinkRow> Selected { get; private set; } = new List<LinkRow>();

        /// <summary>Настройки, с которыми команда будет грузить всю пачку.</summary>
        public LinkPreferences Preferences => _preferences;

        /// <summary>В проекте есть рабочие наборы, то есть он совмещённый.</summary>
        private bool HasHostWorksets => _hostWorksets.Count > 1;

        /// <param name="existing">Связи, уже стоящие в проекте.</param>
        /// <param name="hostWorksets">
        /// Рабочие наборы открытого проекта — те, в которые можно положить связь.
        /// Проект не совмещённый — пустой список, и столбец с набором прячется.
        /// </param>
        /// <param name="readWorksets">
        /// Чтение рабочих наборов выбранных моделей без их открытия.
        /// Всю работу с Revit делает команда — окно только зовёт и показывает итог.
        /// </param>
        /// <param name="host">Где лежит и как называется сам открытый проект; может быть пустым.</param>
        public LinkManagerWindow(
            IReadOnlyList<LinkRow> existing,
            IReadOnlyList<string> hostWorksets,
            Func<IReadOnlyList<LinkRow>, LinkWorksetScan> readWorksets,
            HostModel host)
        {
            _readWorksets = readWorksets;
            _host = host;
            _preferences = LinkPreferences.Load();

            if (hostWorksets != null)
                _hostWorksets.AddRange(hostWorksets);

            Title = WindowTitle;
            Width = 1080;
            Height = 780;
            MinWidth = 760;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            foreach (var row in existing ?? new List<LinkRow>())
            {
                Normalize(row);
                _all.Add(row);
            }

            _scopeBox = new ComboBox { Width = 200, VerticalAlignment = VerticalAlignment.Center };
            _scopeBox.Items.Add("Все связи");
            _scopeBox.Items.Add("Только новые");
            _scopeBox.Items.Add("Только уже в проекте");
            _scopeBox.SelectedIndex = 0;
            _scopeBox.ToolTip = "Работает только то, что показано в таблице: скрытая строка теряет галочку.";
            _scopeBox.SelectionChanged += (s, e) => RebuildVisible();

            _setBox = new ComboBox
            {
                Width = 220,
                IsEditable = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "Сохранённый список моделей. Для BIM360 это главный способ работы:\n" +
                    "один раз собрали список — дальше он подставляется в каждый проект,\n" +
                    "даже когда до облака не достучаться.\n" +
                    "Имя можно выбрать из списка или вписать своё."
            };
            ReloadSetNames();

            _placementBox = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center };
            _placementBox.Items.Add("По общим координатам");
            _placementBox.Items.Add("Совмещение внутренних начал");
            _placementBox.Items.Add("Центр в центр");
            _placementBox.Items.Add("По расположению площадки проекта");
            _placementBox.SelectedIndex = (int)_preferences.Placement;
            _placementBox.ToolTip =
                "Один способ на все новые связи — то, ради чего кнопка и сделана.\n" +
                "У связей, которые уже стоят в проекте, размещение не меняется: Revit этого не даёт.";

            _attachmentBox = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center };
            _attachmentBox.Items.Add("Наложение");
            _attachmentBox.Items.Add("Прикрепление");
            _attachmentBox.SelectedIndex = _preferences.IsAttachment ? 1 : 0;
            _attachmentBox.ToolTip =
                "Наложение — связь не поедет дальше, в модель, которая свяжется с этой.\n" +
                "Прикрепление — поедет.";

            _hostWorksetBox = new ComboBox
            {
                Width = 220,
                ItemsSource = _hostWorksets,
                SelectedIndex = 0,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = HasHostWorksets,
                ToolTip = HasHostWorksets
                    ? "Рабочий набор открытого проекта, в который положить связи.\n" +
                      "Кнопка рядом ставит его всем отмеченным строкам; в самой таблице\n" +
                      "набор у каждой связи меняется отдельно."
                    : "Проект не совмещённый — рабочих наборов в нём нет."
            };

            _matchWorksetBox = new CheckBox
            {
                Content = "Подбирать по имени модели",
                IsChecked = _preferences.MatchProjectWorkset,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                IsEnabled = HasHostWorksets,
                ToolTip =
                    "У новой связи без заданного набора плагин берёт код раздела из имени модели\n" +
                    "(…_OV_R22 → OV) и ставит набор проекта, в имени которого этот код стоит\n" +
                    "отдельным словом: «01_Link_OV», «01_Связи_OV». Ручной выбор в таблице\n" +
                    "не трогается. Список кодов правится в links\\_settings.txt (строки DISCIPLINE)."
            };
            _matchWorksetBox.Checked += (s, e) => ApplyWorksetSuggestions();
            _matchWorksetBox.Unchecked += (s, e) => ClearWorksetSuggestions();

            _relativeBox = new CheckBox
            {
                Content = "Относительный путь",
                IsChecked = _preferences.IsRelativePath,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip =
                    "Только для связей-файлов: путь запоминается относительно проекта,\n" +
                    "и папку с моделями можно переносить целиком.\n" +
                    "Для Revit Server и BIM360 путь всегда абсолютный."
            };

            _worksetModeBox = new ComboBox { Width = 230, VerticalAlignment = VerticalAlignment.Center };
            _worksetModeBox.Items.Add("Открыть все наборы");
            _worksetModeBox.Items.Add("Закрыть все наборы");
            _worksetModeBox.Items.Add("Как при последнем открытии");
            _worksetModeBox.SelectedIndex = (int)_preferences.WorksetMode;
            _worksetModeBox.SelectionChanged += (s, e) => UpdateWorksetCaption();

            _scanButton = new Button
            {
                Content = "Прочитать наборы",
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                IsEnabled = _readWorksets != null,
                ToolTip =
                    "Читает имена рабочих наборов у отмеченных моделей, не открывая их.\n" +
                    "Нужно только чтобы увидеть список имён: при загрузке наборы читаются\n" +
                    "заново в любом случае — по именам ведь надо найти сами наборы."
            };
            _scanButton.Click += OnScanWorksets;

            _ruleBox = new ComboBox { Width = 170, VerticalAlignment = VerticalAlignment.Center };
            _ruleBox.Items.Add("начинающиеся с");
            _ruleBox.Items.Add("содержащие");
            _ruleBox.SelectedIndex = _preferences.WorksetPatternContains ? 1 : 0;

            _patternBox = new TextBox
            {
                Text = _preferences.WorksetPattern,
                MinWidth = 170,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2),
                ToolTip =
                    "Правило действует и при загрузке, а не только по кнопке «Отметить»:\n" +
                    "в каждой связи под него попадут её собственные наборы, даже если\n" +
                    "точного имени в списке ниже нет. Так ловятся «00_Shared Levels and Grids»\n" +
                    "и «00_Общие уровни и оси» одной строкой «00_»."
            };
            _patternBox.TextChanged += (s, e) => UpdateSummary();

            _worksetCaption = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

            _selectAll = new CheckBox
            {
                IsChecked = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Отметить или снять все показанные связи"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildLinkGrid();
            _worksetGrid = BuildWorksetGrid();
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _loadButton = new Button
            {
                Content = "Загрузить",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _loadButton.Click += OnLoad;

            var cancelButton = new Button
            {
                Content = "Закрыть",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(cancelButton);

            foreach (var row in _all)
                row.PropertyChanged += OnRowChanged;

            // Имена наборов из прошлого раза видны сразу: обычно менять их не нужно вовсе.
            foreach (var name in _preferences.Worksets)
                AddWorkset(name, true);

            foreach (var workset in _worksets)
                workset.IsSelected = true;

            UpdateWorksetCaption();
            RebuildVisible();
        }

        // ───────────────────────────── разметка ─────────────────────────────

        private UIElement BuildLayout(Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // подсказка
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // откуда брать
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // сохранённые наборы
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // таблица связей
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // размещение
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // набор проекта
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // наборы внутри связей
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });                  // список наборов
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // статус + кнопки

            var hint = new TextBlock
            {
                Text = "Соберите список моделей, выберите способ размещения и рабочие наборы — " +
                       "и всё отмеченное свяжется одной операцией. Работает только то, что показано в таблице.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var sources = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            sources.Children.Add(SourceButton("Файлы…", "Обычные файлы .rvt: диск или сетевая папка.", OnAddFiles));
            sources.Children.Add(SourceButton("Revit Server…", "Просмотр папок и моделей на Revit Server.", OnBrowseServer));
            sources.Children.Add(SourceButton("BIM360…", "Просмотр учётных записей, проектов и папок BIM360/ACC.", OnBrowseCloud));
            sources.Children.Add(SourceButton("BIM360 по GUID…", "Ввод облачных моделей парой GUID — если просмотр недоступен.", OnAddCloudByGuid));
            sources.Children.Add(SourceButton("Комплект по корпусу…",
                "Сам находит модели всех разделов вашего корпуса — по папкам проекта и номеру корпуса в имени.",
                OnAddKit));
            sources.Children.Add(SourceButton("Убрать из списка", "Убирает отмеченные строки из таблицы. Связи в проекте при этом не трогаются.", OnRemove));

            sources.Children.Add(new TextBlock
            {
                Text = "Показывать:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 8, 0)
            });
            sources.Children.Add(_scopeBox);

            Grid.SetRow(sources, 1);
            root.Children.Add(sources);

            var sets = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            sets.Children.Add(new TextBlock
            {
                Text = "Набор:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            sets.Children.Add(_setBox);
            sets.Children.Add(SourceButton("Загрузить набор", "Добавляет в таблицу модели сохранённого набора.", OnLoadSet));
            sets.Children.Add(SourceButton("Сохранить набор", "Сохраняет показанные строки под именем из поля слева.", OnSaveSet));
            sets.Children.Add(SourceButton("Удалить набор", "Удаляет сохранённый набор из профиля Windows.", OnDeleteSet));

            Grid.SetRow(sets, 2);
            root.Children.Add(sets);

            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            var placement = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            placement.Children.Add(new TextBlock
            {
                Text = "Размещение для всех связей:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            placement.Children.Add(_placementBox);
            placement.Children.Add(new TextBlock
            {
                Text = "Тип связи:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 8, 0)
            });
            placement.Children.Add(_attachmentBox);
            placement.Children.Add(_relativeBox);

            Grid.SetRow(placement, 4);
            root.Children.Add(placement);

            var hostWorkset = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            hostWorkset.Children.Add(new TextBlock
            {
                Text = "Класть связи в рабочий набор проекта:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            hostWorkset.Children.Add(_hostWorksetBox);

            var applyWorkset = SourceButton("Задать отмеченным",
                "Ставит выбранный слева набор всем отмеченным строкам таблицы.", OnApplyHostWorkset);
            applyWorkset.Margin = new Thickness(8, 0, 8, 4);
            applyWorkset.IsEnabled = HasHostWorksets;
            hostWorkset.Children.Add(applyWorkset);

            hostWorkset.Children.Add(_matchWorksetBox);

            hostWorkset.Children.Add(new TextBlock
            {
                Text = HasHostWorksets
                    ? "в таблице набор правится и у каждой связи отдельно"
                    : "проект не совмещённый — рабочих наборов в нём нет",
                Foreground = SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            Grid.SetRow(hostWorkset, 5);
            root.Children.Add(hostWorkset);

            var worksets = new WrapPanel();
            worksets.Children.Add(_scanButton);
            worksets.Children.Add(new TextBlock
            {
                Text = "При загрузке:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            worksets.Children.Add(_worksetModeBox);
            worksets.Children.Add(new TextBlock
            {
                Text = "Правило по имени:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 8, 0)
            });
            worksets.Children.Add(_ruleBox);
            worksets.Children.Add(_patternBox);
            worksets.Children.Add(SourceButton("Отметить по правилу", "Ставит галочки на подходящих именах в списке ниже.", OnMarkWorksets));

            var worksetHeader = new StackPanel();
            worksetHeader.Children.Add(worksets);
            worksetHeader.Children.Add(_worksetCaption);

            Grid.SetRow(worksetHeader, 6);
            root.Children.Add(worksetHeader);

            Grid.SetRow(_worksetGrid, 7);
            root.Children.Add(_worksetGrid);

            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_loadButton);
            buttons.Children.Add(cancelButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 8);
            root.Children.Add(bottom);

            return root;
        }

        private static Button SourceButton(string text, string tooltip, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tooltip
            };
            button.Click += handler;

            return button;
        }

        private DataGrid BuildLinkGrid()
        {
            var grid = new DataGrid
            {
                ItemsSource = _visible,
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
                RowHeaderWidth = 0,
                Margin = new Thickness(0, 4, 0, 8)
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate()
            });

            grid.Columns.Add(GridBuilder.TextColumn("Модель", "Name", new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(GridBuilder.TextColumn("Откуда", "Kind", new DataGridLength(100)));
            grid.Columns.Add(GridBuilder.TextColumn("Расположение", "Location", new DataGridLength(1.2, DataGridLengthUnitType.Star)));

            // Набор проекта — единственное, что правится прямо в строке: он у каждой связи свой,
            // и «задать всем» тут помогает не всегда.
            if (HasHostWorksets)
            {
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = "Набор проекта",
                    Width = new DataGridLength(190),
                    CanUserSort = true,
                    SortMemberPath = "Workset",
                    CellTemplate = BuildWorksetTemplate(_hostWorksets)
                });
            }

            grid.Columns.Add(GridBuilder.TextColumn("Наборы в связи", "Worksets", new DataGridLength(110)));
            grid.Columns.Add(GridBuilder.TextColumn("Состояние", "Status", new DataGridLength(200)));

            grid.MouseDoubleClick += (s, e) => ToggleSelectedRows();
            grid.PreviewKeyDown += OnGridKeyDown;

            return grid;
        }

        private DataGrid BuildWorksetGrid()
        {
            var grid = new DataGrid
            {
                ItemsSource = _worksets,
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
                RowHeaderWidth = 0,
                Margin = new Thickness(0, 6, 0, 8)
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = string.Empty,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate()
            });

            grid.Columns.Add(GridBuilder.TextColumn("Рабочий набор", "Name", new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(GridBuilder.TextColumn("Где есть", "Where", new DataGridLength(160)));

            grid.MouseDoubleClick += (s, e) => ToggleSelectedWorksets();

            return grid;
        }

        /// <summary>
        /// Выпадающий список в ячейке. Стоит в шаблоне ячейки, а не редактирования: таблица
        /// целиком «только для чтения», а так набор виден и меняется одним щелчком, без перехода
        /// строки в режим правки.
        /// </summary>
        private static DataTemplate BuildWorksetTemplate(IEnumerable<string> worksets)
        {
            var combo = new FrameworkElementFactory(typeof(ComboBox));
            combo.SetValue(ItemsControl.ItemsSourceProperty, worksets);
            combo.SetBinding(Selector.SelectedItemProperty,
                new Binding("Workset") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            combo.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));
            combo.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            return new DataTemplate { VisualTree = combo };
        }

        // ───────────────────────────── откуда берутся связи ─────────────────────────────

        private void OnAddFiles(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.Files(this, true), "файлов");
        }

        private void OnBrowseServer(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.Server(this, WindowTitle, _preferences, Keys), "моделей с Revit Server");
        }

        private void OnBrowseCloud(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.Cloud(this, WindowTitle, Keys), "моделей BIM360");
        }

        private void OnAddCloudByGuid(object sender, RoutedEventArgs e)
        {
            Add(ModelPicker.CloudByGuid(this, DefaultRegion()), "облачных моделей");
        }

        /// <summary>
        /// Готовый комплект связей по номеру корпуса. Найденное приходит сюда обычными
        /// записями и дальше живёт как всё остальное: дубли отсеются, рабочий набор проекта
        /// подберётся по разделу — тому же, по которому модель и нашлась.
        /// </summary>
        private void OnAddKit(object sender, RoutedEventArgs e)
        {
            var window = new ModelKitWindow(_host, _preferences, Keys) { Owner = this };

            if (window.ShowDialog() == true)
                Add(window.Selected, "моделей комплекта");
        }

        /// <summary>Регион, с которым окно ввода GUID открывается: тот же, что у уже собранных связей.</summary>
        private string DefaultRegion()
        {
            var region = _all
                .Where(row => row.Entry.Origin == LinkOrigin.Cloud)
                .Select(row => row.Entry.Region)
                .FirstOrDefault(value => !string.IsNullOrEmpty(value));

            return region ?? "US";
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
                return;

            foreach (var row in marked)
            {
                row.PropertyChanged -= OnRowChanged;
                _all.Remove(row);
            }

            RebuildVisible();
        }

        /// <summary>Ставит выбранный рабочий набор проекта всем отмеченным строкам.</summary>
        private void OnApplyHostWorkset(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "Отметьте связи, которым задать рабочий набор.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var workset = (string)_hostWorksetBox.SelectedItem ?? LinkRow.ActiveWorkset;

            foreach (var row in marked)
                row.Workset = workset;
        }

        /// <summary>Добавляет модели в таблицу, отбрасывая те, что в ней уже есть.</summary>
        private void Add(IReadOnlyList<LinkEntry> entries, string what)
        {
            if (entries == null || entries.Count == 0)
                return;

            var known = Keys();
            var added = 0;

            foreach (var entry in entries)
            {
                if (!known.Add(entry.Key))
                    continue;

                var row = new LinkRow(entry) { IsSelected = true };
                Normalize(row);
                SuggestWorkset(row);
                row.PropertyChanged += OnRowChanged;
                _all.Add(row);
                added++;
            }

            RebuildVisible();

            if (added < entries.Count)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Добавлено " + what + ": " + added +
                               ". Уже были в списке: " + (entries.Count - added) + ".";
            }
        }

        private HashSet<string> Keys()
        {
            return new HashSet<string>(_all.Select(row => row.Key), StringComparer.Ordinal);
        }

        /// <summary>
        /// Набор, приехавший из сохранённого списка, в этом проекте может не существовать —
        /// наборы у каждого проекта свои. Молча оставить такое имя нельзя: в выпадающем списке
        /// его нет, ячейка выглядела бы пустой, а при загрузке связь ушла бы не туда.
        /// Сбрасываем на активный и говорим об этом в столбце «Состояние».
        /// </summary>
        private void Normalize(LinkRow row)
        {
            var chosen = row.Entry.Workset;
            if (chosen.Length == 0)
                return;

            if (_hostWorksets.Any(name => string.Equals(name, chosen, StringComparison.CurrentCultureIgnoreCase)))
                return;

            row.Workset = LinkRow.ActiveWorkset;
            row.Note = "Набора «" + chosen + "» в проекте нет";
        }

        // ───────────────────────────── подбор набора проекта по имени ─────────────────────────────

        /// <summary>
        /// Ставит новой связи рабочий набор проекта по коду раздела из имени модели — если
        /// подбор включён, связь новая и набор ей ещё не задан (ни руками, ни из сохранённого
        /// набора). Ровно один подходящий набор — ставим; несколько — только помечаем в
        /// «Состоянии», выбор за пользователем; ни одного — молчим, чтобы не засорять столбец.
        /// </summary>
        private void SuggestWorkset(LinkRow row)
        {
            if (!HasHostWorksets || _matchWorksetBox?.IsChecked != true)
                return;

            if (row.IsExisting || row.Entry.Workset.Length > 0)
                return;

            var code = DisciplineCatalog.Detect(row.Entry.Name, _preferences.EffectiveDisciplines);
            if (code.Length == 0)
            {
                // Кода раздела в имени не видно. Там, где наборы по разделам заведены, это
                // и есть ответ на вопрос «почему пусто»: молчание выглядело бы поломкой.
                if (UsesDisciplineWorksets)
                    row.Note = "Раздела в имени модели не видно";

                return;
            }

            var worksets = _hostWorksets.Skip(1).ToList();
            var matches = DisciplineCatalog.MatchingWorksets(code, worksets);

            // Из нескольких подходящих берётся тот, что назван как сама модель («…_AR_B03»
            // на модели корпуса B03) или как наборы остальных разделов («01_Link_AR» рядом
            // с «01_Link_ES» и «01_Link_OV», а не «05_AR_Фасады»).
            var chosen = DisciplineCatalog.Preferred(
                code, matches, worksets, _preferences.EffectiveDisciplines, row.Entry.Name);

            _suggesting = true;
            try
            {
                if (chosen != null)
                {
                    row.Workset = chosen;
                    row.Note = "Набор по разделу «" + code + "»";
                    _autoWorkset.Add(row);
                }
                else if (matches.Count > 1)
                {
                    // Имена в приписке не для красоты: без них «наборов несколько» ничего
                    // не говорит о том, из чего именно выбирать.
                    row.Note = "Раздел «" + code + "»: подходят " + string.Join(", ", matches.Take(3)) +
                               (matches.Count > 3 ? " и ещё " + (matches.Count - 3) : string.Empty) + " — выберите";
                }
                else if (UsesDisciplineWorksets)
                {
                    // Молчать здесь нельзя: в проекте наборы по разделам заведены, значит
                    // отсутствие нужного — это ответ, а не «подбор не сработал».
                    row.Note = "Раздел «" + code + "»: набора с этим кодом в проекте нет";
                }
            }
            finally
            {
                _suggesting = false;
            }
        }

        /// <summary>
        /// В проекте заведены наборы под разделы. Считается один раз: наборы за время окна
        /// не меняются, а от ответа зависит, говорить ли про каждый ненайденный набор.
        /// </summary>
        private bool UsesDisciplineWorksets
        {
            get
            {
                if (_usesDisciplineWorksets == null)
                    _usesDisciplineWorksets = DisciplineCatalog.HasDisciplineWorksets(
                        _hostWorksets.Skip(1), _preferences.EffectiveDisciplines);

                return _usesDisciplineWorksets.Value;
            }
        }

        /// <summary>Прогоняет подбор по всем строкам — по кнопке включения подбора.</summary>
        private void ApplyWorksetSuggestions()
        {
            foreach (var row in _all)
                SuggestWorkset(row);

            UpdateSummary();
        }

        /// <summary>Снимает то, что поставил подбор; заданное руками не трогает.</summary>
        private void ClearWorksetSuggestions()
        {
            _suggesting = true;
            try
            {
                foreach (var row in _all)
                {
                    if (_autoWorkset.Contains(row))
                        row.Workset = LinkRow.ActiveWorkset;

                    // Приписки про раздел — тоже работа подбора: выключили его, значит
                    // и объяснять больше нечего.
                    if (row.Note.StartsWith("Набор по разделу", StringComparison.Ordinal) ||
                        row.Note.StartsWith("Раздел", StringComparison.Ordinal))
                        row.Note = string.Empty;
                }
            }
            finally
            {
                _suggesting = false;
            }

            _autoWorkset.Clear();
            UpdateSummary();
        }

        // ───────────────────────────── сохранённые наборы ─────────────────────────────

        private void ReloadSetNames()
        {
            var current = _setBox.Text;

            _setBox.Items.Clear();
            foreach (var name in LinkSetLibrary.Names())
                _setBox.Items.Add(name);

            _setBox.Text = current;
        }

        private string SetName => (_setBox.Text ?? string.Empty).Trim();

        private void OnLoadSet(object sender, RoutedEventArgs e)
        {
            if (SetName.Length == 0)
            {
                MessageBox.Show(this, "Выберите набор в списке или впишите его имя.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var entries = LinkSetLibrary.Load(SetName);
            if (entries.Count == 0)
            {
                MessageBox.Show(this, "Набор «" + SetName + "» пуст или его нет.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Add(entries, "моделей из набора");
        }

        private void OnSaveSet(object sender, RoutedEventArgs e)
        {
            if (SetName.Length == 0)
            {
                MessageBox.Show(this, "Впишите имя набора в поле слева.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_visible.Count == 0)
            {
                MessageBox.Show(this, "В таблице нечего сохранять.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // Сохраняется показанное — то же правило, что и у загрузки.
                LinkSetLibrary.Save(SetName, _visible.Select(row => row.Entry));
                ReloadSetNames();

                MessageBox.Show(this,
                    "Набор «" + SetName + "» сохранён: " + _visible.Count + " моделей.\n\n" +
                    LinkSetLibrary.FilePathFor(SetName),
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Сохранить набор не удалось.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnDeleteSet(object sender, RoutedEventArgs e)
        {
            if (SetName.Length == 0)
                return;

            var answer = MessageBox.Show(this, "Удалить сохранённый набор «" + SetName + "»?", WindowTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            try
            {
                LinkSetLibrary.Delete(SetName);
                ReloadSetNames();
                _setBox.Text = string.Empty;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Удалить набор не удалось.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ───────────────────────────── рабочие наборы ─────────────────────────────

        private LinkWorksetMode WorksetMode => (LinkWorksetMode)Math.Max(0, _worksetModeBox.SelectedIndex);

        private string Pattern => _patternBox.Text?.Trim() ?? string.Empty;

        private bool PatternContains => _ruleBox.SelectedIndex == 1;

        /// <summary>Подпись над списком наборов: при «закрыть все» галочка значит ровно обратное.</summary>
        private void UpdateWorksetCaption()
        {
            var closing = WorksetMode != LinkWorksetMode.CloseAll;

            _worksetCaption.Text = closing
                ? "Отмеченные наборы закроются во всех выбранных связях. Набора с таким именем в модели нет — строка просто пропускается."
                : "Открыты будут только отмеченные наборы, остальные закроются.";

            UpdateSummary();
        }

        private void AddWorkset(string name, bool isRemembered)
        {
            var text = (name ?? string.Empty).Trim();
            if (text.Length == 0)
                return;

            // Дубль считается по тому же правилу, по которому набор потом ищется в связи.
            // Иначе имя, отличающееся только пробелом по краю, тихо теряется: строкой
            // не появляется, а найтись при загрузке тоже не может.
            if (_worksets.Any(workset => LinkPreferences.SameWorkset(workset.Name, text)))
                return;

            var row = new WorksetRow(text, isRemembered);
            row.PropertyChanged += OnWorksetChanged;
            _worksets.Add(row);
        }

        /// <summary>
        /// Читает имена наборов у отмеченных моделей. Работа не мгновенная — идёт по сети
        /// к каждому файлу, — поэтому висит на кнопке и показывает итог.
        /// </summary>
        private void OnScanWorksets(object sender, RoutedEventArgs e)
        {
            if (_readWorksets == null)
                return;

            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "Отметьте связи, у которых нужно прочитать наборы.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LinkWorksetScan scan;
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                scan = _readWorksets(marked);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Прочитать рабочие наборы не удалось.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }

            foreach (var name in scan.Names)
                AddWorkset(name, false);

            _worksetsRead = true;
            RecountWorksets();

            var text = "Прочитано моделей: " + scan.Scanned + " из " + marked.Count +
                       ".\nРазных рабочих наборов: " + scan.Names.Count + ".";

            if (scan.Failures.Count > 0)
            {
                const int limit = 10;
                text += "\n\nНе удалось прочитать (" + scan.Failures.Count + "):\n• " +
                        string.Join("\n• ", scan.Failures.Take(limit));

                if (scan.Failures.Count > limit)
                    text += "\n… и ещё " + (scan.Failures.Count - limit);
            }

            MessageBox.Show(this, text, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Пересчитывает, в скольких отмеченных моделях встречается каждое имя набора.</summary>
        private void RecountWorksets()
        {
            var marked = Marked();

            // Считать есть по чему только если хоть у одной отмеченной связи наборы прочитаны:
            // иначе ноль означает «не смотрели», а не «нет ни в одной», и путать эти два
            // состояния нельзя — из второго растёт «плагин не закрыл набор».
            var counted = marked.Any(row => row.WorksetNames != null);

            foreach (var workset in _worksets)
            {
                workset.LinkCount = marked.Count(row => row.WorksetNames != null &&
                    row.WorksetNames.Any(name => LinkPreferences.SameWorkset(name, workset.Name)));

                workset.IsCounted = counted;
            }
        }

        private void OnMarkWorksets(object sender, RoutedEventArgs e)
        {
            if (Pattern.Length == 0)
            {
                MessageBox.Show(this, "Впишите строку правила — например «00_».", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var marked = 0;

            foreach (var workset in _worksets)
            {
                if (!Matches(workset.Name))
                    continue;

                workset.IsSelected = true;
                marked++;
            }

            if (marked == 0)
            {
                MessageBox.Show(this,
                    "В списке нет наборов, подходящих под правило.\n\n" +
                    "Это не помеха: правило применяется к каждой связи отдельно уже при загрузке, " +
                    "даже если сейчас список имён пуст.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private bool Matches(string name)
        {
            var pattern = Pattern;
            if (pattern.Length == 0)
                return false;

            // Обрезка та же, что в команде: правило должно отмечать в окне ровно то,
            // что потом закроется при загрузке.
            var trimmed = LinkPreferences.NormalizeWorkset(name);

            return PatternContains
                ? trimmed.IndexOf(pattern, StringComparison.CurrentCultureIgnoreCase) >= 0
                : trimmed.StartsWith(pattern, StringComparison.CurrentCultureIgnoreCase);
        }

        private void ToggleSelectedWorksets()
        {
            var rows = _worksetGrid.SelectedItems.OfType<WorksetRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);

            foreach (var row in rows)
                row.IsSelected = value;
        }

        private void OnWorksetChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WorksetRow.IsSelected))
                UpdateSummary();
        }

        // ───────────────────────────── таблица связей ─────────────────────────────

        private bool InScope(LinkRow row)
        {
            switch (_scopeBox.SelectedIndex)
            {
                case 1:
                    return !row.IsExisting;
                case 2:
                    return row.IsExisting;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Пересобирает таблицу. Скрытая строка теряет галочку — то же правило, что в окнах
        /// удаления: работает только то, что видно.
        /// </summary>
        private void RebuildVisible()
        {
            _visible.Clear();

            foreach (var row in _all)
            {
                if (InScope(row))
                    _visible.Add(row);
            }

            SetMany(row => InScope(row) && row.IsSelected);
        }

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(row => value && InScope(row));
        }

        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<LinkRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);
            var affected = new HashSet<LinkRow>(rows);

            SetMany(row => affected.Contains(row) ? value : row.IsSelected);
        }

        /// <summary>Пакетная простановка галочек: итог пересчитываем один раз в конце.</summary>
        private void SetMany(Func<LinkRow, bool> value)
        {
            _settingMany = true;

            foreach (var row in _all)
                row.IsSelected = value(row);

            _settingMany = false;

            RecountWorksets();
            UpdateSummary();
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space)
                return;

            ToggleSelectedRows();
            e.Handled = true;
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            // Ручная смена набора в таблице снимает со строки признак «подставлено подбором»:
            // при выключении подбора такой выбор трогать уже нельзя.
            if (e.PropertyName == nameof(LinkRow.Workset))
            {
                if (!_suggesting && _autoWorkset.Remove((LinkRow)sender))
                {
                    var row = (LinkRow)sender;
                    if (row.Note.StartsWith("Набор по разделу", StringComparison.Ordinal))
                        row.Note = string.Empty;
                }

                return;
            }

            if (_settingMany || e.PropertyName != nameof(LinkRow.IsSelected))
                return;

            RecountWorksets();
            UpdateSummary();
        }

        private List<LinkRow> Marked()
        {
            return _visible.Where(row => row.IsSelected).ToList();
        }

        private void UpdateSummary()
        {
            var marked = Marked();
            var fresh = marked.Count(row => !row.IsExisting);
            var again = marked.Count - fresh;

            if (marked.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = _all.Count == 0
                    ? "Список пуст: добавьте модели кнопками сверху или загрузите сохранённый набор."
                    : "Показано связей: " + _visible.Count + " из " + _all.Count + ". Не отмечено ни одной.";
            }
            else
            {
                _status.Foreground = SystemColors.ControlTextBrush;
                _status.Text = "Связать заново: " + fresh +
                               (again > 0 ? ", перезагрузить существующих: " + again : string.Empty) +
                               ". " + WorksetNote();
            }

            _loadButton.IsEnabled = marked.Count > 0;
            _loadButton.Content = marked.Count > 0 ? "Загрузить (" + marked.Count + ")" : "Загрузить";

            _syncingSelectAll = true;
            _selectAll.IsChecked = _visible.Count == 0 || marked.Count == 0
                ? false
                : marked.Count == _visible.Count ? true : (bool?)null;
            _syncingSelectAll = false;

            _scanButton.Content = _worksetsRead ? "Прочитать наборы заново" : "Прочитать наборы";
        }

        private string WorksetNote()
        {
            var names = MarkedWorksets().Count;

            if (names == 0 && Pattern.Length == 0)
                return WorksetMode == LinkWorksetMode.CloseAll
                    ? "Все рабочие наборы связей будут закрыты."
                    : "Рабочие наборы не трогаем.";

            var verb = WorksetMode == LinkWorksetMode.CloseAll ? "Открыть наборов: " : "Закрыть наборов: ";
            var note = verb + names;

            return Pattern.Length > 0 ? note + " плюс подходящие под правило «" + Pattern + "»." : note + ".";
        }

        private List<string> MarkedWorksets()
        {
            return _worksets.Where(workset => workset.IsSelected).Select(workset => workset.Name).ToList();
        }

        // ───────────────────────────── действия ─────────────────────────────

        private void OnLoad(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
                return;

            var fresh = marked.Count(row => !row.IsExisting);
            var again = marked.Count - fresh;

            var text = "Связать моделей: " + fresh + ".\n" +
                       "Размещение: " + _placementBox.SelectedItem + ".\n" +
                       WorksetNote() + "\n";

            if (HasHostWorksets)
            {
                var placed = marked.Count(row => row.Entry.Workset.Length > 0);

                text += placed == 0
                    ? "Рабочий набор проекта не задан ни одной связи — все встанут в активный набор.\n"
                    : "Рабочий набор проекта задан у " + placed + " из " + marked.Count +
                      "; остальные встанут в активный набор.\n";
            }

            text += "\n";

            if (again > 0)
                text += "Существующих связей будет перезагружено: " + again +
                        ". Перезагрузка идёт первой и в отмену не попадает — она стирает всю историю " +
                        "отмены документа. Ctrl+Z после неё вернёт только новые связи, но не то, " +
                        "что вы делали в проекте до нажатия.\n\n";

            text += "Revit на время загрузки перестанет отвечать. Продолжить?";

            var answer = MessageBox.Show(this, text, WindowTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

            if (answer != MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }

        /// <summary>
        /// Настройки сохраняются при любом закрытии окна — и по «Загрузить», и по «Закрыть», и по Esc.
        /// Ради этого всё и затевалось: набор «00_Shared levels and grids» отмечается один раз
        /// и подставляется в следующий проект сам.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            _preferences.Placement = (LinkPlacement)Math.Max(0, _placementBox.SelectedIndex);
            _preferences.IsAttachment = _attachmentBox.SelectedIndex == 1;
            _preferences.IsRelativePath = _relativeBox.IsChecked == true;
            _preferences.WorksetMode = WorksetMode;
            _preferences.WorksetPattern = Pattern;
            _preferences.WorksetPatternContains = PatternContains;
            _preferences.MatchProjectWorkset = _matchWorksetBox.IsChecked == true;

            _preferences.Worksets.Clear();
            _preferences.Worksets.AddRange(MarkedWorksets());

            _preferences.Save();

            base.OnClosed(e);
        }
    }
}
