using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// The "Views to Sheets" window: the views of the project in the folders and the order of the
    /// Project Browser, each with a check box and a scale; the titleblock the new sheets get and the
    /// drawing area inside it; how to number the sheets; and how to position a view on its sheet.
    ///
    /// One checked view is one new sheet, named after the view. The numbers are worked out here, in
    /// the order of the table, skipping the ones the project already has — so the "New sheet" column
    /// shows exactly what the button will make before it is pressed. Positioned "like a sample sheet",
    /// a view already on a sheet can be checked as well: it stays on its sheet and is moved there.
    ///
    /// "Find" only finds: a view the search hides keeps its check mark, the same as in the "Excel"
    /// export window — finding "Level" and then "Section" among hundreds of views must not undo the
    /// first round of ticks. The summary and the confirmation say how many checked views are out of
    /// sight. The other filter, "Show the views that cannot be checked", hides only rows that have no
    /// check box anyway.
    ///
    /// The window knows nothing about the Revit API: it is handed snapshots and hands back a
    /// <see cref="ViewPlacementRequest"/>.
    /// </summary>
    internal sealed class ViewsToSheetsWindow : Window
    {
        private const string WindowTitle = "Views to Sheets";

        /// <summary>The first item of the "Scale for the checked views" list: every view back to the scale it has.</summary>
        private const string OwnScale = "Each view's own";

        /// <summary>What Revit refuses in a sheet number — the same set it refuses in any view name.</summary>
        private static readonly char[] Forbidden = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        /// <summary>Revit's own metric scales; any other is typed in, and Revit makes it a custom scale.</summary>
        private static readonly string[] StandardScales =
            new[] { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000 }.Select(ViewPlacementRow.ScaleCaption).ToArray();

        /// <summary>Half a millimetre: an estimate this close to the edge of the drawing area counts as fitting.</summary>
        private const double FitTolerance = 0.5;

        private const double PreviewWidth = 260;
        private const double PreviewHeight = 184;

        private static readonly Brush FolderBackground = Frozen(new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFB)));
        private static readonly Brush AreaStroke = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xB7)));
        private static readonly Brush FootprintFill = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0xF2, 0xA1, 0x3B)));
        private static readonly Brush FootprintStroke = Frozen(new SolidColorBrush(Color.FromRgb(0xC9, 0x6E, 0x0A)));

        private readonly IReadOnlyList<TitleblockInfo> _titleblocks;
        private readonly IReadOnlyList<SampleViewportInfo> _samples;
        private readonly HashSet<string> _taken;
        private readonly ViewsToSheetsPreferences _preferences;

        private readonly List<ViewPlacementRow> _all = new List<ViewPlacementRow>();
        private readonly ObservableCollection<ViewPlacementRow> _visible = new ObservableCollection<ViewPlacementRow>();

        /// <summary>The view lines the search and the "Show" box leave, folding aside.</summary>
        private HashSet<ViewPlacementRow> _shown = new HashSet<ViewPlacementRow>();

        private readonly ComboBox _titleblockBox;
        private readonly TextBox _leftBox;
        private readonly TextBox _topBox;
        private readonly TextBox _rightBox;
        private readonly TextBox _bottomBox;
        private readonly TextBlock _areaText;
        private readonly TextBox _prefixBox;
        private readonly TextBox _startBox;
        private readonly TextBlock _numberingText;
        private readonly Dictionary<ViewCentering, RadioButton> _centeringOptions = new Dictionary<ViewCentering, RadioButton>();
        private readonly ComboBox _sampleBox;
        private readonly TextBlock _sampleNote;
        private readonly Canvas _preview;
        private readonly TextBlock _previewText;
        private readonly TextBox _searchBox;
        private readonly CheckBox _showAll;
        private readonly ComboBox _scaleBox;
        private readonly Button _setScaleButton;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _placeButton;

        private TitleblockInfo _currentTitleblock;
        private bool _syncing;
        private bool _syncingSelectAll;
        private bool _loadingMargins;
        private bool _ready;

        /// <summary>What to do, as the window was closed with "Place".</summary>
        public ViewPlacementRequest Request { get; private set; }

        /// <param name="views">Every view that could appear on a sheet, in the order the Project Browser lists them.</param>
        /// <param name="titleblocks">The project's titleblock types, measured.</param>
        /// <param name="takenNumbers">Every sheet number the project already has, placeholder sheets included.</param>
        /// <param name="samples">Every viewport on a sheet — what "Like a sample sheet" can repeat.</param>
        /// <param name="defaultSample">The UniqueId of the viewport to offer as the sample: the selected one, or the one on the open sheet; null when there is none.</param>
        /// <param name="organization">The name of the browser organization the folders come from; empty when unknown.</param>
        public ViewsToSheetsWindow(
            IReadOnlyList<PlaceableViewInfo> views,
            IReadOnlyList<TitleblockInfo> titleblocks,
            IEnumerable<string> takenNumbers,
            IReadOnlyList<SampleViewportInfo> samples,
            string defaultSample,
            string organization,
            ViewsToSheetsPreferences preferences)
        {
            _titleblocks = titleblocks;
            _samples = samples ?? new List<SampleViewportInfo>();
            _taken = new HashSet<string>(takenNumbers ?? new string[0], StringComparer.OrdinalIgnoreCase);
            _preferences = preferences;

            BuildTree(views);

            Title = WindowTitle;
            Width = 1200;
            Height = 960;
            MinWidth = 960;
            MinHeight = 700;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _titleblockBox = new ComboBox
            {
                Width = 460,
                ItemsSource = _titleblocks,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "The titleblock every new sheet gets. The size is what the titleblock actually draws — measured on a sheet\n" +
                          "the button made and threw away again."
            };

            _leftBox = MarginBox("Left");
            _topBox = MarginBox("Top");
            _rightBox = MarginBox("Right");
            _bottomBox = MarginBox("Bottom");

            _areaText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };

            _prefixBox = new TextBox
            {
                Width = 90,
                Text = _preferences.Prefix,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Written in front of every number: \"A-\" gives A-101, A-102…"
            };
            _prefixBox.TextChanged += (s, e) => Revalidate();

            _startBox = new TextBox
            {
                Width = 70,
                Text = _preferences.Start,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "The first number. Leading zeros are kept: 001 gives 001, 002…\n" +
                          "A number the project already has is skipped, so the new sheets never clash with the old ones."
            };
            _startBox.TextChanged += (s, e) => Revalidate();

            _numberingText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), Foreground = SystemColors.GrayTextBrush };

            var centering = _preferences.Centering == ViewCentering.Sample && _samples.Count == 0 ? ViewCentering.Viewport : _preferences.Centering;

            foreach (var option in ViewCenteringText.All)
            {
                var radio = new RadioButton
                {
                    GroupName = "Centering",
                    Margin = new Thickness(0, 0, 0, 6),
                    IsChecked = option == centering,
                    IsEnabled = option != ViewCentering.Sample || _samples.Count > 0,
                    Content = OptionContent(ViewCenteringText.Caption(option), ViewCenteringText.Description(option))
                };
                radio.Checked += (s, e) => RebuildVisible();
                _centeringOptions[option] = radio;
            }

            if (_samples.Count == 0)
                _centeringOptions[ViewCentering.Sample].ToolTip = "No view stands on a sheet yet — place one, move it where it belongs, and it can be the sample.";

            _sampleBox = new ComboBox
            {
                Width = 520,
                ItemsSource = _samples,
                IsEnabled = _samples.Count > 0,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "The viewport to repeat. Offered first: the one selected in Revit, or the one on the sheet that is open."
            };
            _sampleBox.SelectedItem = _samples.FirstOrDefault(sample => string.Equals(sample.ViewportUniqueId, defaultSample, StringComparison.Ordinal));
            _sampleBox.SelectionChanged += (s, e) =>
            {
                if (_sampleBox.SelectedItem != null)
                    _centeringOptions[ViewCentering.Sample].IsChecked = true;

                RebuildVisible();
            };

            _sampleNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(26, 2, 0, 4) };

            _preview = new Canvas { Width = PreviewWidth, Height = PreviewHeight, ClipToBounds = true };
            _previewText = new TextBlock
            {
                Width = PreviewWidth,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 6, 0, 0)
            };

            _searchBox = new TextBox
            {
                Width = 240,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Finds views by name, kind or folder. A view the search hides keeps its check mark —\n" +
                          "find one lot, tick it, then find the next."
            };
            _searchBox.TextChanged += OnSearchChanged;

            var collapseAll = new Button { Content = "Collapse all", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(10, 0, 0, 0) };
            collapseAll.Click += (s, e) => SetAllExpanded(false);

            var expandAll = new Button { Content = "Expand all", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            expandAll.Click += (s, e) => SetAllExpanded(true);

            _showAll = new CheckBox
            {
                Content = "Show the views that cannot be checked",
                IsChecked = _preferences.ShowPlaced,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
                ToolTip = "Empty views, the sample itself, and views already on a sheet — a view stands on one sheet only\n" +
                          "(legends excepted), so it can only be moved there, \"like a sample sheet\".\n" +
                          "They are listed for reference and have no check box."
            };
            _showAll.Checked += (s, e) => RebuildVisible();
            _showAll.Unchecked += (s, e) => RebuildVisible();

            _scaleBox = new ComboBox
            {
                Width = 130,
                IsEditable = true,
                ItemsSource = new[] { OwnScale }.Concat(StandardScales).ToList(),
                Text = string.IsNullOrEmpty(_preferences.Scale) ? OwnScale : _preferences.Scale,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "A scale from the list, or typed: 1:250 or simply 250. \"" + OwnScale + "\" puts every checked view back to the scale it has."
            };

            _setScaleButton = new Button
            {
                Content = "Set for checked",
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Writes the scale into the \"Scale\" cell of every checked view whose scale can be changed.\n" +
                          "Each cell can also be changed on its own."
            };
            _setScaleButton.Click += OnSetScale;

            _selectAll = new CheckBox
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every view the search leaves in the table"
            };
            _selectAll.Checked += (s, e) => SetAllChecked(true);
            _selectAll.Unchecked += (s, e) => SetAllChecked(false);

            _grid = BuildGrid();

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _placeButton = new Button
            {
                Content = "Place",
                MinWidth = 120,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false,
                ToolTip = "Makes one new sheet per checked view and positions the view on it — one operation, one Ctrl+Z."
            };
            _placeButton.Click += OnPlace;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 100,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(organization, collapseAll, expandAll, closeButton);

            _grid.ItemsSource = _visible;

            _titleblockBox.SelectionChanged += (s, e) => OnTitleblockChanged();
            _titleblockBox.SelectedItem =
                _titleblocks.FirstOrDefault(titleblock => titleblock.IsMeasured && string.Equals(titleblock.Name, _preferences.Titleblock, StringComparison.Ordinal)) ??
                _titleblocks.FirstOrDefault(titleblock => titleblock.IsMeasured) ??
                _titleblocks.FirstOrDefault();

            _ready = true;
            OnTitleblockChanged();
            RebuildVisible();

            Loaded += (s, e) => _searchBox.Focus();
            Closing += OnClosing;
        }

        // ───────────────────────────── the tree ─────────────────────────────

        /// <summary>
        /// Lays the views' folder paths out as folder lines. The views arrive sorted by their paths, so a
        /// folder's views are consecutive, and a new folder line is needed only where a view's path parts
        /// from the one before it.
        /// </summary>
        private void BuildTree(IReadOnlyList<PlaceableViewInfo> views)
        {
            var open = new List<ViewPlacementRow>();

            foreach (var view in views)
            {
                var path = view.Folders;

                var common = 0;
                while (common < open.Count && common < path.Count && string.Equals(open[common].FolderName, path[common], StringComparison.Ordinal))
                    common++;

                open.RemoveRange(common, open.Count - common);

                for (var level = common; level < path.Count; level++)
                {
                    var folder = ViewPlacementRow.Folder(level == 0 ? null : open[level - 1], level, path[level]);
                    folder.Toggled += OnToggled;
                    open.Add(folder);
                    _all.Add(folder);
                }

                var row = ViewPlacementRow.ForView(open.Count == 0 ? null : open[open.Count - 1], path.Count, view);
                row.Toggled += OnToggled;
                row.PropertyChanged += OnRowChanged;
                _all.Add(row);

                foreach (var folder in open)
                    folder.Views.Add(row);
            }
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(string organization, Button collapseAll, Button expandAll, Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // settings + preview
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // find + scale
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "Check the views to place: each one goes on a new sheet of its own, named after the view, and is positioned " +
                       "in the drawing area of the titleblock. The views are in the folders and the order of the Project Browser" +
                       (string.IsNullOrEmpty(organization) ? "" : " (view organization \"" + organization + "\")") +
                       "; a folder's check box takes every view in it. Everything goes in one operation — one Ctrl+Z.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var settings = new Grid();
            settings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            settings.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(SheetGroup());
            left.Children.Add(CenteringGroup());
            Grid.SetColumn(left, 0);
            settings.Children.Add(left);

            var previewPanel = new StackPanel();
            previewPanel.Children.Add(new Border
            {
                Width = PreviewWidth,
                Height = PreviewHeight,
                Background = SystemColors.ControlLightBrush,
                Child = _preview
            });
            previewPanel.Children.Add(_previewText);

            var preview = new GroupBox
            {
                Header = "The sheet",
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Content = previewPanel
            };
            Grid.SetColumn(preview, 1);
            settings.Children.Add(preview);

            Grid.SetRow(settings, 1);
            root.Children.Add(settings);

            var tools = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 6) };

            var find = new StackPanel { Orientation = Orientation.Horizontal };
            find.Children.Add(new TextBlock { Text = "Find:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            find.Children.Add(_searchBox);
            find.Children.Add(collapseAll);
            find.Children.Add(expandAll);
            find.Children.Add(_showAll);
            DockPanel.SetDock(find, Dock.Left);
            tools.Children.Add(find);

            var scale = new StackPanel { Orientation = Orientation.Horizontal };
            scale.Children.Add(new TextBlock { Text = "Scale for the checked views:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            scale.Children.Add(_scaleBox);
            scale.Children.Add(_setScaleButton);
            DockPanel.SetDock(scale, Dock.Right);
            tools.Children.Add(scale);

            Grid.SetRow(tools, 2);
            root.Children.Add(tools);

            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            buttons.Children.Add(_placeButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 4);
            root.Children.Add(bottom);

            return root;
        }

        private GroupBox SheetGroup()
        {
            var panel = new StackPanel();

            var titleblock = new StackPanel { Orientation = Orientation.Horizontal };
            titleblock.Children.Add(FieldLabel("Titleblock:", 110));
            titleblock.Children.Add(_titleblockBox);
            panel.Children.Add(titleblock);

            var margins = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            margins.Children.Add(FieldLabel("Margins, mm:", 110));
            AddMargin(margins, "left", _leftBox);
            AddMargin(margins, "top", _topBox);
            AddMargin(margins, "right", _rightBox);
            AddMargin(margins, "bottom", _bottomBox);
            margins.Children.Add(_areaText);
            panel.Children.Add(margins);

            panel.Children.Add(new TextBlock
            {
                Text = "Measured from the titleblock's outer edge. Take the title panel or the stamp into the margin on its side — " +
                       "the drawing area is what is left. Remembered for every titleblock type separately.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(110, 4, 0, 0)
            });

            var numbering = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            numbering.Children.Add(FieldLabel("Sheet numbers:", 110));
            numbering.Children.Add(new TextBlock { Text = "prefix", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            numbering.Children.Add(_prefixBox);
            numbering.Children.Add(new TextBlock { Text = "start at", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
            numbering.Children.Add(_startBox);
            numbering.Children.Add(_numberingText);
            panel.Children.Add(numbering);

            return new GroupBox
            {
                Header = "New sheets — the sheet name is the view name",
                Padding = new Thickness(10, 8, 10, 10),
                Content = panel
            };
        }

        private GroupBox CenteringGroup()
        {
            var panel = new StackPanel();
            foreach (var centering in ViewCenteringText.All)
                panel.Children.Add(_centeringOptions[centering]);

            var sample = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 0, 0, 0) };
            sample.Children.Add(new TextBlock { Text = "Sample:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            sample.Children.Add(_sampleBox);
            panel.Children.Add(sample);
            panel.Children.Add(_sampleNote);

            return new GroupBox
            {
                Header = "Position by",
                Padding = new Thickness(10, 8, 10, 4),
                Margin = new Thickness(0, 10, 0, 0),
                Content = panel
            };
        }

        private static UIElement OptionContent(string caption, string description)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = caption, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = SystemColors.GrayTextBrush, MaxWidth = 780 });
            return panel;
        }

        private static TextBlock FieldLabel(string text, double width)
        {
            return new TextBlock { Text = text, Width = width, VerticalAlignment = VerticalAlignment.Center };
        }

        private TextBox MarginBox(string side)
        {
            var box = new TextBox
            {
                Width = 52,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = side + " margin, millimetres from the titleblock's outer edge"
            };
            box.TextChanged += (s, e) =>
            {
                if (!_loadingMargins)
                    Revalidate();
            };
            return box;
        }

        private static void AddMargin(Panel panel, string caption, TextBox box)
        {
            panel.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(panel.Children.Count > 1 ? 10 : 0, 0, 4, 0) });
            panel.Children.Add(box);
        }

        private DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                // The order is the Project Browser's — and the order the sheets are numbered in.
                CanUserSortColumns = false,
                IsReadOnly = true,
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
                CellTemplate = GridBuilder.CheckBoxTemplate(nameof(ViewPlacementRow.IsChecked), nameof(ViewPlacementRow.IsCheckable))
            });

            // Not GridBuilder.TextColumn: that one sets a fixed margin, and here the margin is the tree's indent.
            var viewStyle = new Style(typeof(TextBlock));
            viewStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            viewStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            viewStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(ViewPlacementRow.ViewText))));
            viewStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Binding(nameof(ViewPlacementRow.Indent))));
            viewStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, new Binding(nameof(ViewPlacementRow.Weight))));
            viewStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(ViewPlacementRow.NameBrush))));

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "View",
                Width = new DataGridLength(2.2, DataGridLengthUnitType.Star),
                Binding = new Binding(nameof(ViewPlacementRow.ViewText)),
                ElementStyle = viewStyle
            });

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = HeaderWithTip("Scale", "The scale the view gets. Pick one, or type 1:250 or simply 250.\n" +
                                                "A view whose view template sets the scale keeps it — the template decides, not the button.\n" +
                                                "Views already on sheets, and templated ones: the \"Sheet Scale\" button."),
                Width = new DataGridLength(96),
                CellTemplate = ScaleTemplate()
            });

            var size = GridBuilder.TextColumn(string.Empty, nameof(ViewPlacementRow.PaperSize), new DataGridLength(130));
            size.Header = HeaderWithTip("On paper", "Estimated from the crop region and the scale. Views with no crop region are measured once they are placed.");
            grid.Columns.Add(size);

            grid.Columns.Add(GridBuilder.TextColumn("Sheet", nameof(ViewPlacementRow.SheetText), new DataGridLength(1.5, DataGridLengthUnitType.Star)));

            var status = GridBuilder.TextColumn("State", nameof(ViewPlacementRow.StatusText), new DataGridLength(2, DataGridLengthUnitType.Star));
            status.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(ViewPlacementRow.StatusBrush))));
            grid.Columns.Add(status);

            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 26.0));
            var folder = new DataTrigger { Binding = new Binding(nameof(ViewPlacementRow.IsFolder)), Value = true };
            folder.Setters.Add(new Setter(Control.BackgroundProperty, FolderBackground));
            folder.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            rowStyle.Triggers.Add(folder);
            grid.RowStyle = rowStyle;

            grid.PreviewMouseLeftButtonDown += OnGridMouseDown;
            grid.MouseDoubleClick += OnGridDoubleClick;
            grid.PreviewKeyDown += OnGridKeyDown;
            grid.SelectionChanged += (s, e) => DrawPreview();

            return grid;
        }

        /// <summary>
        /// An editable combo box in the cell, bound through its text only — one binding, the rule of the
        /// "DataGridComboBoxColumn" key decision: a pick from the list and a typed "1:250" arrive the same way.
        /// </summary>
        private static DataTemplate ScaleTemplate()
        {
            var combo = new FrameworkElementFactory(typeof(ComboBox));
            combo.SetValue(ComboBox.IsEditableProperty, true);
            combo.SetValue(ItemsControl.ItemsSourceProperty, StandardScales);
            combo.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));
            combo.SetValue(Selector.IsSynchronizedWithCurrentItemProperty, (bool?)false);
            combo.SetBinding(ComboBox.TextProperty,
                new Binding(nameof(ViewPlacementRow.ScaleText)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            combo.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(ViewPlacementRow.CanSetScale)));
            combo.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(ViewPlacementRow.ScaleVisibility)));

            return new DataTemplate { VisualTree = combo };
        }

        private static TextBlock HeaderWithTip(string text, string tip)
        {
            return new TextBlock { Text = text, ToolTip = tip };
        }

        private static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        // ───────────────────────────── what can be checked ─────────────────────────────

        private ViewCentering Centering => _centeringOptions.FirstOrDefault(pair => pair.Value.IsChecked == true).Key;

        private SampleViewportInfo Sample => Centering == ViewCentering.Sample ? _sampleBox.SelectedItem as SampleViewportInfo : null;

        private bool IsSampleRow(ViewPlacementRow row)
        {
            var sample = _sampleBox.SelectedItem as SampleViewportInfo;
            return Centering == ViewCentering.Sample && sample != null && string.Equals(row.View.UniqueId, sample.ViewUniqueId, StringComparison.Ordinal);
        }

        /// <summary>The view stands on a sheet already and is not a legend: checked, it is moved there, not put on a new sheet.</summary>
        private static bool IsMove(ViewPlacementRow row)
        {
            return !row.View.CanPlace && row.View.CanMove;
        }

        /// <summary>
        /// Whether a line can be checked in the positioning chosen: a view that can go on a new sheet
        /// always, a view already on a sheet only "like a sample sheet", the sample itself never. A line
        /// that stops being checkable loses its check mark, so a switch of positioning cannot leave a
        /// move behind that nobody sees.
        /// </summary>
        private void UpdateCheckable()
        {
            var sample = Centering == ViewCentering.Sample;

            _syncing = true;

            foreach (var row in _all.Where(row => !row.IsFolder))
            {
                row.IsCheckable = row.View.Problem == null && !IsSampleRow(row) && (row.View.CanPlace || sample && row.View.CanMove);
                if (!row.IsCheckable && row.IsChecked == true)
                    row.SetChecked(false);
            }

            foreach (var folder in _all.Where(row => row.IsFolder))
                folder.IsCheckable = folder.Views.Exists(row => row.IsCheckable);

            _syncing = false;
        }

        // ───────────────────────────── what is shown ─────────────────────────────

        private string Query => (_searchBox?.Text ?? string.Empty).Trim();

        private void RebuildVisible()
        {
            if (!_ready)
                return;

            UpdateCheckable();

            var query = Query;
            var showAll = _showAll.IsChecked == true;

            _shown = new HashSet<ViewPlacementRow>(_all.Where(row => !row.IsFolder && (showAll || row.IsCheckable) && Matches(row, query)));

            var target = _all
                .Where(row => row.IsFolder ? row.Views.Any(_shown.Contains) : _shown.Contains(row))
                .Where(Unfolded)
                .ToList();

            FlatTree.Sync(_visible, target);
            Revalidate();
        }

        private static bool Matches(ViewPlacementRow row, string query)
        {
            if (query.Length == 0)
                return true;

            if (Contains(row.View.Name, query) || Contains(row.View.Kind, query) || Contains(row.View.PlacedOn, query))
                return true;

            return row.View.Folders.Any(folder => Contains(folder, query));
        }

        private static bool Contains(string text, string query)
        {
            return (text ?? string.Empty).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private static bool Unfolded(ViewPlacementRow row)
        {
            for (var folder = row.Parent; folder != null; folder = folder.Parent)
            {
                if (!folder.IsExpanded)
                    return false;
            }

            return true;
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            // A match inside a folded folder would otherwise be found and still not shown.
            if (Query.Length > 0)
            {
                foreach (var row in _all.Where(row => row.IsFolder))
                    row.IsExpanded = true;
            }

            RebuildVisible();
        }

        private void SetAllExpanded(bool value)
        {
            foreach (var row in _all.Where(row => row.IsFolder))
                row.IsExpanded = value;

            RebuildVisible();
        }

        private void ToggleFolding(ViewPlacementRow folder)
        {
            folder.IsExpanded = !folder.IsExpanded;
            RebuildVisible();
        }

        // ───────────────────────────── check boxes and scales ─────────────────────────────

        /// <summary>
        /// A click on a check box. A folder's click goes to every view under it that the search shows and
        /// that can be checked — the folder's own value is only a summary of theirs and is worked out
        /// again afterwards, so a partly checked folder clears on a click, the same as the header box does.
        /// </summary>
        private void OnToggled(ViewPlacementRow row, bool? before)
        {
            if (_syncing)
                return;

            _syncing = true;

            if (row.IsFolder)
            {
                var value = row.IsChecked == true;
                foreach (var view in row.Views.Where(view => view.IsCheckable && _shown.Contains(view)))
                    view.SetChecked(value);
            }
            else if (!row.IsCheckable)
            {
                row.SetChecked(false);
            }

            _syncing = false;

            Revalidate();
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!_syncing && e.PropertyName == nameof(ViewPlacementRow.ScaleText))
                Revalidate();
        }

        private void SetAllChecked(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(_shown, value);
        }

        private void SetMany(IEnumerable<ViewPlacementRow> rows, bool value)
        {
            _syncing = true;

            foreach (var row in rows.Where(row => !row.IsFolder && row.IsCheckable).ToList())
                row.SetChecked(value);

            _syncing = false;

            Revalidate();
        }

        /// <summary>Space or a double click: the highlighted lines flip together — folders standing for their shown views.</summary>
        private void ToggleSelectedRows()
        {
            var rows = _grid.SelectedItems.OfType<ViewPlacementRow>()
                .SelectMany(row => row.IsFolder ? row.Views.Where(_shown.Contains) : new[] { row })
                .Where(row => row.IsCheckable)
                .Distinct()
                .ToList();

            if (rows.Count == 0)
                return;

            SetMany(rows, !rows.All(row => row.IsChecked == true));
        }

        /// <summary>"Set for checked": one scale into every checked view's cell — or each view's own back.</summary>
        private void OnSetScale(object sender, RoutedEventArgs e)
        {
            var rows = Marked().Where(row => row.CanSetScale).ToList();
            if (rows.Count == 0)
                return;

            var text = (_scaleBox.Text ?? string.Empty).Trim();
            var own = string.Equals(text, OwnScale, StringComparison.CurrentCultureIgnoreCase);
            var scale = own ? 0 : ViewPlacementRow.ParseScale(text);

            if (!own && scale == 0)
            {
                MessageBox.Show(this, "\"" + text + "\" is not a scale. Pick one from the list, or type 1:250 or simply 250.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _syncing = true;

            foreach (var row in rows)
                row.ScaleText = ViewPlacementRow.ScaleCaption(own ? row.View.Scale : scale);

            _syncing = false;

            Revalidate();
        }

        // ───────────────────────────── the mouse and the keys ─────────────────────────────

        private void OnGridMouseDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (!(ItemsControl.ContainerFromElement(_grid, source) is DataGridRow container) ||
                !(container.Item is ViewPlacementRow row) || !row.IsFolder || IsInControl(source))
                return;

            // The second click of a double click is swallowed, so a double click folds once, not twice.
            if (e.ClickCount == 1)
                ToggleFolding(row);

            e.Handled = true;
        }

        /// <summary>The click landed on a check box or a scale box, which handle it themselves.</summary>
        private static bool IsInControl(DependencyObject element)
        {
            for (var current = element; current != null && !(current is DataGridRow); current = VisualTreeHelper.GetParent(current))
            {
                if (current is CheckBox || current is ComboBox)
                    return true;
            }

            return false;
        }

        private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (ItemsControl.ContainerFromElement(_grid, source) is DataGridRow container &&
                container.Item is ViewPlacementRow row && !row.IsFolder && !IsInControl(source) && row.IsCheckable)
                row.IsChecked = row.IsChecked != true;
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            // Typing a scale into a cell: the keys are the box's, not the table's.
            if (e.OriginalSource is TextBox)
                return;

            var row = _grid.SelectedItem as ViewPlacementRow;

            switch (e.Key)
            {
                case Key.Space:
                    ToggleSelectedRows();
                    e.Handled = true;
                    break;
                case Key.Enter when row != null && row.IsFolder:
                case Key.Left when row != null && row.IsFolder && row.IsExpanded:
                case Key.Right when row != null && row.IsFolder && !row.IsExpanded:
                    ToggleFolding(row);
                    e.Handled = true;
                    break;
            }
        }

        // ───────────────────────────── the titleblock and the margins ─────────────────────────────

        private TitleblockInfo Titleblock => _titleblockBox.SelectedItem as TitleblockInfo;

        /// <summary>The margins typed for one titleblock are kept for it before the next one's are loaded.</summary>
        private void OnTitleblockChanged()
        {
            if (!_ready)
                return;

            if (_currentTitleblock != null && TryMargins(out var typed, out _))
                _preferences.SetMargins(_currentTitleblock.Name, typed);

            _currentTitleblock = Titleblock;

            var margins = _preferences.Margins(_currentTitleblock?.Name);

            _loadingMargins = true;
            _leftBox.Text = Format(margins[0]);
            _topBox.Text = Format(margins[1]);
            _rightBox.Text = Format(margins[2]);
            _bottomBox.Text = Format(margins[3]);
            _loadingMargins = false;

            Revalidate();
        }

        private static string Format(double value)
        {
            return value.ToString("0.##", CultureInfo.CurrentCulture);
        }

        /// <summary>Left, top, right, bottom, in millimetres; a comma or a point both read as the decimal separator.</summary>
        private bool TryMargins(out double[] margins, out string error)
        {
            margins = new double[4];
            error = null;

            var boxes = new[] { _leftBox, _topBox, _rightBox, _bottomBox };
            for (var i = 0; i < boxes.Length; i++)
            {
                var text = (boxes[i].Text ?? string.Empty).Trim().Replace(',', '.');
                if (text.Length == 0)
                    text = "0";

                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out margins[i]) || margins[i] < 0 || double.IsNaN(margins[i]))
                {
                    error = "A margin has to be a number of millimetres, 0 or more.";
                    return false;
                }
            }

            return true;
        }

        // ───────────────────────────── validation ─────────────────────────────

        /// <summary>The checked views, in the order of the table — the order their sheets are numbered in.</summary>
        private List<ViewPlacementRow> Marked()
        {
            return _all.Where(row => !row.IsFolder && row.IsChecked == true && row.IsCheckable).ToList();
        }

        /// <summary>What one pass of <see cref="Revalidate"/> found out about the settings, for the lines to be described by.</summary>
        private sealed class Context
        {
            public Dictionary<ViewPlacementRow, string> Numbers;
            public ViewCentering Centering;
            public SampleViewportInfo Sample;
            public Dictionary<string, int> Groups;
            public bool HasArea;
            public double AreaWidth;
            public double AreaHeight;
        }

        /// <summary>
        /// Brings the whole window in line with the settings and the check boxes: the drawing area, the
        /// numbers, every line's "Sheet" and "State", the folders' check boxes, the summary, the preview —
        /// in one method, so the table can never promise one thing while "Place" does another.
        /// </summary>
        private void Revalidate()
        {
            if (!_ready)
                return;

            var errors = new List<string>();
            var marked = Marked();
            var newSheets = marked.Where(row => !IsMove(row)).ToList();
            var titleblock = Titleblock;

            var context = new Context { Centering = Centering, Sample = Sample };

            // The titleblock and the margins only matter to new sheets; moving views on their own sheets needs neither.
            if (titleblock == null || !titleblock.IsMeasured)
            {
                if (newSheets.Count > 0)
                    errors.Add(titleblock == null
                        ? "Choose a titleblock."
                        : "\"" + titleblock.Name + "\" could not be measured (" + (titleblock.Problem ?? "no extents") + ") — choose another titleblock.");
            }
            else if (!TryMargins(out var margins, out var marginError))
            {
                if (newSheets.Count > 0)
                    errors.Add(marginError);
            }
            else
            {
                context.AreaWidth = titleblock.Width - margins[0] - margins[2];
                context.AreaHeight = titleblock.Height - margins[1] - margins[3];
                context.HasArea = context.AreaWidth > 0 && context.AreaHeight > 0;

                if (!context.HasArea && newSheets.Count > 0)
                    errors.Add("The margins leave no drawing area: the titleblock is " + titleblock.Size + ".");
            }

            _areaText.Text = context.HasArea ? "Drawing area: " + TitleblockInfo.Millimetres(context.AreaWidth, context.AreaHeight) : string.Empty;

            context.Numbers = Numbers(newSheets, out var skipped, out var numberingError);
            if (numberingError != null && newSheets.Count > 0)
                errors.Add(numberingError);

            _numberingText.Text = numberingError == null ? Example() : string.Empty;

            if (context.Centering == ViewCentering.Sample && context.Sample == null)
                errors.Add("Choose the sample — the viewport the checked views are to be put like.");

            var badScales = marked.Count(row => row.CanSetScale && row.TargetScale == 0);
            if (badScales > 0)
                errors.Add((badScales == 1 ? "1 checked view has" : badScales + " checked views have") + " a scale that is not a scale — type 1:100 or 100.");

            var conflict = ScaleConflict(marked);
            if (conflict != null)
                errors.Add(conflict);

            context.Groups = marked
                .Where(row => row.View.AlignGroup != null)
                .GroupBy(row => row.View.AlignGroup)
                .ToDictionary(group => group.Key, group => group.Count());

            var tooLarge = 0;

            foreach (var row in _all.Where(row => !row.IsFolder))
            {
                if (Describe(row, context))
                    tooLarge++;
            }

            UpdateFolders();
            UpdateSampleNote(context.Sample, newSheets.Count > 0 ? titleblock : null);
            UpdateSummary(marked, newSheets, errors, skipped, tooLarge);
            DrawPreview();
        }

        /// <summary>
        /// Two checked dependents of one primary view given different scales: Revit keeps one scale for
        /// all of them, so one of the two would silently lose. Asked to be settled rather than guessed.
        /// </summary>
        private static string ScaleConflict(IEnumerable<ViewPlacementRow> marked)
        {
            var clash = marked
                .Where(row => row.CanSetScale && row.TargetScale > 0 && row.View.ScaleGroup != null)
                .GroupBy(row => row.View.ScaleGroup)
                .FirstOrDefault(group => group.Select(row => row.TargetScale).Distinct().Count() > 1);

            if (clash == null)
                return null;

            var names = clash.Take(3).Select(row => "\"" + row.View.Name + "\" " + row.ScaleText);
            return "A primary view and its dependents share one scale, and these are given different ones: " + string.Join(", ", names) + ".";
        }

        /// <summary>Fills in one view line's "Sheet" and "State"; true when the view is larger than the drawing area.</summary>
        private bool Describe(ViewPlacementRow row, Context context)
        {
            var view = row.View;

            if (!row.IsCheckable)
            {
                row.SheetText = string.Empty;
                row.StatusText = NotCheckable(row);
                row.IsWarning = false;
                return false;
            }

            var parts = new List<string>();
            var warning = false;
            var tooLarge = false;

            var isChecked = row.IsChecked == true;
            var move = IsMove(row);

            if (!isChecked)
                row.SheetText = string.Empty;
            else if (move)
                row.SheetText = "stays on " + view.PlacedOn;
            else
                row.SheetText = context.Numbers.TryGetValue(row, out var number) ? number + " - " + view.Name : string.Empty;

            if (isChecked)
            {
                var scale = row.CanSetScale ? row.TargetScale : view.Scale;

                if (row.CanSetScale && scale == 0)
                {
                    parts.Add("\"" + row.ScaleText + "\" is not a scale — type 1:100 or 100");
                    warning = true;
                }
                else if (row.CanSetScale && scale != view.Scale)
                {
                    parts.Add("scale " + ViewPlacementRow.ScaleCaption(view.Scale) + " → " + ViewPlacementRow.ScaleCaption(scale) +
                              (view.ScaleGroup == null ? string.Empty
                                  : view.IsDependent ? " — its primary view and every dependent of it get it too"
                                  : " — its dependent views get it too"));
                }
                else if (view.ScaleLock != null && view.Scale > 0)
                {
                    parts.Add("keeps " + ViewPlacementRow.ScaleCaption(view.Scale) + ": " + view.ScaleLock);
                }

                if (!move && context.HasArea && view.HasSize && scale > 0 &&
                    (view.WidthAt(scale) > context.AreaWidth + FitTolerance || view.HeightAt(scale) > context.AreaHeight + FitTolerance))
                {
                    parts.Insert(0, "Larger than the drawing area (" + TitleblockInfo.Millimetres(context.AreaWidth, context.AreaHeight) + ") — it will stick out");
                    warning = true;
                    tooLarge = true;
                }

                var position = Position(view, context);
                if (position != null)
                    parts.Add(position);
            }

            if (view.IsLegend && view.PlacedOn != null)
                parts.Add("also on " + view.PlacedOn + " — a legend may stand on many sheets");

            row.StatusText = Capitalised(string.Join("; ", parts));
            row.IsWarning = warning;
            return tooLarge;
        }

        /// <summary>How a checked view will be positioned, when it is anything but the plain choice.</summary>
        private static string Position(PlaceableViewInfo view, Context context)
        {
            switch (context.Centering)
            {
                case ViewCentering.CropRegion:
                    return view.AlignGroup == null ? "centred by its viewport: " + view.NotAlignable : null;

                case ViewCentering.ModelPoint:
                    if (view.AlignGroup == null)
                        return "centred by its viewport: " + view.NotAlignable;

                    var others = context.Groups.TryGetValue(view.AlignGroup, out var count) ? count - 1 : 0;
                    return others > 0
                        ? "lines up with " + others + " other checked " + (others == 1 ? "view" : "views")
                        : "no other checked view looks the same way — centred by its crop region";

                case ViewCentering.Sample:
                    if (context.Sample == null)
                        return null;

                    if (context.Sample.AlignGroup != null && view.AlignGroup == context.Sample.AlignGroup)
                        return "lines up with the sample through the model";

                    return "its viewport's middle goes where the sample's is" + (context.Sample.AlignGroup == null
                        ? string.Empty
                        : ": " + (view.AlignGroup == null ? view.NotAlignable : "it looks another way than the sample"));

                default:
                    return null;
            }
        }

        private string NotCheckable(ViewPlacementRow row)
        {
            var view = row.View;

            if (view.Problem != null)
                return view.Problem;

            if (IsSampleRow(row))
                return "The sample — the checked views are put where it stands";

            if (view.PlacedOn != null)
                return "On sheet " + view.PlacedOn + " — a view stands on one sheet only; \"" +
                       ViewCenteringText.Caption(ViewCentering.Sample) + "\" can move it there, \"Sheet Scale\" changes its scale";

            return string.Empty;
        }

        private static string Capitalised(string text)
        {
            return text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text.Substring(1);
        }

        /// <summary>A folder's check box sums up the views under it that the search shows; its "State" counts them.</summary>
        private void UpdateFolders()
        {
            _syncing = true;

            foreach (var folder in _all.Where(row => row.IsFolder))
            {
                var checkable = folder.Views.Where(row => row.IsCheckable && _shown.Contains(row)).ToList();
                var checkedCount = checkable.Count(row => row.IsChecked == true);

                folder.SetChecked(checkable.Count == 0 || checkedCount == 0 ? false : checkedCount == checkable.Count ? true : (bool?)null);

                var shown = folder.Views.Count(_shown.Contains);
                folder.StatusText = Views(shown) + (checkedCount > 0 ? ", " + checkedCount + " checked" : string.Empty);
            }

            _syncing = false;
        }

        private void UpdateSampleNote(SampleViewportInfo sample, TitleblockInfo newSheets)
        {
            if (sample == null)
            {
                _sampleNote.Text = Centering == ViewCentering.Sample ? "No sample chosen." : string.Empty;
                return;
            }

            var text = "The view of the sample stays where it is.";

            if (newSheets != null && sample.Titleblock.Length > 0 && !string.Equals(sample.Titleblock, newSheets.Name, StringComparison.Ordinal))
                text += " It stands on a sheet of \"" + sample.Titleblock + "\", the new sheets get \"" + newSheets.Name +
                        "\" — the position is copied as it is, from the sheet's origin.";

            _sampleNote.Text = text;
        }

        private static string Views(int count)
        {
            return count == 1 ? "1 view" : count + " views";
        }

        // ───────────────────────────── numbering ─────────────────────────────

        /// <summary>
        /// Gives every view going on a new sheet its sheet number: the prefix, then a counter from "start
        /// at", with the counter's leading zeros kept. A number the project already holds — a placeholder
        /// sheet's included — is skipped rather than clashed with: Revit refuses a second sheet of one number.
        /// </summary>
        private Dictionary<ViewPlacementRow, string> Numbers(IReadOnlyList<ViewPlacementRow> rows, out List<string> skipped, out string error)
        {
            var numbers = new Dictionary<ViewPlacementRow, string>();
            skipped = new List<string>();
            error = null;

            if (!TryNumbering(out var prefix, out var counter, out var width, out error))
                return numbers;

            foreach (var row in rows)
            {
                while (true)
                {
                    var number = prefix + counter.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
                    counter++;

                    if (_taken.Contains(number))
                    {
                        skipped.Add(number);
                        continue;
                    }

                    numbers[row] = number;
                    break;
                }
            }

            return numbers;
        }

        private bool TryNumbering(out string prefix, out long start, out int width, out string error)
        {
            prefix = (_prefixBox.Text ?? string.Empty).Trim();
            var text = (_startBox.Text ?? string.Empty).Trim();
            start = 0;
            width = text.Length;
            error = null;

            if (prefix.IndexOfAny(Forbidden) >= 0)
            {
                error = "The prefix holds a character Revit does not allow in a sheet number: " + string.Join(" ", Forbidden);
                return false;
            }

            if (text.Length == 0 || text.Length > 9 || !text.All(character => character >= '0' && character <= '9'))
            {
                error = "\"Start at\" has to be a whole number: 101, 001…";
                return false;
            }

            start = long.Parse(text, CultureInfo.InvariantCulture);
            return true;
        }

        private string Example()
        {
            if (!TryNumbering(out var prefix, out var start, out var width, out _))
                return string.Empty;

            return "→ " + prefix + start.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') + ", " +
                   prefix + (start + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') + "…";
        }

        // ───────────────────────────── the summary ─────────────────────────────

        private void UpdateSummary(
            IReadOnlyList<ViewPlacementRow> marked,
            IReadOnlyList<ViewPlacementRow> newSheets,
            IReadOnlyList<string> errors,
            IReadOnlyList<string> skipped,
            int tooLarge)
        {
            var checkableShown = _shown.Where(row => row.IsCheckable).ToList();
            var checkedShown = checkableShown.Count(row => row.IsChecked == true);

            _syncingSelectAll = true;
            _selectAll.IsChecked = checkableShown.Count == 0 || checkedShown == 0
                ? false
                : checkedShown == checkableShown.Count ? true : (bool?)null;
            _syncingSelectAll = false;

            _setScaleButton.IsEnabled = marked.Any(row => row.CanSetScale);

            var ok = errors.Count == 0 && marked.Count > 0;
            _placeButton.IsEnabled = ok;
            _placeButton.Content = marked.Count > 0 ? "Place (" + marked.Count + ")" : "Place";

            if (errors.Count > 0)
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = string.Join(" ", errors);
                return;
            }

            if (marked.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Nothing is checked. Views that can be checked: " + _all.Count(row => !row.IsFolder && row.IsCheckable) + ".";
                return;
            }

            var parts = new List<string>();

            if (newSheets.Count > 0)
            {
                var first = Number(newSheets.First().SheetText);
                var last = Number(newSheets.Last().SheetText);
                parts.Add((newSheets.Count == 1 ? "1 new sheet" : newSheets.Count + " new sheets") + ", " + first +
                          (newSheets.Count > 1 ? " … " + last : string.Empty));
            }

            var moves = marked.Count - newSheets.Count;
            if (moves > 0)
                parts.Add(moves + " moved on " + (moves == 1 ? "its sheet" : "their sheets"));

            var rescaled = marked.Count(row => row.CanSetScale && row.TargetScale > 0 && row.TargetScale != row.View.Scale);
            if (rescaled > 0)
                parts.Add(rescaled + " with a new scale");

            var text = "Checked: " + Views(marked.Count) + " → " + string.Join(", ", parts) + ".";

            if (skipped.Count > 0)
                text += " Taken, skipped: " + string.Join(", ", skipped.Take(8)) + (skipped.Count > 8 ? " and " + (skipped.Count - 8) + " more" : string.Empty) + ".";

            var hidden = marked.Count(row => !_shown.Contains(row));
            if (hidden > 0)
                text += " " + hidden + " of them out of sight — the search hides them.";

            if (tooLarge > 0)
                text += " " + (tooLarge == 1 ? "1 is" : tooLarge + " are") + " larger than the drawing area.";

            _status.Foreground = tooLarge > 0 ? Brushes.Firebrick : SystemColors.ControlTextBrush;
            _status.Text = text;
        }

        private static string Number(string sheetTitle)
        {
            var dash = sheetTitle.IndexOf(" - ", StringComparison.Ordinal);
            return dash > 0 ? sheetTitle.Substring(0, dash) : sheetTitle;
        }

        // ───────────────────────────── the preview ─────────────────────────────

        /// <summary>
        /// The sheet to scale: the titleblock's outline, the drawing area inside it, and the highlighted
        /// view's estimated footprint where it lands — in the middle, or round the middle of the sample's
        /// box when positioning like a sample. Only an estimate: the real outline is known once the view
        /// is on the sheet, and the report checks that one.
        /// </summary>
        private void DrawPreview()
        {
            if (!_ready)
                return;

            _preview.Children.Clear();

            var titleblock = Titleblock;
            if (titleblock == null || !titleblock.IsMeasured)
            {
                _previewText.Text = "No titleblock to show.";
                return;
            }

            const double pad = 8;
            var scale = Math.Min((PreviewWidth - 2 * pad) / titleblock.Width, (PreviewHeight - 2 * pad) / titleblock.Height);
            var x0 = (PreviewWidth - titleblock.Width * scale) / 2;
            var y0 = (PreviewHeight - titleblock.Height * scale) / 2;

            double X(double x) => x0 + (x - titleblock.Left) * scale;
            double Y(double y) => y0 + (titleblock.Top - y) * scale;

            Add(new Rectangle { Width = titleblock.Width * scale, Height = titleblock.Height * scale, Fill = Brushes.White, Stroke = Brushes.DimGray, StrokeThickness = 1 }, x0, y0);

            var text = "Sheet " + titleblock.Size;
            double middleX, middleY;

            if (TryMargins(out var margins, out _) && titleblock.Width - margins[0] - margins[2] > 0 && titleblock.Height - margins[1] - margins[3] > 0)
            {
                var areaWidth = titleblock.Width - margins[0] - margins[2];
                var areaHeight = titleblock.Height - margins[1] - margins[3];

                Add(new Rectangle
                {
                    Width = areaWidth * scale,
                    Height = areaHeight * scale,
                    Stroke = AreaStroke,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 2 }
                }, X(titleblock.Left + margins[0]), Y(titleblock.Top - margins[1]));

                text += ", drawing area " + TitleblockInfo.Millimetres(areaWidth, areaHeight);
                middleX = titleblock.Left + margins[0] + areaWidth / 2;
                middleY = titleblock.Bottom + margins[3] + areaHeight / 2;
            }
            else
            {
                middleX = (titleblock.Left + titleblock.Right) / 2;
                middleY = (titleblock.Bottom + titleblock.Top) / 2;
            }

            var sample = Sample;
            if (sample != null)
            {
                Add(new Rectangle
                {
                    Width = (sample.Right - sample.Left) * scale,
                    Height = (sample.Top - sample.Bottom) * scale,
                    Stroke = FootprintStroke,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 2 }
                }, X(sample.Left), Y(sample.Top));

                middleX = (sample.Left + sample.Right) / 2;
                middleY = (sample.Bottom + sample.Top) / 2;
                text += "; dashed orange — the sample on " + sample.SheetTitle;
            }

            var shown = (_grid.SelectedItem as ViewPlacementRow) is ViewPlacementRow selected && !selected.IsFolder && selected.View.HasSize
                ? selected
                : Marked().FirstOrDefault(row => row.View.HasSize);

            if (shown != null)
            {
                var viewScale = shown.TargetScale > 0 ? shown.TargetScale : shown.View.Scale;
                var width = shown.View.WidthAt(viewScale);
                var height = shown.View.HeightAt(viewScale);

                Add(new Rectangle { Width = width * scale, Height = height * scale, Fill = FootprintFill, Stroke = FootprintStroke, StrokeThickness = 1 },
                    X(middleX - width / 2),
                    Y(middleY + height / 2));

                text += ". Shown: \"" + shown.View.Name + "\", " + shown.PaperSize;
            }

            _previewText.Text = text + ".";
        }

        private void Add(UIElement element, double left, double top)
        {
            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            _preview.Children.Add(element);
        }

        // ───────────────────────────── closing ─────────────────────────────

        private void OnPlace(object sender, RoutedEventArgs e)
        {
            Revalidate();
            if (!_placeButton.IsEnabled)
                return;

            var marked = Marked();
            var newSheets = marked.Where(row => !IsMove(row)).ToList();
            var numbers = Numbers(newSheets, out _, out var error);
            if (newSheets.Count > 0 && (error != null || numbers.Count != newSheets.Count))
                return;

            var titleblock = Titleblock;
            var margins = ViewsToSheetsPreferences.DefaultMargins;
            if (newSheets.Count > 0 && (titleblock == null || !TryMargins(out margins, out _)))
                return;

            var hidden = marked.Count(row => !_shown.Contains(row));
            if (hidden > 0)
            {
                var answer = MessageBox.Show(this,
                    hidden + " of the " + marked.Count + " checked views are out of sight — the search hides them. They are placed too.\n\n" +
                    "Place all " + marked.Count + "?",
                    WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes)
                    return;
            }

            Request = new ViewPlacementRequest(
                newSheets.Count > 0 ? titleblock : null,
                margins,
                Centering,
                Sample,
                MarginsByTitleblock(),
                marked.Select(row => new ViewPlacementItem(
                        row.View,
                        IsMove(row) ? null : numbers[row],
                        row.CanSetScale ? row.TargetScale : 0))
                    .ToList());

            DialogResult = true;
        }

        /// <summary>The margins of every titleblock type, the one in the window as typed — what a moved view is checked against on its own sheet.</summary>
        private Dictionary<string, double[]> MarginsByTitleblock()
        {
            var margins = _titleblocks.ToDictionary(titleblock => titleblock.Name, titleblock => _preferences.Margins(titleblock.Name), StringComparer.Ordinal);

            if (Titleblock != null && TryMargins(out var typed, out _))
                margins[Titleblock.Name] = typed;

            return margins;
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            var titleblock = Titleblock;
            if (titleblock != null)
            {
                _preferences.Titleblock = titleblock.Name;
                if (TryMargins(out var margins, out _))
                    _preferences.SetMargins(titleblock.Name, margins);
            }

            _preferences.Centering = Centering;
            _preferences.Prefix = (_prefixBox.Text ?? string.Empty).Trim();
            _preferences.Start = (_startBox.Text ?? string.Empty).Trim();
            _preferences.ShowPlaced = _showAll.IsChecked == true;
            _preferences.Scale = (_scaleBox.Text ?? string.Empty).Trim();
            _preferences.Save();
        }
    }
}
