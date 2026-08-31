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

namespace VladTools.UI
{
    /// <summary>
    /// Окно «Удалить общие параметры проекта»: таблица всех общих параметров открытого проекта
    /// с галочкой слева у каждой строки. Удаляются отмеченные параметры.
    ///
    /// Галочки ставятся так же, как в окне «Удалить параметры» для семейства: вручную
    /// (щелчок, двойной щелчок по строке, пробел, галочка в шапке — все сразу) и правилом
    /// «начинаются с» / «содержат», которое работает как поиск: подходящие имена остаются
    /// в таблице и отмечаются, остальные из неё уходят, а «Инвертировать поиск» меняет стороны
    /// местами. К ним добавлен отбор «Показывать»: кроме параметров
    /// проекта, в файле живут общие параметры, приехавшие с загруженными семействами,
    /// и на глаз они друг от друга не отличаются.
    ///
    /// Удаляется только то, что показано в таблице: скрытая строка теряет галочку.
    ///
    /// Отдельно — защита семейств: параметр, которым помечен размер внутри семейства,
    /// держит его геометрию. В проекте этого не видно, узнать можно только открыв каждое
    /// семейство, поэтому проверка не идёт сама, а запускается кнопкой «Проверить семейства».
    ///
    /// Окно собрано кодом, без XAML — проект не включает WPF-сборку разметки.
    /// </summary>
    internal sealed class DeleteProjectParametersWindow : Window
    {
        private const string WindowTitle = "Удалить общие параметры проекта";

        private readonly IReadOnlyList<ProjectParameterRow> _all;
        private readonly ObservableCollection<ProjectParameterRow> _visible = new ObservableCollection<ProjectParameterRow>();

        private readonly int _familyCount;
        private readonly Func<bool, FamilyDimensionScan> _scanFamilies;

        private readonly ComboBox _scopeBox;
        private readonly ComboBox _ruleBox;
        private readonly TextBox _patternBox;
        private readonly CheckBox _caseBox;
        private readonly CheckBox _invertBox;
        private readonly CheckBox _skipDimensionsBox;
        private readonly Button _scanButton;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _deleteButton;

        private bool _syncingSelectAll;
        private bool _settingMany;
        private int _pendingFamilies;
        private DateTime? _checkedAt;

        /// <summary>Параметры, которые пользователь подтвердил к удалению.</summary>
        public IReadOnlyList<ProjectParameterRow> Selected { get; private set; } = new List<ProjectParameterRow>();

        /// <param name="familyCount">Сколько всего семейств в проекте — для кнопки «Проверить заново».</param>
        /// <param name="pendingFamilies">Сколько из них сохранённая проверка не покрывает.</param>
        /// <param name="checkedAt">Когда сохранена проверка; её ещё не было — null.</param>
        /// <param name="scanFamilies">
        /// Проверка семейств на метки размеров; аргумент — «пройти заново, кэш не читать».
        /// Всю работу с Revit делает команда.
        /// </param>
        public DeleteProjectParametersWindow(
            IReadOnlyList<ProjectParameterRow> parameters,
            int familyCount,
            int pendingFamilies,
            DateTime? checkedAt,
            Func<bool, FamilyDimensionScan> scanFamilies)
        {
            _all = parameters ?? new List<ProjectParameterRow>();
            _familyCount = familyCount;
            _pendingFamilies = pendingFamilies;
            _checkedAt = checkedAt;
            _scanFamilies = scanFamilies;

            Title = WindowTitle;
            Width = 1000;
            Height = 620;
            MinWidth = 640;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _scopeBox = new ComboBox { Width = 230, VerticalAlignment = VerticalAlignment.Center };
            _scopeBox.Items.Add("Все общие параметры");
            _scopeBox.Items.Add("Только параметры проекта");
            _scopeBox.Items.Add("Только не привязанные");
            _scopeBox.SelectedIndex = 0;
            _scopeBox.ToolTip =
                "Удаляется только то, что показано в таблице.\n" +
                "«Параметры проекта» — привязанные к категориям, те самые, что видны\n" +
                "в «Управление → Параметры проекта».\n" +
                "«Не привязанные» — общие параметры, оставшиеся в файле от загруженных\n" +
                "семейств и от снятых привязок.";
            _scopeBox.SelectionChanged += (s, e) => RebuildVisible();

            // Проверка хоть чего-то уже есть — значит галочке есть что прятать.
            var known = _familyCount == 0 || _pendingFamilies < _familyCount;

            _scanButton = new Button
            {
                Content = ScanButtonText(),
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = _scanFamilies != null && _familyCount > 0,
                ToolTip =
                    "Открывает загруженное семейство и смотрит, каким общим параметром\n" +
                    "помечены его размеры. Результат сохраняется в профиле Windows,\n" +
                    "поэтому в следующий раз открываются только новые и изменившиеся семейства.\n\n" +
                    "Когда открывать нечего, кнопка проходит проверку заново по всем семействам:\n" +
                    "Revit помечает семейство изменённым только при сохранении, поэтому\n" +
                    "перезагруженное в этом сеансе семейство сохранённая проверка не заметит."
            };
            _scanButton.Click += OnScanFamilies;

            _skipDimensionsBox = new CheckBox
            {
                Content = "Не показывать параметры, используемые в размерах",
                IsChecked = known,
                IsEnabled = known,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip =
                    "Параметр, которым помечен размер, держит геометрию семейства.\n" +
                    "Удалить его из проекта — значит сломать параметрику.\n" +
                    "Галочка включается, когда проверка семейств пройдена хотя бы частично."
            };
            _skipDimensionsBox.Checked += (s, e) => RebuildVisible();
            _skipDimensionsBox.Unchecked += (s, e) => RebuildVisible();

            _ruleBox = new ComboBox { Width = 250, VerticalAlignment = VerticalAlignment.Center };
            _ruleBox.Items.Add("Отобрать параметры, начинающиеся с");
            _ruleBox.Items.Add("Отобрать параметры, содержащие");
            _ruleBox.SelectedIndex = 0;
            _ruleBox.SelectionChanged += (s, e) => RebuildVisible();

            _patternBox = new TextBox
            {
                MinWidth = 180,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2)
            };
            _patternBox.TextChanged += (s, e) => RebuildVisible();

            _caseBox = new CheckBox
            {
                Content = "Учитывать регистр",
                VerticalAlignment = VerticalAlignment.Center
            };
            _caseBox.Checked += (s, e) => RebuildVisible();
            _caseBox.Unchecked += (s, e) => RebuildVisible();

            _invertBox = new CheckBox
            {
                Content = "Инвертировать поиск",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip =
                    "Правило работает наоборот: в таблице остаются параметры, которые ему НЕ подходят.\n" +
                    "Например «содержащие» + «ADSK» + инверсия — все параметры, кроме ADSK-овских.\n" +
                    "Пустая строка правила по-прежнему показывает весь список и не отмечает ничего."
            };
            _invertBox.Checked += (s, e) => RebuildVisible();
            _invertBox.Unchecked += (s, e) => RebuildVisible();

            _selectAll = new CheckBox
            {
                IsChecked = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Отметить или снять все показанные параметры"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _deleteButton = new Button
            {
                Content = "Удалить",
                MinWidth = 130,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _deleteButton.Click += OnDelete;

            var cancelButton = new Button
            {
                Content = "Отмена",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(cancelButton);

            foreach (var row in _all)
                row.PropertyChanged += OnRowChanged;

            Loaded += (s, e) => _patternBox.Focus();

            RebuildVisible();
        }

        // ───────────────────────────── разметка ─────────────────────────────

        private UIElement BuildLayout(Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // подсказка
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // отбор
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // защита семейств
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // правило
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // таблица
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // статус + кнопки

            var hint = new TextBlock
            {
                Text = "Удаляются отмеченные общие параметры — вместе со значениями во всех элементах проекта. " +
                       "Правило ниже оставляет в таблице только подходящие по имени и сразу их отмечает; " +
                       "дальше галочки правятся вручную.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var scopePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            scopePanel.Children.Add(new TextBlock
            {
                Text = "Показывать:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            scopePanel.Children.Add(_scopeBox);
            Grid.SetRow(scopePanel, 1);
            root.Children.Add(scopePanel);

            var dimensionPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            dimensionPanel.Children.Add(_scanButton);
            dimensionPanel.Children.Add(_skipDimensionsBox);
            Grid.SetRow(dimensionPanel, 2);
            root.Children.Add(dimensionPanel);

            var rulePanel = new StackPanel { Orientation = Orientation.Horizontal };
            rulePanel.Children.Add(_ruleBox);
            rulePanel.Children.Add(_patternBox);
            rulePanel.Children.Add(_caseBox);
            rulePanel.Children.Add(_invertBox);
            Grid.SetRow(rulePanel, 3);
            root.Children.Add(rulePanel);

            Grid.SetRow(_grid, 4);
            root.Children.Add(_grid);

            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_deleteButton);
            buttons.Children.Add(cancelButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 5);
            root.Children.Add(bottom);

            return root;
        }

        private DataGrid BuildGrid()
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
                Margin = new Thickness(0, 10, 0, 8)
            };

            // Галочка стоит слева от параметра — первым столбцом, с галочкой «все» в шапке.
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = BuildCheckBoxTemplate()
            });

            grid.Columns.Add(TextColumn("Имя", "Name", new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(TextColumn("Экземпляр/Тип", "Binding", new DataGridLength(110)));
            grid.Columns.Add(TextColumn("Размеры", "DimensionUse", new DataGridLength(110)));
            grid.Columns.Add(TextColumn("Категории", "Categories", new DataGridLength(230)));
            grid.Columns.Add(TextColumn("Группа", "Group", new DataGridLength(150)));
            grid.Columns.Add(TextColumn("GUID", "Guid", new DataGridLength(240)));

            // Двойной щелчок по строке и пробел тоже переключают галочку —
            // попадать в маленький квадрат необязательно.
            grid.MouseDoubleClick += (s, e) => ToggleSelectedRows();
            grid.PreviewKeyDown += OnGridKeyDown;

            return grid;
        }

        private static DataGridTextColumn TextColumn(string header, string property, DataGridLength width)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(property)));

            return new DataGridTextColumn
            {
                Header = header,
                Width = width,
                Binding = new Binding(property),
                ElementStyle = style
            };
        }

        /// <summary>Галочка в ячейке: со своим шаблоном она срабатывает с первого щелчка.</summary>
        private static DataTemplate BuildCheckBoxTemplate()
        {
            var checkBox = new FrameworkElementFactory(typeof(CheckBox));
            checkBox.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding("IsSelected") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            checkBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            return new DataTemplate { VisualTree = checkBox };
        }

        // ───────────────────────────── отбор ─────────────────────────────

        private NameRule Rule => _ruleBox.SelectedIndex == 1 ? NameRule.Contains : NameRule.StartsWith;

        private string Pattern => _patternBox.Text?.Trim() ?? string.Empty;

        private StringComparison Comparison =>
            _caseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        /// <summary>
        /// Строка попадает в таблицу. Скрытая строка — не просто невидимая: удалить её нельзя,
        /// поэтому она теряет галочку.
        /// </summary>
        private bool InScope(ProjectParameterRow row)
        {
            return MatchesScope(row) && !HiddenByDimensions(row) && MatchesPattern(row);
        }

        /// <summary>
        /// Пустое правило не прячет ничего: пустой строке подходит любое имя — в том числе
        /// при инверсии, иначе одна галочка убирала бы из таблицы всё разом.
        /// </summary>
        private bool MatchesPattern(ProjectParameterRow row)
        {
            var pattern = Pattern;
            if (pattern.Length == 0)
                return true;

            var found = Rule == NameRule.StartsWith
                ? row.Name.StartsWith(pattern, Comparison)
                : row.Name.IndexOf(pattern, Comparison) >= 0;

            return _invertBox.IsChecked == true ? !found : found;
        }

        private bool MatchesScope(ProjectParameterRow row)
        {
            switch (_scopeBox.SelectedIndex)
            {
                case 1:
                    return row.IsBound;
                case 2:
                    return !row.IsBound;
                default:
                    return true;
            }
        }

        /// <summary>Параметр держит размер в семействе, и пользователь просил такие не показывать.</summary>
        private bool HiddenByDimensions(ProjectParameterRow row)
        {
            return _skipDimensionsBox.IsChecked == true && row.UsedInDimensions;
        }

        /// <summary>
        /// Пересобирает таблицу. Правило работает как поиск: оставляет в списке только подходящие
        /// имена и сразу их отмечает, остальные строки уходят из таблицы и теряют галочку —
        /// удаляется только то, что видно. Пустое правило показывает всё и не отмечает ничего,
        /// чтобы «ничего не вписал» не означало «удалить всё».
        /// </summary>
        private void RebuildVisible()
        {
            _visible.Clear();

            foreach (var row in _all)
            {
                if (InScope(row))
                    _visible.Add(row);
            }

            var byRule = Pattern.Length > 0;

            SetMany(row => InScope(row) && (byRule || row.IsSelected));
        }

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(row => value && InScope(row));
        }

        /// <summary>Переключает галочки у выделенных в таблице строк — двойным щелчком или пробелом.</summary>
        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<ProjectParameterRow>().ToList();
            if (rows.Count == 0)
                return;

            // Разнобой приводим к одному состоянию: если отмечены не все — отмечаем все.
            var value = !rows.All(row => row.IsSelected);
            var affected = new HashSet<ProjectParameterRow>(rows);

            SetMany(row => affected.Contains(row) ? value : row.IsSelected);
        }

        /// <summary>
        /// Пакетная простановка галочек: итог пересчитываем один раз в конце,
        /// а не на каждую строку.
        /// </summary>
        private void SetMany(Func<ProjectParameterRow, bool> value)
        {
            _settingMany = true;

            foreach (var row in _all)
                row.IsSelected = value(row);

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

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_settingMany || e.PropertyName != nameof(ProjectParameterRow.IsSelected))
                return;

            UpdateSummary();
        }

        private List<ProjectParameterRow> Marked()
        {
            return _visible.Where(row => row.IsSelected).ToList();
        }

        private void UpdateSummary()
        {
            var marked = Marked().Count;

            if (marked == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Показано: " + _visible.Count + " из " + _all.Count +
                               " общих параметров проекта. Не отмечено ни одного." + DimensionNote();
            }
            else
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "Будет удалено: " + marked + " из " + _visible.Count +
                               " показанных." + DimensionNote();
            }

            _deleteButton.IsEnabled = marked > 0;
            _deleteButton.Content = marked > 0 ? "Удалить (" + marked + ")" : "Удалить";

            _syncingSelectAll = true;
            _selectAll.IsChecked = _visible.Count == 0 || marked == 0
                ? false
                : marked == _visible.Count ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        /// <summary>Приписка к строке состояния: как обстоит дело с проверкой и что из-за неё скрыто.</summary>
        private string DimensionNote()
        {
            if (_familyCount == 0)
                return string.Empty;

            var hidden = _all.Count(row => MatchesScope(row) && HiddenByDimensions(row));
            var note = hidden > 0 ? " Скрыто как метки размеров: " + hidden + "." : string.Empty;

            if (_pendingFamilies >= _familyCount)
                return " Семейства на метки размеров не проверялись.";

            if (_pendingFamilies > 0)
                return note + " Не проверено семейств: " + _pendingFamilies + ".";

            return note + (_checkedAt.HasValue
                ? " Проверка семейств от " + _checkedAt.Value.ToString("dd.MM.yyyy HH:mm") + "."
                : string.Empty);
        }

        // ───────────────────────────── проверка семейств ─────────────────────────────

        /// <summary>Проверять нечего — значит кнопка предлагает пройти всё заново, минуя сохранённое.</summary>
        private bool ForcesRescan => _pendingFamilies == 0;

        private string ScanButtonText()
        {
            return ForcesRescan
                ? "Проверить заново (" + _familyCount + ")"
                : "Проверить семейства (" + _pendingFamilies + ")";
        }

        /// <summary>
        /// Открывает загруженные семейства и отмечает параметры, которыми помечены размеры.
        /// Работа долгая и блокирующая, поэтому сначала спрашиваем — сама по себе она не идёт.
        /// </summary>
        private void OnScanFamilies(object sender, RoutedEventArgs e)
        {
            if (_scanFamilies == null)
                return;

            var force = ForcesRescan;

            if (!Confirm(force))
                return;

            FamilyDimensionScan scan;
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                scan = _scanFamilies(force);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Проверить семейства не удалось.\n\n" + exception.Message,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }

            foreach (var row in _all)
                row.UsedInDimensions = scan.ParameterGuids.Contains(row.Guid);

            // Осталось непроверенным ровно то, что не удалось открыть.
            _pendingFamilies = scan.Failures.Count;
            _checkedAt = DateTime.Now;
            _scanButton.Content = ScanButtonText();
            _skipDimensionsBox.IsEnabled = true;

            // Простановка галочки сама вызовет пересборку; если она уже стоит, событие не придёт.
            if (_skipDimensionsBox.IsChecked == true)
                RebuildVisible();
            else
                _skipDimensionsBox.IsChecked = true;

            ReportScan(scan);
        }

        private bool Confirm(bool force)
        {
            var text = force
                ? "Проверка пройдёт заново по всем семействам (" + _familyCount + "), " +
                  "сохранённый результат будет заменён.\n\n" +
                  "Это нужно после перезагрузки семейств в текущем сеансе: Revit помечает семейство " +
                  "изменённым только при сохранении, и сохранённая проверка такой правки не замечает."
                : "В проекте не видно, каким параметром помечен размер внутри семейства — " +
                  "чтобы это узнать, надо открыть семейство.\n\n" +
                  "Открыть предстоит: " + _pendingFamilies + " из " + _familyCount +
                  "; остальные возьмутся из сохранённой проверки.";

            var answer = MessageBox.Show(
                this,
                text + "\n\nRevit на это время перестанет отвечать. Продолжить?",
                WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            return answer == MessageBoxResult.Yes;
        }

        private void ReportScan(FamilyDimensionScan scan)
        {
            var used = _all.Count(row => row.UsedInDimensions);

            var text = "Открыто семейств: " + scan.OpenedFamilies +
                       ", взято из сохранённой проверки: " + scan.ReusedFamilies +
                       " (всего в проекте " + _familyCount + ").\n" +
                       "Общих параметров, которыми помечены размеры: " + used + ".\n\n" +
                       "Результат сохранён — в следующий раз окно откроется уже с проверкой.";

            if (scan.Failures.Count > 0)
            {
                const int limit = 10;
                text += "\n\nНе удалось открыть (" + scan.Failures.Count + "):\n• " +
                        string.Join("\n• ", scan.Failures.Take(limit));

                if (scan.Failures.Count > limit)
                    text += "\n… и ещё " + (scan.Failures.Count - limit);

                text += "\n\nПараметры этих семейств проверены не были — удаляйте их с оглядкой.";
            }

            MessageBox.Show(this, text, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ───────────────────────────── действия ─────────────────────────────

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "Не отмечено ни одного параметра.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(
                this,
                "Удалить из проекта " + marked.Count + " общих параметров?\n\n" +
                Preview(marked) + "\n\nВместе с параметром пропадут его значения во всех элементах проекта, " +
                "а также поля спецификаций и фильтры, которые на него ссылались.\n" +
                (_pendingFamilies == 0
                    ? string.Empty
                    : "Не проверено семейств: " + _pendingFamilies + " — среди отмеченных может оказаться параметр, " +
                      "который держит геометрию семейства.\n") +
                "Действие отменяется только через «Отменить» (Ctrl+Z) в Revit.",
                WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }

        private static string Preview(IReadOnlyList<ProjectParameterRow> rows)
        {
            const int limit = 12;
            var shown = string.Join("\n", rows.Take(limit).Select(row => "• " + row.Name));

            return rows.Count > limit
                ? shown + "\n… и ещё " + (rows.Count - limit)
                : shown;
        }
    }
}
