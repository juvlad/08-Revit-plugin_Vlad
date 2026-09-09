using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// Окно «Комплект по корпусу»: плагин сам предлагает модели смежников, которые нужно
    /// связать с открытой моделью.
    ///
    /// Работа, ради которой оно написано: в каждую модель каждого корпуса грузятся связями
    /// одни и те же разделы — АР, КР, ES, PS, PT, OV, VK, — и каждый раз их выбирают руками
    /// по папкам, следя, чтобы не подцепить соседний корпус. Между тем в проекте всё названо
    /// так, что выбирать не за чем: папки разделов зовутся <c>3.0_AR</c>, <c>4.2_KR</c>,
    /// <c>5.1_ES</c>, а в имени модели стоит номер корпуса — <c>MK3-VSC-B01-AR</c>.
    ///
    /// Поэтому окно ничего не спрашивает без нужды: номер корпуса берётся из имени открытой
    /// модели, папка проекта — из того, где эта модель лежит, и поиск запускается сам при
    /// открытии. Пользователю остаётся посмотреть на таблицу и нажать «Добавить».
    ///
    /// Найденное показывается **до** того, как что-то будет связано, и это не перестраховка:
    /// разбор имён — эвристика (см. <see cref="ModelKit"/>), а раздел, в котором моделей
    /// оказалось несколько, окно отмечать отказывается — там выбор за человеком.
    ///
    /// Окно собрано кодом, без XAML — проект не включает WPF-сборку разметки.
    /// </summary>
    internal sealed class ModelKitWindow : Window
    {
        private const string WindowTitle = "Комплект по корпусу";

        private readonly ObservableCollection<ModelKitRow> _rows = new ObservableCollection<ModelKitRow>();

        private readonly HostModel _host;
        private readonly LinkPreferences _preferences;
        private readonly Func<HashSet<string>> _known;

        private readonly ComboBox _buildingBox;
        private readonly TextBox _codesBox;
        private readonly CheckBox _deepBox;
        private readonly TextBlock _rootText;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _addButton;

        private ModelFolder _root;

        /// <summary>Итог последнего поиска: к нему дописывается число отмеченного.</summary>
        private string _lastSummary = "Нажмите «Найти», чтобы обойти папки разделов.";

        /// <summary>Модели, которые пользователь согласился добавить в список связей.</summary>
        public IReadOnlyList<LinkEntry> Selected { get; private set; } = new List<LinkEntry>();

        /// <param name="host">Открытая модель: из её имени берётся корпус, из её папки — где искать.</param>
        /// <param name="known">Ключи моделей, уже собранных в таблице связей, — их предлагать незачем.</param>
        public ModelKitWindow(HostModel host, LinkPreferences preferences, Func<HashSet<string>> known)
        {
            _host = host ?? new HostModel(null, null, string.Empty);
            _preferences = preferences;
            _known = known ?? (() => new HashSet<string>(StringComparer.Ordinal));

            Title = WindowTitle;
            Width = 900;
            Height = 640;
            MinWidth = 680;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _buildingBox = new ComboBox
            {
                Width = 140,
                IsEditable = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "Кусок имени модели, по которому отбираются модели корпуса.\n" +
                    "В списке — куски имени открытой модели; обычно нужен третий:\n" +
                    "MK3-VSC-B01-AR → B01. Можно вписать своё."
            };

            foreach (var token in ModelKit.Tokens(_host.Name))
                _buildingBox.Items.Add(token);

            _buildingBox.Text = InitialBuilding();

            _codesBox = new TextBox
            {
                Text = string.Join(", ", _preferences.EffectiveKit),
                MinWidth = 320,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2),
                ToolTip =
                    "Разделы, которые нужно догрузить. Папка считается папкой раздела,\n" +
                    "если код стоит в её имени отдельным словом: «3.0_AR» — да, «Provod» — нет.\n" +
                    "Список сохраняется в links\\_settings.txt (строки KIT)."
            };

            _deepBox = new CheckBox
            {
                Content = "и во вложенных папках раздела",
                IsChecked = _preferences.KitDeep,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip =
                    "Заходить внутрь папки раздела, если модели лежат не прямо в ней («3.0_AR\\Модели»).\n" +
                    "Каждая папка облака — запрос по сети, поэтому глубже двух уровней поиск не идёт."
            };

            _rootText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0)
            };

            _grid = BuildGrid();
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _addButton = new Button
            {
                Content = "Добавить",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _addButton.Click += OnAdd;

            var cancelButton = new Button
            {
                Content = "Закрыть",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(cancelButton);

            _root = StartFolder();
            ShowRoot();
            UpdateSummary();

            // Поиск сам, при открытии: спрашивать «искать ли», когда и корпус, и папка уже
            // известны, значит просить лишний щелчок ровно за тем, ради чего окно и открыли.
            Loaded += (sender, args) =>
            {
                if (_root != null && Building.Length > 0)
                    Search(false);
            };
        }

        // ───────────────────────────── разметка ─────────────────────────────

        private UIElement BuildLayout(Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // подсказка
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // корпус
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // разделы
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // где искать
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // таблица
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // статус + кнопки

            var hint = new TextBlock
            {
                Text = "Плагин ищет модели вашего корпуса в папках разделов и предлагает их связать. " +
                       "Номер корпуса взят из имени открытой модели, папка — из того, где она лежит; " +
                       "и то и другое можно поменять.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var building = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            building.Children.Add(Caption("Корпус:"));
            building.Children.Add(_buildingBox);
            building.Children.Add(new TextBlock
            {
                Text = _host.Name.Length > 0 ? "из имени: " + _host.Name : "открытая модель ещё не сохранена",
                Foreground = SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            });

            Grid.SetRow(building, 1);
            root.Children.Add(building);

            var codes = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            codes.Children.Add(Caption("Разделы:"));
            codes.Children.Add(_codesBox);
            codes.Children.Add(_deepBox);

            Grid.SetRow(codes, 2);
            root.Children.Add(codes);

            var where = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            where.Children.Add(Caption("Где искать:"));
            where.Children.Add(_rootText);
            where.Children.Add(SourceButton("Папка модели", "Вернуться к папке, в которой лежит открытая модель.", OnRootAuto));
            where.Children.Add(SourceButton("На сервере…", "Выбрать папку на Revit Server.", OnRootServer));
            where.Children.Add(SourceButton("В BIM360…", "Выбрать папку в BIM360 / Autodesk Docs.", OnRootCloud));
            where.Children.Add(SourceButton("На диске…", "Выбрать папку на диске или в сети — указанием любой модели в ней.", OnRootFile));
            where.Children.Add(SourceButton("Найти", "Обойти папки разделов заново.", OnFind));

            Grid.SetRow(where, 3);
            root.Children.Add(where);

            Grid.SetRow(_grid, 4);
            root.Children.Add(_grid);

            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_addButton);
            buttons.Children.Add(cancelButton);
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

        private static Button SourceButton(string text, string tooltip, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(8, 0, 0, 4),
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
                Header = string.Empty,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate()
            });

            grid.Columns.Add(GridBuilder.TextColumn("Раздел", "Discipline", new DataGridLength(80)));
            grid.Columns.Add(GridBuilder.TextColumn("Модель", "Name", new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(GridBuilder.TextColumn("Папка", "Folder", new DataGridLength(180)));
            grid.Columns.Add(GridBuilder.TextColumn("Состояние", "Status", new DataGridLength(200)));

            grid.MouseDoubleClick += (sender, args) => ToggleSelected();
            grid.PreviewKeyDown += OnGridKeyDown;

            return grid;
        }

        // ───────────────────────────── откуда и что искать ─────────────────────────────

        private string Building => (_buildingBox.Text ?? string.Empty).Trim();

        /// <summary>Разделы из поля ввода; пусто — список по умолчанию, иначе искать было бы нечего.</summary>
        private IReadOnlyList<string> Codes
        {
            get
            {
                var codes = (_codesBox.Text ?? string.Empty)
                    .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(code => code.Trim())
                    .Where(code => code.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return codes.Count > 0 ? (IReadOnlyList<string>)codes : DisciplineCatalog.KitDefaults;
            }
        }

        /// <summary>
        /// Номер корпуса при открытии: из имени открытой модели тем куском, который выбран
        /// в настройках. Имени нет или кусков меньше — остаётся номер из прошлого раза.
        /// </summary>
        private string InitialBuilding()
        {
            var building = ModelKit.Building(_host.Name, _preferences.BuildingToken);

            return building.Length > 0 ? building : _preferences.KitBuilding ?? string.Empty;
        }

        /// <summary>
        /// Папка, с которой начинать: та, где лежит открытая модель (поднявшись из папки
        /// раздела на уровень выше). Модель не сохранена — папка из прошлого раза.
        /// </summary>
        private ModelFolder StartFolder()
        {
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                var root = ModelKit.Root(_host.Folder, Codes);

                return root ?? ModelFolder.Parse(_preferences.KitRoot);
            }
            catch (Exception)
            {
                // Папку не выяснили — её укажут кнопками рядом.
                return ModelFolder.Parse(_preferences.KitRoot);
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }
        }

        private void ShowRoot()
        {
            _rootText.Text = _root == null ? "не выбрана" : _root.Display;
            _rootText.Foreground = _root == null ? Brushes.Firebrick : SystemColors.ControlTextBrush;
            _rootText.ToolTip = _rootText.Text;
        }

        private void OnRootAuto(object sender, RoutedEventArgs e)
        {
            if (_host.Folder == null)
            {
                MessageBox.Show(this,
                    "Где лежит открытая модель, выяснить не удалось: проект ни разу не сохранён " +
                    "или открыт отсоединённым. Укажите папку соседними кнопками.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetRoot(ModelKit.Root(_host.Folder, Codes));
        }

        private void OnRootServer(object sender, RoutedEventArgs e)
        {
            SetRoot(ModelPicker.PickServerFolder(this, WindowTitle, _preferences));
        }

        private void OnRootCloud(object sender, RoutedEventArgs e)
        {
            SetRoot(ModelPicker.PickCloudFolder(this, WindowTitle));
        }

        private void OnRootFile(object sender, RoutedEventArgs e)
        {
            SetRoot(ModelPicker.PickFileFolder(this));
        }

        private void SetRoot(ModelFolder folder)
        {
            if (folder == null)
                return;

            _root = folder;
            ShowRoot();
        }

        // ───────────────────────────── поиск ─────────────────────────────

        private void OnFind(object sender, RoutedEventArgs e)
        {
            Search(true);
        }

        /// <param name="loud">Показывать ли отказы диалогом: при поиске самим окном лишний диалог не нужен.</param>
        private void Search(bool loud)
        {
            if (Building.Length == 0)
            {
                if (loud)
                    MessageBox.Show(this, "Впишите номер корпуса — например «B01».", WindowTitle,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_root == null)
            {
                if (loud)
                    MessageBox.Show(this, "Укажите папку, в которой искать.", WindowTitle,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var codes = Codes;
            var keys = _known();
            if (_host.Key.Length > 0)
                keys.Add(_host.Key);

            ModelKitScan scan;
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                scan = ModelKit.Find(_root, Building, codes, _deepBox.IsChecked == true, key => keys.Contains(key));
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Просмотреть папки не удалось.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }

            // Поиск мог подняться на уровень выше — показываем ту папку, в которой он и шёл,
            // иначе в следующий раз запомнилась бы не та.
            if (scan.Root != null)
            {
                _root = scan.Root;
                ShowRoot();
            }

            Fill(scan, codes);
        }

        /// <summary>
        /// Раскладывает найденное по строкам. Отмечается только то, что можно отметить не гадая:
        /// раздел с одной моделью. Несколько моделей в разделе — строки без галочек и приписка
        /// в «Состоянии»: выбор за человеком, как и при подборе рабочего набора.
        /// </summary>
        private void Fill(ModelKitScan scan, IReadOnlyList<string> codes)
        {
            foreach (var row in _rows)
                row.PropertyChanged -= OnRowChanged;

            _rows.Clear();

            var order = codes
                .Select((code, index) => new { code, index })
                .ToDictionary(pair => pair.code, pair => pair.index, StringComparer.OrdinalIgnoreCase);

            var groups = scan.Hits
                .GroupBy(hit => hit.Discipline, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => order.ContainsKey(group.Key) ? order[group.Key] : int.MaxValue)
                .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var group in groups)
            {
                var choices = group.Count(hit => !hit.IsKnown);

                foreach (var hit in group.OrderBy(hit => hit.Entry.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var status = Status(hit, choices);

                    var row = new ModelKitRow(hit, !hit.IsKnown && choices == 1, status);
                    row.PropertyChanged += OnRowChanged;
                    _rows.Add(row);
                }
            }

            UpdateSummary(scan, codes);
            Complain(scan);
        }

        /// <summary>
        /// Почему строка не отмечена. Свою же модель отличаем от чужой уже собранной: «уже
        /// в списке» про открытый проект звучало бы как ошибка подбора, а это его законное место
        /// в разделе — и раздел благодаря ей не попадёт в «не нашлось».
        /// </summary>
        private string Status(ModelKitHit hit, int choices)
        {
            if (hit.Entry.Key == _host.Key)
                return "это открытая модель";

            if (hit.IsKnown)
                return "уже в списке связей";

            return choices > 1 ? "в разделе несколько — выберите нужную" : string.Empty;
        }

        /// <summary>Непрочитанные папки — не мелочь: комплект в них мог оказаться неполным.</summary>
        private void Complain(ModelKitScan scan)
        {
            if (scan.Failures.Count == 0 && !scan.IsTruncated)
                return;

            const int limit = 10;
            var text = string.Empty;

            if (scan.Failures.Count > 0)
            {
                text = "Не удалось прочитать папок: " + scan.Failures.Count + "\n• " +
                       string.Join("\n• ", scan.Failures.Take(limit));

                if (scan.Failures.Count > limit)
                    text += "\n… и ещё " + (scan.Failures.Count - limit);
            }

            if (scan.IsTruncated)
            {
                if (text.Length > 0)
                    text += "\n\n";

                text += "Просмотр остановлен: папок оказалось слишком много. " +
                        "Похоже, поиск начат не с той папки — укажите ту, внутри которой лежат папки разделов.";
            }

            MessageBox.Show(this, text, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ───────────────────────────── таблица ─────────────────────────────

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModelKitRow.IsSelected))
                UpdateSummary();
        }

        private void ToggleSelected()
        {
            var rows = _grid.SelectedItems.OfType<ModelKitRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);

            foreach (var row in rows)
                row.IsSelected = value;
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space)
                return;

            ToggleSelected();
            e.Handled = true;
        }

        private void UpdateSummary()
        {
            UpdateSummary(null, null);
        }

        private void UpdateSummary(ModelKitScan scan, IReadOnlyList<string> codes)
        {
            var marked = _rows.Count(row => row.IsSelected);

            if (scan != null)
            {
                var missing = scan.Missing(codes);

                _lastSummary = _rows.Count == 0
                    ? "Ничего не нашлось. Прочитано папок: " + scan.Folders + "."
                    : "Найдено моделей: " + _rows.Count +
                      ", разделов: " + (codes.Count - missing.Count) + " из " + codes.Count +
                      (missing.Count > 0 ? ". Не нашлось: " + string.Join(", ", missing) : string.Empty) + ".";
            }

            _status.Foreground = marked == 0 ? SystemColors.GrayTextBrush : SystemColors.ControlTextBrush;
            _status.Text = marked == 0
                ? _lastSummary
                : _lastSummary + " Отмечено: " + marked + ".";

            _addButton.IsEnabled = marked > 0;
            _addButton.Content = marked > 0 ? "Добавить (" + marked + ")" : "Добавить";
        }

        // ───────────────────────────── действия ─────────────────────────────

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            var marked = _rows.Where(row => row.IsSelected).Select(row => row.Entry).ToList();
            if (marked.Count == 0)
                return;

            Selected = marked;
            DialogResult = true;
        }

        /// <summary>
        /// Настройки запоминаются при любом закрытии — как и в «Link Manager», из которого
        /// окно открыто: в файл их пишет он сам, здесь только заполняются поля.
        /// Ради этого всё и затевалось: во второй раз окно откроется уже настроенным.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            _preferences.KitBuilding = Building;
            _preferences.KitDeep = _deepBox.IsChecked == true;

            _preferences.Kit.Clear();
            _preferences.Kit.AddRange(Codes);

            if (_root != null)
                _preferences.KitRoot = _root.Format();

            // Номер куска запоминается, только если выбранное действительно им является:
            // вписанное руками значение к разбору имени отношения не имеет, и подменять
            // им настройку — значит испортить подстановку в следующем проекте.
            var tokens = ModelKit.Tokens(_host.Name);
            for (var index = 0; index < tokens.Count; index++)
            {
                if (!string.Equals(tokens[index], Building, StringComparison.OrdinalIgnoreCase))
                    continue;

                _preferences.BuildingToken = index + 1;
                break;
            }

            base.OnClosed(e);
        }
    }
}
