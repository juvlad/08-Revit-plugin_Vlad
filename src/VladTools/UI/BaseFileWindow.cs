using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// The "Base File" button's window: one coordination model and a list of what to do with it
    /// when loading it into a discipline model.
    ///
    /// The work the window gathers into one click looks like this in Revit: link the coordination
    /// file, acquire shared coordinates from it, rename the site, pin the link, switch to
    /// "00_Shared levels and grids", and only then start copying the levels and grids. Five
    /// dialogs scattered across the ribbon, and the order between them matters.
    ///
    /// The window offers the coordination file itself: every building's base files live in one
    /// folder next to the discipline folders, and the open model's name carries the building
    /// number (<c>MK3-VSC-B01-VOIDS</c>) — so the file needed is called <c>MK3-VSC-B01-BM</c> and
    /// there is no need to hunt for it in a tree (<see cref="BaseFileFinder"/>). What is guessed
    /// is only offered: parsing names is a heuristic, so the caption under the model name says
    /// where it came from, and when several models match, none is filled in.
    ///
    /// The window does not promise the last step — copy-monitoring: the Revit API cannot create
    /// monitoring links at all (only reading existing ones is possible). All that can honestly be
    /// done is to open "Copy/Monitor" itself, and that is the last check box in the list.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class BaseFileWindow : Window
    {
        private const string WindowTitle = "Base File";

        private readonly IReadOnlyList<LinkRow> _existing;
        private readonly BaseFilePreferences _preferences;

        /// <summary>The open model: the building comes from its name, the base file search from its folder.</summary>
        private readonly HostModel _host;

        /// <summary>The "Link Manager" settings — for the shared Revit Server list.</summary>
        private readonly LinkPreferences _linkPreferences;

        /// <summary>The open project's worksets — with "(active)" as the first entry.</summary>
        private readonly List<string> _hostWorksets = new List<string> { LinkRow.ActiveWorkset };

        private readonly TextBlock _modelName;
        private readonly TextBlock _modelPath;
        private readonly ComboBox _placementBox;
        private readonly ComboBox _linkWorksetBox;
        private readonly CheckBox _acquireBox;
        private readonly CheckBox _renameBox;
        private readonly TextBox _siteBox;
        private readonly CheckBox _pinBox;
        private readonly CheckBox _activateBox;
        private readonly ComboBox _worksetBox;
        private readonly CheckBox _monitorBox;
        private readonly CheckBox _autoBox;
        private readonly TextBlock _status;
        private readonly Button _runButton;

        private LinkRow _row;

        /// <summary>The coordination model the command should work with.</summary>
        public LinkRow Selected { get; private set; }

        /// <summary>Exactly what to do with it.</summary>
        public BaseFilePreferences Preferences => _preferences;

        /// <param name="existing">The links already in the project: the chosen model is looked for among them.</param>
        /// <param name="hostWorksets">
        /// The open project's worksets. An empty list means the project is not workshared,
        /// and both workset-related steps are disabled.
        /// </param>
        /// <param name="host">The open model — the base file is guessed from it.</param>
        public BaseFileWindow(IReadOnlyList<LinkRow> existing, IReadOnlyList<string> hostWorksets, HostModel host)
        {
            _existing = existing ?? new List<LinkRow>();
            _host = host ?? new HostModel(null, null, string.Empty);
            _preferences = BaseFilePreferences.Load();
            _linkPreferences = LinkPreferences.Load();

            if (hostWorksets != null)
                _hostWorksets.AddRange(hostWorksets);

            Title = WindowTitle;
            Width = 720;
            Height = 620;
            MinWidth = 560;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _modelName = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap
            };

            _modelPath = new TextBlock
            {
                Foreground = SystemColors.GrayTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };

            _placementBox = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center };
            _placementBox.Items.Add("Origin to origin");
            _placementBox.Items.Add("By shared coordinates");
            _placementBox.Items.Add("Centre to centre");
            _placementBox.Items.Add("By project site location");
            _placementBox.SelectedIndex = PlacementIndex(_preferences.Placement);
            _placementBox.ToolTip =
                "\"Origin to origin\" is the usual choice for a coordination file: the shared coordinates " +
                "have not yet been acquired from it, so there is nothing to place by yet.";

            // The list is editable for the same reason as the workset to switch to: "01_Link_BM"
            // has not been created yet in a new discipline, there is nothing to pick from a list,
            // and the command will create such a workset.
            _linkWorksetBox = new ComboBox
            {
                Width = 240,
                IsEditable = true,
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = _hostWorksets,
                IsEnabled = HasHostWorksets,
                ToolTip = "The project workset the link itself will go into. " +
                          "If no workset with this name exists, the command will create it."
            };

            _acquireBox = Option("Acquire shared coordinates from the base file",
                "Revit: \"Coordinates → Acquire Coordinates\". The project's shared coordinate system " +
                "will become the same as the coordination file's.",
                _preferences.Acquire);

            _renameBox = Option("Rename the project site to:",
                "Revit: \"Manage Place and Locations → Site\". The site name is what shows up in the " +
                "\"Project Location\" dialog and in the consultants' site lists later.",
                _preferences.Rename);

            _siteBox = new TextBox
            {
                Width = 260,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2),
                Text = _preferences.Site
            };

            _pinBox = Option("Pin the link",
                "The base file is inserted by coordinates, and an accidental drag with the mouse is later hunted down by the whole team.",
                _preferences.Pin);

            _activateBox = Option("Switch to the workset:",
                "The project's active workset — the one the copied levels and grids will land in. " +
                "If no workset with this name exists, the command will create it.",
                _preferences.Activate);
            _activateBox.IsEnabled = HasHostWorksets;

            _worksetBox = new ComboBox
            {
                Width = 260,
                IsEditable = true,
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = _hostWorksets.Skip(1).ToList(),
                Text = _preferences.Workset,
                IsEnabled = HasHostWorksets
            };

            _monitorBox = Option("Open \"Copy/Monitor → Select Link\"",
                "The Revit API cannot create monitoring links — only Revit itself can. " +
                "The command gets you to that mode: all that is left is to pick the link and check off the levels and grids.",
                _preferences.Monitor);

            _autoBox = new CheckBox
            {
                Content = "guess it automatically",
                IsChecked = _preferences.AutoPick,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 4),
                ToolTip =
                    "Look for your building's base file when the window opens: by the building number\n" +
                    "in the open model's name, in the \"" + _preferences.BaseFolder + "\" folder next to it.\n" +
                    "Every search reads store folders, so this is a check box, not always-on."
            };

            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

            _runButton = new Button
            {
                Content = "Run",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true,
                IsEnabled = false
            };
            _runButton.Click += OnRun;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(closeButton);

            if (!HasHostWorksets)
                _activateBox.IsChecked = false;

            // Text on the editable ComboBox is set after layout: before the template is applied
            // it gets lost if the list has no entry matching that value.
            _worksetBox.Text = _preferences.Workset;
            _linkWorksetBox.Text = Named(_preferences.LinkWorkset);

            Show(_preferences.Model == null ? null : Match(_preferences.Model));

            // The guess runs on its own when the window opens: asking "search now?" when both the
            // building and the folder are already known from the open model would mean an extra
            // click for exactly what the window was opened to do. Not while building the layout,
            // but on Loaded — reading store folders takes a second, and the window should already
            // be on screen by then.
            Loaded += (sender, args) => AutoPick();
        }

        private bool HasHostWorksets => _hostWorksets.Count > 1;

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // model source
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // the model itself
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // steps
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "Choose the coordination file and check what to do with it. " +
                       "Everything checked runs as one operation and rolls back with a single Ctrl+Z.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var sources = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            sources.Children.Add(SourceButton("Guess",
                "Find your building's base file automatically: by the building number in the open model's " +
                "name, in the base file folder next to it.", OnAutoPick));
            sources.Children.Add(SourceButton("File…", "An ordinary .rvt file: a disk or a network folder.", OnPickFile));
            sources.Children.Add(SourceButton("Revit Server…", "Browse folders and models on Revit Server.", OnBrowseServer));
            sources.Children.Add(SourceButton("BIM360…", "Browse BIM360/ACC accounts, projects and folders.", OnBrowseCloud));
            sources.Children.Add(SourceButton("BIM360 by GUID…", "Enter a cloud model as a pair of GUIDs — when browsing is unavailable.", OnPickCloudByGuid));
            sources.Children.Add(_autoBox);

            Grid.SetRow(sources, 1);
            root.Children.Add(sources);

            var model = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 10),
                Background = SystemColors.ControlBrush
            };
            var inner = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
            inner.Children.Add(_modelName);
            inner.Children.Add(_modelPath);
            model.Children.Add(inner);

            Grid.SetRow(model, 2);
            root.Children.Add(model);

            var steps = new StackPanel();

            steps.Children.Add(new TextBlock
            {
                Text = "What to do:",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            steps.Children.Add(Row(
                new TextBlock
                {
                    Text = "Link to the project, placement:",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(22, 0, 8, 0)
                },
                _placementBox));

            steps.Children.Add(Row(
                new TextBlock
                {
                    Text = "put the link into the workset:",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(22, 0, 8, 0),
                    Foreground = HasHostWorksets ? SystemColors.ControlTextBrush : SystemColors.GrayTextBrush
                },
                _linkWorksetBox));

            steps.Children.Add(_acquireBox);
            steps.Children.Add(Row(_renameBox, _siteBox));
            steps.Children.Add(_pinBox);
            steps.Children.Add(Row(_activateBox, _worksetBox));
            steps.Children.Add(_monitorBox);

            if (!HasHostWorksets)
            {
                steps.Children.Add(new TextBlock
                {
                    Text = "The project is not workshared — it has no worksets, and both workset steps are disabled.",
                    Foreground = SystemColors.GrayTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(22, 6, 0, 0)
                });
            }

            Grid.SetRow(steps, 3);
            root.Children.Add(steps);

            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_runButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 4);
            root.Children.Add(bottom);

            return root;
        }

        private static CheckBox Option(string text, string tooltip, bool isChecked)
        {
            return new CheckBox
            {
                Content = text,
                IsChecked = isChecked,
                ToolTip = tooltip,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(22, 5, 8, 5)
            };
        }

        private static UIElement Row(UIElement left, UIElement right)
        {
            var panel = new WrapPanel();
            panel.Children.Add(left);
            panel.Children.Add(right);

            return panel;
        }

        private static Button SourceButton(string text, string tooltip, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tooltip
            };
            button.Click += handler;

            return button;
        }

        // ───────────────────────────── where the model comes from ─────────────────────────────

        private void OnAutoPick(object sender, RoutedEventArgs e)
        {
            Pick(true);
        }

        private void OnPickFile(object sender, RoutedEventArgs e)
        {
            Take(ModelPicker.Files(this, false));
        }

        private void OnBrowseServer(object sender, RoutedEventArgs e)
        {
            Take(ModelPicker.Server(this, WindowTitle, _linkPreferences, Keys));
        }

        private void OnBrowseCloud(object sender, RoutedEventArgs e)
        {
            Take(ModelPicker.Cloud(this, WindowTitle, Keys));
        }

        private void OnPickCloudByGuid(object sender, RoutedEventArgs e)
        {
            Take(ModelPicker.CloudByGuid(this, Region()));
        }

        /// <summary>
        /// There is only one model here, but the sources return a list: several can be checked in
        /// the tree. We take the first one and say so — silently dropping the rest would be a lie.
        /// </summary>
        private void Take(IReadOnlyList<LinkEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return;

            Show(Match(entries[0]));

            if (entries.Count > 1)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "There is only one base file: the first checked model was taken.";
            }
        }

        /// <summary>
        /// A link to this model may already be in the project — then we work with it rather than
        /// setting up a second one: a repeated <c>Create</c> would just answer "such a link
        /// already exists" anyway.
        /// </summary>
        private LinkRow Match(LinkEntry entry)
        {
            var known = _existing.FirstOrDefault(row => string.Equals(row.Key, entry.Key, StringComparison.Ordinal));

            return known ?? new LinkRow(entry);
        }

        private void Show(LinkRow row)
        {
            _row = row;

            if (row == null)
            {
                _modelName.Text = "No model chosen";
                _modelPath.Text = "Choose the coordination file with the buttons above.";
                _runButton.IsEnabled = false;
                return;
            }

            _modelName.Text = row.Name;
            _modelPath.Text = row.Kind + " · " + row.Location;
            _runButton.IsEnabled = true;

            // For a link already in the project we show its current workset — the user has to see
            // what is being changed. For a new one the workset comes from the settings and is
            // filled in even before the model is chosen: "01_Link_BM" is the same across every discipline.
            if (row.IsExisting)
                _linkWorksetBox.Text = Named(row.Entry.Workset);

            _status.Foreground = SystemColors.GrayTextBrush;
            _status.Text = row.IsExisting
                ? "A link to this model is already in the project — it will be used."
                : string.Empty;
        }

        /// <summary>
        /// The workset name to show in the field: empty means "active", and an empty entry in the
        /// drop-down would look like an oversight.
        /// </summary>
        private static string Named(string workset)
        {
            return string.IsNullOrEmpty(workset) ? LinkRow.ActiveWorkset : workset;
        }

        /// <summary>Keys of the models to show greyed out in the tree: the chosen one is already taken.</summary>
        private HashSet<string> Keys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (_row != null)
                keys.Add(_row.Key);

            return keys;
        }

        /// <summary>The region the GUID-entry window opens with: the same as the previous model's.</summary>
        private string Region()
        {
            if (_row != null && _row.Entry.Origin == LinkOrigin.Cloud && _row.Entry.Region.Length > 0)
                return _row.Entry.Region;

            var remembered = _preferences.Model;

            return remembered != null && remembered.Origin == LinkOrigin.Cloud && remembered.Region.Length > 0
                ? remembered.Region
                : "US";
        }

        // ───────────────────────────── guessing the base file ─────────────────────────────

        /// <summary>
        /// The guess made when the window opens. Unlike the button, it stays quiet about failures:
        /// the window has just opened, and a modal dialog on top of it out of nowhere is the worst
        /// way to greet the user.
        /// </summary>
        private void AutoPick()
        {
            if (_autoBox.IsChecked != true)
                return;

            var building = ModelKit.Building(_host.Name, _linkPreferences.BuildingToken);

            // The remembered model is already from this building — nothing to search for: in a
            // new discipline of the same building the base file is the same one, and walking the
            // folders costs network requests.
            if (building.Length > 0 && _row != null && DisciplineCatalog.HasToken(_row.Name, building))
            {
                Note("The base file from last time — the same building (" + building + ").", false);
                return;
            }

            Pick(false);
        }

        /// <param name="loud">
        /// Whether to show a failure as a dialog and a red line. From the button — yes: the user
        /// pressed it and is waiting for an answer. When the window opens — no: it is a hint, not a breakage.
        /// </param>
        private void Pick(bool loud)
        {
            if (_host.Folder == null || _host.Name.Length == 0)
            {
                Complain(loud,
                    "Could not work out where the open model lives: the project has never been saved " +
                    "or is open as detached. Choose the base file with the buttons above.");
                return;
            }

            BaseFileScan scan;
            var cursor = Mouse.OverrideCursor;

            try
            {
                // Reading happens right on the UI thread, as in the browser tree:
                // the window is modal, Revit waits regardless.
                Mouse.OverrideCursor = Cursors.Wait;

                scan = BaseFileFinder.Find(
                    _host,
                    _preferences.BaseFolder,
                    _preferences.BaseCode,
                    _linkPreferences.BuildingToken);
            }
            catch (Exception exception)
            {
                Complain(loud, "Could not guess the base file: " + LinkCatalog.Short(exception.Message));
                return;
            }
            finally
            {
                Mouse.OverrideCursor = cursor;
            }

            Apply(scan, loud);
        }

        /// <summary>
        /// What to do with the result of the guess. There are five outcomes, and each has to sound
        /// different: "no building in the name", "no folder found", "the folder has no model of
        /// this building", "several match" and "here it is". One shared "could not guess" for all
        /// of this will not do: the reasons differ, and so does the fix.
        /// </summary>
        private void Apply(BaseFileScan scan, bool loud)
        {
            var tail = scan.Failures.Count > 0
                ? " Could not be read: " + string.Join("; ", scan.Failures.Take(2)) + "."
                : string.Empty;

            if (scan.Building.Length == 0)
            {
                Complain(loud,
                    "There is no building number at position " + _linkPreferences.BuildingToken + " in the name \"" +
                    _host.Name + "\" — nothing to guess from. The position is set in links\\_settings.txt (KIT_TOKEN)." + tail);
                return;
            }

            if (scan.Folder == null)
            {
                Complain(loud,
                    "The folder \"" + _preferences.BaseFolder + "\" was not found next to the open model. " +
                    "The folder name is set in basefile\\_settings.txt (BASE_FOLDER)." + tail);
                return;
            }

            if (scan.Hits.Count == 0)
            {
                Complain(loud, "The folder \"" + scan.Folder.Name + "\" has no model of building " + scan.Building + "." + tail);
                return;
            }

            if (scan.Hits.Count > 1)
            {
                // Several matches is not a reason to take the first one: the same rule as when
                // guessing a workset or a building kit. But saying what to choose from is
                // mandatory — without the names, "several match" says nothing.
                Complain(loud,
                    "Several models in the folder \"" + scan.Folder.Name + "\" match building " + scan.Building + ": " +
                    string.Join(", ", scan.Hits.Select(hit => hit.Name)) + ". Choose the right one with the buttons above." + tail);
                return;
            }

            Show(Match(scan.Hits[0]));

            Note("Guessed from the open model's name: building " + scan.Building +
                 ", folder \"" + scan.Folder.Name + "\"." +
                 (scan.IsLoose
                     ? " The code \"" + _preferences.BaseCode + "\" is missing from the name — only the building matched, check the model."
                     : string.Empty) + tail,
                 scan.IsLoose);
        }

        /// <summary>A guess failure: always as a note, as a dialog only when the button was pressed.</summary>
        private void Complain(bool loud, string text)
        {
            Note(text, loud);

            if (loud)
                MessageBox.Show(this, text, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// The caption under the chosen model. A note about a link already in the project is
        /// always appended: it matters more than any guess hint and must not get lost behind it.
        /// </summary>
        private void Note(string text, bool isProblem)
        {
            _status.Foreground = isProblem ? Brushes.Firebrick : SystemColors.GrayTextBrush;
            _status.Text = _row != null && _row.IsExisting
                ? text + " A link to this model is already in the project — it will be used."
                : text;
        }

        // ───────────────────────────── running ─────────────────────────────

        private void OnRun(object sender, RoutedEventArgs e)
        {
            if (_row == null)
                return;

            var site = _siteBox.Text.Trim();
            if (_renameBox.IsChecked == true && site.Length == 0)
            {
                MessageBox.Show(this,
                    "Type in a site name or clear the \"Rename the project site\" box.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                _siteBox.Focus();
                return;
            }

            var workset = (_worksetBox.Text ?? string.Empty).Trim();
            if (_activateBox.IsChecked == true && workset.Length == 0)
            {
                MessageBox.Show(this,
                    "Type in a workset name or clear the \"Switch to the workset\" box.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                _worksetBox.Focus();
                return;
            }

            Collect();
            Selected = _row;
            DialogResult = true;
        }

        /// <summary>
        /// Carries the window's state into the settings. Called both from "Run" and on closing:
        /// this window's fields are the same kind of thing as the formulas and link sets — they
        /// have to survive Revit closing.
        /// </summary>
        private void Collect()
        {
            var linkWorkset = (_linkWorksetBox.Text ?? string.Empty).Trim();
            if (linkWorkset == LinkRow.ActiveWorkset)
                linkWorkset = string.Empty;

            if (_row != null)
            {
                // The project workset is stored on the entry itself — the command takes it from there.
                _row.Workset = linkWorkset;
                _preferences.Model = _row.Entry;
            }

            _preferences.LinkWorkset = linkWorkset;
            _preferences.Placement = PlacementFor(_placementBox.SelectedIndex);
            _preferences.Site = _siteBox.Text.Trim();
            _preferences.Workset = (_worksetBox.Text ?? string.Empty).Trim();
            _preferences.Acquire = _acquireBox.IsChecked == true;
            _preferences.Rename = _renameBox.IsChecked == true;
            _preferences.Pin = _pinBox.IsChecked == true;
            _preferences.Activate = _activateBox.IsChecked == true;
            _preferences.Monitor = _monitorBox.IsChecked == true;
            _preferences.AutoPick = _autoBox.IsChecked == true;
        }

        protected override void OnClosed(EventArgs e)
        {
            Collect();
            _preferences.Save();

            // A Revit Server name may have appeared while browsing — it is shared with "Link Manager".
            _linkPreferences.Save();

            base.OnClosed(e);
        }

        private static int PlacementIndex(LinkPlacement placement)
        {
            switch (placement)
            {
                case LinkPlacement.Shared:
                    return 1;
                case LinkPlacement.Centered:
                    return 2;
                case LinkPlacement.Site:
                    return 3;
                default:
                    return 0;
            }
        }

        private static LinkPlacement PlacementFor(int index)
        {
            switch (index)
            {
                case 1:
                    return LinkPlacement.Shared;
                case 2:
                    return LinkPlacement.Centered;
                case 3:
                    return LinkPlacement.Site;
                default:
                    return LinkPlacement.Origin;
            }
        }
    }
}
