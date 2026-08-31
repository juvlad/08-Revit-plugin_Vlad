using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// Узел дерева в окне просмотра моделей: либо папка, которую можно раскрыть,
    /// либо модель с галочкой.
    ///
    /// Одно дерево обслуживает и Revit Server, и BIM360: устроены они одинаково —
    /// вложенные списки, которые дорого читать целиком и потому читаются по мере раскрытия.
    /// Чем именно заполнять детей, узел не знает: это дело того, кто его создал.
    ///
    /// Свойства для оформления (шрифт, цвет, видимость галочки) лежат прямо здесь:
    /// разметка в проекте собирается кодом, привязка к готовому свойству короче
    /// и понятнее, чем преобразователь значений на каждый случай.
    /// </summary>
    internal sealed class BrowseNode : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _note;

        private BrowseNode(string name, bool isModel, LinkEntry entry, object context)
        {
            Name = name ?? string.Empty;
            IsModel = isModel;
            Entry = entry;
            Context = context;
        }

        /// <summary>Папка, содержимое которой подгружается при раскрытии.</summary>
        public static BrowseNode Folder(string name, object context)
        {
            var node = new BrowseNode(name, false, null, context);

            // Пустышка нужна, чтобы у папки появился треугольник раскрытия:
            // без единого ребёнка WPF считает узел листом и раскрыть его не даст.
            node.Children.Add(new BrowseNode("…", false, null, null) { IsPlaceholder = true });

            return node;
        }

        /// <summary>Модель с галочкой. <paramref name="entry"/> — то, что уйдёт в таблицу связей.</summary>
        public static BrowseNode Model(LinkEntry entry, string note, bool isCheckable)
        {
            return new BrowseNode(entry.Name, true, entry, null)
            {
                _note = note ?? string.Empty,
                IsCheckable = isCheckable
            };
        }

        /// <summary>Строка вместо содержимого: «пусто» или причина отказа.</summary>
        public static BrowseNode Message(string text, bool isError)
        {
            return new BrowseNode(text, false, null, null) { IsPlaceholder = true, IsError = isError };
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string Name { get; }

        public bool IsModel { get; }

        /// <summary>Что за модель — заполнено только у листьев дерева.</summary>
        public LinkEntry Entry { get; }

        /// <summary>Всё, что нужно знать тому, кто будет раскрывать эту папку: сервер, проект, идентификатор.</summary>
        public object Context { get; }

        public ObservableCollection<BrowseNode> Children { get; } = new ObservableCollection<BrowseNode>();

        /// <summary>Служебная строка: пустышка под треугольник, «пусто» или ошибка.</summary>
        public bool IsPlaceholder { get; private set; }

        public bool IsError { get; private set; }

        /// <summary>Содержимое уже прочитано — второй раз к службе не ходим.</summary>
        public bool IsLoaded { get; set; }

        /// <summary>Галочку можно поставить: у уже связанной модели — нельзя.</summary>
        public bool IsCheckable { get; private set; } = true;

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

        /// <summary>Серая приписка справа от имени: «уже в проекте», регион, число моделей.</summary>
        public string Note
        {
            get { return _note ?? string.Empty; }
            set
            {
                _note = value;
                Raise(nameof(Note));
            }
        }

        // ───────────────────────────── оформление ─────────────────────────────

        public Visibility CheckBoxVisibility => IsModel ? Visibility.Visible : Visibility.Collapsed;

        public FontWeight Weight => IsModel || IsPlaceholder ? FontWeights.Normal : FontWeights.SemiBold;

        public Brush Foreground
        {
            get
            {
                if (IsError)
                    return Brushes.Firebrick;

                return IsPlaceholder || !IsCheckable ? SystemColors.GrayTextBrush : SystemColors.ControlTextBrush;
            }
        }

        /// <summary>Все отмеченные модели этого узла и всех вложенных.</summary>
        public IEnumerable<BrowseNode> CheckedModels()
        {
            if (IsModel && IsSelected)
                yield return this;

            foreach (var child in Children)
            {
                foreach (var node in child.CheckedModels())
                    yield return node;
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
