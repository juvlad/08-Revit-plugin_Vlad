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
    /// Окно «Принять изменения»: расхождения между осями и уровнями проекта и координационным
    /// файлом, с галочкой у каждого. Отмеченные применяются к проекту одной операцией.
    ///
    /// Показать список до правки обязательно, а не «принять всё молча»: в «Просмотре координации»
    /// пользователь видит каждое изменение, и кнопка не должна знать о модели меньше, чем он.
    /// Отдельно от того, что кнопка умеет применить, в таблице стоят строки только для чтения —
    /// пропавшие и новые элементы связи: сделать с ними через API нечего, но узнать о них нужно.
    ///
    /// Окно собрано кодом, без XAML — проект не включает WPF-сборку разметки.
    /// </summary>
    internal sealed class AcceptCoordinationWindow : Window
    {
        private const string WindowTitle = "Принять координационные изменения";

        private readonly IReadOnlyList<CoordinationScan> _scans;
        private readonly ObservableCollection<CoordinationChangeRow> _visible =
            new ObservableCollection<CoordinationChangeRow>();

        private readonly ComboBox _linkBox;
        private readonly ComboBox _scopeBox;
        private readonly CheckBox _selectAll;
        private readonly CheckBox _openReviewBox;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _applyButton;

        private bool _syncingSelectAll;
        private bool _settingMany;

        /// <summary>Изменения, которые пользователь подтвердил к применению.</summary>
        public IReadOnlyList<CoordinationChangeRow> Selected { get; private set; } =
            new List<CoordinationChangeRow>();

        /// <summary>Связь, по которой правим проект, — она же попадает в отчёт.</summary>
        public CoordinationScan Chosen => Current;

        /// <summary>После применения открыть «Просмотр координации» — проверить, что список пуст.</summary>
        public bool OpenReview => _openReviewBox.IsChecked == true;

        public AcceptCoordinationWindow(IReadOnlyList<CoordinationScan> scans)
        {
            _scans = scans ?? new List<CoordinationScan>();

            Title = WindowTitle;
            Width = 980;
            Height = 600;
            MinWidth = 640;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _linkBox = new ComboBox
            {
                Width = 460,
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = _scans,
                DisplayMemberPath = nameof(CoordinationScan.Caption),
                SelectedIndex = _scans.Count > 0 ? 0 : -1,
                IsEnabled = _scans.Count > 1,
                ToolTip =
                    "Связи, за которыми следят оси и уровни проекта.\n" +
                    "Первой стоит та, где расхождений больше всего."
            };
            _linkBox.SelectionChanged += (s, e) => Reload();

            _scopeBox = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center };
            _scopeBox.Items.Add("Все расхождения");
            _scopeBox.Items.Add("Только положение");
            _scopeBox.Items.Add("Только имена");
            _scopeBox.Items.Add("Только то, что кнопка не применит");
            _scopeBox.SelectedIndex = 0;
            _scopeBox.ToolTip =
                "Применяется только то, что показано в таблице:\n" +
                "строка, ушедшая из неё, теряет галочку.";
            _scopeBox.SelectionChanged += (s, e) => RebuildVisible();

            _selectAll = new CheckBox
            {
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Отметить или снять все показанные изменения"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _openReviewBox = new CheckBox
            {
                Content = "Открыть «Просмотр координации» после применения",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "Кнопка правит саму модель, а не список Revit: нажать «Принять» внутри\n" +
                    "«Просмотра координации» из API нельзя — этого в нём нет вовсе.\n" +
                    "Когда элемент встал на место, Revit перестаёт считать его расхождением сам,\n" +
                    "и список пустеет; открыть его стоит хотя бы затем, чтобы в этом убедиться."
            };

            _grid = BuildGrid();
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _applyButton = new Button
            {
                Content = "Принять",
                MinWidth = 140,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _applyButton.Click += OnApply;

            var closeButton = new Button
            {
                Content = "Закрыть",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(closeButton);

            Reload();
        }

        private CoordinationScan Current => _linkBox.SelectedItem as CoordinationScan;

        // ───────────────────────────── разметка ─────────────────────────────

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // подсказка
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // связь
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // отбор
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // таблица
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // «Просмотр координации»
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // статус + кнопки

            var hint = new TextBlock
            {
                Text = "Оси и уровни проекта, которые следят за координационным файлом, сверены с ним. " +
                       "Отмеченные встанут по файлу одной операцией — она отменяется одним Ctrl+Z. " +
                       "Строки без галочки Revit API применить не даёт: их видно, чтобы разобрать вручную.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var linkPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            linkPanel.Children.Add(new TextBlock
            {
                Text = "Координационный файл:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            linkPanel.Children.Add(_linkBox);
            Grid.SetRow(linkPanel, 1);
            root.Children.Add(linkPanel);

            var scopePanel = new StackPanel { Orientation = Orientation.Horizontal };
            scopePanel.Children.Add(new TextBlock
            {
                Text = "Показывать:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            scopePanel.Children.Add(_scopeBox);
            Grid.SetRow(scopePanel, 2);
            root.Children.Add(scopePanel);

            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            Grid.SetRow(_openReviewBox, 4);
            root.Children.Add(_openReviewBox);

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
                Margin = new Thickness(0, 10, 0, 0)
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = BuildCheckBoxTemplate()
            });

            grid.Columns.Add(TextColumn("Тип", "Type", new DataGridLength(80)));
            grid.Columns.Add(TextColumn("Имя", "Name", new DataGridLength(150)));
            grid.Columns.Add(TextColumn("Что изменилось", "What", new DataGridLength(210)));
            grid.Columns.Add(TextColumn("Было → станет", "Detail", new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(TextColumn("Состояние", "Note", new DataGridLength(260)));

            grid.MouseDoubleClick += (s, e) => ToggleSelectedRows();
            grid.PreviewKeyDown += OnGridKeyDown;

            // Строки, которые кнопка применить не может, гасим цветом: галочки у них
            // всё равно нет, и путать их с рабочими не нужно.
            var style = new Style(typeof(DataGridRow));
            var trigger = new DataTrigger
            {
                Binding = new Binding(nameof(CoordinationChangeRow.CanApply)),
                Value = false
            };
            trigger.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.GrayTextBrush));
            style.Triggers.Add(trigger);
            grid.RowStyle = style;

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

        /// <summary>
        /// Галочка в ячейке. Доступность привязана к самой строке: у пропавших и новых элементов
        /// связи применять нечего, и галочка не должна создавать впечатление, будто есть.
        /// </summary>
        private static DataTemplate BuildCheckBoxTemplate()
        {
            var checkBox = new FrameworkElementFactory(typeof(CheckBox));
            checkBox.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding(nameof(CoordinationChangeRow.IsSelected))
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });
            checkBox.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(CoordinationChangeRow.CanApply)));
            checkBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            return new DataTemplate { VisualTree = checkBox };
        }

        // ───────────────────────────── отбор ─────────────────────────────

        private IReadOnlyList<CoordinationChangeRow> All =>
            Current != null ? Current.Rows : (IReadOnlyList<CoordinationChangeRow>)new List<CoordinationChangeRow>();

        private bool InScope(CoordinationChangeRow row)
        {
            switch (_scopeBox.SelectedIndex)
            {
                case 1:
                    return row.Kind == CoordinationChangeKind.Position;
                case 2:
                    return row.Kind == CoordinationChangeKind.Name;
                case 3:
                    return !row.CanApply;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Переключение связи: таблица собирается заново и всё применимое сразу отмечается.
        ///
        /// Здесь «показали — значит отметили» не самодеятельность, а смысл кнопки: её просят
        /// принять изменения координационного файла разом. От правила «пустой отбор не значит
        /// выбрать всё» это не отступление — то правило защищает от случайного удаления,
        /// а тут ничего не удаляется и всё откатывается одним Ctrl+Z.
        /// </summary>
        private void Reload()
        {
            foreach (var scan in _scans)
            {
                foreach (var row in scan.Rows)
                    row.PropertyChanged -= OnRowChanged;
            }

            foreach (var row in All)
                row.PropertyChanged += OnRowChanged;

            SetMany(row => row.CanApply);
            RebuildVisible();
        }

        /// <summary>
        /// Пересобирает таблицу. Строка, ушедшая из неё, теряет галочку — применяется только
        /// то, что видно; то же правило, что в окнах удаления.
        /// </summary>
        private void RebuildVisible()
        {
            _visible.Clear();

            foreach (var row in All)
            {
                if (InScope(row))
                    _visible.Add(row);
            }

            SetMany(row => InScope(row) && row.CanApply && row.IsSelected);
        }

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(row => value && InScope(row) && row.CanApply);
        }

        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<CoordinationChangeRow>().Where(row => row.CanApply).ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);
            var affected = new HashSet<CoordinationChangeRow>(rows);

            SetMany(row => affected.Contains(row) ? value : row.IsSelected);
        }

        /// <summary>Пакетная простановка галочек: итог пересчитывается один раз в конце.</summary>
        private void SetMany(Func<CoordinationChangeRow, bool> value)
        {
            _settingMany = true;

            foreach (var row in All)
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
            if (_settingMany || e.PropertyName != nameof(CoordinationChangeRow.IsSelected))
                return;

            UpdateSummary();
        }

        private List<CoordinationChangeRow> Marked()
        {
            return _visible.Where(row => row.IsSelected && row.CanApply).ToList();
        }

        private void UpdateSummary()
        {
            var scan = Current;
            var marked = Marked();
            var applicable = _visible.Count(row => row.CanApply);

            if (scan == null)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Связей, за которыми следят оси и уровни, в проекте нет.";
            }
            else if (!scan.IsLoaded)
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "Связь не загружена — сравнивать не с чем. Загрузите её в «Диспетчере связей».";
            }
            else if (scan.Rows.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Расхождений нет: все " + scan.MonitoredCount +
                               " осей и уровней стоят по координационному файлу.";
            }
            else if (marked.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Показано: " + _visible.Count + " из " + scan.Rows.Count +
                               " расхождений. Не отмечено ни одного." + Unsupported();
            }
            else
            {
                _status.Foreground = Brushes.DarkGreen;
                _status.Text = "Будет принято: " + marked.Count + " из " + applicable +
                               " применимых." + Unsupported();
            }

            _applyButton.IsEnabled = marked.Count > 0;
            _applyButton.Content = marked.Count > 0 ? "Принять (" + marked.Count + ")" : "Принять";

            _syncingSelectAll = true;
            _selectAll.IsChecked = applicable == 0 || marked.Count == 0
                ? false
                : marked.Count == applicable ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        /// <summary>Приписка про то, что кнопке не по силам: молчать об этом нельзя.</summary>
        private string Unsupported()
        {
            var scan = Current;
            if (scan == null)
                return string.Empty;

            var count = scan.Rows.Count(row => !row.CanApply);
            return count == 0 ? string.Empty : " Вручную придётся разобрать: " + count + ".";
        }

        // ───────────────────────────── действия ─────────────────────────────

        private void OnApply(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "Не отмечено ни одного изменения.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var moves = marked.Count(row => row.Kind == CoordinationChangeKind.Position);
            var names = marked.Count - moves;

            var answer = MessageBox.Show(
                this,
                "Принять изменений: " + marked.Count + " (положение — " + moves + ", имена — " + names + ")?\n\n" +
                Preview(marked) + "\n\n" +
                "Оси и уровни встанут по координационному файлу; всё, что к ним привязано, " +
                "поедет вместе с ними.\n" +
                "Действие отменяется одним «Отменить» (Ctrl+Z) в Revit.",
                WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);

            if (answer != MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }

        private static string Preview(IReadOnlyList<CoordinationChangeRow> rows)
        {
            const int limit = 12;
            var shown = string.Join("\n", rows.Take(limit).Select(row => "• " + row.Title + " — " + row.Detail));

            return rows.Count > limit
                ? shown + "\n… и ещё " + (rows.Count - limit)
                : shown;
        }
    }
}
