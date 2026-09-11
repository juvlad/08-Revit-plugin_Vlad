using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// The two pieces repeated in every table of the add-in: a text column and a check box column.
    ///
    /// Extracted once the same dozen lines were needed by a third window. There is nothing custom
    /// here — only what would have been a style in XAML markup; the project builds its windows in
    /// code (see "Key decisions"), so the shared style lives in a method of its own.
    /// </summary>
    internal static class GridBuilder
    {
        /// <summary>A text column: centred in the row, with an ellipsis and a full-width tooltip.</summary>
        public static DataGridTextColumn TextColumn(string header, string property, DataGridLength width)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(property)));

            return new DataGridTextColumn
            {
                Header = header,
                Width = width,
                Binding = new Binding(property),
                ElementStyle = style
            };
        }

        /// <summary>
        /// A check box in a cell: with its own template it reacts to the first click. Binds to
        /// "IsSelected" by default; <paramref name="enabledPath"/> additionally disables the box
        /// where the setting makes no sense for that row (a type-bound row's "varies across groups", say).
        ///
        /// **Name the path explicitly whenever the row's flag is not called "IsSelected".** A WPF
        /// binding to a property that does not exist fails silently: the box still ticks on screen,
        /// because an unbound <c>IsChecked</c> keeps its own local value, and the tick simply never
        /// reaches the row. That is what made the category picker look like it refused to remember
        /// anything.
        /// </summary>
        public static DataTemplate CheckBoxTemplate(string bindingPath = "IsSelected", string enabledPath = null)
        {
            var checkBox = new FrameworkElementFactory(typeof(CheckBox));
            checkBox.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding(bindingPath) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

            if (enabledPath != null)
                checkBox.SetBinding(FrameworkElement.IsEnabledProperty, new Binding(enabledPath));

            checkBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            return new DataTemplate { VisualTree = checkBox };
        }

        /// <summary>
        /// A combo box in a cell, bound one way only — never with a <c>DataGridComboBoxColumn</c>,
        /// whose three mutually exclusive bindings silently drop a choice if more than one is set
        /// (see CLAUDE.md, the "DataGridComboBoxColumn" key decision). The list is shared by every
        /// row, so <c>IsSynchronizedWithCurrentItem</c> is turned off — otherwise a choice in one row
        /// would drag the others along.
        /// </summary>
        public static DataTemplate ComboTemplate(
            System.Collections.IEnumerable itemsSource,
            string displayMemberPath,
            string selectedValuePath,
            string bindingPath)
        {
            var combo = new FrameworkElementFactory(typeof(ComboBox));
            combo.SetValue(ItemsControl.ItemsSourceProperty, itemsSource);
            combo.SetValue(ItemsControl.DisplayMemberPathProperty, displayMemberPath);
            combo.SetValue(Selector.SelectedValuePathProperty, selectedValuePath);
            combo.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));
            combo.SetValue(Selector.IsSynchronizedWithCurrentItemProperty, (bool?)false);
            combo.SetBinding(Selector.SelectedValueProperty,
                new Binding(bindingPath) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

            return new DataTemplate { VisualTree = combo };
        }
    }
}
