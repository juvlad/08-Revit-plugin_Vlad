using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// Окно «Очистка модели»: список того, что можно убрать из открытого проекта,
    /// с галочкой у каждого пункта и «Выбрать всё» над списком.
    ///
    /// Здесь не таблица, а список: пунктов восемь, они разнородны, и у каждого важнее
    /// не строка данных, а объяснение, что именно исчезнет. Число рядом с заголовком —
    /// то, что команда нашла в модели при открытии окна; пункт с нулём выключен,
    /// и «Выбрать всё» его не отмечает — то же правило, что в окнах удаления:
    /// отмечается только то, что вообще можно сделать.
    ///
    /// Окно собрано кодом, без XAML — проект не включает WPF-сборку разметки.
    /// </summary>
    internal sealed class CleanupWindow : Window
    {
        private const string WindowTitle = "Очистка модели";

        private readonly IReadOnlyList<CleanupOption> _options;

        private readonly CheckBox _selectAll;
        private readonly TextBlock _status;
        private readonly Button _cleanButton;

        private bool _syncingSelectAll;
        private bool _settingMany;

        /// <summary>Пункты, которые пользователь подтвердил к выполнению.</summary>
        public IReadOnlyList<CleanupOption> Selected { get; private set; } = new List<CleanupOption>();

        public CleanupWindow(IReadOnlyList<CleanupOption> options)
        {
            _options = options ?? new List<CleanupOption>();

            Title = WindowTitle;
            Width = 660;
            Height = 720;
            MinWidth = 520;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _selectAll = new CheckBox
            {
                Content = "Выбрать всё",
                FontWeight = FontWeights.SemiBold,
                IsEnabled = _options.Any(option => option.IsAvailable),
                ToolTip = "Отметить или снять все пункты, которым есть что убирать"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _cleanButton = new Button
            {
                Content = "Очистить",
                MinWidth = 130,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _cleanButton.Click += OnClean;

            var cancelButton = new Button
            {
                Content = "Отмена",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(cancelButton);

            foreach (var option in _options)
                option.PropertyChanged += OnOptionChanged;

            Loaded += (s, e) => _selectAll.Focus();

            UpdateSummary();
        }

        // ───────────────────────────── разметка ─────────────────────────────

        private UIElement BuildLayout(Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // подсказка
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // выбрать всё
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // список
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // статус + кнопки

            var hint = new TextBlock
            {
                Text = "Отметьте, что убрать из открытого проекта. Всё отмеченное выполняется одной операцией — " +
                       "её можно отменить в Revit через Ctrl+Z, но проект перед очисткой лучше сохранить. " +
                       "Пункты, которым в этой модели нечего убирать, недоступны.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            Grid.SetRow(_selectAll, 1);
            root.Children.Add(_selectAll);

            var list = new StackPanel { Margin = new Thickness(0, 12, 8, 0) };

            foreach (var option in _options)
                list.Children.Add(BuildOptionRow(option));

            var scroll = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderBrush = SystemColors.ControlDarkBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 10, 4, 10),
                Margin = new Thickness(0, 6, 0, 8)
            };
            Grid.SetRow(scroll, 2);
            root.Children.Add(scroll);

            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_cleanButton);
            buttons.Children.Add(cancelButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 3);
            root.Children.Add(bottom);

            return root;
        }

        /// <summary>Пункт списка: галочка, заголовок с числом и серое пояснение под ним.</summary>
        private static UIElement BuildOptionRow(CleanupOption option)
        {
            var text = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };

            text.Children.Add(new TextBlock
            {
                Text = option.Caption,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });

            text.Children.Add(new TextBlock
            {
                Text = option.Description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 2, 0, 0)
            });

            var box = new CheckBox
            {
                Content = text,
                DataContext = option,
                IsEnabled = option.IsAvailable,
                VerticalContentAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 14)
            };
            box.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding("IsSelected") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

            return box;
        }

        // ───────────────────────────── галочки ─────────────────────────────

        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(option => value && option.IsAvailable);
        }

        /// <summary>Пакетная простановка галочек: итог пересчитываем один раз в конце.</summary>
        private void SetMany(Func<CleanupOption, bool> value)
        {
            _settingMany = true;

            foreach (var option in _options)
                option.IsSelected = value(option);

            _settingMany = false;

            UpdateSummary();
        }

        private void OnOptionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_settingMany || e.PropertyName != nameof(CleanupOption.IsSelected))
                return;

            UpdateSummary();
        }

        private List<CleanupOption> Marked()
        {
            return _options.Where(option => option.IsSelected && option.IsAvailable).ToList();
        }

        private void UpdateSummary()
        {
            var marked = Marked();
            var available = _options.Count(option => option.IsAvailable);

            if (marked.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = available == 0
                    ? "Чистить нечего: ни один пункт в этой модели ничего не находит."
                    : "Ничего не отмечено. Доступно пунктов: " + available + " из " + _options.Count + ".";
            }
            else
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "Отмечено пунктов: " + marked.Count + " из " + available +
                               ". Затронуто объектов: " + marked.Sum(option => option.Count) + ".";
            }

            _cleanButton.IsEnabled = marked.Count > 0;
            _cleanButton.Content = marked.Count > 0 ? "Очистить (" + marked.Count + ")" : "Очистить";

            _syncingSelectAll = true;
            _selectAll.IsChecked = available == 0 || marked.Count == 0
                ? false
                : marked.Count == available ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        // ───────────────────────────── действия ─────────────────────────────

        private void OnClean(object sender, RoutedEventArgs e)
        {
            var marked = Marked();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "Не отмечено ни одного пункта.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(
                this,
                "Очистить проект? Отмечено пунктов: " + marked.Count + ".\n\n" +
                string.Join("\n", marked.Select(option => "• " + option.Caption)) + "\n\n" +
                "Всё выполняется одной операцией: отменить её можно только целиком, через «Отменить» (Ctrl+Z) " +
                "в Revit. Если проект не сохранён — сохраните его перед очисткой.",
                WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }
    }
}
