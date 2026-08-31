using System;
using System.ComponentModel;

namespace VladTools.UI
{
    /// <summary>
    /// Строка списка в окне «Очистка модели»: галочка, заголовок с числом найденного
    /// и пояснение, что именно исчезнет.
    ///
    /// Текст пунктов живёт здесь, а не в команде: команда считает в документе, окно
    /// показывает — а как назвать пункт пользователю, знает сам пункт. Пункт, которому
    /// в этой модели нечего убирать, показывается серым и не отмечается: галочка на нём
    /// только сбивала бы с толку.
    /// </summary>
    internal sealed class CleanupOption : INotifyPropertyChanged
    {
        private readonly string _hint;
        private readonly string _emptyHint;

        private bool _isSelected;

        private CleanupOption(CleanupTarget target, int count, string title, string hint, string emptyHint)
        {
            Target = target;
            Count = count;
            Title = title;
            _hint = hint;
            _emptyHint = emptyHint;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public CleanupTarget Target { get; }

        /// <summary>Сколько нашлось при открытии окна.</summary>
        public int Count { get; }

        public string Title { get; }

        /// <summary>Заголовок с числом: без него пункт был бы обещанием, а не отчётом.</summary>
        public string Caption => Count > 0 ? Title + " (" + Count + ")" : Title;

        /// <summary>Пояснение под заголовком; у пустого пункта — почему он недоступен.</summary>
        public string Description => Count > 0 ? _hint : _emptyHint;

        /// <summary>Убирать нечего — пункт выключен.</summary>
        public bool IsAvailable => Count > 0;

        /// <summary>Галочка «убрать это из модели».</summary>
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

        /// <summary>Собирает пункт: команда даёт число, весь текст пункт берёт из своей таблицы.</summary>
        public static CleanupOption For(CleanupTarget target, int count)
        {
            switch (target)
            {
                case CleanupTarget.UnusedFamilies:
                    return new CleanupOption(target, count,
                        "Очистить неиспользуемые семейства",
                        "Загруженные семейства и их типоразмеры, которых нет ни в одном элементе модели; " +
                        "в скобках — число типоразмеров. Выполняется последним: после очистки листов и видов " +
                        "ненужными становятся ещё и рамки, марки, узловые элементы.",
                        "Все загруженные семейства чем-то заняты.");

                case CleanupTarget.Sheets:
                    return new CleanupOption(target, count,
                        "Очистить все листы в модели",
                        "Листы уходят вместе с видовыми экранами, рамками и штампами. " +
                        "Сами виды остаются в браузере — снятыми с листов.",
                        "В модели нет листов.");

                case CleanupTarget.Filters:
                    return new CleanupOption(target, count,
                        "Очистить все фильтры в модели",
                        "Всё из «Вид → Фильтры»: и фильтры видов с правилами, и фильтры выбора. " +
                        "Настройки видимости, которые на них опирались, пропадают вместе с ними.",
                        "В модели нет фильтров.");

                case CleanupTarget.Views:
                    return new CleanupOption(target, count,
                        "Очистить все виды в модели",
                        "Планы, планы потолков, разрезы, фасады, узлы, 3D, обходы и чертёжные виды. " +
                        "Активный вид остаётся — Revit не даёт удалить тот, на котором вы стоите; " +
                        "шаблоны видов не трогаются.",
                        "Кроме активного, других видов в модели нет.");

                case CleanupTarget.Legends:
                    return new CleanupOption(target, count,
                        "Очистить все легенды",
                        "Виды-легенды целиком, вместе с компонентами на них.",
                        "В модели нет легенд.");

                case CleanupTarget.Schedules:
                    return new CleanupOption(target, count,
                        "Очистить все спецификации",
                        "Спецификации, ведомости материалов и примечаний, спецификации панелей. " +
                        "Служебные не трогаются: спецификация изменений внутри рамки листа и внутренняя " +
                        "спецификация ключевых примечаний остаются.",
                        "В модели нет спецификаций.");

                case CleanupTarget.ModelGroups:
                    return new CleanupOption(target, count,
                        "Очистить группы модели",
                        "Группы модели распускаются: элементы остаются на своих местах, исчезают только " +
                        "сами группы. Закреплённые группы предварительно открепляются. Опустевшие типы групп " +
                        "уберёт следующий пункт.",
                        "В модели нет размещённых групп модели.");

                case CleanupTarget.UnusedGroups:
                    return new CleanupOption(target, count,
                        "Очистить неиспользуемые группы в модели",
                        "Типы групп — модели, узлов и прикреплённых узлов, — которых нет ни в одном месте " +
                        "модели: те, что висят в браузере после удаления или роспуска групп.",
                        "Все типы групп размещены в модели.");

                default:
                    throw new ArgumentOutOfRangeException(nameof(target));
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
