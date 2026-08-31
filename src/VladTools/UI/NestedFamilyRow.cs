using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.DB;

namespace VladTools.UI
{
    /// <summary>Что именно стоит за строкой: вложенное семейство или его типоразмер.</summary>
    internal enum NestedKind
    {
        Family,
        Symbol
    }

    /// <summary>Результат проверки нового имени — от него зависит текст и цвет столбца «Статус».</summary>
    internal enum RenameStatus
    {
        /// <summary>Новое имя совпадает со старым: строка остаётся как есть.</summary>
        Unchanged,

        /// <summary>Имя годное и отличается от текущего — будет записано.</summary>
        Ready,

        /// <summary>Имя пустое, с запрещёнными знаками или уже занято другим объектом.</summary>
        Error
    }

    /// <summary>
    /// Строка таблицы в окне «Переименовать вложенные»: галочка, текущее имя, новое имя и проверка.
    /// Хранит сам элемент Revit — команде нужно, чему присваивать новое имя.
    /// </summary>
    internal sealed class NestedFamilyRow : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private string _newName;
        private string _statusText = string.Empty;
        private RenameStatus _status = RenameStatus.Unchanged;

        public NestedFamilyRow(Element element, NestedKind kind, string currentName, string ownerName, int instances)
        {
            Element = element;
            Kind = kind;
            CurrentName = currentName ?? string.Empty;
            OwnerName = ownerName ?? string.Empty;
            Instances = instances;
            _newName = CurrentName;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Элемент Revit, которому будет присвоено новое имя.</summary>
        public Element Element { get; }

        public NestedKind Kind { get; }

        public string KindText
        {
            get { return Kind == NestedKind.Family ? "Семейство" : "Типоразмер"; }
        }

        /// <summary>Имя в документе на текущий момент.</summary>
        public string CurrentName { get; }

        /// <summary>Для типоразмера — имя семейства, которому он принадлежит.</summary>
        public string OwnerName { get; }

        /// <summary>Сколько экземпляров этого семейства расставлено в открытом семействе.</summary>
        public int Instances { get; }

        public string InstancesText
        {
            get { return Instances > 0 ? Instances.ToString() : "—"; }
        }

        /// <summary>Галочка «переименовать эту строку».</summary>
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
        /// Имя, которое получится после переименования. Правило подставляет его само,
        /// но ячейка редактируется — правка руками важнее правила и им не затирается.
        /// </summary>
        public string NewName
        {
            get { return _newName; }
            set
            {
                var text = value ?? string.Empty;

                // Сюда пишет только редактор ячейки: значит, строку правили руками.
                IsManual = true;

                if (_newName == text)
                    return;

                _newName = text;
                Raise(nameof(NewName));
            }
        }

        /// <summary>Имя в этой строке задано руками, а не правилом.</summary>
        public bool IsManual { get; private set; }

        public string TrimmedNewName
        {
            get { return _newName.Trim(); }
        }

        public string StatusText
        {
            get { return _statusText; }
        }

        public RenameStatus Status
        {
            get { return _status; }
        }

        public Brush StatusBrush
        {
            get
            {
                switch (_status)
                {
                    case RenameStatus.Ready:
                        return Brushes.SeaGreen;
                    case RenameStatus.Error:
                        return Brushes.Firebrick;
                    default:
                        return SystemColors.GrayTextBrush;
                }
            }
        }

        /// <summary>Строка, которая действительно уйдёт в переименование.</summary>
        public bool WillRename
        {
            get { return _isSelected && _status == RenameStatus.Ready; }
        }

        /// <summary>Подстановка имени правилом: в отличие от NewName не помечает строку как правленую.</summary>
        public void SetPreview(string name)
        {
            var text = name ?? string.Empty;
            if (_newName == text)
                return;

            _newName = text;
            Raise(nameof(NewName));
        }

        /// <summary>Возвращает строку к исходному имени и снимает пометку о ручной правке.</summary>
        public void ResetPreview()
        {
            IsManual = false;
            SetPreview(CurrentName);
        }

        public void SetStatus(RenameStatus status, string text)
        {
            _status = status;
            _statusText = text ?? string.Empty;

            Raise(nameof(Status));
            Raise(nameof(StatusText));
            Raise(nameof(StatusBrush));
        }

        private void Raise(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
