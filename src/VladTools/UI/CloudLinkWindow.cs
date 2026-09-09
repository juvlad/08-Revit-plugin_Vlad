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
    /// Entering cloud models as a pair of GUIDs — the fallback path to BIM360 when browsing is
    /// unavailable: the user is not signed in to Autodesk, the service cannot be reached, or
    /// it is a different Revit version and the token cannot be obtained from it.
    ///
    /// There is one multi-line field: GUIDs come as a list anyway — from a saved set, from an
    /// email, from someone else's settings file — and typing them in one at a time would be
    /// torture. Parsing is deliberately loose: two GUIDs are looked for in a line, and the
    /// separators, the region and the name are recognised as best they can be. What did not
    /// parse is shown right under the field, not silently dropped.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class CloudLinkWindow : Window
    {
        private const string WindowTitle = "BIM360 Links by GUID";

        /// <summary>The Autodesk cloud regions Revit accepts in a model path.</summary>
        private static readonly string[] Regions = { "US", "EMEA", "AUS", "CAN", "DEU", "GBR", "IND", "JPN" };

        private static readonly Regex GuidPattern = new Regex(
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            RegexOptions.Compiled);

        private readonly ComboBox _regionBox;
        private readonly TextBox _textBox;
        private readonly TextBlock _status;
        private readonly Button _addButton;

        /// <summary>The parsed models.</summary>
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
                "The region the project lives in. Used when the line itself does not name one.\n" +
                "Get the region wrong and Revit will simply not find the model.";
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
                Content = "Add",
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

            Content = BuildLayout(cancelButton);

            Loaded += (s, e) => _textBox.Focus();

            UpdateSummary();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button cancelButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // region
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // field
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "One line per model: the project GUID and the model GUID, with anything at all between them. " +
                       "A region and a name can be added too:\n" +
                       "    EMEA | 5f7a3c2e-… | 9b1d4e6f-… | AR_Building 1.rvt\n" +
                       "Both are optional: no region — the one chosen below is used; no name — the table will show the GUID.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var regionPanel = new StackPanel { Orientation = Orientation.Horizontal };
            regionPanel.Children.Add(new TextBlock
            {
                Text = "Default region:",
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

        // ───────────────────────────── parsing ─────────────────────────────

        private string Region => (string)_regionBox.SelectedItem ?? "US";

        /// <summary>
        /// Parses a line: the first two GUIDs are the project and the model, a recognised word
        /// like EMEA is the region, and the longest of what remains is the name. The GUID order
        /// is exactly the one Autodesk writes them in and a set stores them in.
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
                _status.Text = "Paste in lines with GUIDs.";
            }
            else if (bad > 0)
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = "Models parsed: " + entries.Count +
                               ". Lines without a GUID pair: " + bad + " — they will be skipped.";
            }
            else
            {
                _status.Foreground = SystemColors.ControlTextBrush;
                _status.Text = "Models parsed: " + entries.Count + ".";
            }

            _addButton.IsEnabled = entries.Count > 0;
            _addButton.Content = entries.Count > 0 ? "Add (" + entries.Count + ")" : "Add";
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
