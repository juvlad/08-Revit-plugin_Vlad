using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// Окно кнопки «Базовый файл»: одна координационная модель и список того, что с ней
    /// сделать при загрузке в модель раздела.
    ///
    /// Работа, которую окно собирает в один щелчок, в Revit выглядит так: связать
    /// координационный файл, получить из него общие координаты, переименовать площадку,
    /// закрепить связь булавкой, перейти в «00_Shared levels and grids» и только потом
    /// начать копирование уровней и осей. Пять диалогов в разных углах ленты, и порядок
    /// между ними важен.
    ///
    /// Последний шаг — копирование мониторингом — окно не обещает: API Revit создавать
    /// связи мониторинга не умеет вовсе (есть только чтение уже существующих). Всё, что
    /// можно сделать честно, — открыть сам режим «Копирование/Мониторинг», и это последняя
    /// галочка в списке.
    ///
    /// Окно собрано кодом, без XAML — проект не включает WPF-сборку разметки.
    /// </summary>
    internal sealed class BaseFileWindow : Window
    {
        private const string WindowTitle = "Базовый файл";

        private readonly IReadOnlyList<LinkRow> _existing;
        private readonly BaseFilePreferences _preferences;

        /// <summary>Настройки «Link Manager» — ради общего списка серверов Revit Server.</summary>
        private readonly LinkPreferences _linkPreferences;

        /// <summary>Рабочие наборы открытого проекта — с «(активный)» первой строкой.</summary>
        private readonly List<string> _hostWorksets = new List<string> { LinkRow.ActiveWorkset };

        private readonly TextBlock _modelName;
        private readonly TextBlock _modelPath;
        private readonly ComboBox _placementBox;
        private readonly ComboBox _linkWorksetBox;
        private readonly CheckBox _acquireBox;
        private readonly CheckBox _renameBox;
        private readonly TextBox _siteBox;
        private readonly CheckBox _pinBox;
        private readonly CheckBox _activateBox;
        private readonly ComboBox _worksetBox;
        private readonly CheckBox _monitorBox;
        private readonly TextBlock _status;
        private readonly Button _runButton;

        private LinkRow _row;

        /// <summary>Координационная модель, с которой команде работать.</summary>
        public LinkRow Selected { get; private set; }

        /// <summary>Что именно с ней делать.</summary>
        public BaseFilePreferences Preferences => _preferences;

        /// <param name="existing">Связи, уже стоящие в проекте: выбранную модель ищем среди них.</param>
        /// <param name="hostWorksets">
        /// Рабочие наборы открытого проекта. Пустой список — проект не совмещённый,
        /// и оба относящихся к наборам шага выключаются.
        /// </param>
        public BaseFileWindow(IReadOnlyList<LinkRow> existing, IReadOnlyList<string> hostWorksets)
        {
            _existing = existing ?? new List<LinkRow>();
            _preferences = BaseFilePreferences.Load();
            _linkPreferences = LinkPreferences.Load();

            if (hostWorksets != null)
                _hostWorksets.AddRange(hostWorksets);

            Title = WindowTitle;
            Width = 720;
            Height = 620;
            MinWidth = 560;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _modelName = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap
            };

            _modelPath = new TextBlock
            {
                Foreground = SystemColors.GrayTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };

            _placementBox = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center };
            _placementBox.Items.Add("Совмещение внутренних начал");
            _placementBox.Items.Add("По общим координатам");
            _placementBox.Items.Add("Центр в центр");
            _placementBox.Items.Add("По расположению площадки проекта");
            _placementBox.SelectedIndex = PlacementIndex(_preferences.Placement);
            _placementBox.ToolTip =
                "Для координационного файла обычно «Совмещение внутренних начал»: общие координаты " +
                "из него ещё только предстоит получить, и вставлять по ним пока нечего.";

            // Список редактируемый по той же причине, что и у набора для перехода: в новом
            // разделе «01_Link_BM» ещё не заведён, выбрать его из списка нечем, а команда
            // такой набор создаст.
            _linkWorksetBox = new ComboBox
            {
                Width = 240,
                IsEditable = true,
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = _hostWorksets,
                IsEnabled = HasHostWorksets,
                ToolTip = "Рабочий набор проекта, в который встанет сама связь. " +
                          "Набора с таким именем в проекте нет — команда его создаст."
            };

            _acquireBox = Option("Получить общие координаты из базового файла",
                "Revit: «Координаты → Получить координаты». Общая система координат проекта " +
                "станет такой же, как у координационного файла.",
                _preferences.Acquire);

            _renameBox = Option("Переименовать площадку проекта в:",
                "Revit: «Расположение → Площадка». Имя площадки — то, что потом видно в диалоге " +
                "«Расположение проекта» и в списке площадок у смежников.",
                _preferences.Rename);

            _siteBox = new TextBox
            {
                Width = 260,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2),
                Text = _preferences.Site
            };

            _pinBox = Option("Закрепить связь булавкой",
                "Базовый файл вставлен по координатам, и случайный сдвиг мышью потом ищут всей командой.",
                _preferences.Pin);

            _activateBox = Option("Перейти в рабочий набор:",
                "Активный набор проекта — тот, в который попадут скопированные уровни и оси. " +
                "Набора с таким именем в проекте нет — команда его создаст.",
                _preferences.Activate);
            _activateBox.IsEnabled = HasHostWorksets;

            _worksetBox = new ComboBox
            {
                Width = 260,
                IsEditable = true,
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = _hostWorksets.Skip(1).ToList(),
                Text = _preferences.Workset,
                IsEnabled = HasHostWorksets
            };

            _monitorBox = Option("Открыть режим «Копирование/Мониторинг → Выбрать связь»",
                "Создавать связи мониторинга API Revit не умеет — их делает только сам Revit. " +
                "Команда доводит до режима: останется выбрать связь и отметить уровни и оси.",
                _preferences.Monitor);

            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

            _runButton = new Button
            {
                Content = "Выполнить",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true,
                IsEnabled = false
            };
            _runButton.Click += OnRun;

            var closeButton = new Button
            {
                Content = "Закрыть",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(closeButton);

            if (!HasHostWorksets)
                _activateBox.IsChecked = false;

            // Text у редактируемого ComboBox задаётся после разметки: до применения шаблона
            // он теряется, если в списке нет строки с таким же значением.
            _worksetBox.Text = _preferences.Workset;
            _linkWorksetBox.Text = Named(_preferences.LinkWorkset);

            Show(_preferences.Model == null ? null : Match(_preferences.Model));
        }

        private bool HasHostWorksets => _hostWorksets.Count > 1;

        // ───────────────────────────── разметка ─────────────────────────────

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // подсказка
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // выбор модели
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // сама модель
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // шаги
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // статус + кнопки

            var hint = new TextBlock
            {
                Text = "Выберите координационный файл и отметьте, что с ним сделать. " +
                       "Всё отмеченное выполняется одной операцией и откатывается одним Ctrl+Z.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var sources = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            sources.Children.Add(SourceButton("Файл…", "Обычный файл .rvt: диск или сетевая папка.", OnPickFile));
            sources.Children.Add(SourceButton("Revit Server…", "Просмотр папок и моделей на Revit Server.", OnBrowseServer));
            sources.Children.Add(SourceButton("BIM360…", "Просмотр учётных записей, проектов и папок BIM360/ACC.", OnBrowseCloud));
            sources.Children.Add(SourceButton("BIM360 по GUID…", "Ввод облачной модели парой GUID — если просмотр недоступен.", OnPickCloudByGuid));

            Grid.SetRow(sources, 1);
            root.Children.Add(sources);

            var model = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 10),
                Background = SystemColors.ControlBrush
            };
            var inner = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
            inner.Children.Add(_modelName);
            inner.Children.Add(_modelPath);
            model.Children.Add(inner);

            Grid.SetRow(model, 2);
            root.Children.Add(model);

            var steps = new StackPanel();

            steps.Children.Add(new TextBlock
            {
                Text = "Что сделать:",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            steps.Children.Add(Row(
                new TextBlock
                {
                    Text = "Связать с проектом, размещение:",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(22, 0, 8, 0)
                },
                _placementBox));

            steps.Children.Add(Row(
                new TextBlock
                {
                    Text = "класть связь в рабочий набор:",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(22, 0, 8, 0),
                    Foreground = HasHostWorksets ? SystemColors.ControlTextBrush : SystemColors.GrayTextBrush
                },
                _linkWorksetBox));

            steps.Children.Add(_acquireBox);
            steps.Children.Add(Row(_renameBox, _siteBox));
            steps.Children.Add(_pinBox);
            steps.Children.Add(Row(_activateBox, _worksetBox));
            steps.Children.Add(_monitorBox);

            if (!HasHostWorksets)
            {
                steps.Children.Add(new TextBlock
                {
                    Text = "Проект не совмещённый — рабочих наборов в нём нет, и оба шага с наборами выключены.",
                    Foreground = SystemColors.GrayTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(22, 6, 0, 0)
                });
            }

            Grid.SetRow(steps, 3);
            root.Children.Add(steps);

            var bottom = new Grid();
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

        private static CheckBox Option(string text, string tooltip, bool isChecked)
        {
            return new CheckBox
            {
                Content = text,
                IsChecked = isChecked,
                ToolTip = tooltip,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(22, 5, 8, 5)
            };
        }

        private static UIElement Row(UIElement left, UIElement right)
        {
            var panel = new WrapPanel();
            panel.Children.Add(left);
            panel.Children.Add(right);

            return panel;
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

        // ───────────────────────────── откуда берётся модель ─────────────────────────────

        private void OnPickFile(object sender, RoutedEventArgs e)
        {
            Take(ModelPicker.Files(this, false));
        }

        private void OnBrowseServer(object sender, RoutedEventArgs e)
        {
            Take(ModelPicker.Server(this, WindowTitle, _linkPreferences, Keys));
        }

        private void OnBrowseCloud(object sender, RoutedEventArgs e)
        {
            Take(ModelPicker.Cloud(this, WindowTitle, Keys));
        }

        private void OnPickCloudByGuid(object sender, RoutedEventArgs e)
        {
            Take(ModelPicker.CloudByGuid(this, Region()));
        }

        /// <summary>
        /// Модель здесь одна, а источники отдают список: из дерева можно отметить несколько.
        /// Берём первую и говорим об этом — молча отбросить остальные значило бы соврать.
        /// </summary>
        private void Take(IReadOnlyList<LinkEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return;

            Show(Match(entries[0]));

            if (entries.Count > 1)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Базовый файл один: взята первая из отмеченных моделей.";
            }
        }

        /// <summary>
        /// Связь на эту модель уже может стоять в проекте — тогда работаем с ней, а не заводим
        /// вторую: Revit на повторную <c>Create</c> всё равно ответит «такая связь уже есть».
        /// </summary>
        private LinkRow Match(LinkEntry entry)
        {
            var known = _existing.FirstOrDefault(row => string.Equals(row.Key, entry.Key, StringComparison.Ordinal));

            return known ?? new LinkRow(entry);
        }

        private void Show(LinkRow row)
        {
            _row = row;

            if (row == null)
            {
                _modelName.Text = "Модель не выбрана";
                _modelPath.Text = "Выберите координационный файл кнопками выше.";
                _runButton.IsEnabled = false;
                return;
            }

            _modelName.Text = row.Name;
            _modelPath.Text = row.Kind + " · " + row.Location;
            _runButton.IsEnabled = true;

            // У связи, уже стоящей в проекте, показываем её нынешний набор — пользователь должен
            // видеть, что меняет. У новой набор берётся из настроек и подставлен ещё до выбора
            // модели: «01_Link_BM» один и тот же во всех разделах.
            if (row.IsExisting)
                _linkWorksetBox.Text = Named(row.Entry.Workset);

            _status.Foreground = SystemColors.GrayTextBrush;
            _status.Text = row.IsExisting
                ? "Связь на эту модель в проекте уже есть — она и будет использована."
                : string.Empty;
        }

        /// <summary>
        /// Имя набора для показа в поле: пустое значит «активный», и пустая строка
        /// в выпадающем списке выглядела бы недосмотром.
        /// </summary>
        private static string Named(string workset)
        {
            return string.IsNullOrEmpty(workset) ? LinkRow.ActiveWorkset : workset;
        }

        /// <summary>Ключи моделей, которые в дереве показывать серыми: выбранная — уже взята.</summary>
        private HashSet<string> Keys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (_row != null)
                keys.Add(_row.Key);

            return keys;
        }

        /// <summary>Регион, с которым открывается окно ввода GUID: тот же, что у прошлой модели.</summary>
        private string Region()
        {
            if (_row != null && _row.Entry.Origin == LinkOrigin.Cloud && _row.Entry.Region.Length > 0)
                return _row.Entry.Region;

            var remembered = _preferences.Model;

            return remembered != null && remembered.Origin == LinkOrigin.Cloud && remembered.Region.Length > 0
                ? remembered.Region
                : "US";
        }

        // ───────────────────────────── выполнение ─────────────────────────────

        private void OnRun(object sender, RoutedEventArgs e)
        {
            if (_row == null)
                return;

            var site = _siteBox.Text.Trim();
            if (_renameBox.IsChecked == true && site.Length == 0)
            {
                MessageBox.Show(this,
                    "Впишите имя площадки или снимите галочку «Переименовать площадку проекта».",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                _siteBox.Focus();
                return;
            }

            var workset = (_worksetBox.Text ?? string.Empty).Trim();
            if (_activateBox.IsChecked == true && workset.Length == 0)
            {
                MessageBox.Show(this,
                    "Впишите имя рабочего набора или снимите галочку «Перейти в рабочий набор».",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                _worksetBox.Focus();
                return;
            }

            Collect();
            Selected = _row;
            DialogResult = true;
        }

        /// <summary>
        /// Переносит состояние окна в настройки. Зовётся и при «Выполнить», и при закрытии:
        /// поля этого окна — то же, что формулы и наборы связей, они должны переживать Revit.
        /// </summary>
        private void Collect()
        {
            var linkWorkset = (_linkWorksetBox.Text ?? string.Empty).Trim();
            if (linkWorkset == LinkRow.ActiveWorkset)
                linkWorkset = string.Empty;

            if (_row != null)
            {
                // Набор проекта хранится в самой записи — оттуда его берёт команда.
                _row.Workset = linkWorkset;
                _preferences.Model = _row.Entry;
            }

            _preferences.LinkWorkset = linkWorkset;
            _preferences.Placement = PlacementFor(_placementBox.SelectedIndex);
            _preferences.Site = _siteBox.Text.Trim();
            _preferences.Workset = (_worksetBox.Text ?? string.Empty).Trim();
            _preferences.Acquire = _acquireBox.IsChecked == true;
            _preferences.Rename = _renameBox.IsChecked == true;
            _preferences.Pin = _pinBox.IsChecked == true;
            _preferences.Activate = _activateBox.IsChecked == true;
            _preferences.Monitor = _monitorBox.IsChecked == true;
        }

        protected override void OnClosed(EventArgs e)
        {
            Collect();
            _preferences.Save();

            // Имя сервера Revit Server могло появиться при просмотре — оно общее с «Link Manager».
            _linkPreferences.Save();

            base.OnClosed(e);
        }

        private static int PlacementIndex(LinkPlacement placement)
        {
            switch (placement)
            {
                case LinkPlacement.Shared:
                    return 1;
                case LinkPlacement.Centered:
                    return 2;
                case LinkPlacement.Site:
                    return 3;
                default:
                    return 0;
            }
        }

        private static LinkPlacement PlacementFor(int index)
        {
            switch (index)
            {
                case 1:
                    return LinkPlacement.Shared;
                case 2:
                    return LinkPlacement.Centered;
                case 3:
                    return LinkPlacement.Site;
                default:
                    return LinkPlacement.Origin;
            }
        }
    }
}
