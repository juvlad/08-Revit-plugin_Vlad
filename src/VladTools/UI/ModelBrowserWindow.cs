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
    /// A tree of models with check boxes — one tree for two stores. Both Revit Server and
    /// BIM360 are built the same way: nested folders whose contents are read over the network
    /// and are therefore loaded only when a node is expanded. The only thing that differs is
    /// what fills the children, and the window gets that as a ready-made lambda.
    ///
    /// The same tree is also used to pick a **folder** — when the "Building Kit" asks where to
    /// search for discipline models. There is no separate window for that and none is needed:
    /// it is the same tree, only the check boxes change (there are none at all in folder mode)
    /// and so does what leaves at the end — one highlighted folder instead of checked models.
    ///
    /// Reading happens right on the UI thread, under a wait cursor: asynchrony here would be
    /// needless complexity — the window is modal anyway, and Revit does nothing while the
    /// dialog is up. A service failure does not close the window or bring the button down: it
    /// becomes a red line inside the folder that could not be read.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class ModelBrowserWindow : Window
    {
        private readonly ObservableCollection<BrowseNode> _roots = new ObservableCollection<BrowseNode>();
        private readonly Func<BrowseNode, IReadOnlyList<BrowseNode>> _expand;

        /// <summary>Set — the window picks a folder rather than models; the predicate itself says whether a folder qualifies.</summary>
        private readonly Func<BrowseNode, bool> _pickFolder;

        private readonly TreeView _tree;
        private readonly TextBlock _status;
        private readonly Button _addButton;

        /// <summary>The models checked by the user.</summary>
        public IReadOnlyList<LinkEntry> Selected { get; private set; } = new List<LinkEntry>();

        /// <summary>The folder chosen in folder-picking mode; null in the ordinary mode.</summary>
        public BrowseNode SelectedFolder { get; private set; }

        /// <param name="hint">The line above the tree: what is shown here and what to do with it.</param>
        /// <param name="topStrip">The control strip above the tree; pass null if none is needed.</param>
        /// <param name="expand">What to fill a folder with. An exception from here is shown inside the node.</param>
        /// <param name="pickFolder">
        /// Set — the window picks a single folder instead of models, and the predicate says
        /// whether the highlighted node qualifies: a BIM360 account or project is not a folder.
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
                Content = _pickFolder == null ? "Add" : "Choose",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _addButton.Click += OnAdd;

            var cancelButton = new Button
            {
                Content = "Cancel",
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

        /// <summary>Adds a tree root — another Revit Server, say.</summary>
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

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(string hint, UIElement topStrip, Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // control strip
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // tree
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

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

            // A folder's contents are read exactly once — the moment it is expanded.
            tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnExpanded));

            if (_pickFolder != null)
                tree.SelectedItemChanged += (sender, args) => UpdateSummary();

            return tree;
        }

        /// <param name="withCheckBoxes">
        /// There are no check boxes in folder-picking mode: there is nothing to check, and a
        /// visible but meaningless check box on a model would look like yet another way to
        /// select something.
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

        // ───────────────────────────── reading the contents ─────────────────────────────

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
                node.Children.Add(BrowseNode.Message("empty", false));
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

        /// <summary>A check box at any depth of the tree must recompute the total at the bottom of the window.</summary>
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

        // ───────────────────────────── the result ─────────────────────────────

        private List<BrowseNode> Marked()
        {
            return _roots.SelectMany(root => root.CheckedModels()).ToList();
        }

        /// <summary>The folder highlighted in the tree, if it qualifies for picking; otherwise null.</summary>
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
                    ? "Highlight the folder holding the discipline folders."
                    : "Folder chosen: " + folder.Name;

                _addButton.IsEnabled = folder != null;
                return;
            }

            var marked = Marked().Count;

            if (marked == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = _roots.Count == 0
                    ? "Empty so far."
                    : "Expand a folder and check some models.";
            }
            else
            {
                _status.Foreground = SystemColors.ControlTextBrush;
                _status.Text = "Models checked: " + marked + ".";
            }

            _addButton.IsEnabled = marked > 0;
            _addButton.Content = marked > 0 ? "Add (" + marked + ")" : "Add";
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
