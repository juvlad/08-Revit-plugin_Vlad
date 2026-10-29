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
    /// The "Insert Links" window: which links, into which project worksets, into which models — three
    /// steps, one tab each, in the order the work is thought of.
    ///
    /// **1. Links** — models on Revit Server, gathered the way every batch list here is gathered
    /// (<see cref="ModelListPanel"/>, server-only), plus one placement for all of them.
    ///
    /// **2. Worksets** — two different things, kept apart as in "Link Manager". The *project workset* of a
    /// link is where the link goes in each model, and one name per link serves every model: the office
    /// names its link worksets the same everywhere ("01_Link_AR"). The list offers the open model's
    /// worksets and those read from the models of step 3, but a name can be typed too — the cell is an
    /// editable drop-down. The *worksets inside the links* are what to close inside every link: "Link
    /// Manager"'s list, rule and mode, applied by the same code (<see cref="LinkWorksetRule"/>).
    ///
    /// **3. Models** — the models on Revit Server the links go into, again a <see cref="ModelListPanel"/>.
    /// Their worksets are read — on a button, and on "Insert" for any not read yet — so that before
    /// anything runs the window can say which models lack a link's workset, and what will happen there.
    ///
    /// The window knows nothing about the Revit API: reading a model's worksets is a call-back, and the
    /// window hands back the checked links, the checked models and its settings.
    /// </summary>
    internal sealed class InsertLinksWindow : Window
    {
        private const string WindowTitle = "Insert Links";

        private readonly InsertLinksPreferences _preferences;
        private readonly LinkPreferences _linkPreferences;
        private readonly Func<IReadOnlyList<LinkEntry>, LinkWorksetScan> _readWorksets;

        private readonly TabControl _tabs;
        private readonly TabItem _linksTab;
        private readonly TabItem _worksetsTab;
        private readonly TabItem _modelsTab;

        // ── step 1 ──
        private readonly ModelListPanel _links;
        private readonly ComboBox _placementBox;
        private readonly ComboBox _attachmentBox;

        // ── step 2: the project workset of each link ──
        private readonly Dictionary<string, InsertLinkRow> _rows = new Dictionary<string, InsertLinkRow>(StringComparer.Ordinal);
        private readonly ObservableCollection<InsertLinkRow> _shown = new ObservableCollection<InsertLinkRow>();
        private readonly ObservableCollection<string> _offered = new ObservableCollection<string>();
        private readonly DataGrid _linkGrid;
        private readonly ComboBox _allWorksetBox;
        private readonly CheckBox _matchBox;

        /// <summary>Rows whose workset the name-based guess filled in — cleared again when the guess is turned off.</summary>
        private readonly HashSet<InsertLinkRow> _autoWorkset = new HashSet<InsertLinkRow>();

        /// <summary>The guess is changing a row's workset right now — not a hand edit.</summary>
        private bool _suggesting;

        // ── step 2: the worksets inside the links ──
        private readonly ObservableCollection<WorksetRow> _worksets = new ObservableCollection<WorksetRow>();
        private readonly DataGrid _worksetGrid;
        private readonly ComboBox _worksetModeBox;
        private readonly ComboBox _ruleBox;
        private readonly TextBox _patternBox;
        private readonly Button _readInsideButton;
        private readonly TextBlock _worksetCaption;
        private bool _insideRead;

        // ── step 3 ──
        private readonly ModelListPanel _models;
        private readonly CheckBox _createBox;
        private readonly TextBlock _modelCaption;

        /// <summary>The worksets of every model read, by its key.</summary>
        private readonly Dictionary<string, IReadOnlyList<string>> _modelWorksets =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        /// <summary>Models whose worksets could not be read, by key, with the reason.</summary>
        private readonly Dictionary<string, string> _modelUnread = new Dictionary<string, string>(StringComparer.Ordinal);

        // ── the bottom ──
        private readonly TextBlock _status;
        private readonly Button _backButton;
        private readonly Button _nextButton;
        private readonly Button _insertButton;

        /// <summary>The checked links, each with its project workset in <see cref="LinkEntry.Workset"/>.</summary>
        public IReadOnlyList<LinkEntry> Links { get; private set; } = new List<LinkEntry>();

        /// <summary>The checked models the links go into.</summary>
        public IReadOnlyList<LinkEntry> Models { get; private set; } = new List<LinkEntry>();

        /// <summary>The placement, the worksets inside the links and the rest — what the run is set up with.</summary>
        public InsertLinksPreferences Preferences => _preferences;

        /// <param name="projectWorksets">The open model's worksets — offered in the drop-down before any model is read; may be empty.</param>
        /// <param name="readWorksets">Reads models' worksets without opening them. All the work with Revit is the command's.</param>
        public InsertLinksWindow(
            InsertLinksPreferences preferences,
            LinkPreferences linkPreferences,
            IReadOnlyList<string> projectWorksets,
            Func<IReadOnlyList<LinkEntry>, LinkWorksetScan> readWorksets)
        {
            _preferences = preferences ?? new InsertLinksPreferences();
            _linkPreferences = linkPreferences ?? new LinkPreferences();
            _readWorksets = readWorksets;

            Title = WindowTitle;
            Width = 1100;
            Height = 780;
            MinWidth = 780;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            Offer(projectWorksets);

            // ── step 1 ──
            _links = new ModelListPanel(WindowTitle, _linkPreferences, _preferences.LinkSet, false, false, false);

            _placementBox = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center };
            _placementBox.Items.Add("By shared coordinates");
            _placementBox.Items.Add("Origin to origin");
            _placementBox.Items.Add("Centre to centre");
            _placementBox.Items.Add("By project site location");
            _placementBox.SelectedIndex = (int)_preferences.Placement;
            _placementBox.ToolTip = "One placement for every link in every model.";

            _attachmentBox = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center };
            _attachmentBox.Items.Add("Overlay");
            _attachmentBox.Items.Add("Attachment");
            _attachmentBox.SelectedIndex = _preferences.IsAttachment ? 1 : 0;
            _attachmentBox.ToolTip =
                "Overlay — the link will not carry over into a model that links to this one.\n" +
                "Attachment — it will.";

            // ── step 2 ──
            _allWorksetBox = new ComboBox
            {
                Width = 220,
                IsEditable = true,
                ItemsSource = _offered,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "A project workset to set on the highlighted rows of the table below — picked from the list or typed in."
            };

            _matchBox = new CheckBox
            {
                Content = "Guess from the model name",
                IsChecked = _preferences.MatchProjectWorkset,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
                ToolTip =
                    "For a link with no workset chosen, the add-in takes the discipline code from the model name\n" +
                    "(…_OV_R22 → OV) and picks the offered workset whose name carries that code as a separate word:\n" +
                    "\"01_Link_OV\", and the like. A choice made by hand is never overwritten.\n" +
                    "The code list is edited in links\\_settings.txt (the DISCIPLINE lines)."
            };
            _matchBox.Checked += (s, e) => ApplySuggestions();
            _matchBox.Unchecked += (s, e) => ClearSuggestions();

            _linkGrid = BuildLinkGrid();

            _worksetModeBox = new ComboBox { Width = 230, VerticalAlignment = VerticalAlignment.Center };
            _worksetModeBox.Items.Add("Open all worksets");
            _worksetModeBox.Items.Add("Close all worksets");
            _worksetModeBox.Items.Add("As last opened");
            _worksetModeBox.SelectedIndex = (int)_preferences.WorksetMode;
            _worksetModeBox.SelectionChanged += (s, e) => UpdateWorksetCaption();

            _readInsideButton = new Button
            {
                Content = "Read Worksets",
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                IsEnabled = _readWorksets != null,
                ToolTip =
                    "Reads the workset names of the checked links without loading them.\n" +
                    "This is only for seeing the name list: the run reads every link's worksets again,\n" +
                    "in every model, right before linking it."
            };
            _readInsideButton.Click += OnReadInside;

            _ruleBox = new ComboBox { Width = 170, VerticalAlignment = VerticalAlignment.Center };
            _ruleBox.Items.Add("starting with");
            _ruleBox.Items.Add("containing");
            _ruleBox.SelectedIndex = _preferences.WorksetPatternContains ? 1 : 0;

            _patternBox = new TextBox
            {
                Text = _preferences.WorksetPattern,
                MinWidth = 170,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2),
                ToolTip =
                    "The rule applies to every link on its own, not only through \"Check by rule\":\n" +
                    "it catches that link's own worksets even when the exact name is not in the list below."
            };
            _patternBox.TextChanged += (s, e) => UpdateSummary();

            _worksetCaption = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            _worksetGrid = BuildWorksetGrid();

            // ── step 3 ──
            _models = new ModelListPanel(WindowTitle, _linkPreferences, _preferences.ModelSet, false, false);

            _createBox = new CheckBox
            {
                Content = "Make a link's workset in a model that does not have it",
                IsChecked = _preferences.CreateWorksets,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip =
                    "On: the workset is made in that model and the link goes into it.\n" +
                    "Off: the link is left out of that model, and the report says so.\n" +
                    "A name typed wrong would become a stray workset in every model — check \"In the models\" on step 2 first."
            };
            _createBox.Checked += (s, e) => UpdateSummary();
            _createBox.Unchecked += (s, e) => UpdateSummary();

            _modelCaption = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 6, 0, 0)
            };

            // ── the bottom ──
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _backButton = BottomButton("< Back", 90);
            _backButton.Click += (s, e) => _tabs.SelectedIndex = Math.Max(0, _tabs.SelectedIndex - 1);

            _nextButton = BottomButton("Next >", 90);
            _nextButton.Click += (s, e) => _tabs.SelectedIndex = Math.Min(_tabs.Items.Count - 1, _tabs.SelectedIndex + 1);

            _insertButton = BottomButton("Insert links…", 170);
            _insertButton.IsEnabled = false;
            _insertButton.Click += OnInsert;

            var closeButton = BottomButton("Close", 110);
            closeButton.IsCancel = true;

            _linksTab = new TabItem { Content = BuildLinksStep() };
            _worksetsTab = new TabItem { Content = BuildWorksetsStep() };
            _modelsTab = new TabItem { Content = BuildModelsStep() };

            _tabs = new TabControl();
            _tabs.Items.Add(_linksTab);
            _tabs.Items.Add(_worksetsTab);
            _tabs.Items.Add(_modelsTab);
            _tabs.SelectionChanged += (s, e) =>
            {
                // A ComboBox inside a tab raises SelectionChanged too, and it bubbles up here.
                if (e.OriginalSource == _tabs)
                    UpdateSummary();
            };

            Content = BuildLayout(closeButton);

            // The inside worksets checked last time are offered checked: usually nothing has to change.
            foreach (var name in _preferences.Worksets)
                AddWorkset(name, true);

            foreach (var workset in _worksets)
                workset.IsSelected = true;

            _links.Changed += (s, e) => SyncRows();
            _links.Notice += (s, text) => ShowNotice(text);
            _models.Changed += (s, e) =>
            {
                RecountModels();
                UpdateSummary();
            };
            _models.Notice += (s, text) => ShowNotice(text);

            _links.Add(_preferences.Links, "link(s) from last time");
            _models.Add(_preferences.Models, "model(s) from last time");

            UpdateWorksetCaption();
            UpdateModelCaption();
            SyncRows();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(_tabs, 0);
            root.Children.Add(_tabs);

            var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_backButton);
            buttons.Children.Add(_nextButton);
            buttons.Children.Add(_insertButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 1);
            root.Children.Add(bottom);

            return root;
        }

        private UIElement BuildLinksStep()
        {
            var step = new Grid { Margin = new Thickness(8) };
            step.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            step.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // links
            step.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // placement

            var hint = Hint("The models to link into every model of step 3 — on Revit Server: \"Folder…\", \"Revit Server…\" " +
                            "or a saved set. The sets are the same as in \"Link Manager\", and a set's project worksets come " +
                            "along with it. Only the checked links are inserted.");
            Grid.SetRow(hint, 0);
            step.Children.Add(hint);

            Grid.SetRow(_links, 1);
            step.Children.Add(_links);

            var placement = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            placement.Children.Add(Label("Placement for every link:"));
            placement.Children.Add(_placementBox);
            placement.Children.Add(Label("Link type:", 16));
            placement.Children.Add(_attachmentBox);
            placement.Children.Add(new TextBlock
            {
                Text = "every new link is pinned",
                Foreground = SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            });

            Grid.SetRow(placement, 2);
            step.Children.Add(placement);

            return step;
        }

        private UIElement BuildWorksetsStep()
        {
            var step = new Grid { Margin = new Thickness(8) };
            step.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            step.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // set for rows
            step.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // the links
            step.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // inside: header
            step.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // inside: caption
            step.RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });                  // inside: list

            var hint = Hint("The project workset each link goes into. The names are the same in every model, so one name per link " +
                            "is enough: pick it from the list or type it in. The list offers the worksets of the open model and of " +
                            "the models read on step 3; \"In the models\" says where a workset is missing.");
            Grid.SetRow(hint, 0);
            step.Children.Add(hint);

            var setter = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            setter.Children.Add(Label("Workset:"));
            setter.Children.Add(_allWorksetBox);
            setter.Children.Add(SmallButton("Set for highlighted rows",
                "Sets the workset on the left on every row highlighted in the table (click, Ctrl+click, Shift+click).",
                OnSetWorkset, new Thickness(8, 0, 0, 4)));
            setter.Children.Add(_matchBox);
            setter.Children.Add(SmallButton("Read the models' worksets",
                "Reads the worksets of the models checked on step 3 without opening them —\n" +
                "fills the list and the \"In the models\" column.", OnReadModels, new Thickness(0, 0, 0, 4)));
            Grid.SetRow(setter, 1);
            step.Children.Add(setter);

            Grid.SetRow(_linkGrid, 2);
            step.Children.Add(_linkGrid);

            var inside = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            inside.Children.Add(new TextBlock
            {
                Text = "Worksets inside the links, as in \"Link Manager\":",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 4)
            });
            inside.Children.Add(_readInsideButton);
            inside.Children.Add(Label("On linking:"));
            inside.Children.Add(_worksetModeBox);
            inside.Children.Add(Label("Name rule:", 16));
            inside.Children.Add(_ruleBox);
            inside.Children.Add(_patternBox);
            inside.Children.Add(SmallButton("Check by rule", "Checks the names matching the rule in the list below.",
                OnMarkWorksets, new Thickness(0, 0, 0, 4)));
            Grid.SetRow(inside, 3);
            step.Children.Add(inside);

            Grid.SetRow(_worksetCaption, 4);
            step.Children.Add(_worksetCaption);

            Grid.SetRow(_worksetGrid, 5);
            step.Children.Add(_worksetGrid);

            return step;
        }

        private UIElement BuildModelsStep()
        {
            var step = new Grid { Margin = new Thickness(8) };
            step.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            step.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // models
            step.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // read + create
            step.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // caption

            var hint = Hint("The models on Revit Server the links go into. Each is opened as a local copy with only the links' " +
                            "worksets open, the links are inserted and checked, and the model is synchronised with central with " +
                            "everything handed back. A link a model has already is left as it is. Best run with no model open: " +
                            "a model open in Revit cannot be worked on here, and a link to a model open as a project will not load.");
            Grid.SetRow(hint, 0);
            step.Children.Add(hint);

            Grid.SetRow(_models, 1);
            step.Children.Add(_models);

            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            actions.Children.Add(SmallButton("Read worksets of the checked models",
                "Reads every checked model's worksets without opening it: which of them lack a link's workset\n" +
                "shows on step 2 and in the confirmation. \"Insert\" reads the ones not read yet by itself.",
                OnReadModels, new Thickness(0, 0, 0, 4)));
            actions.Children.Add(_createBox);
            Grid.SetRow(actions, 2);
            step.Children.Add(actions);

            Grid.SetRow(_modelCaption, 3);
            step.Children.Add(_modelCaption);

            return step;
        }

        private DataGrid BuildLinkGrid()
        {
            var grid = new DataGrid
            {
                ItemsSource = _shown,
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
                Margin = new Thickness(0, 4, 0, 0)
            };

            grid.Columns.Add(GridBuilder.TextColumn("Link", nameof(InsertLinkRow.Name), new DataGridLength(1, DataGridLengthUnitType.Star)));

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Project workset",
                Width = new DataGridLength(230),
                CanUserSort = true,
                SortMemberPath = nameof(InsertLinkRow.Workset),
                CellTemplate = BuildWorksetTemplate()
            });

            var inModels = GridBuilder.TextColumn("In the models", nameof(InsertLinkRow.InModels), new DataGridLength(190));
            inModels.ElementStyle.Triggers.Add(new DataTrigger
            {
                Binding = new Binding(nameof(InsertLinkRow.IsMissing)),
                Value = true,
                Setters = { new Setter(TextBlock.ForegroundProperty, Brushes.DarkRed) }
            });
            grid.Columns.Add(inModels);

            grid.Columns.Add(GridBuilder.TextColumn("Worksets inside", nameof(InsertLinkRow.Inside), new DataGridLength(110)));
            grid.Columns.Add(GridBuilder.TextColumn("State", nameof(InsertLinkRow.Note), new DataGridLength(1, DataGridLengthUnitType.Star)));

            return grid;
        }

        /// <summary>
        /// An editable drop-down in the cell, bound by its text alone: a name not offered yet — no model read,
        /// no model open — can still be typed in, and one binding is the only binding (see the
        /// "DataGridComboBoxColumn" key decision). The list is shared by every row, so the current item is
        /// not synchronised — a choice in one row would drag the others along.
        /// </summary>
        private DataTemplate BuildWorksetTemplate()
        {
            var combo = new FrameworkElementFactory(typeof(ComboBox));
            combo.SetValue(ItemsControl.ItemsSourceProperty, _offered);
            combo.SetValue(ComboBox.IsEditableProperty, true);
            combo.SetValue(Selector.IsSynchronizedWithCurrentItemProperty, (bool?)false);
            combo.SetBinding(ComboBox.TextProperty,
                new Binding(nameof(InsertLinkRow.Workset)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            combo.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));
            combo.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            return new DataTemplate { VisualTree = combo };
        }

        private DataGrid BuildWorksetGrid()
        {
            var grid = new DataGrid
            {
                ItemsSource = _worksets,
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
                Margin = new Thickness(0, 6, 0, 0)
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = string.Empty,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate(nameof(WorksetRow.IsSelected))
            });

            grid.Columns.Add(GridBuilder.TextColumn("Workset inside the links", nameof(WorksetRow.Name), new DataGridLength(1, DataGridLengthUnitType.Star)));
            grid.Columns.Add(GridBuilder.TextColumn("Found in", nameof(WorksetRow.Where), new DataGridLength(160)));

            grid.MouseDoubleClick += (s, e) => ToggleSelectedWorksets();

            return grid;
        }

        private static TextBlock Hint(string text)
        {
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        }

        private static TextBlock Label(string text, double left = 0)
        {
            return new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(left, 0, 8, 4)
            };
        }

        private static Button SmallButton(string text, string tooltip, RoutedEventHandler handler, Thickness margin)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = margin,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tooltip
            };
            button.Click += handler;

            return button;
        }

        private static Button BottomButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                MinWidth = width,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0)
            };
        }

        private void ShowNotice(string text)
        {
            _status.Foreground = SystemColors.GrayTextBrush;
            _status.Text = text;
        }

        // ───────────────────────────── step 2: the project workset of each link ─────────────────────────────

        /// <summary>Brings the workset table in line with the links of step 1: a row per checked link, in the list's order.</summary>
        private void SyncRows()
        {
            var keys = new HashSet<string>(_links.Rows.Select(row => row.Key), StringComparer.Ordinal);

            foreach (var gone in _rows.Keys.Where(key => !keys.Contains(key)).ToList())
            {
                var row = _rows[gone];
                row.PropertyChanged -= OnLinkRowChanged;
                _autoWorkset.Remove(row);
                _rows.Remove(gone);
            }

            foreach (var source in _links.Rows)
            {
                if (_rows.ContainsKey(source.Key))
                    continue;

                var row = new InsertLinkRow(source.Entry);
                row.PropertyChanged += OnLinkRowChanged;
                _rows[source.Key] = row;
                Suggest(row);
            }

            _shown.Clear();
            foreach (var source in _links.Marked())
                _shown.Add(_rows[source.Key]);

            RecountInside();
            RecountModels();
            UpdateSummary();
        }

        private void OnLinkRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(InsertLinkRow.Workset))
                return;

            var row = (InsertLinkRow)sender;

            // A hand edit clears the "filled in by the guess" flag — turning the guess off must leave it alone —
            // and whatever the guess said about the row: it is no longer the guess's.
            if (!_suggesting)
            {
                _autoWorkset.Remove(row);

                if (IsGuessNote(row.Note))
                    row.Note = string.Empty;
            }

            Recount(row, _models.Marked());
            UpdateSummary();
        }

        private void OnSetWorkset(object sender, RoutedEventArgs e)
        {
            var rows = _linkGrid.SelectedItems.OfType<InsertLinkRow>().ToList();
            if (rows.Count == 0)
            {
                MessageBox.Show(this, "Highlight the rows to set the workset for — click, Ctrl+click or Shift+click a link's name.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var workset = LinkPreferences.NormalizeWorkset(_allWorksetBox.Text);
            if (workset.Length == 0)
            {
                MessageBox.Show(this, "Pick a workset in the list on the left or type its name.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var row in rows)
                row.Workset = workset;
        }

        /// <summary>Adds names to the drop-down's list, keeping it in order and free of repeats.</summary>
        private void Offer(IEnumerable<string> names)
        {
            if (names == null)
                return;

            foreach (var name in names.Select(LinkPreferences.NormalizeWorkset).Where(name => name.Length > 0))
            {
                if (_offered.Any(known => LinkPreferences.SameWorkset(known, name)))
                    continue;

                var index = 0;
                while (index < _offered.Count && string.Compare(_offered[index], name, StringComparison.CurrentCultureIgnoreCase) < 0)
                    index++;

                _offered.Insert(index, name);
            }
        }

        /// <summary>
        /// The project workset from the discipline code in the link's name — only for a row with none chosen,
        /// only out of the names offered, and only when exactly one fits (or one wins by
        /// <see cref="DisciplineCatalog.Preferred"/>): several mean the user chooses, as in "Link Manager".
        /// </summary>
        private void Suggest(InsertLinkRow row)
        {
            if (_matchBox?.IsChecked != true || row.Workset.Trim().Length > 0 || _offered.Count == 0)
                return;

            var codes = _linkPreferences.EffectiveDisciplines;
            var perDiscipline = DisciplineCatalog.HasDisciplineWorksets(_offered, codes);
            var code = DisciplineCatalog.Detect(row.Name, codes);

            if (code.Length == 0)
            {
                // Where the worksets are split by discipline, "no code in the name" is the answer to "why is it empty".
                row.Note = perDiscipline ? "No discipline visible in the model name" : string.Empty;
                return;
            }

            var names = _offered.ToList();
            var matches = DisciplineCatalog.MatchingWorksets(code, names);
            var chosen = DisciplineCatalog.Preferred(code, matches, names, codes, row.Name);

            _suggesting = true;
            try
            {
                if (chosen != null)
                {
                    row.Workset = chosen;
                    row.Note = "Workset by discipline \"" + code + "\"";
                    _autoWorkset.Add(row);
                }
                else if (matches.Count > 1)
                {
                    row.Note = "Discipline \"" + code + "\": matches " + string.Join(", ", matches.Take(3)) +
                               (matches.Count > 3 ? " and " + (matches.Count - 3) + " more" : string.Empty) + " — choose one";
                }
                else
                {
                    row.Note = perDiscipline ? "Discipline \"" + code + "\": no offered workset carries this code" : string.Empty;
                }
            }
            finally
            {
                _suggesting = false;
            }
        }

        private void ApplySuggestions()
        {
            foreach (var row in _rows.Values)
                Suggest(row);

            UpdateSummary();
        }

        /// <summary>Clears what the guess set; choices made by hand are left alone.</summary>
        private void ClearSuggestions()
        {
            _suggesting = true;
            try
            {
                foreach (var row in _rows.Values)
                {
                    if (_autoWorkset.Contains(row))
                        row.Workset = string.Empty;

                    if (IsGuessNote(row.Note))
                        row.Note = string.Empty;
                }
            }
            finally
            {
                _suggesting = false;
            }

            _autoWorkset.Clear();
            UpdateSummary();
        }

        /// <summary>A note <see cref="Suggest"/> wrote — the only kind this table's "State" holds.</summary>
        private static bool IsGuessNote(string note)
        {
            return note.StartsWith("Workset by discipline", StringComparison.Ordinal) ||
                   note.StartsWith("Discipline", StringComparison.Ordinal) ||
                   note.StartsWith("No discipline", StringComparison.Ordinal);
        }

        // ───────────────────────────── step 2: the worksets inside the links ─────────────────────────────

        private LinkWorksetMode WorksetMode => (LinkWorksetMode)Math.Max(0, _worksetModeBox.SelectedIndex);

        private string Pattern => _patternBox.Text?.Trim() ?? string.Empty;

        private bool PatternContains => _ruleBox.SelectedIndex == 1;

        /// <summary>Under "close all" a check mark means exactly the opposite — the caption says which.</summary>
        private void UpdateWorksetCaption()
        {
            _worksetCaption.Text = WorksetMode != LinkWorksetMode.CloseAll
                ? "The checked worksets will be closed in every link. A link with no workset of that name simply has nothing to close."
                : "Only the checked worksets will be open in every link, the rest will close.";

            UpdateSummary();
        }

        private void AddWorkset(string name, bool isRemembered)
        {
            var text = LinkPreferences.NormalizeWorkset(name);
            if (text.Length == 0)
                return;

            // A duplicate is judged by the rule the workset is later looked up in a link by.
            if (_worksets.Any(workset => LinkPreferences.SameWorkset(workset.Name, text)))
                return;

            var row = new WorksetRow(text, isRemembered);
            row.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(WorksetRow.IsSelected))
                    UpdateSummary();
            };
            _worksets.Add(row);
        }

        private void OnReadInside(object sender, RoutedEventArgs e)
        {
            var rows = _shown.ToList();
            if (rows.Count == 0)
            {
                MessageBox.Show(this, "Check the links on step 1 first.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var scan = Read(rows.Select(row => row.Entry).ToList());
            if (scan == null)
                return;

            foreach (var row in rows)
            {
                IReadOnlyList<string> names;
                if (scan.ByModel.TryGetValue(row.Key, out names))
                    row.InsideNames = names;
            }

            foreach (var name in scan.Names)
                AddWorkset(name, false);

            _insideRead = true;
            RecountInside();
            UpdateSummary();

            MessageBox.Show(this, "Links read: " + scan.Scanned + " of " + rows.Count + ".\nDistinct worksets: " + scan.Names.Count + "." +
                                  Failures(scan.Failures), WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>In how many checked links each name occurs — "in none of them" only once something was read.</summary>
        private void RecountInside()
        {
            var rows = _shown.ToList();
            var counted = rows.Any(row => row.InsideNames != null);

            foreach (var workset in _worksets)
            {
                workset.LinkCount = rows.Count(row => row.InsideNames != null &&
                                                      row.InsideNames.Any(name => LinkPreferences.SameWorkset(name, workset.Name)));
                workset.IsCounted = counted;
            }
        }

        private void OnMarkWorksets(object sender, RoutedEventArgs e)
        {
            var rule = new LinkWorksetRule(WorksetMode, null, Pattern, PatternContains);
            if (rule.Pattern.Length == 0)
            {
                MessageBox.Show(this, "Type in a rule string — \"00_\", say.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var marked = 0;
            foreach (var workset in _worksets.Where(workset => rule.Matches(workset.Name)))
            {
                workset.IsSelected = true;
                marked++;
            }

            if (marked == 0)
            {
                MessageBox.Show(this,
                    "No workset in the list matches the rule.\n\n" +
                    "That is not a problem: the rule is applied to each link separately when it is linked, " +
                    "even if the name list is empty right now.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ToggleSelectedWorksets()
        {
            var rows = _worksetGrid.SelectedItems.OfType<WorksetRow>().ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);
            foreach (var row in rows)
                row.IsSelected = value;
        }

        private List<string> MarkedWorksets()
        {
            return _worksets.Where(workset => workset.IsSelected).Select(workset => workset.Name).ToList();
        }

        private string InsideNote()
        {
            var names = MarkedWorksets().Count;

            if (names == 0 && Pattern.Length == 0)
                return WorksetMode == LinkWorksetMode.CloseAll
                    ? "Every workset inside the links will be closed."
                    : "The worksets inside the links are left alone.";

            var note = (WorksetMode == LinkWorksetMode.CloseAll ? "Worksets inside to open: " : "Worksets inside to close: ") + names;

            return Pattern.Length > 0 ? note + " plus whatever matches \"" + Pattern + "\"." : note + ".";
        }

        // ───────────────────────────── step 3: the models ─────────────────────────────

        private void OnReadModels(object sender, RoutedEventArgs e)
        {
            var models = _models.Marked();
            if (models.Count == 0)
            {
                MessageBox.Show(this, "Check the models on step 3 first.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var scan = ReadModels(models);
            if (scan == null)
                return;

            MessageBox.Show(this, "Models read: " + scan.Scanned + " of " + models.Count + ".\nDistinct worksets: " + scan.Names.Count + "." +
                                  Failures(scan.Failures), WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Reads the models' worksets, keeps them per model, offers the names and redoes everything that
        /// depends on them: "In the models", the guess, the caption. Null when the read itself failed.
        /// </summary>
        private LinkWorksetScan ReadModels(IReadOnlyList<BatchModelRow> models)
        {
            var scan = Read(models.Select(row => row.Entry).ToList());
            if (scan == null)
                return null;

            foreach (var model in models)
            {
                IReadOnlyList<string> names;
                if (scan.ByModel.TryGetValue(model.Key, out names))
                {
                    _modelWorksets[model.Key] = names;
                    _modelUnread.Remove(model.Key);
                }
                else
                {
                    _modelUnread[model.Key] = scan.Failures.FirstOrDefault(line => line.StartsWith(model.Name + " — ", StringComparison.Ordinal)) ??
                                              model.Name + " — not read";
                }
            }

            Offer(scan.Names);
            ApplySuggestions();
            RecountModels();
            UpdateModelCaption();

            return scan;
        }

        /// <summary>"In the models" on every row, against the models checked now.</summary>
        private void RecountModels()
        {
            var models = _models.Marked();

            foreach (var row in _rows.Values)
                Recount(row, models);

            UpdateModelCaption();
        }

        private void Recount(InsertLinkRow row, IReadOnlyList<BatchModelRow> models)
        {
            var workset = row.Workset.Trim();
            var read = models.Where(model => _modelWorksets.ContainsKey(model.Key)).ToList();

            if (workset.Length == 0 || models.Count == 0)
            {
                row.InModels = string.Empty;
                row.IsMissing = false;
                return;
            }

            if (read.Count == 0)
            {
                row.InModels = "not read";
                row.IsMissing = false;
                return;
            }

            var missing = read.Count(model => !Has(model, workset));
            var partly = read.Count < models.Count ? " read (of " + models.Count + ")" : string.Empty;

            row.InModels = missing == 0
                ? "in all " + read.Count + partly
                : "missing in " + missing + " of " + read.Count + partly;
            row.IsMissing = missing > 0;
        }

        private bool Has(BatchModelRow model, string workset)
        {
            IReadOnlyList<string> names;
            return _modelWorksets.TryGetValue(model.Key, out names) && names.Any(name => LinkPreferences.SameWorkset(name, workset));
        }

        private void UpdateModelCaption()
        {
            if (_modelCaption == null)
                return;

            var models = _models.Marked();
            var read = models.Count(model => _modelWorksets.ContainsKey(model.Key));
            var unread = models.Where(model => _modelUnread.ContainsKey(model.Key)).ToList();

            if (models.Count == 0)
            {
                _modelCaption.Text = string.Empty;
                return;
            }

            _modelCaption.Text = read == 0 && unread.Count == 0
                ? "The worksets of the checked models are not read yet — \"Insert\" reads them before it asks."
                : "Worksets read: " + read + " of " + models.Count + " checked models." +
                  (unread.Count > 0
                      ? " Could not be read: " + string.Join("; ", unread.Take(3).Select(model => _modelUnread[model.Key])) +
                        (unread.Count > 3 ? " and " + (unread.Count - 3) + " more" : string.Empty)
                      : string.Empty);
        }

        /// <summary>The call-back under a wait cursor; a failure of the read itself is a message, not the end of the window.</summary>
        private LinkWorksetScan Read(IReadOnlyList<LinkEntry> entries)
        {
            if (_readWorksets == null)
                return null;

            var cursor = Mouse.OverrideCursor;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                return _readWorksets(entries);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not read the worksets.\n\n" + exception.Message, WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }
        }

        private static string Failures(IReadOnlyList<string> failures)
        {
            const int limit = 10;

            if (failures.Count == 0)
                return string.Empty;

            return "\n\nCould not be read (" + failures.Count + "):\n• " + string.Join("\n• ", failures.Take(limit)) +
                   (failures.Count > limit ? "\n… and " + (failures.Count - limit) + " more" : string.Empty);
        }

        // ───────────────────────────── the summary ─────────────────────────────

        private void UpdateSummary()
        {
            if (_insertButton == null || _tabs == null)
                return;

            var links = _shown.Count;
            var unnamed = _shown.Count(row => row.Workset.Trim().Length == 0);
            var models = _models.Marked().Count;
            var ready = links > 0 && unnamed == 0 && models > 0;

            _linksTab.Header = "1. Links" + (links > 0 ? " (" + links + ")" : string.Empty);
            _worksetsTab.Header = "2. Worksets" + (unnamed > 0 ? " (" + unnamed + " to choose)" : string.Empty);
            _modelsTab.Header = "3. Models" + (models > 0 ? " (" + models + ")" : string.Empty);

            if (links == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Step 1: check the links to insert.";
            }
            else if (unnamed > 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Step 2: choose the project workset for " + unnamed + " of " + links + " link(s).";
            }
            else if (models == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Step 3: check the models the links go into.";
            }
            else
            {
                _status.Foreground = Brushes.DarkGreen;
                _status.Text = "Will insert " + links + " link(s) into " + models + " model(s). " + InsideNote();
            }

            _insertButton.IsEnabled = ready;
            _insertButton.Content = ready ? "Insert links (" + links + " → " + models + ")…" : "Insert links…";

            _backButton.IsEnabled = _tabs.SelectedIndex > 0;
            _nextButton.IsEnabled = _tabs.SelectedIndex < _tabs.Items.Count - 1;
            _readInsideButton.Content = _insideRead ? "Read worksets again" : "Read Worksets";
        }

        // ───────────────────────────── insert ─────────────────────────────

        private void OnInsert(object sender, RoutedEventArgs e)
        {
            var links = _shown.ToList();
            if (links.Count == 0)
            {
                _tabs.SelectedItem = _linksTab;
                return;
            }

            var unnamed = links.Where(row => row.Workset.Trim().Length == 0).ToList();
            if (unnamed.Count > 0)
            {
                _tabs.SelectedItem = _worksetsTab;
                MessageBox.Show(this, "Choose the project workset for:\n• " + string.Join("\n• ", unnamed.Select(row => row.Name)),
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Two links of one file name: Revit names a link type after its file, and the run would leave
            // the second out as "a link of this name is in the model already" — better said now.
            var twins = links
                .GroupBy(row => ServerModelIndex.Bare(row.Name), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .ToList();

            if (twins.Count > 0)
            {
                _tabs.SelectedItem = _linksTab;
                MessageBox.Show(this,
                    "Two checked links have the same name — keep one of each:\n• " +
                    string.Join("\n• ", twins.SelectMany(group => group.Select(row => row.Entry.Path))),
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var models = _models.Marked();
            if (models.Count == 0)
            {
                _tabs.SelectedItem = _modelsTab;
                return;
            }

            // What every model lacks is said before the run, not found out hours into it.
            var unread = models.Where(model => !_modelWorksets.ContainsKey(model.Key) && !_modelUnread.ContainsKey(model.Key)).ToList();
            if (unread.Count > 0)
                ReadModels(unread);

            foreach (var row in links)
                row.Workset = LinkPreferences.NormalizeWorkset(row.Workset);

            if (MessageBox.Show(this, Confirmation(links, models), WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            Capture();

            Links = links.Select(row => row.Entry).ToList();
            Models = models.Select(row => row.Entry).ToList();
            DialogResult = true;
        }

        private string Confirmation(IReadOnlyList<InsertLinkRow> links, IReadOnlyList<BatchModelRow> models)
        {
            const int limit = 12;

            var text = "Insert " + links.Count + " link(s) into " + models.Count + " model(s) on Revit Server?\n\n" +
                       string.Join("\n", links.Take(limit).Select(row => "• " + row.Name + " → " + row.Workset)) +
                       (links.Count > limit ? "\n… and " + (links.Count - limit) + " more" : string.Empty) + "\n\n" +
                       "Placement: " + _placementBox.SelectedItem + ", " + ((string)_attachmentBox.SelectedItem ?? "Overlay").ToLowerInvariant() +
                       ", pinned.\n" + InsideNote() + "\n";

            var read = models.Where(model => _modelWorksets.ContainsKey(model.Key)).ToList();
            var gaps = new List<string>();

            foreach (var workset in links.Select(row => row.Workset).Distinct(StringComparer.CurrentCultureIgnoreCase))
            {
                var lacking = read.Where(model => !Has(model, workset)).ToList();
                if (lacking.Count == 0)
                    continue;

                gaps.Add("\"" + workset + "\" — not in " + lacking.Count + " of " + read.Count + ": " +
                         string.Join(", ", lacking.Take(3).Select(model => model.Name)) +
                         (lacking.Count > 3 ? " and " + (lacking.Count - 3) + " more" : string.Empty));
            }

            if (gaps.Count > 0)
            {
                text += "\nSome models lack a link's workset — " +
                        (_createBox.IsChecked == true ? "it will be made there:" : "those links will be left out there:") +
                        "\n• " + string.Join("\n• ", gaps.Take(limit)) + "\n";
            }

            var unread = models.Count - read.Count;
            if (unread > 0)
                text += "\nThe worksets of " + unread + " model(s) could not be read; whatever they lack is found out in the run.\n";

            text += "\nEach model is opened as a local copy, the links are inserted into their worksets and checked, and the " +
                    "model is synchronised with central with everything handed back. A link a model has already is left " +
                    "as it is. Revit is busy until the whole list is done; the full report is written as it goes into\n" +
                    InsertLinksPreferences.ReportsFolder;

            return text;
        }

        /// <summary>The window's state into the settings — on "Insert" and on every close.</summary>
        private void Capture()
        {
            _preferences.Placement = (LinkPlacement)Math.Max(0, _placementBox.SelectedIndex);
            _preferences.IsAttachment = _attachmentBox.SelectedIndex == 1;
            _preferences.WorksetMode = WorksetMode;
            _preferences.WorksetPattern = Pattern;
            _preferences.WorksetPatternContains = PatternContains;
            _preferences.MatchProjectWorkset = _matchBox.IsChecked == true;
            _preferences.CreateWorksets = _createBox.IsChecked == true;

            _preferences.Worksets.Clear();
            _preferences.Worksets.AddRange(MarkedWorksets());

            _preferences.LinkSet = _links.SetName;
            _preferences.ModelSet = _models.SetName;

            // Both lists as they stand, check marks aside: a run that broke off is started again over the same lists.
            _preferences.Links.Clear();
            _preferences.Links.AddRange(_links.Rows.Select(row => row.Entry));

            _preferences.Models.Clear();
            _preferences.Models.AddRange(_models.Rows.Select(row => row.Entry));
        }

        protected override void OnClosed(EventArgs e)
        {
            Capture();
            _preferences.Save();

            // The server names typed in here belong to the whole add-in, not to this window.
            _linkPreferences.Save();

            base.OnClosed(e);
        }
    }
}
