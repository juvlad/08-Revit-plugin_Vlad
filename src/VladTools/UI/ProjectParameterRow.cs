using System.ComponentModel;
using Autodesk.Revit.DB;

namespace VladTools.UI
{
    /// <summary>
    /// Строка таблицы в окне «Удалить общие параметры проекта»: галочка, то, что видит
    /// пользователь, плюс Id элемента-параметра, который команда удалит из документа.
    /// Хранится именно Id, а не элемент: после удаления элемент становится негодным,
    /// а по Id можно спросить документ, жив ли параметр ещё.
    /// Галочка ставится и вручную, и правилом, поэтому строка сообщает об изменениях.
    /// </summary>
    internal sealed class ProjectParameterRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _usedInDimensions;

        public ProjectParameterRow(
            ElementId id,
            string name,
            string guid,
            string binding,
            string group,
            string categories,
            bool isBound)
        {
            Id = id;
            Name = name;
            Guid = guid;
            Binding = binding;
            Group = group;
            Categories = categories;
            IsBound = isBound;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Галочка «удалить этот параметр».</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                Raise(nameof(IsSelected));
            }
        }

        /// <summary>
        /// Параметр стоит меткой на размере хотя бы в одном загруженном семействе.
        /// Заполняется не сразу: чтобы это узнать, надо открыть каждое семейство,
        /// поэтому проверка запускается кнопкой в окне.
        /// </summary>
        public bool UsedInDimensions
        {
            get { return _usedInDimensions; }
            set
            {
                if (_usedInDimensions == value)
                    return;

                _usedInDimensions = value;
                Raise(nameof(UsedInDimensions));
                Raise(nameof(DimensionUse));
            }
        }

        /// <summary>Подпись для столбца «Размеры»: у непричастных параметров — пусто.</summary>
        public string DimensionUse => UsedInDimensions ? "Метка размера" : string.Empty;

        /// <summary>Id элемента общего параметра в документе проекта.</summary>
        public ElementId Id { get; }

        public string Name { get; }

        public string Guid { get; }

        /// <summary>«Экземпляр», «Тип» или «Нет привязки».</summary>
        public string Binding { get; }

        /// <summary>Группа параметра, как она подписана в интерфейсе Revit.</summary>
        public string Group { get; }

        /// <summary>Категории, к которым параметр привязан; у непривязанного — пусто.</summary>
        public string Categories { get; }

        /// <summary>
        /// Параметр привязан к категориям, то есть виден в «Управление → Параметры проекта».
        /// Непривязанный остался в файле от загруженных семейств или от снятой привязки.
        /// </summary>
        public bool IsBound { get; }

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
