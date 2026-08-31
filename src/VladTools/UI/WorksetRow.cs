using System.ComponentModel;

namespace VladTools.UI
{
    /// <summary>
    /// Строка списка рабочих наборов в окне «Link Manager»: имя набора и галочка,
    /// которая относится сразу ко всем выбранным связям.
    ///
    /// Набор здесь — это именно имя, а не набор конкретной модели: у каждой модели свои
    /// идентификаторы наборов, и единственное, что у «00_Shared levels and grids» общее
    /// во всех связях, — это имя. Поэтому <see cref="LinkCount"/> и нужен: он показывает,
    /// в скольких выбранных моделях такой набор вообще есть.
    /// </summary>
    internal sealed class WorksetRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private int _linkCount;

        public WorksetRow(string name, bool isRemembered)
        {
            Name = name ?? string.Empty;
            IsRemembered = isRemembered;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string Name { get; }

        /// <summary>
        /// Имя пришло из сохранённых настроек, а не из прочитанной модели.
        /// Такое имя может в этих связях и не встретиться — это не ошибка.
        /// </summary>
        public bool IsRemembered { get; }

        /// <summary>Галочка: этот набор трогать во всех выбранных связях.</summary>
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

        /// <summary>В скольких выбранных моделях такой набор нашёлся; наборы не читали — 0.</summary>
        public int LinkCount
        {
            get { return _linkCount; }
            set
            {
                if (_linkCount == value)
                    return;

                _linkCount = value;
                Raise(nameof(LinkCount));
                Raise(nameof(Where));
            }
        }

        /// <summary>Подпись столбца «Где есть».</summary>
        public string Where => LinkCount > 0
            ? "в " + LinkCount + " связях"
            : IsRemembered ? "из прошлого раза" : string.Empty;

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
