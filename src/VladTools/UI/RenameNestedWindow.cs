using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// Окно «Переименовать вложенные»: таблица вложенных семейств (и, если нужно, их типоразмеров)
    /// со столбцами «Текущее имя» и «Новое имя».
    ///
    /// Работает как «Найти и заменить» в Excel: вписал «DN» → «ДУ» — и во всех отмеченных строках
    /// сразу видно, что получится. К этому же добавляются приставка и окончание. Любую ячейку
    /// «Новое имя» можно поправить руками — правило такую строку больше не трогает.
    ///
    /// Справа — буфер имён: часто повторяющиеся куски имён сохраняются в профиле пользователя
    /// и подставляются в поля одним щелчком.
    ///
    /// Окно собрано кодом, без XAML — проект не включает WPF-сборку разметки.
    /// </summary>
    internal sealed class RenameNestedWindow : Window
    {
        private const string WindowTitle = "Переименовать вложенные";

        /// <summary>Знаки, которые Revit в именах не принимает.</summary>
        private static readonly char[] Forbidden = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        private readonly IReadOnlyList<NestedFamilyRow> _all;
        private readonly ObservableCollection<NestedFamilyRow> _visible = new ObservableCollection<NestedFamilyRow>();
        private readonly ObservableCollection<string> _buffer = new ObservableCollection<string>();

        private readonly ComboBox _scopeBox;
        private readonly TextBox _findBox;
        private readonly TextBox _replaceBox;
        private readonly CheckBox _caseBox;
        private readonly TextBox _prefixBox;
        private readonly TextBox _suffixBox;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly ListBox _bufferList;
        private readonly TextBox _bufferBox;
        private readonly TextBlock _summary;
        private readonly Button _renameButton;

        private TextBox _lastField;
        private bool _refreshing;
        private bool _syncingSelectAll;

        /// <summary>Строки, которые пользователь подтвердил к переименованию.</summary>
        public IReadOnlyList<NestedFamilyRow> Selected { get; private set; } = new List<NestedFamilyRow>();

        public RenameNestedWindow(IReadOnlyList<NestedFamilyRow> rows)
        {
            _all = rows ?? new List<NestedFamilyRow>();

            Title = WindowTitle;
            Width = 1120;
            Height = 640;
            MinWidth = 860;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _scopeBox = new ComboBox { Width = 230, VerticalAlignment = VerticalAlignment.Center };
            _scopeBox.Items.Add("Вложенные семейства");
            _scopeBox.Items.Add("Типоразмеры вложенных");
            _scopeBox.Items.Add("Семейства и типоразмеры");
            _scopeBox.SelectedIndex = 0;
            _scopeBox.ToolTip = "Переименовывается только то, что показано в таблице.\n" +
                                "При смене списка новые имена сбрасываются к текущим.";
            _scopeBox.SelectionChanged += (s, e) => RebuildVisible();

            _findBox = RuleBox(160);
            _replaceBox = RuleBox(160);
            _prefixBox = RuleBox(130);
            _suffixBox = RuleBox(130);

            _caseBox = new CheckBox
            {
                Content = "Учитывать регистр",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            _caseBox.Checked += (s, e) => Refresh();
            _caseBox.Unchecked += (s, e) => Refresh();

            _selectAll = new CheckBox
            {
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Отметить или снять все строки"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();

            _bufferList = new ListBox { ItemsSource = _buffer, Margin = new Thickness(0, 4, 0, 4) };
            _bufferList.MouseDoubleClick += (s, e) => PasteBuffer(_lastField);

            _bufferBox = new TextBox
            {
                Padding = new Thickness(3, 2, 3, 2),
                ToolTip = "Значение для кнопки «Сохранить». Пустое поле — берётся текст из того поля правила, где стоял курсор."
            };

            _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _renameButton = new Button
            {
                Content = "Переименовать",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _renameButton.Click += OnRename;

            var closeButton = new Button
            {
                Content = "Закрыть",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(closeButton);

            foreach (var row in _all)
                row.PropertyChanged += OnRowChanged;

            foreach (var value in NameBuffer.Load())
                _buffer.Add(value);

            Loaded += (s, e) => _findBox.Focus();

            RebuildVisible();
        }

        // ───────────────────────────── разметка ─────────────────────────────

        private TextBox RuleBox(double width)
        {
            var box = new TextBox
            {
                Width = width,
                Margin = new Thickness(4, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2)
            };

            box.TextChanged += (s, e) => Refresh();

            // Буфер подставляет значение в то поле, где пользователь был последним.
            box.GotKeyboardFocus += (s, e) => _lastField = box;

            return box;
        }

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // подсказка
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // правило
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // таблица + буфер
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // статус + кнопки

            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap };
            hint.Inlines.Add(new Run("Переименование вложенных семейств пакетом: «Найти» и «Заменить на» " +
                                     "работают по всем отмеченным строкам, результат сразу виден в столбце «Новое имя»."));
            hint.Inlines.Add(new LineBreak());
            hint.Inlines.Add(new Run("Ячейку «Новое имя» можно исправить руками — такую строку правило больше не меняет. " +
                                     "Открытое (родительское) семейство не затрагивается.")
            {
                Foreground = SystemColors.GrayTextBrush
            });
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var rules = BuildRulePanel();
            Grid.SetRow(rules, 1);
            root.Children.Add(rules);

            var middle = new Grid { Margin = new Thickness(0, 10, 0, 8) };
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });

            Grid.SetColumn(_grid, 0);
            middle.Children.Add(_grid);

            var buffer = BuildBufferPanel();
            Grid.SetColumn(buffer, 1);
            middle.Children.Add(buffer);

            Grid.SetRow(middle, 2);
            root.Children.Add(middle);

            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_summary, 0);
            bottom.Children.Add(_summary);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_renameButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 3);
            root.Children.Add(bottom);

            return root;
        }

        private UIElement BuildRulePanel()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

            var first = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            first.Children.Add(Label("Показывать:"));
            first.Children.Add(_scopeBox);
            first.Children.Add(Label("Найти:", 12));
            first.Children.Add(_findBox);
            first.Children.Add(Label("Заменить на:"));
            first.Children.Add(_replaceBox);
            first.Children.Add(_caseBox);
            panel.Children.Add(first);

            var second = new StackPanel { Orientation = Orientation.Horizontal };
            second.Children.Add(Label("Добавить в начало:"));
            second.Children.Add(_prefixBox);
            second.Children.Add(Label("Добавить в конец:"));
            second.Children.Add(_suffixBox);

            var reset = new Button
            {
                Content = "Сбросить",
                MinWidth = 100,
                Padding = new Thickness(10, 3, 10, 3),
                ToolTip = "Очищает правило и возвращает все новые имена к текущим."
            };
            reset.Click += OnReset;
            second.Children.Add(reset);

            panel.Children.Add(second);

            return panel;
        }

        private static TextBlock Label(string text, double left = 0)
        {
            return new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(left, 0, 0, 0)
            };
        }

        private UIElement BuildBufferPanel()
        {
            var panel = new Grid { Margin = new Thickness(12, 0, 0, 0) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // заголовок
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // список
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // поле
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // сохранить/удалить
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // подстановка
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // подсказка

            var header = new TextBlock { Text = "Буфер имён", FontWeight = FontWeights.Bold };
            Grid.SetRow(header, 0);
            panel.Children.Add(header);

            Grid.SetRow(_bufferList, 1);
            panel.Children.Add(_bufferList);

            Grid.SetRow(_bufferBox, 2);
            panel.Children.Add(_bufferBox);

            var save = BufferButton("Сохранить", "Кладёт значение в буфер — он живёт в профиле Windows и открывается в следующем семействе.", OnBufferSave);
            var remove = BufferButton("Удалить", "Убирает выбранное значение из буфера.", OnBufferRemove);
            var keep = Pair(save, remove);
            Grid.SetRow(keep, 3);
            panel.Children.Add(keep);

            var toFind = BufferButton("→ Найти", "Подставить в поле «Найти».", (s, e) => PasteBuffer(_findBox));
            var toReplace = BufferButton("→ Заменить", "Подставить в поле «Заменить на».", (s, e) => PasteBuffer(_replaceBox));
            var toPrefix = BufferButton("→ В начало", "Подставить в поле «Добавить в начало».", (s, e) => PasteBuffer(_prefixBox));
            var toSuffix = BufferButton("→ В конец", "Подставить в поле «Добавить в конец».", (s, e) => PasteBuffer(_suffixBox));

            var pasteRows = new StackPanel();
            pasteRows.Children.Add(Pair(toFind, toReplace));
            pasteRows.Children.Add(Pair(toPrefix, toSuffix));
            Grid.SetRow(pasteRows, 4);
            panel.Children.Add(pasteRows);

            var hint = new TextBlock
            {
                Text = "Двойной щелчок по значению вставляет его в поле, где стоял курсор.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 6, 0, 0)
            };
            Grid.SetRow(hint, 5);
            panel.Children.Add(hint);

            return panel;
        }

        private static Button BufferButton(string text, string tooltip, RoutedEventHandler click)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(4, 3, 4, 3),
                Margin = new Thickness(0, 4, 4, 0),
                ToolTip = tooltip
            };

            button.Click += click;
            return button;
        }

        /// <summary>Две кнопки в ряд одинаковой ширины.</summary>
        private static UIElement Pair(UIElement left, UIElement right)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(left, 0);
            grid.Children.Add(left);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            return grid;
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
                CellTemplate = BuildCheckBoxTemplate()
            });

            grid.Columns.Add(TextColumn("Что это", "KindText", new DataGridLength(88)));
            grid.Columns.Add(TextColumn("Внутри семейства", "OwnerName", new DataGridLength(120)));

            var currentName = TextColumn("Текущее имя", "CurrentName", new DataGridLength(1, DataGridLengthUnitType.Star));
            currentName.MinWidth = 150;
            grid.Columns.Add(currentName);

            var newName = new DataGridTextColumn
            {
                Header = "Новое имя",
                Width = new DataGridLength(1.2, DataGridLengthUnitType.Star),
                MinWidth = 150,
                Binding = new Binding("NewName") { Mode = BindingMode.TwoWay },
                ElementStyle = CellStyle(),
                EditingElementStyle = EditorStyle()
            };
            grid.Columns.Add(newName);

            grid.Columns.Add(TextColumn("Экз.", "InstancesText", new DataGridLength(46)));

            var status = TextColumn("Статус", "StatusText", new DataGridLength(150));
            status.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding("StatusBrush")));
            grid.Columns.Add(status);

            // Пробел переключает галочки у выделенных строк — попадать в маленький квадрат необязательно.
            grid.PreviewKeyDown += OnGridKeyDown;

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

        // ───────────────────────────── правило ─────────────────────────────

        private StringComparison Comparison
        {
            get { return _caseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase; }
        }

        /// <summary>
        /// Что показано в таблице, то и переименовывается. Строки других видов
        /// возвращаются к своим именам — иначе правило меняло бы то, чего не видно.
        /// </summary>
        private void RebuildVisible()
        {
            _visible.Clear();

            foreach (var row in _all)
            {
                row.ResetPreview();

                if (InScope(row))
                    _visible.Add(row);
            }

            Refresh();
        }

        private bool InScope(NestedFamilyRow row)
        {
            switch (_scopeBox.SelectedIndex)
            {
                case 1:
                    return row.Kind == NestedKind.Symbol;
                case 2:
                    return true;
                default:
                    return row.Kind == NestedKind.Family;
            }
        }

        /// <summary>Заново считает новые имена по правилу, проверяет их и обновляет нижнюю подпись.</summary>
        private void Refresh()
        {
            if (_refreshing)
                return;

            _refreshing = true;
            try
            {
                foreach (var row in _visible)
                {
                    if (!row.IsSelected)
                        row.ResetPreview();
                    else if (!row.IsManual)
                        row.SetPreview(Apply(row.CurrentName));
                }

                Validate();
            }
            finally
            {
                _refreshing = false;
            }

            UpdateSummary();
        }

        /// <summary>Правило целиком: сначала замена внутри имени, потом приставка и окончание.</summary>
        private string Apply(string name)
        {
            var find = _findBox.Text ?? string.Empty;
            var result = find.Length > 0
                ? ReplaceAll(name, find, _replaceBox.Text ?? string.Empty, Comparison)
                : name;

            return (_prefixBox.Text ?? string.Empty) + result + (_suffixBox.Text ?? string.Empty);
        }

        /// <summary>Замена всех вхождений: у string.Replace в net48 нет перегрузки со сравнением.</summary>
        private static string ReplaceAll(string source, string find, string replacement, StringComparison comparison)
        {
            var builder = new StringBuilder();
            var start = 0;

            while (true)
            {
                var index = source.IndexOf(find, start, comparison);
                if (index < 0)
                    break;

                builder.Append(source, start, index - start).Append(replacement);
                start = index + find.Length;
            }

            return builder.Append(source, start, source.Length - start).ToString();
        }

        /// <summary>
        /// Проверка новых имён: пустое, с запрещёнными знаками или совпадающее с чужим именем
        /// Revit не примет. Занятость считается по всем строкам, а не только по отмеченным:
        /// имя, которое сейчас носит непереименовываемый объект, тоже занято.
        /// </summary>
        private void Validate()
        {
            foreach (var row in _all)
            {
                if (!_visible.Contains(row) || !row.IsSelected)
                {
                    row.SetStatus(RenameStatus.Unchanged, string.Empty);
                    continue;
                }

                var name = row.TrimmedNewName;

                if (name.Length == 0)
                {
                    row.SetStatus(RenameStatus.Error, "Пустое имя");
                    continue;
                }

                var forbidden = Forbidden.Where(character => name.IndexOf(character) >= 0).ToArray();
                if (forbidden.Length > 0)
                {
                    row.SetStatus(RenameStatus.Error, "Revit не примет знаки: " + string.Join(" ", forbidden));
                    continue;
                }

                var same = string.Equals(name, row.CurrentName, StringComparison.Ordinal);
                row.SetStatus(
                    same ? RenameStatus.Unchanged : RenameStatus.Ready,
                    same ? "Без изменений" : "Будет переименовано");
            }

            MarkDuplicates();
        }

        /// <summary>
        /// Ищет совпадения имён. Типоразмеры сравниваются внутри своего семейства,
        /// семейства — между собой; регистр Revit при проверке занятости не различает.
        /// </summary>
        private void MarkDuplicates()
        {
            var taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in _all)
            {
                var key = NameKey(row);
                int count;
                taken[key] = taken.TryGetValue(key, out count) ? count + 1 : 1;
            }

            foreach (var row in _all.Where(row => row.Status == RenameStatus.Ready).ToList())
            {
                if (taken[NameKey(row)] > 1)
                    row.SetStatus(RenameStatus.Error, "Имя уже занято");
            }
        }

        /// <summary>Имя, которое строка будет носить после применения, вместе с областью уникальности.</summary>
        private static string NameKey(NestedFamilyRow row)
        {
            var name = row.Status == RenameStatus.Ready ? row.TrimmedNewName : row.CurrentName;
            var scope = row.Kind == NestedKind.Family ? string.Empty : row.OwnerName;

            return row.KindText + " " + scope + " " + name;
        }

        private void UpdateSummary()
        {
            var selected = _visible.Where(row => row.IsSelected).ToList();
            var ready = _visible.Count(row => row.WillRename);
            var broken = selected.Count(row => row.Status == RenameStatus.Error);

            _summary.Foreground = broken > 0 ? Brushes.Firebrick : SystemColors.GrayTextBrush;
            _summary.Text = "В списке: " + _visible.Count + ". Отмечено: " + selected.Count +
                            ". Будет переименовано: " + ready +
                            (broken > 0 ? ". С ошибками: " + broken + "." : ".");

            _renameButton.Content = ready > 0 ? "Переименовать (" + ready + ")" : "Переименовать";
            _renameButton.IsEnabled = ready > 0;

            _syncingSelectAll = true;
            _selectAll.IsChecked = _visible.Count == 0 || selected.Count == 0
                ? false
                : selected.Count == _visible.Count ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        // ───────────────────────────── буфер имён ─────────────────────────────

        /// <summary>Кладёт выбранное значение буфера в поле — туда, где стоял курсор.</summary>
        private void PasteBuffer(TextBox target)
        {
            var value = _bufferList.SelectedItem as string;
            if (value == null)
            {
                MessageBox.Show(this, "Выберите значение в буфере имён.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var box = target ?? _replaceBox;
            var caret = box.SelectionStart;

            box.SelectedText = value;
            box.SelectionStart = caret + value.Length;
            box.SelectionLength = 0;
            box.Focus();
        }

        private void OnBufferSave(object sender, RoutedEventArgs e)
        {
            var value = (_bufferBox.Text ?? string.Empty).Trim();

            // Пустое поле — значит сохраняем то, что человек только что набрал в правиле.
            if (value.Length == 0 && _lastField != null)
                value = (_lastField.Text ?? string.Empty).Trim();

            if (value.Length == 0)
            {
                MessageBox.Show(this, "Впишите значение в поле под списком — оно и уйдёт в буфер.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!_buffer.Contains(value, StringComparer.Ordinal))
                _buffer.Add(value);

            _bufferBox.Clear();
            _bufferList.SelectedItem = value;
        }

        private void OnBufferRemove(object sender, RoutedEventArgs e)
        {
            var value = _bufferList.SelectedItem as string;
            if (value == null)
            {
                MessageBox.Show(this, "Выберите значение, которое нужно убрать из буфера.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _buffer.Remove(value);
        }

        // ───────────────────────────── действия ─────────────────────────────

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_refreshing)
                return;

            if (e.PropertyName == nameof(NestedFamilyRow.IsSelected) || e.PropertyName == nameof(NestedFamilyRow.NewName))
                Refresh();
        }

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            _refreshing = true;
            foreach (var row in _visible)
                row.IsSelected = value;
            _refreshing = false;

            Refresh();
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            // В режиме правки ячейки пробел — обычный пробел в имени.
            if (e.Key != Key.Space || IsEditing())
                return;

            var rows = _grid.SelectedItems.OfType<NestedFamilyRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);

            _refreshing = true;
            foreach (var row in rows)
                row.IsSelected = value;
            _refreshing = false;

            Refresh();
            e.Handled = true;
        }

        private bool IsEditing()
        {
            return Keyboard.FocusedElement is TextBox;
        }

        private void OnReset(object sender, RoutedEventArgs e)
        {
            _refreshing = true;

            _findBox.Clear();
            _replaceBox.Clear();
            _prefixBox.Clear();
            _suffixBox.Clear();

            foreach (var row in _all)
                row.ResetPreview();

            _refreshing = false;

            Refresh();
        }

        private void OnRename(object sender, RoutedEventArgs e)
        {
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            Refresh();

            var ready = _visible.Where(row => row.WillRename).ToList();
            if (ready.Count == 0)
            {
                MessageBox.Show(this, "Нет ни одной строки с новым именем.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var broken = _visible.Where(row => row.IsSelected && row.Status == RenameStatus.Error).ToList();
            if (broken.Count > 0)
            {
                var answer = MessageBox.Show(
                    this,
                    Problems(broken) + "\n\nПереименовать остальные (" + ready.Count + ")?",
                    WindowTitle,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Yes);

                if (answer != MessageBoxResult.Yes)
                    return;
            }

            var confirm = MessageBox.Show(
                this,
                "Переименовать " + ready.Count + " шт.?\n\n" + Preview(ready) +
                "\n\nДействие отменяется только через «Отменить» (Ctrl+Z) в Revit.",
                WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            if (confirm != MessageBoxResult.Yes)
                return;

            Selected = ready;
            DialogResult = true;
        }

        private static string Problems(IReadOnlyList<NestedFamilyRow> broken)
        {
            const int limit = 10;
            var shown = string.Join("\n", broken.Take(limit).Select(row => "• " + row.CurrentName + " — " + row.StatusText));

            var text = "Не получится переименовать: " + broken.Count + "\n\n" + shown;
            return broken.Count > limit ? text + "\n… и ещё " + (broken.Count - limit) : text;
        }

        private static string Preview(IReadOnlyList<NestedFamilyRow> rows)
        {
            const int limit = 12;
            var shown = string.Join("\n", rows.Take(limit).Select(row => "• " + row.CurrentName + "  →  " + row.TrimmedNewName));

            return rows.Count > limit
                ? shown + "\n… и ещё " + (rows.Count - limit)
                : shown;
        }

        /// <summary>Буфер сохраняется при любом закрытии окна — он живёт отдельно от семейства.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            try
            {
                NameBuffer.Save(_buffer);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this,
                    "Буфер имён не удалось сохранить:\n" + exception.Message + "\n\nФайл: " + NameBuffer.FilePath,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
