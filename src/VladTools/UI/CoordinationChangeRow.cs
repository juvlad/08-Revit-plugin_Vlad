using System.ComponentModel;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// Строка таблицы в окне «Принять изменения»: одно расхождение между проектом
    /// и координационным файлом.
    ///
    /// Кроме того, что видит пользователь, строка несёт готовую правку
    /// (<see cref="Update"/>) — как <c>SharedParameterRow</c> несёт <c>FamilyParameter</c>.
    /// Окно в неё не заглядывает: считает правку <c>CoordinationCatalog</c>, применяет команда.
    /// </summary>
    internal sealed class CoordinationChangeRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public CoordinationChangeRow(
            CoordinationChangeKind kind,
            bool isLevel,
            string name,
            string detail,
            string note,
            DatumUpdate update)
        {
            Kind = kind;
            IsLevel = isLevel;
            Name = name;
            Detail = detail;
            Note = note;
            Update = update;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Галочка «принять это изменение».</summary>
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

        public CoordinationChangeKind Kind { get; }

        /// <summary>Кнопка умеет это применить; у остальных строк галочка недоступна.</summary>
        public bool CanApply => CoordinationChangeKinds.CanApply(Kind);

        /// <summary>Уровень или ось — от этого зависит и подпись, и способ правки.</summary>
        public bool IsLevel { get; }

        /// <summary>«Уровень» или «Ось».</summary>
        public string Type => IsLevel ? "Уровень" : "Ось";

        /// <summary>Имя элемента в проекте; у нового элемента связи — его имя в связи.</summary>
        public string Name { get; }

        /// <summary>Что именно разошлось — подпись вида изменения.</summary>
        public string What => CoordinationChangeKinds.Label(Kind);

        /// <summary>Было и станет: «сдвиг 150 мм», «+3000 → +3150», «„1“ → „1а“».</summary>
        public string Detail { get; }

        /// <summary>Почему строка не применяется или на что посмотреть глазами.</summary>
        public string Note { get; }

        /// <summary>Как называть элемент в отчёте: «Ось „1“».</summary>
        public string Title => Type + " «" + Name + "»";

        /// <summary>Готовая правка; у неприменимых строк — null.</summary>
        public DatumUpdate Update { get; }

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
