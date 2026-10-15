using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// "Sheets → Several models…": the models whose sheet lists are to be written out, and where to.
    ///
    /// Nothing is shown before the run: what sheets a model has cannot be known until it is opened,
    /// and opening a dozen models to fill a table that would then be written out anyway would cost the
    /// run twice over. So the window is about the models only — the list is <see cref="ModelListPanel"/>,
    /// the same as in "Accept Changes → Other models…" — and the answer is the reports: one per model,
    /// exactly what "Export to Excel…" writes for an open one, and a summary of them all.
    ///
    /// Nothing in any model is changed, so there is no confirmation beyond choosing where the summary
    /// goes; the models' own reports go into a folder named after it, next to it.
    ///
    /// The window knows nothing about the Revit API: it hands back the checked models and a path.
    /// </summary>
    internal sealed class SheetsBatchWindow : Window
    {
        private const string WindowTitle = "Sheets — several models";

        private readonly SheetPreferences _preferences;
        private readonly LinkPreferences _linkPreferences;

        private readonly ModelListPanel _models;
        private readonly TextBlock _status;
        private readonly Button _runButton;

        /// <summary>The models the user ran the batch on, in the table's order.</summary>
        public IReadOnlyList<BatchModelRow> Selected { get; private set; } = new List<BatchModelRow>();

        /// <summary>Where the summary goes; the models' own reports go into <see cref="SheetReport.ReportsFolder"/> of it.</summary>
        public string FilePath { get; private set; } = string.Empty;

        public SheetsBatchWindow(SheetPreferences preferences, LinkPreferences linkPreferences)
        {
            _preferences = preferences ?? new SheetPreferences();
            _linkPreferences = linkPreferences ?? new LinkPreferences();

            Title = WindowTitle;
            Width = 980;
            Height = 600;
            MinWidth = 720;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _models = new ModelListPanel(WindowTitle, _linkPreferences, _preferences.Set);

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _runButton = new Button
            {
                Content = "Write reports…",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false,
                ToolTip = "Asks where to save the summary, then reads every checked model in turn.\n" +
                          "The models' own reports go into a folder of the same name next to the summary."
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
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // the models
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text = "Each checked model is read in turn, and its sheets are written into a report of its own — the same " +
                       "table \"Export to Excel…\" writes for an open model — plus one summary of every model: what each " +
                       "holds, every sheet of all of them in one filterable list, sheets with nothing placed, and sheet " +
                       "numbers repeated across models. Nothing in any model is changed.\n" +
                       "A model is opened detached from its central file, with every workset closed: nothing is locked, " +
                       "and only sheets and views are loaded, not the geometry. A model already open in Revit is read as it " +
                       "stands. Revit is busy until the whole batch is done.",
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

        private void UpdateSummary()
        {
            var total = _models.Rows.Count;
            var marked = _models.Marked().Count;

            if (total == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "Add the models to report on — a whole folder with \"Folder…\", or load a saved set.";
            }
            else if (marked == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = "In the list: " + total + ". Nothing is checked.";
            }
            else
            {
                _status.Foreground = Brushes.DarkGreen;
                _status.Text = "Will be read: " + marked + " of " + total + ".";
            }

            _runButton.IsEnabled = marked > 0;
            _runButton.Content = marked > 0 ? "Write reports (" + marked + ")…" : "Write reports…";
        }

        private void OnRun(object sender, RoutedEventArgs e)
        {
            var marked = _models.Marked();
            if (marked.Count == 0)
                return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the summary — the models' own reports go into a folder of the same name next to it",
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "Sheets summary " + DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".xlsx"
            };

            var folder = _preferences.ExistingBatchFolder();
            if (folder != null)
                dialog.InitialDirectory = folder;

            if (dialog.ShowDialog(this) != true)
                return;

            var reports = SheetReport.ReportsFolder(dialog.FileName);
            if (File.Exists(reports))
            {
                MessageBox.Show(this,
                    "The models' reports go into a folder named after the summary, and a file of that name is already there:\n\n" +
                    reports + "\n\nChoose another name for the summary.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Selected = marked;
            FilePath = dialog.FileName;
            _preferences.BatchFolder = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            DialogResult = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            _preferences.Set = _models.SetName;

            // The list is remembered as it stands, check marks aside — the same rule as in the
            // "Accept Changes" batch: the models are the same next time, which of them to run is not.
            _preferences.Models.Clear();
            _preferences.Models.AddRange(_models.Rows.Select(row => row.Entry));

            _preferences.Save();

            // The server names typed in here belong to the whole add-in, not to this window.
            _linkPreferences.Save();

            base.OnClosed(e);
        }
    }
}
