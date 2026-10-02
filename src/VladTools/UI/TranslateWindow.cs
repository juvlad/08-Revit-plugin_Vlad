using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// The "Translate" window: every Russian text of the open project, once per spelling, with a
    /// column for its English.
    ///
    /// Translations arrive three ways, and all three end up in the same column: the dictionary
    /// fills in whatever was translated in an earlier project the moment the window opens; "Import…"
    /// reads a file translated outside Revit (by a person, DeepL or an AI, from the list "Export…"
    /// wrote); and any cell can be typed into by hand. Whatever the column holds when the window
    /// closes goes back into the dictionary, applied or not — a translation typed once is never typed
    /// again.
    ///
    /// The search box and the "Show" list only *find* rows, they do not select them: a template has
    /// hundreds of texts, and typing "план" to reach one row must not take the check marks off every
    /// other. That is the exception to the add-in's "only what is shown gets applied" rule (see
    /// CLAUDE.md, "Conventions"), and the confirmation says outright how many of the rows going in
    /// are not on screen.
    ///
    /// The window knows nothing about the Revit API: it is handed <see cref="TranslationText"/>
    /// snapshots and <see cref="NameScope"/>s, and hands back original → translation.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class TranslateWindow : Window
    {
        private const string WindowTitle = "Translate";

        /// <summary>Characters Revit will not accept in names.</summary>
        private static readonly char[] Forbidden = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        private static readonly string[] ShowOptions =
        {
            "All texts",
            "Not translated yet",
            "Translated",
            "Need attention",
            "Cannot be changed"
        };

        private readonly IReadOnlyList<TranslationRow> _rows;
        private readonly IReadOnlyList<NameScope> _scopes;
        private readonly ObservableCollection<TranslationRow> _visible = new ObservableCollection<TranslationRow>();
        private readonly string _projectName;

        // The dictionary as it was read when the window opened, and what "Import…" brought in that
        // matches no text of this project — both are written back on closing, with the table on top.
        private readonly Dictionary<string, string> _dictionary;
        private readonly Dictionary<string, string> _imported = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly TranslatePreferences _preferences;

        private readonly TextBox _searchBox;
        private readonly ComboBox _showBox;
        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly Button _applyButton;

        private bool _syncingSelectAll;
        private bool _settingMany;

        /// <summary>What the user confirmed: original → translation, both normalised.</summary>
        public IReadOnlyDictionary<string, string> Selected { get; private set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <param name="texts">Every distinct Russian text of the open project.</param>
        /// <param name="scopes">The sets of names Revit keeps unique, for the clash check.</param>
        /// <param name="projectName">The open project — the caption above the table and the export's file name.</param>
        public TranslateWindow(IReadOnlyList<TranslationText> texts, IReadOnlyList<NameScope> scopes, string projectName)
        {
            _scopes = scopes ?? new List<NameScope>();
            _projectName = projectName ?? string.Empty;
            _dictionary = TranslationDictionary.Load();
            _preferences = TranslatePreferences.Load();

            _rows = (texts ?? new List<TranslationText>())
                .OrderBy(text => text.Original, StringComparer.CurrentCultureIgnoreCase)
                .Select(text => new TranslationRow(text))
                .ToList();

            var known = 0;
            foreach (var row in _rows)
            {
                if (_dictionary.TryGetValue(row.Original, out var translation))
                {
                    row.Translation = translation;
                    known++;
                }
            }

            Title = WindowTitle;
            Width = 1240;
            Height = 720;
            MinWidth = 900;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _searchBox = new TextBox
            {
                Width = 240,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Finds a row by its Russian text, its translation or where it is used.\n" +
                          "Only finds: rows out of sight keep their check marks and are translated too."
            };
            _searchBox.TextChanged += (s, e) => RebuildVisible();

            _showBox = new ComboBox { Width = 170, Margin = new Thickness(6, 0, 0, 0), ItemsSource = ShowOptions, SelectedIndex = 0 };
            _showBox.SelectionChanged += (s, e) => RebuildVisible();

            _selectAll = new CheckBox
            {
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every row shown in the table"
            };
            _selectAll.Checked += (s, e) => SetAllSelected(true);
            _selectAll.Unchecked += (s, e) => SetAllSelected(false);

            _grid = BuildGrid();

            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _applyButton = new Button
            {
                Content = "Translate",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _applyButton.Click += OnApply;

            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 110,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            Content = BuildLayout(known, closeButton);

            foreach (var row in _rows)
                row.PropertyChanged += OnRowChanged;

            _grid.ItemsSource = _visible;

            Loaded += (s, e) => _searchBox.Focus();
            Closing += OnClosing;

            Revalidate();
            RebuildVisible();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(int known, Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // project
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // tools
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock
            {
                Text =
                    "Every text in the open project that contains Cyrillic — once per spelling. Fill in the English " +
                    "column: by hand, or \"Export…\" the list, translate it (a translator, DeepL, an AI) and " +
                    "\"Import…\" it back. Each text is replaced everywhere it is used at once — in names, parameter " +
                    "values, schedules, view filter rules and text notes — so filters keep matching what they matched " +
                    "before. Translations are remembered and offered again in the next project.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var project = new TextBlock
            {
                Text = "Project: " + (_projectName.Length == 0 ? "(unsaved)" : _projectName) +
                       "   ·   Russian texts: " + _rows.Count +
                       "   ·   Already in the dictionary: " + known,
                Foreground = SystemColors.GrayTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(project, 1);
            root.Children.Add(project);

            var tools = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };

            tools.Children.Add(Label("Find:"));
            tools.Children.Add(_searchBox);
            tools.Children.Add(Label("Show:", 14));
            tools.Children.Add(_showBox);

            var importButton = ToolButton("Import…",
                "Reads a translated file: the Russian text, a TAB, the English one — the format \"Export…\" writes.\n" +
                "Matching rows get the file's translation; lines this project does not have go into the dictionary.");
            importButton.Click += OnImport;
            DockPanel.SetDock(importButton, Dock.Right);

            var exportButton = ToolButton("Export…",
                "Writes the texts to a file for translating outside Revit: one line per text — the Russian original,\n" +
                "a TAB, the English (filled in where it is already known), a TAB, where the text is used.\n" +
                "Excel opens it as columns; a translator or an AI fills in the second one.");
            exportButton.Click += OnExport;
            DockPanel.SetDock(exportButton, Dock.Right);

            tools.Children.Add(importButton);
            tools.Children.Add(exportButton);

            Grid.SetRow(tools, 2);
            root.Children.Add(tools);

            Grid.SetRow(_grid, 3);
            root.Children.Add(_grid);

            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_status, 0);
            bottom.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_applyButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 4);
            root.Children.Add(bottom);

            return root;
        }

        private static TextBlock Label(string text, double left = 0)
        {
            return new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(left, 0, 6, 0)
            };
        }

        private static Button ToolButton(string text, string tooltip)
        {
            return new Button
            {
                Content = text,
                MinWidth = 100,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = tooltip
            };
        }

        private DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                CanUserSortColumns = true,
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
                CanUserSort = false,
                CellTemplate = GridBuilder.CheckBoxTemplate(nameof(TranslationRow.IsSelected), nameof(TranslationRow.CanApply))
            });

            grid.Columns.Add(Text("Russian", nameof(TranslationRow.Original), nameof(TranslationRow.Original), 1));

            var editor = new Style(typeof(TextBox));
            editor.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            editor.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0)));

            var english = Text("English", nameof(TranslationRow.Translation), nameof(TranslationRow.Translation), 1);
            english.IsReadOnly = false;
            english.Binding = new Binding(nameof(TranslationRow.Translation)) { Mode = BindingMode.TwoWay };
            english.EditingElementStyle = editor;
            grid.Columns.Add(english);

            grid.Columns.Add(Text("Where", nameof(TranslationRow.Places), nameof(TranslationRow.PlacesDetail), 1));

            var status = Text("State", nameof(TranslationRow.StatusText), nameof(TranslationRow.StatusText), 1);
            status.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(TranslationRow.StatusBrush))));
            grid.Columns.Add(status);

            grid.PreviewKeyDown += OnGridKeyDown;

            return grid;
        }

        /// <summary>A read-only text column whose tooltip may show a fuller text than the cell — the "Where" column's full list of places.</summary>
        private static DataGridTextColumn Text(string header, string property, string tooltipProperty, double stars)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(tooltipProperty)));

            return new DataGridTextColumn
            {
                Header = header,
                Width = new DataGridLength(stars, DataGridLengthUnitType.Star),
                MinWidth = 120,
                Binding = new Binding(property),
                IsReadOnly = true,
                ElementStyle = style
            };
        }

        // ───────────────────────────── finding rows ─────────────────────────────

        private void RebuildVisible()
        {
            CommitEdits();

            var query = (_searchBox?.Text ?? string.Empty).Trim();

            _visible.Clear();
            foreach (var row in _rows)
            {
                if (Shows(row) && Matches(row, query))
                    _visible.Add(row);
            }

            SyncSelectAll();
        }

        private bool Shows(TranslationRow row)
        {
            switch (_showBox?.SelectedIndex ?? 0)
            {
                case 1:
                    return row.CanApply && !row.IsTranslated;
                case 2:
                    return row.IsTranslated;
                case 3:
                    return row.IsWarning;
                case 4:
                    return row.Info.FixedCount > 0;
                default:
                    return true;
            }
        }

        private static bool Matches(TranslationRow row, string query)
        {
            if (query.Length == 0)
                return true;

            return Contains(row.Original, query) || Contains(row.Translation, query) || Contains(row.PlacesDetail, query);
        }

        private static bool Contains(string text, string query)
        {
            return (text ?? string.Empty).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        /// <summary>
        /// A cell still being typed into is committed before the list changes under it — the grid
        /// refuses a refresh in the middle of an edit, and the typed text would be lost besides.
        /// </summary>
        private void CommitEdits()
        {
            if (_grid == null)
                return;

            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        // ───────────────────────────── check boxes ─────────────────────────────

        /// <summary>The header box acts on the rows shown — that is what a person looking at a filtered table means by "all".</summary>
        private void SetAllSelected(bool value)
        {
            if (_syncingSelectAll)
                return;

            SetMany(_visible.Where(row => row.CanApply).ToList(), row => row.IsSelected = value);
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            // While a cell is being typed into, the space bar is just a space in the translation.
            if (e.Key != Key.Space || Keyboard.FocusedElement is TextBox)
                return;

            var rows = _grid.SelectedItems.OfType<TranslationRow>().Where(row => row.CanApply).ToList();
            if (rows.Count == 0)
                return;

            var value = !rows.All(row => row.IsSelected);
            SetMany(rows, row => row.IsSelected = value);
            e.Handled = true;
        }

        /// <summary>Changing many rows at once: the table is revalidated once, at the end.</summary>
        private void SetMany(IEnumerable<TranslationRow> rows, Action<TranslationRow> change)
        {
            _settingMany = true;

            foreach (var row in rows)
                change(row);

            _settingMany = false;

            Revalidate();
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_settingMany)
                return;

            if (e.PropertyName == nameof(TranslationRow.IsSelected) || e.PropertyName == nameof(TranslationRow.Translation))
                Revalidate();
        }

        private void SyncSelectAll()
        {
            var candidates = _visible.Where(row => row.CanApply).ToList();
            var selected = candidates.Count(row => row.IsSelected);

            _syncingSelectAll = true;
            _selectAll.IsChecked = candidates.Count == 0 || selected == 0
                ? false
                : selected == candidates.Count ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        // ───────────────────────────── validation ─────────────────────────────

        /// <summary>
        /// Brings every row's "State", the summary and the button in line with the table. One method
        /// rather than a handler per control, so the table can never promise one thing while the
        /// button does another — the same rule as in the "Worksets" window.
        /// </summary>
        private void Revalidate()
        {
            var applied = _rows
                .Where(row => row.WillApply)
                .ToDictionary(row => row.Original, row => row.CleanTranslation, StringComparer.Ordinal);

            var clashes = FindClashes(applied);

            foreach (var row in _rows)
                Describe(row, clashes);

            UpdateSummary();
            SyncSelectAll();
        }

        /// <summary>
        /// Every translated name that would land on a name already standing in the same scope — an
        /// English name an element already has, or the translation of another Russian one. Compared
        /// ignoring case: Revit does not tell names apart by case either.
        /// </summary>
        private Dictionary<string, string> FindClashes(IReadOnlyDictionary<string, string> applied)
        {
            var clashes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (applied.Count == 0)
                return clashes;

            foreach (var scope in _scopes)
            {
                var groups = scope.Names.GroupBy(
                    name => applied.TryGetValue(name, out var translation) ? translation : name,
                    StringComparer.CurrentCultureIgnoreCase);

                foreach (var group in groups)
                {
                    var members = group.ToList();
                    if (members.Count < 2)
                        continue;

                    foreach (var original in members.Where(applied.ContainsKey))
                    {
                        if (clashes.ContainsKey(original))
                            continue;

                        var other = members.First(member => member != original);

                        clashes[original] = applied.ContainsKey(other)
                            ? "Among " + scope.Caption + ", \"" + other + "\" becomes \"" + group.Key + "\" too"
                            : "Among " + scope.Caption + ", the name \"" + group.Key + "\" is already taken";
                    }
                }
            }

            return clashes;
        }

        private static void Describe(TranslationRow row, IReadOnlyDictionary<string, string> clashes)
        {
            var info = row.Info;
            row.IsBlocked = false;
            row.IsWarning = false;

            if (!row.CanApply)
            {
                row.StatusText = info.FixedNote;
                return;
            }

            var tail = info.FixedCount > 0 ? "; " + info.FixedNote : string.Empty;

            if (!row.IsTranslated)
            {
                row.StatusText = (row.CleanTranslation.Length == 0 ? "Not translated" : "Same as the original — nothing to change") + tail;
                return;
            }

            if (!row.IsSelected)
            {
                row.StatusText = "Left as it is — the check box is cleared";
                return;
            }

            var translation = row.CleanTranslation;

            if (info.HasNames)
            {
                var forbidden = Forbidden.Where(character => translation.IndexOf(character) >= 0).ToArray();
                if (forbidden.Length > 0 || translation.IndexOf('\n') >= 0)
                {
                    row.IsBlocked = true;
                    row.IsWarning = true;
                    row.StatusText = "It is used as a name, and a name cannot hold " +
                                     (forbidden.Length > 0 ? string.Join(" ", forbidden) : "a line break");
                    return;
                }

                if (clashes.TryGetValue(row.Original, out var clash))
                {
                    row.IsBlocked = true;
                    row.IsWarning = true;
                    row.StatusText = clash;
                    return;
                }
            }

            if (TranslationDictionary.HasCyrillic(translation))
            {
                row.IsWarning = true;
                row.StatusText = "The translation still has Cyrillic letters" + tail;
                return;
            }

            row.StatusText = "Will be replaced in " + Places(info.ChangeableCount) + tail;
        }

        private static string Places(int count)
        {
            return count == 1 ? "1 place" : count + " places";
        }

        private void UpdateSummary()
        {
            var changeable = _rows.Count(row => row.CanApply);
            var translated = _rows.Count(row => row.CanApply && row.IsTranslated);
            var going = _rows.Where(row => row.WillApply).ToList();
            var blocked = going.Count(row => row.IsBlocked);

            _applyButton.Content = "Translate";

            if (going.Count == 0)
            {
                _status.Foreground = SystemColors.GrayTextBrush;
                _status.Text = changeable == 0
                    ? "None of these texts can be changed through the Revit API — the table shows why."
                    : "Translated: " + translated + " of " + changeable + ". Nothing to apply yet — fill in the " +
                      "English column, or \"Import…\" a translated file.";
                _applyButton.IsEnabled = false;
                return;
            }

            if (blocked > 0)
            {
                _status.Foreground = Brushes.Firebrick;
                _status.Text = blocked + (blocked == 1 ? " translation cannot" : " translations cannot") +
                               " go in as it stands — Show → \"Need attention\" lists them. Fix the English or clear the check box.";
                _applyButton.IsEnabled = false;
                return;
            }

            _status.Foreground = SystemColors.GrayTextBrush;
            _status.Text = "Translated: " + translated + " of " + changeable + ". Will be applied: " + going.Count +
                           (going.Count == 1 ? " text in " : " texts in ") + Places(going.Sum(row => row.Info.ChangeableCount)) + ".";

            _applyButton.IsEnabled = true;
            _applyButton.Content = "Translate (" + going.Count + ")";
        }

        // ───────────────────────────── export and import ─────────────────────────────

        private void OnExport(object sender, RoutedEventArgs e)
        {
            CommitEdits();

            var rows = _rows.Where(row => row.CanApply).ToList();
            if (rows.Count == 0)
            {
                MessageBox.Show(this, "None of the texts can be changed, so there is nothing to translate.", WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export the texts to translate",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".txt",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = SafeFileName(_projectName.Length == 0 ? "Project" : Path.GetFileNameWithoutExtension(_projectName)) +
                           " - texts to translate.txt"
            };

            var folder = Folder();
            if (folder != null)
                dialog.InitialDirectory = folder;

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                TranslationDictionary.Write(
                    dialog.FileName,
                    ExportHeader(),
                    rows.Select(row => (IReadOnlyList<string>)new[] { row.Original, row.CleanTranslation, row.Places }));

                _preferences.Folder = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "The file could not be written:\n" + exception.Message, WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var untranslated = rows.Count(row => !row.IsTranslated);
            var fixedOnly = _rows.Count - rows.Count;

            MessageBox.Show(this,
                "Texts written: " + rows.Count + ", of them not translated yet: " + untranslated + ".\n" +
                dialog.FileName + "\n\n" +
                "Fill in the second column — in Notepad or Excel, or hand the file to a translator or an AI " +
                "(the lines at the top of the file explain the format) — and bring it back with \"Import…\"." +
                (fixedOnly > 0
                    ? "\n\nLeft out: " + fixedOnly + (fixedOnly == 1 ? " text" : " texts") +
                      " the Revit API cannot change anywhere (the table says why)."
                    : string.Empty),
                WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>The lines at the top of an export — written for whoever does the translating, a person or an AI.</summary>
        private IEnumerable<string> ExportHeader()
        {
            return new[]
            {
                "# Texts of the Revit project \"" + _projectName + "\" to translate from Russian into English.",
                "# Every line: the Russian original <TAB> the English translation <TAB> where the text is used in the project.",
                "# Fill in the second column only. Keep the first column exactly as it is: that is how each line finds its text again.",
                "# An empty second column means \"not translated\" — such a line is skipped when the file is imported.",
                "# Inside a text, \\n stands for a line break and \\t for a tab — keep them where they are.",
                "# A text used as a name (of a view, a type, a family, a level…) must not contain any of: \\ : { } [ ] | ; < > ? ` ~",
                "# The texts are building-design terms: translate them the way an English drawing set would say it,",
                "# and give the same Russian word the same English word everywhere.",
                "# Saving from Excel: choose \"Unicode Text (*.txt)\" — the other text formats lose the Cyrillic."
            };
        }

        private void OnImport(object sender, RoutedEventArgs e)
        {
            CommitEdits();

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import translations",
                Filter = "Text files (*.txt;*.tsv)|*.txt;*.tsv|All files (*.*)|*.*",
                CheckFileExists = true
            };

            var folder = Folder();
            if (folder != null)
                dialog.InitialDirectory = folder;

            if (dialog.ShowDialog(this) != true)
                return;

            Dictionary<string, string> entries;
            int unreadable;

            try
            {
                entries = TranslationDictionary.Read(dialog.FileName, out unreadable);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "The file could not be read:\n" + exception.Message, WindowTitle,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _preferences.Folder = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;

            var byOriginal = _rows.ToDictionary(row => row.Original, StringComparer.Ordinal);
            int filled = 0, changed = 0, same = 0, foreign = 0;

            _settingMany = true;

            foreach (var entry in entries)
            {
                if (!byOriginal.TryGetValue(entry.Key, out var row))
                {
                    // Not a text of this project — kept for the dictionary, where the next project may need it.
                    _imported[entry.Key] = entry.Value;
                    foreign++;
                    continue;
                }

                if (row.CleanTranslation == entry.Value)
                {
                    same++;
                    continue;
                }

                if (row.CleanTranslation.Length == 0)
                    filled++;
                else
                    changed++;

                row.Translation = entry.Value;
            }

            _settingMany = false;

            Revalidate();
            RebuildVisible();

            var text = entries.Count == 0
                ? "No translated line was found in the file. Every line needs the Russian text, a TAB and the English one — " +
                  "the format \"Export…\" writes."
                : "Translations filled in: " + filled + ", replaced: " + changed + ", already the same: " + same + ".";

            if (foreign > 0)
                text += "\n\nLines that match no text of this project: " + foreign + ". They are kept in the dictionary " +
                        "for the next project. If there are many, the first column may have been edited — it has to stay " +
                        "the Russian text exactly as exported.";

            if (unreadable > 0)
                text += "\n\nLines without a TAB between the columns, skipped: " + unreadable + ".";

            MessageBox.Show(this, text, WindowTitle, MessageBoxButton.OK,
                entries.Count == 0 || unreadable > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private string Folder()
        {
            var folder = _preferences.Folder;
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        }

        // ───────────────────────────── applying and closing ─────────────────────────────

        private void OnApply(object sender, RoutedEventArgs e)
        {
            CommitEdits();
            Revalidate();

            var going = _rows.Where(row => row.WillApply).ToList();
            if (going.Count == 0)
                return;

            var blocked = going.Where(row => row.IsBlocked).ToList();
            if (blocked.Count > 0)
            {
                MessageBox.Show(this,
                    "These translations cannot go in as they stand:\n\n" +
                    string.Join("\n", blocked.Take(10).Select(row => "• " + row.Original + " → " + row.CleanTranslation + ": " + row.StatusText)) +
                    (blocked.Count > 10 ? "\n… and " + (blocked.Count - 10) + " more" : string.Empty),
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!Confirm(going))
                return;

            Selected = going.ToDictionary(row => row.Original, row => row.CleanTranslation, StringComparer.Ordinal);
            DialogResult = true;
        }

        private bool Confirm(IReadOnlyList<TranslationRow> going)
        {
            var places = going.Sum(row => row.Info.ChangeableCount);
            var hidden = going.Count(row => !_visible.Contains(row));
            var cyrillic = going.Count(row => TranslationDictionary.HasCyrillic(row.CleanTranslation));

            var text = "Translate " + going.Count + (going.Count == 1 ? " text" : " texts") + " in " + Places(places) +
                       " of the project?\n\nNames, parameter values, schedule headings and filters, view filter rules and " +
                       "text notes are all replaced in one operation, so it undoes with a single Ctrl+Z.";

            if (hidden > 0)
                text += "\n\n" + hidden + " of them " + (hidden == 1 ? "is" : "are") + " not shown in the table right now " +
                        "(the search or the \"Show\" list hides them) — they are translated too.";

            if (cyrillic > 0)
                text += "\n\n" + cyrillic + (cyrillic == 1 ? " translation still has" : " translations still have") +
                        " Cyrillic letters in it.";

            text += "\n\nSave a copy of the file first: once it is saved, Ctrl+Z will not bring the Russian back.";

            return MessageBox.Show(this, text, WindowTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        /// <summary>
        /// Whatever the English column holds goes into the dictionary, applied or not: a translation
        /// typed once must not have to be typed again in the next project. A row whose translation
        /// was cleared takes its entry out — the table is the newer word on every text it shows.
        /// </summary>
        private void OnClosing(object sender, CancelEventArgs e)
        {
            CommitEdits();

            var merged = new Dictionary<string, string>(_dictionary, StringComparer.Ordinal);

            foreach (var entry in _imported)
                merged[entry.Key] = entry.Value;

            foreach (var row in _rows)
            {
                var translation = row.CleanTranslation;
                if (translation.Length == 0)
                    merged.Remove(row.Original);
                else
                    merged[row.Original] = translation;
            }

            if (merged.Count != _dictionary.Count ||
                merged.Any(entry => !_dictionary.TryGetValue(entry.Key, out var old) || old != entry.Value))
            {
                try
                {
                    TranslationDictionary.Save(merged);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this,
                        "The dictionary could not be saved, so the translations typed here will not be offered next time:\n" +
                        exception.Message + "\n\n" + TranslationDictionary.FilePath,
                        WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            _preferences.Save();
        }
    }
}
