using System.ComponentModel;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// Строка таблицы окна «Комплект по корпусу»: одна найденная модель раздела.
    ///
    /// Про Revit не знает ничего — как и все окна плагина: всё, что нужно, уже лежит
    /// в <see cref="LinkEntry"/>, а отсюда наружу уходит только он.
    /// </summary>
    internal sealed class ModelKitRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ModelKitRow(ModelKitHit hit, bool isSelected, string status)
        {
            Hit = hit;
            _isSelected = isSelected;
            Status = status ?? string.Empty;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ModelKitHit Hit { get; }

        public LinkEntry Entry => Hit.Entry;

        /// <summary>Галочка «добавить эту модель в список связей».</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;

                var handler = PropertyChanged;
                if (handler != null)
                    handler(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public string Discipline => Hit.Discipline;

        public string Name => Hit.Entry.Name;

        /// <summary>Папка, в которой модель нашлась, — по ней и видно, что подбор не промахнулся.</summary>
        public string Folder => Hit.Folder;

        /// <summary>Почему строка не отмечена: моделей в разделе несколько, модель уже в списке.</summary>
        public string Status { get; }
    }
}
