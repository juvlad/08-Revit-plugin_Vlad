using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// The "Add Formulas to Shared Parameters" window: a "parameter — formula" table that opens
    /// already filled with the saved list (the first time, with the default formulas).
    ///
    /// Every row is validated right away against the open family: does the parameter exist, and
    /// do the parameters the formula refers to. The "Apply" button hands the command every
    /// checked row at once — that is the batch application.
    ///
    /// The window is built in code, without XAML — the project does not include the WPF markup assembly.
    /// </summary>
    internal sealed class AddFormulasWindow : Window
    {
        private const string WindowTitle = "Add Formulas to Shared Parameters";

        private readonly Dictionary<string, FamilyParameterInfo> _parameters;
        private readonly ObservableCollection<FormulaRule> _rules = new ObservableCollection<FormulaRule>();

        private readonly CheckBox _selectAll;
        private readonly DataGrid _grid;
        private readonly TextBlock _summary;
        private readonly Button _applyButton;

        private bool _syncingSelectAll;

        /// <summary>The checked rows the command should apply to the family.</summary>
        public IReadOnlyList<FormulaRule> Selected { get; private set; } = new List<FormulaRule>();

        public AddFormulasWindow(IEnumerable<FamilyParameterInfo> familyParameters)
        {
            // Parameter names in Revit formulas are case-sensitive — we compare the same way.
            _parameters = (familyParameters ?? Enumerable.Empty<FamilyParameterInfo>())
                .Where(parameter => !string.IsNullOrEmpty(parameter.Name))
                .GroupBy(parameter => parameter.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            Title = WindowTitle;
            Width = 900;
            Height = 560;
            MinWidth = 640;
            MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _selectAll = new CheckBox
            {
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Check or clear every formula"
            };
            _selectAll.Checked += (s, e) => SetAllEnabled(true);
            _selectAll.Unchecked += (s, e) => SetAllEnabled(false);

            _grid = BuildGrid();
            _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            _applyButton = new Button
            {
                Content = "Apply",
                MinWidth = 150,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true
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

            Content = BuildLayout(closeButton);

            _rules.CollectionChanged += OnRulesChanged;
            foreach (var entry in FormulaLibrary.Load())
                _rules.Add(new FormulaRule(entry.ParameterName, entry.Formula, entry.IsEnabled));

            Refresh();
        }

        // ───────────────────────────── layout ─────────────────────────────

        private UIElement BuildLayout(Button closeButton)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // hint
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // table
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status + buttons

            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap };
            hint.Inlines.Add(new Run("Formulas are applied to the parameters of the open family. " +
                                     "The list is saved and offered in the next family."));
            hint.Inlines.Add(new LineBreak());
            hint.Inlines.Add(new Run("A new formula is typed into the blank row at the bottom of the table. List file: " + FormulaLibrary.FilePath)
            {
                Foreground = SystemColors.GrayTextBrush
            });
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            Grid.SetRow(_grid, 1);
            root.Children.Add(_grid);

            var bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var deleteButton = new Button
            {
                Content = "Delete row",
                MinWidth = 120,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 12, 0)
            };
            deleteButton.Click += OnDeleteRows;
            Grid.SetColumn(deleteButton, 0);
            bottom.Children.Add(deleteButton);

            Grid.SetColumn(_summary, 1);
            bottom.Children.Add(_summary);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_applyButton);
            buttons.Children.Add(closeButton);
            Grid.SetColumn(buttons, 2);
            bottom.Children.Add(buttons);

            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            return root;
        }

        private DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                ItemsSource = _rules,
                AutoGenerateColumns = false,
                CanUserAddRows = true,
                CanUserDeleteRows = true,
                CanUserResizeRows = false,
                CanUserSortColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = SystemColors.ControlLightBrush,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                SelectionMode = DataGridSelectionMode.Extended,
                RowHeaderWidth = 0,
                Margin = new Thickness(0, 10, 0, 8)
            };

            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = _selectAll,
                Width = new DataGridLength(36),
                CanUserResize = false,
                CellTemplate = BuildCheckBoxTemplate()
            });

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Parameter",
                Width = new DataGridLength(240),
                Binding = new Binding("ParameterName"),
                ElementStyle = CellStyle(null),
                EditingElementStyle = EditorStyle(null)
            });

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Formula",
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                Binding = new Binding("Formula"),
                ElementStyle = CellStyle("Consolas"),
                EditingElementStyle = EditorStyle("Consolas")
            });

            var status = new DataGridTextColumn
            {
                Header = "Status",
                Width = new DataGridLength(260),
                Binding = new Binding("StatusText"),
                IsReadOnly = true,
                ElementStyle = CellStyle(null)
            };
            status.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding("StatusBrush")));
            status.ElementStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            status.ElementStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("StatusText")));
            grid.Columns.Add(status);

            return grid;
        }

        /// <summary>A check box in a cell: with its own template it reacts to the first click.</summary>
        private static DataTemplate BuildCheckBoxTemplate()
        {
            var checkBox = new FrameworkElementFactory(typeof(CheckBox));
            checkBox.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding("IsEnabled") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            checkBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            return new DataTemplate { VisualTree = checkBox };
        }

        private static Style CellStyle(string fontFamily)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));

            if (!string.IsNullOrEmpty(fontFamily))
                style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily(fontFamily)));

            return style;
        }

        private static Style EditorStyle(string fontFamily)
        {
            var style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0)));

            if (!string.IsNullOrEmpty(fontFamily))
                style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily(fontFamily)));

            return style;
        }

        // ───────────────────────────── validation ─────────────────────────────

        /// <summary>Re-validates every row and updates the caption at the bottom.</summary>
        private void Refresh()
        {
            foreach (var rule in _rules)
                Validate(rule);

            UpdateSummary();
        }

        /// <summary>
        /// Validates a single row against the open family: does the parameter itself exist, and
        /// do every name in the formula exist. This is exactly what the user sees in the "Status" column.
        /// </summary>
        private void Validate(FormulaRule rule)
        {
            if (rule.IsBlank)
            {
                rule.SetStatus(FormulaStatus.Blank, string.Empty);
                return;
            }

            var name = rule.TrimmedName;
            if (name.Length == 0)
            {
                rule.SetStatus(FormulaStatus.Error, "No parameter given");
                return;
            }

            var formula = rule.TrimmedFormula;
            if (formula.Length == 0)
            {
                rule.SetStatus(FormulaStatus.Error, "No formula given");
                return;
            }

            FamilyParameterInfo parameter;
            if (!_parameters.TryGetValue(name, out parameter))
            {
                rule.SetStatus(FormulaStatus.Error, NotFound(name));
                return;
            }

            if (!parameter.CanAssignFormula)
            {
                rule.SetStatus(FormulaStatus.Error, "Parameter " + name + " cannot be given a formula");
                return;
            }

            var unknown = FormulaParser.FindUnknownParameters(formula, _parameters.Keys);
            if (unknown.Count > 0)
            {
                rule.SetStatus(FormulaStatus.Error, NotFound(unknown));
                return;
            }

            rule.SetStatus(
                parameter.HasFormula ? FormulaStatus.Replace : FormulaStatus.Ready,
                parameter.HasFormula ? "The formula will be replaced" : "Ready to apply");
        }

        private static string NotFound(string name)
        {
            return "Parameter " + name + " not found";
        }

        private static string NotFound(IReadOnlyList<string> names)
        {
            return names.Count == 1
                ? NotFound(names[0])
                : "Parameters " + string.Join(", ", names) + " not found";
        }

        private void UpdateSummary()
        {
            var filled = _rules.Where(rule => !rule.IsBlank).ToList();
            var marked = filled.Where(rule => rule.IsEnabled).ToList();
            var broken = marked.Count(rule => rule.Status == FormulaStatus.Error);
            var ready = marked.Count - broken;

            _summary.Foreground = broken > 0 ? Brushes.Firebrick : SystemColors.GrayTextBrush;
            _summary.Text = "Formulas in the list: " + filled.Count + ". Checked: " + marked.Count +
                            ", of which ready: " + ready +
                            (broken > 0 ? ", with errors: " + broken + "." : ".");

            _applyButton.Content = ready > 0 ? "Apply (" + ready + ")" : "Apply";
            _applyButton.IsEnabled = marked.Count > 0;

            _syncingSelectAll = true;
            _selectAll.IsChecked = filled.Count == 0 || marked.Count == 0
                ? false
                : marked.Count == filled.Count ? true : (bool?)null;
            _syncingSelectAll = false;
        }

        // ───────────────────────────── actions ─────────────────────────────

        private void OnRulesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (FormulaRule rule in e.OldItems)
                    rule.PropertyChanged -= OnRuleChanged;

            if (e.NewItems != null)
                foreach (FormulaRule rule in e.NewItems)
                    rule.PropertyChanged += OnRuleChanged;

            Refresh();
        }

        private void OnRuleChanged(object sender, PropertyChangedEventArgs e)
        {
            var rule = sender as FormulaRule;
            if (rule == null)
                return;

            if (e.PropertyName == nameof(FormulaRule.ParameterName) || e.PropertyName == nameof(FormulaRule.Formula))
            {
                Validate(rule);
                UpdateSummary();
            }
            else if (e.PropertyName == nameof(FormulaRule.IsEnabled))
            {
                UpdateSummary();
            }
        }

        private void SetAllEnabled(bool value)
        {
            if (_syncingSelectAll)
                return;

            foreach (var rule in _rules.Where(rule => !rule.IsBlank))
                rule.IsEnabled = value;

            UpdateSummary();
        }

        private void OnDeleteRows(object sender, RoutedEventArgs e)
        {
            _grid.CommitEdit(DataGridEditingUnit.Row, true);

            var selected = _grid.SelectedItems.OfType<FormulaRule>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Select the row in the table you want to remove from the list.",
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var rule in selected)
                _rules.Remove(rule);
        }

        /// <summary>
        /// The checked rows go to the command as a single batch. Rows with an error are not
        /// applied: the same message shown in the "Status" column is shown about them.
        /// </summary>
        private void OnApply(object sender, RoutedEventArgs e)
        {
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            Refresh();

            var marked = _rules.Where(rule => !rule.IsBlank && rule.IsEnabled).ToList();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "No formula is checked.", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var broken = marked.Where(rule => rule.Status == FormulaStatus.Error).ToList();
            var ready = marked.Where(rule => rule.Status != FormulaStatus.Error).ToList();

            if (broken.Count > 0)
            {
                var text = Problems(broken);

                if (ready.Count == 0)
                {
                    MessageBox.Show(this, text, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var answer = MessageBox.Show(
                    this,
                    text + "\n\nApply the remaining formulas (" + ready.Count + ")?",
                    WindowTitle,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Yes);

                if (answer != MessageBoxResult.Yes)
                    return;
            }

            Selected = ready;
            DialogResult = true;
        }

        /// <summary>One problem row gets a short message, several get a list.</summary>
        private static string Problems(IReadOnlyList<FormulaRule> broken)
        {
            if (broken.Count == 1)
                return broken[0].StatusText;

            return "Formulas that cannot be applied: " + broken.Count + "\n\n• " +
                   string.Join("\n• ", broken.Select(rule => rule.TrimmedName + " — " + rule.StatusText));
        }

        /// <summary>The list is saved whenever the window closes — it lives independently of the family.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            try
            {
                _grid.CommitEdit(DataGridEditingUnit.Row, true);

                FormulaLibrary.Save(_rules
                    .Where(rule => !rule.IsBlank)
                    .Select(rule => new FormulaEntry(rule.TrimmedName, rule.TrimmedFormula, rule.IsEnabled)));
            }
            catch (Exception exception)
            {
                MessageBox.Show(this,
                    "Could not save the formula list:\n" + exception.Message + "\n\nFile: " + FormulaLibrary.FilePath,
                    WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
