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
    /// <summary>Правило отбора параметров по имени.</summary>
    internal enum NameRule
    {
        StartsWith,
        Contains
    }

    /// <summary>
    /// Окно «Удалить параметры»: таблица всех общих параметров семейства с галочкой слева
    /// у каждой строки. Удаляются отмеченные параметры.
    ///
    /// Галочки ставятся двумя способами: вручную (щелчок по галочке, галочка в шапке — все сразу)
    /// и правилом «начинаются с» / «содержат». Правило работает как поиск: подходящие имена
    /// остаются в таблице и отмечаются, остальные из неё уходят; «Инвертировать поиск»
    /// меняет стороны местами.
    ///
    /// Параметры, стоящие метками на размерах, по умолчанию из списка убраны: удалить такой —
    /// значит снять метку и сломать параметрику семейства. Удаляется только то, что показано
    /// в таблице, поэтому скрытая строка теряет галочку.
    ///
    /// Окно собрано кодом, без XAML — проект не включает WPF-сборку разметки.
    /// </summary>
    internal sealed class DeleteParametersWindow : Window
    {
        private const string WindowTitle = "Удалить параметры";

        private readonly IReadOnlyList<SharedParameterRow> _all;
        private readonly ObservableCollection<SharedParameterRow> _visible = new ObservableCollection<SharedParameterRow>();

        private readonly ComboBox _ruleBox;
        private readonly TextBox _patternBox;
        private readonly CheckBox _caseBox;
        private readonly CheckBox _invertBox;
        private readonly CheckBox _skipDimensionsBox;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _deleteButton;

        private bool _syncingSelectAll;
        private bool _settingMany;

        /// <summary>Параметры, которые пользователь подтвердил к удалению.</summary>
        public IReadOnlyList<SharedParameterRow> Selected { get; private set; } = new List<SharedParameterRow>();

        public DeleteParametersWindow(IReadOnlyList<SharedParameterRow> parameters)
        {
            _all = parameters ?? new List<SharedParameterRow>();

            Title = WindowTitle;
            Width = 940;
            Height = 580;
            MinWidth = 620;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _skipDimensionsBox = new CheckBox
            {
                Content = "Не показывать параметры, используемые в размерах",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip =
                    "Параметр, которым помечен размер, держит геометрию семейства.\n" +
                    "Удалить его — значит снять метку с размера и сломать параметрику,\n" +
                    "поэтому такие параметры по умолчанию в списке не показываются.\n" +
                    "Снимите галочку, чтобы увидеть их (в столбце «Размеры» — пометка)."
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
                ToolTip = "Отметить или снять все параметры"
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
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // правило
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // таблица
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // статус + кнопки

            var hint = new TextBlock
            {
                Text = "Удаляются отмеченные параметры. Правило ниже оставляет в таблице только " +
                       "подходящие по имени и сразу их отмечает; дальше галочки правятся вручную.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var scopePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            scopePanel.Children.Add(_skipDimensionsBox);
            Grid.SetRow(scopePanel, 1);
            root.Children.Add(scopePanel);

            var rulePanel = new StackPanel { Orientation = Orientation.Horizontal };
            rulePanel.Children.Add(_ruleBox);
            rulePanel.Children.Add(_patternBox);
            rulePanel.Children.Add(_caseBox);
            rulePanel.Children.Add(_invertBox);
            Grid.SetRow(rulePanel, 2);
            root.Children.Add(rulePanel);

            Grid.SetRow(_grid, 3);
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

            Grid.SetRow(bottom, 4);
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
            grid.Columns.Add(TextColumn("Экземпляр/Тип", "Binding", new DataGridLength(105)));
            grid.Columns.Add(TextColumn("Размеры", "DimensionUse", new DataGridLength(110)));
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
        private bool InScope(SharedParameterRow row)
        {
            return !HiddenByDimensions(row) && MatchesPattern(row);
        }

        /// <summary>Параметр держит размер, и пользователь просил такие не показывать.</summary>
        private bool HiddenByDimensions(SharedParameterRow row)
        {
            return _skipDimensionsBox.IsChecked == true && row.UsedInDimensions;
        }

        /// <summary>
        /// Пустое правило не прячет ничего: пустой строке подходит любое имя — в том числе
        /// при инверсии, иначе одна галочка убирала бы из таблицы всё разом.
        /// </summary>
        private bool MatchesPattern(SharedParameterRow row)
        {
            var pattern = Pattern;
            if (pattern.Length == 0)
                return true;

            var found = Rule == NameRule.StartsWith
                ? row.Name.StartsWith(pattern, Comparison)
                : row.Name.IndexOf(pattern, Comparison) >= 0;

            return _invertBox.IsChecked == true ? !found : found;
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
            var rows = _grid.SelectedItems.OfType<SharedParameterRow>().ToList();
            if (rows.Count == 0)
                return;

            // Разнобой приводим к одному состоянию: если отмечены не все — отмечаем все.
            var value = !rows.All(row => row.IsSelected);
            var affected = new HashSet<SharedParameterRow>(rows);

            SetMany(row => affected.Contains(row) ? value : row.IsSelected);
        }

        /// <summary>
        /// Пакетная простановка галочек: итог пересчитываем один раз в конце,
        /// а не на каждую строку.
        /// </summary>
        private void SetMany(Func<SharedParameterRow, bool> value)
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
            if (_settingMany || e.PropertyName != nameof(SharedParameterRow.IsSelected))
                return;

            UpdateSummary();
        }

        private List<SharedParameterRow> Marked()
        {
            return _visible.Where(row => row.IsSelected).ToList();
        }

        private void UpdateSummary()
        {
            var marked = Marked().Count;
            var hidden = _all.Count(HiddenByDimensions);
            var hiddenText = hidden > 0 ? " Скрыто как метки размеров: " + hidden + "." : string.Empty;

            if (marked == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Показано: " + _visible.Count + " из " + _all.Count +
                               " общих параметров семейства." + hiddenText + " Не отмечено ни одного.";
            }
            else
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "Будет удалено: " + marked + " из " + _visible.Count + " показанных." + hiddenText;
            }

            _deleteButton.IsEnabled = marked > 0;
            _deleteButton.Content = marked > 0 ? "Удалить (" + marked + ")" : "Удалить";

            _syncingSelectAll = true;
            _selectAll.IsChecked = _visible.Count == 0 || marked == 0
                ? false
                : marked == _visible.Count ? true : (bool?)null;
            _syncingSelectAll = false;
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
                "Удалить из семейства " + marked.Count + " общих параметров?\n\n" +
                Preview(marked) + "\n\nДействие отменяется только через «Отменить» (Ctrl+Z) в Revit.",
                WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }

        private static string Preview(IReadOnlyList<SharedParameterRow> rows)
        {
            const int limit = 12;
            var shown = string.Join("\n", rows.Take(limit).Select(row => "• " + row.Name));

            return rows.Count > limit
                ? shown + "\n… и ещё " + (rows.Count - limit)
                : shown;
        }
    }
}
