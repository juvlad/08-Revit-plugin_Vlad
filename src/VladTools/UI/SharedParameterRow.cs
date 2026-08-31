using System.ComponentModel;
using Autodesk.Revit.DB;

namespace VladTools.UI
{
    /// <summary>
    /// Строка таблицы в окне «Удалить параметры»: галочка, то, что видит пользователь,
    /// плюс ссылка на сам параметр семейства для удаления.
    /// Галочка ставится и вручную, и правилом, поэтому строка сообщает об изменениях.
    /// </summary>
    internal sealed class SharedParameterRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public SharedParameterRow(
            FamilyParameter parameter,
            string name,
            string guid,
            string binding,
            string group,
            bool usedInDimensions)
        {
            Parameter = parameter;
            Name = name;
            Guid = guid;
            Binding = binding;
            Group = group;
            UsedInDimensions = usedInDimensions;
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

                var handler = PropertyChanged;
                if (handler != null)
                    handler(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        /// <summary>Параметр семейства, который стоит за строкой.</summary>
        public FamilyParameter Parameter { get; }

        public string Name { get; }

        public string Guid { get; }

        /// <summary>«Экземпляр» или «Тип».</summary>
        public string Binding { get; }

        /// <summary>Группа параметра, как она подписана в интерфейсе Revit.</summary>
        public string Group { get; }

        /// <summary>
        /// Параметр стоит меткой на размере. Удалить такой — значит снять метку и сломать
        /// параметрику семейства, поэтому окно по умолчанию такие строки не показывает.
        /// </summary>
        public bool UsedInDimensions { get; }

        /// <summary>Подпись для столбца «Размеры»: у непричастных параметров — пусто.</summary>
        public string DimensionUse => UsedInDimensions ? "Метка размера" : string.Empty;
    }
}
