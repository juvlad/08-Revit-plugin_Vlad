using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// "Link Updater → Several models…": the Revit Server models whose links are to be pointed at the
    /// server — a whole server, folders, single models or a saved set.
    ///
    /// Nothing is shown per link before the run: what links a model has cannot be known until it is
    /// opened, and a batch exists so that nobody has to open each one. So the window is about the models
    /// only — the list is <see cref="ModelListPanel"/>, server-only — and the rule of what each link gets
    /// is the second pass of "Upload to Server": the one model of the link's name round the model's own
    /// folder, or nothing. Whatever is less certain is listed in the report for the window of "Link
    /// Updater", with the model open.
    ///
    /// The window knows nothing about the Revit API: it hands back the checked models.
    /// </summary>
    internal sealed class LinkUpdaterBatchWindow : Window
    {
        private const string WindowTitle = "Link Updater — several models";

        private readonly LinkUpdaterPreferences _preferences;
        private readonly LinkPreferences _linkPreferences;

        private readonly ModelListPanel _models;
        private readonly TextBlock _status;
        private readonly Button _runButton;

        /// <summary>The models the user ran the batch on, in the table's order — every one on Revit Server.</summary>
        public IReadOnlyList<BatchModelRow> Selected { get; private set; } = new List<BatchModelRow>();

        public LinkUpdaterBatchWindow(LinkUpdaterPreferences preferences, LinkPreferences linkPreferences)
        {
            _preferences = preferences ?? new LinkUpdaterPreferences();
            _linkPreferences = linkPreferences ?? new LinkPreferences();

            Title = WindowTitle;
            Width = 980;
            Height = 620;
            MinWidth = 720;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _models = new ModelListPanel(WindowTitle, _linkPreferences, _preferences.Set, false, false);

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _runButton = new Button
            {
                Content = "Update links…",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
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

            _models.Changed += (s, e) => UpdateSummary();
            _models.Notice += (s, text) =>
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = text;
            };

            _models.Add(_preferences.Models, "model(s) from last time");
            UpdateSummary();
        }

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // models
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "Each checked model on Revit Server is opened as a local copy, with only the worksets its links stand in. " +
                       "Every link is pointed at the one model of its name found round the model's own folder on the server — " +
                       "the way \"Link Updater\" searches — and the model is synchronised back with everything handed back. " +
                       "Several models of one name, a name that only resembles, or a link to a local file are left as they are " +
                       "and listed: finish those in \"Link Updater\" with the model open.\n" +
                       "Revit's warnings on the way (\"needs Coordination Review\" after a link is reloaded) are dismissed and " +
                       "listed in the report instead of stopping the run. Best run with no model open: a model open in Revit " +
                       "cannot be updated here, and a link to it cannot be repointed. Revit is busy until the whole list is done; " +
                       "the full report is written as it goes.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            Grid.SetRow(_models, 1);
            root.Children.Add(_models);

            var bottom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_runButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            return root;
        }

        /// <summary>The checked models the run can take — the panel keeps everything else out, so this is a guard, not a filter anyone sees.</summary>
        private List<BatchModelRow> Runnable()
        {
            return _models.Marked().Where(row => row.Entry.Origin == LinkOrigin.Server).ToList();
        }

        private void UpdateSummary()
        {
            var total = _models.Rows.Count;
            var marked = Runnable().Count;

            if (total == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Add the models — the whole server, a folder with \"Folder…\", single models, or a saved set.";
            }
            else if (marked == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "In the list: " + total + ". Nothing is checked.";
            }
            else
            {
                _status.Foreground = Brushes.DarkGreen;
                _status.Text = "Will be updated: " + marked + " of " + total + ".";
            }

            _runButton.IsEnabled = marked > 0;
            _runButton.Content = marked > 0 ? "Update links (" + marked + ")…" : "Update links…";
        }

        private void OnRun(object sender, RoutedEventArgs e)
        {
            var marked = Runnable();
            if (marked.Count == 0)
                return;

            var text = "Update the links in " + marked.Count + " model(s) on Revit Server?\n\n" +
                       "Each model is opened as a local copy, its links are reloaded from the server models of the same " +
                       "name, and it is synchronised with central. A model where nothing was repointed is not synchronised. " +
                       "Anyone working in these models keeps working; what they hold checked out stays theirs, and a link " +
                       "held by somebody is left and named.\n\n" +
                       "Revit is busy until the whole list is done — about as long as opening each model twice. " +
                       "The full report is written as it goes into\n" + LinkUpdaterPreferences.ReportsFolder;

            if (MessageBox.Show(this, text, WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) !=
                MessageBoxResult.Yes)
                return;

            Selected = marked;
            DialogResult = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            _preferences.Set = _models.SetName;

            // The list is remembered as it stands, check marks aside: a run that broke off is started
            // again over the same list.
            _preferences.Models.Clear();
            _preferences.Models.AddRange(_models.Rows.Select(row => row.Entry));

            _preferences.Save();

            // The server names typed in here belong to the whole add-in, not to this window.
            _linkPreferences.Save();

            base.OnClosed(e);
        }
    }
}
