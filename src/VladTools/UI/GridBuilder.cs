using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// Две сборки, повторяющиеся во всех таблицах плагина: колонка текста и колонка с галочкой.
    ///
    /// Вынесено, когда одна и та же дюжина строк понадобилась третьему окну. Ничего своего
    /// здесь нет — только то, что в разметке XAML было бы стилем; проект собирает окна кодом
    /// (см. «Ключевые решения»), поэтому общий стиль и живёт отдельным методом.
    /// </summary>
    internal static class GridBuilder
    {
        /// <summary>Колонка текста: по центру строки, с многоточием и подсказкой во всю ширину.</summary>
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

        /// <summary>Галочка в ячейке: со своим шаблоном она срабатывает с первого щелчка.</summary>
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
