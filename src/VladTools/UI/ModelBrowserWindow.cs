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
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// Дерево моделей с галочками — одно на два хранилища. И Revit Server, и BIM360
    /// устроены одинаково: вложенные папки, содержимое которых читается по сети и потому
    /// подгружается только при раскрытии узла. Различается лишь то, чем заполняются дети,
    /// и это окно получает готовой лямбдой.
    ///
    /// Тем же деревом выбирается и **папка** — когда «Комплект по корпусу» спрашивает, где
    /// искать модели разделов. Отдельного окна для этого нет и не нужно: дерево то же самое,
    /// меняются только галочки (в режиме папки их нет вовсе) и то, что уходит наружу, —
    /// вместо отмеченных моделей одна выделенная папка.
    ///
    /// Чтение идёт прямо в потоке интерфейса, под курсором ожидания: асинхронность здесь
    /// была бы лишней сложностью — окно всё равно модальное, а Revit на время диалога
    /// ничего не делает. Отказ службы не закрывает окно и не роняет кнопку: он становится
    /// красной строкой внутри той папки, которую не удалось прочитать.
    ///
    /// Окно собрано кодом, без XAML — проект не включает WPF-сборку разметки.
    /// </summary>
    internal sealed class ModelBrowserWindow : Window
    {
        private readonly ObservableCollection<BrowseNode> _roots = new ObservableCollection<BrowseNode>();
        private readonly Func<BrowseNode, IReadOnlyList<BrowseNode>> _expand;

        /// <summary>Задан — окно выбирает папку, а не модели; сам предикат говорит, годится ли эта папка.</summary>
        private readonly Func<BrowseNode, bool> _pickFolder;

        private readonly TreeView _tree;
        private readonly TextBlock _status;
        private readonly Button _addButton;

        /// <summary>Модели, отмеченные пользователем.</summary>
        public IReadOnlyList<LinkEntry> Selected { get; private set; } = new List<LinkEntry>();

        /// <summary>Папка, выбранная в режиме выбора папки; в обычном — null.</summary>
        public BrowseNode SelectedFolder { get; private set; }

        /// <param name="hint">Строка над деревом: что здесь показано и что с этим делать.</param>
        /// <param name="topStrip">Полоса управления над деревом; не нужна — null.</param>
        /// <param name="expand">Чем заполнить папку. Исключение отсюда показывается внутри узла.</param>
        /// <param name="pickFolder">
        /// Задан — окно выбирает одну папку вместо моделей, а предикат говорит, годится ли
        /// выделенный узел: учётная запись и проект BIM360 папками не являются.
        /// </param>
        public ModelBrowserWindow(
            string title,
            string hint,
            UIElement topStrip,
            IEnumerable<BrowseNode> roots,
            Func<BrowseNode, IReadOnlyList<BrowseNode>> expand,
            Func<BrowseNode, bool> pickFolder = null)
        {
            _expand = expand;
            _pickFolder = pickFolder;

            Title = title;
            Width = 760;
            Height = 600;
            MinWidth = 480;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _tree = BuildTree();
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _addButton = new Button
            {
                Content = _pickFolder == null ? "Добавить" : "Выбрать",
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

            Content = BuildLayout(hint, topStrip, cancelButton);

            foreach (var root in roots ?? Enumerable.Empty<BrowseNode>())
                AddRoot(root);

            UpdateSummary();
        }

        /// <summary>Добавляет корень дерева — например, ещё один сервер Revit Server.</summary>
        public void AddRoot(BrowseNode node)
        {
            if (node == null)
                return;

            Watch(node);
            _roots.Add(node);
            UpdateSummary();
        }

        public bool HasRoot(Func<BrowseNode, bool> match)
        {
            return _roots.Any(match);
        }

        // ───────────────────────────── разметка ─────────────────────────────

        private UIElement BuildLayout(string hint, UIElement topStrip, Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // подсказка
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // полоса управления
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // дерево
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // статус + кнопки

            var text = new TextBlock
            {
                Text = hint,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(text, 0);
            root.Children.Add(text);

            if (topStrip != null)
            {
                Grid.SetRow(topStrip, 1);
                root.Children.Add(topStrip);
            }

            Grid.SetRow(_tree, 2);
            root.Children.Add(_tree);

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

        private TreeView BuildTree()
        {
            var tree = new TreeView
            {
                ItemsSource = _roots,
                ItemTemplate = BuildNodeTemplate(_pickFolder == null),
                BorderBrush = SystemColors.ControlDarkBrush,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 6, 0, 8)
            };

            // Содержимое папки читается ровно один раз — в тот момент, когда её раскрыли.
            tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnExpanded));

            if (_pickFolder != null)
                tree.SelectedItemChanged += (sender, args) => UpdateSummary();

            return tree;
        }

        /// <param name="withCheckBoxes">
        /// В режиме выбора папки галочек нет: отмечать нечего, а видимая, но бессмысленная
        /// галочка у модели выглядела бы как ещё один способ что-то выбрать.
        /// </param>
        private static HierarchicalDataTemplate BuildNodeTemplate(bool withCheckBoxes)
        {
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            if (withCheckBoxes)
            {
                var check = new FrameworkElementFactory(typeof(CheckBox));
                check.SetBinding(ToggleButton.IsCheckedProperty,
                    new Binding("IsSelected") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                check.SetBinding(UIElement.VisibilityProperty, new Binding("CheckBoxVisibility"));
                check.SetBinding(UIElement.IsEnabledProperty, new Binding("IsCheckable"));
                check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                check.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
                panel.AppendChild(check);
            }

            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            name.SetBinding(TextBlock.FontWeightProperty, new Binding("Weight"));
            name.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground"));
            name.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            panel.AppendChild(name);

            var note = new FrameworkElementFactory(typeof(TextBlock));
            note.SetBinding(TextBlock.TextProperty, new Binding("Note"));
            note.SetValue(TextBlock.ForegroundProperty, SystemColors.GrayTextBrush);
            note.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            note.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
            panel.AppendChild(note);

            return new HierarchicalDataTemplate(typeof(BrowseNode))
            {
                VisualTree = panel,
                ItemsSource = new Binding("Children")
            };
        }

        // ───────────────────────────── чтение содержимого ─────────────────────────────

        private void OnExpanded(object sender, RoutedEventArgs e)
        {
            var item = e.OriginalSource as TreeViewItem;
            var node = item == null ? null : item.DataContext as BrowseNode;

            if (node == null || node.IsModel || node.IsPlaceholder || node.IsLoaded || _expand == null)
                return;

            Fill(node);
        }

        private void Fill(BrowseNode node)
        {
            IReadOnlyList<BrowseNode> children;
            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                children = _expand(node);
            }
            catch (Exception exception)
            {
                children = new List<BrowseNode> { BrowseNode.Message(exception.Message, true) };
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }

            node.IsLoaded = true;
            node.Children.Clear();

            if (children == null || children.Count == 0)
            {
                node.Children.Add(BrowseNode.Message("пусто", false));
                UpdateSummary();
                return;
            }

            foreach (var child in children)
            {
                Watch(child);
                node.Children.Add(child);
            }

            UpdateSummary();
        }

        /// <summary>Галочка на любой глубине дерева должна пересчитывать итог внизу окна.</summary>
        private void Watch(BrowseNode node)
        {
            node.PropertyChanged += OnNodeChanged;

            foreach (var child in node.Children)
                Watch(child);
        }

        private void OnNodeChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BrowseNode.IsSelected))
                UpdateSummary();
        }

        // ───────────────────────────── итог ─────────────────────────────

        private List<BrowseNode> Marked()
        {
            return _roots.SelectMany(root => root.CheckedModels()).ToList();
        }

        /// <summary>Выделенная в дереве папка, годная для выбора; иначе null.</summary>
        private BrowseNode Highlighted()
        {
            var node = _tree.SelectedItem as BrowseNode;

            return node != null && !node.IsModel && !node.IsPlaceholder && _pickFolder(node) ? node : null;
        }

        private void UpdateSummary()
        {
            if (_pickFolder != null)
            {
                var folder = Highlighted();

                _status.Foreground = folder == null ? SystemColors.GrayTextBrush : SystemColors.ControlTextBrush;
                _status.Text = folder == null
                    ? "Выделите папку, внутри которой лежат папки разделов."
                    : "Выбрана папка: " + folder.Name;

                _addButton.IsEnabled = folder != null;
                return;
            }

            var marked = Marked().Count;

            if (marked == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = _roots.Count == 0
                    ? "Пока пусто."
                    : "Раскройте папку и отметьте модели.";
            }
            else
            {
                _status.Foreground = SystemColors.ControlTextBrush;
                _status.Text = "Отмечено моделей: " + marked + ".";
            }

            _addButton.IsEnabled = marked > 0;
            _addButton.Content = marked > 0 ? "Добавить (" + marked + ")" : "Добавить";
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            if (_pickFolder != null)
            {
                SelectedFolder = Highlighted();
                if (SelectedFolder == null)
                    return;

                DialogResult = true;
                return;
            }

            var marked = Marked();
            if (marked.Count == 0)
                return;

            Selected = marked.Select(node => node.Entry).Where(entry => entry != null).ToList();
            DialogResult = true;
        }
    }
}
