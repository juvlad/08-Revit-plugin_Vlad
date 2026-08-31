using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// Ввод облачных моделей парой GUID — запасной путь к BIM360, когда просмотр недоступен:
    /// пользователь не вошёл в Autodesk, до службы не достучаться или Revit другой версии
    /// и токен из него не достать.
    ///
    /// Поле одно и многострочное: GUID всё равно приходят списком — из сохранённого набора,
    /// из письма, из чужого файла настроек, — и вводить их по одному было бы издевательством.
    /// Разбор нарочно нестрогий: в строке ищутся два GUID, а разделители, регион и имя
    /// распознаются как получится. Что не разобралось, окно показывает прямо под полем,
    /// а не молча выбрасывает.
    ///
    /// Окно собрано кодом, без XAML — проект не включает WPF-сборку разметки.
    /// </summary>
    internal sealed class CloudLinkWindow : Window
    {
        private const string WindowTitle = "Связи BIM360 по GUID";

        /// <summary>Регионы облака Autodesk, которые Revit принимает в пути модели.</summary>
        private static readonly string[] Regions = { "US", "EMEA", "AUS", "CAN", "DEU", "GBR", "IND", "JPN" };

        private static readonly Regex GuidPattern = new Regex(
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            RegexOptions.Compiled);

        private readonly ComboBox _regionBox;
        private readonly TextBox _textBox;
        private readonly TextBlock _status;
        private readonly Button _addButton;

        /// <summary>Разобранные модели.</summary>
        public IReadOnlyList<LinkEntry> Selected { get; private set; } = new List<LinkEntry>();

        public CloudLinkWindow(string defaultRegion)
        {
            Title = WindowTitle;
            Width = 720;
            Height = 480;
            MinWidth = 480;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _regionBox = new ComboBox { Width = 110, VerticalAlignment = VerticalAlignment.Center };
            foreach (var region in Regions)
                _regionBox.Items.Add(region);

            _regionBox.SelectedIndex = Math.Max(0, Array.IndexOf(Regions, (defaultRegion ?? "US").ToUpperInvariant()));
            _regionBox.ToolTip =
                "Регион, в котором живёт проект. Берётся, если в самой строке региона нет.\n" +
                "Ошибка в регионе — и Revit просто не найдёт модель.";
            _regionBox.SelectionChanged += (s, e) => UpdateSummary();

            _textBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 6, 0, 8)
            };
            _textBox.TextChanged += (s, e) => UpdateSummary();

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
                Content = "Отмена",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(cancelButton);

            Loaded += (s, e) => _textBox.Focus();

            UpdateSummary();
        }

        // ───────────────────────────── разметка ─────────────────────────────

        private UIElement BuildLayout(Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // подсказка
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // регион
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // поле
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // статус + кнопки

            var hint = new TextBlock
            {
                Text = "Строка на модель: GUID проекта и GUID модели, между ними — что угодно. " +
                       "Можно дописать регион и имя:\n" +
                       "    EMEA | 5f7a3c2e-… | 9b1d4e6f-… | АР_Корпус 1.rvt\n" +
                       "Регион и имя необязательны: региона нет — возьмётся выбранный ниже, имени нет — " +
                       "в таблице будет виден GUID.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var regionPanel = new StackPanel { Orientation = Orientation.Horizontal };
            regionPanel.Children.Add(new TextBlock
            {
                Text = "Регион по умолчанию:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            regionPanel.Children.Add(_regionBox);
            Grid.SetRow(regionPanel, 1);
            root.Children.Add(regionPanel);

            Grid.SetRow(_textBox, 2);
            root.Children.Add(_textBox);

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

            Grid.SetRow(bottom, 3);
            root.Children.Add(bottom);

            return root;
        }

        // ───────────────────────────── разбор ─────────────────────────────

        private string Region => (string)_regionBox.SelectedItem ?? "US";

        /// <summary>
        /// Разбирает строку: два первых GUID — проект и модель, известное слово вроде EMEA —
        /// регион, самый длинный из оставшихся кусков — имя. Порядок GUID именно такой,
        /// как их пишет Autodesk и как их хранит набор.
        /// </summary>
        private LinkEntry Parse(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return null;

            var guids = GuidPattern.Matches(text).Cast<Match>().Select(match => match.Value).ToList();
            if (guids.Count < 2)
                return null;

            var rest = GuidPattern.Replace(text, "|")
                .Split('|', ';', '\t', ',')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToList();

            var region = rest.FirstOrDefault(part => Regions.Contains(part.ToUpperInvariant())) ?? Region;

            var name = rest
                .Where(part => !Regions.Contains(part.ToUpperInvariant()))
                .OrderByDescending(part => part.Length)
                .FirstOrDefault() ?? guids[1];

            return LinkEntry.ForCloud(region.ToUpperInvariant(), guids[0], guids[1], name);
        }

        private List<LinkEntry> ParseAll(out int bad)
        {
            var entries = new List<LinkEntry>();
            bad = 0;

            foreach (var line in (_textBox.Text ?? string.Empty).Split('\n'))
            {
                var text = line.Trim();
                if (text.Length == 0 || text[0] == '#')
                    continue;

                var entry = Parse(text);
                if (entry == null)
                    bad++;
                else
                    entries.Add(entry);
            }

            return entries;
        }

        private void UpdateSummary()
        {
            int bad;
            var entries = ParseAll(out bad);

            if (entries.Count == 0 && bad == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Вставьте строки с GUID.";
            }
            else if (bad > 0)
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "Разобрано моделей: " + entries.Count +
                               ". Строк без пары GUID: " + bad + " — они будут пропущены.";
            }
            else
            {
                _status.Foreground = SystemColors.ControlTextBrush;
                _status.Text = "Разобрано моделей: " + entries.Count + ".";
            }

            _addButton.IsEnabled = entries.Count > 0;
            _addButton.Content = entries.Count > 0 ? "Добавить (" + entries.Count + ")" : "Добавить";
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            int bad;
            var entries = ParseAll(out bad);

            if (entries.Count == 0)
                return;

            Selected = entries;
            DialogResult = true;
        }
    }
}
