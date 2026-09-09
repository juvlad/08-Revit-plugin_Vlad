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

        /// <summary>A check box in a cell: with its own template it reacts to the first click.</summary>
        public static DataTemplate CheckBoxTemplate()
        {
            var checkBox = new FrameworkElementFactory(typeof(CheckBox));
            checkBox.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding("IsSelected") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            checkBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            return new DataTemplate { VisualTree = checkBox };
        }
    }
}
