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
        private bool _isCounted;

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

        /// <summary>
        /// Наборы отмеченных связей уже читали, так что <see cref="LinkCount"/> — это счёт,
        /// а не «ещё не смотрели». Без этого признака ноль значил и то и другое.
        /// </summary>
        public bool IsCounted
        {
            get { return _isCounted; }
            set
            {
                if (_isCounted == value)
                    return;

                _isCounted = value;
                Raise(nameof(IsCounted));
                Raise(nameof(Where));
            }
        }

        /// <summary>Подпись столбца «Где есть».</summary>
        public string Where
        {
            get
            {
                if (LinkCount > 0)
                    return "в " + LinkCount + " связях";

                // Прочитали и не нашли — совсем не то же, что «не читали»: у имени, которого
                // нет ни в одной связи, закрывать нечего, и это самая частая причина
                // «набор не закрылся». Такую строку пользователь должен видеть.
                if (IsCounted)
                    return "нет ни в одной";

                return IsRemembered ? "из прошлого раза" : string.Empty;
            }
        }

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
