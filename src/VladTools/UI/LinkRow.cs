using System.Collections.Generic;
using System.ComponentModel;
using Autodesk.Revit.DB;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// Строка таблицы в окне «Link Manager»: одна модель, которую предстоит связать.
    ///
    /// Сама модель описана в <see cref="Entry"/> — путём или парой GUID; остальное здесь
    /// нужно только окну: галочка, подписи столбцов и то, что выяснилось про эту модель
    /// по ходу дела (прочитанные рабочие наборы, отказ чтения).
    ///
    /// Строка знает про <see cref="ElementId"/> уже существующей связи — то же исключение
    /// из правила «окна не знают про Revit», что и в <see cref="ProjectParameterRow"/>:
    /// команде нужно, что перезагружать, а окну — только признак «уже в проекте».
    /// </summary>
    internal sealed class LinkRow : INotifyPropertyChanged
    {
        /// <summary>
        /// Подпись первой строки в списке рабочих наборов — «набор не выбран».
        /// Пустая строка в выпадающем списке выглядела бы как недосмотр, поэтому у неё есть имя;
        /// наружу, в <see cref="LinkEntry.Workset"/>, она всё равно уходит пустой.
        /// </summary>
        public const string ActiveWorkset = "(активный)";

        private bool _isSelected;
        private IReadOnlyList<string> _worksetNames;
        private string _note = string.Empty;

        public LinkRow(LinkEntry entry, ElementId existingId = null)
        {
            Entry = entry;
            ExistingId = existingId ?? ElementId.InvalidElementId;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Описание модели: путь либо регион и пара GUID.</summary>
        public LinkEntry Entry { get; }

        /// <summary>Связь на эту модель уже стоит в проекте; иначе <c>InvalidElementId</c>.</summary>
        public ElementId ExistingId { get; }

        public bool IsExisting => ExistingId != null && ExistingId != ElementId.InvalidElementId;

        /// <summary>Ключ сравнения моделей: путь без учёта регистра либо пара GUID.</summary>
        public string Key => Entry.Key;

        /// <summary>Галочка «работать с этой связью».</summary>
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
        /// Рабочие наборы модели, прочитанные без её открытия. Пока не читали — null,
        /// и это не то же самое, что пустой список: у несовмещённой модели наборов нет вовсе.
        /// </summary>
        public IReadOnlyList<string> WorksetNames
        {
            get { return _worksetNames; }
            set
            {
                _worksetNames = value;
                Raise(nameof(WorksetNames));
                Raise(nameof(Worksets));
            }
        }

        /// <summary>
        /// Рабочий набор открытого проекта, в который встанет связь. Не путать с
        /// <see cref="WorksetNames"/>: те — наборы внутри самой связи, а этот — снаружи, в проекте.
        /// </summary>
        public string Workset
        {
            get { return Entry.Workset.Length == 0 ? ActiveWorkset : Entry.Workset; }
            set
            {
                var chosen = value == ActiveWorkset ? string.Empty : value ?? string.Empty;
                if (Entry.Workset == chosen)
                    return;

                Entry.Workset = chosen;
                Raise(nameof(Workset));
            }
        }

        /// <summary>Короткая приписка: почему наборы не прочитались, что вышло при загрузке.</summary>
        public string Note
        {
            get { return _note; }
            set
            {
                if (_note == value)
                    return;

                _note = value ?? string.Empty;
                Raise(nameof(Note));
                Raise(nameof(Status));
            }
        }

        public string Name => Entry.Name;

        /// <summary>Подпись столбца «Откуда».</summary>
        public string Kind
        {
            get
            {
                switch (Entry.Origin)
                {
                    case LinkOrigin.Server:
                        return "Revit Server";
                    case LinkOrigin.Cloud:
                        return "BIM360";
                    default:
                        return "Файл";
                }
            }
        }

        /// <summary>Где лежит модель: папка файла, путь на сервере или регион и GUID проекта.</summary>
        public string Location
        {
            get
            {
                switch (Entry.Origin)
                {
                    case LinkOrigin.Cloud:
                        return Entry.Region + " · проект " + Entry.ProjectGuid;

                    case LinkOrigin.Server:
                        return Entry.Path;

                    default:
                        return System.IO.Path.GetDirectoryName(Entry.Path) ?? Entry.Path;
                }
            }
        }

        /// <summary>Подпись столбца «Наборы»: сколько их в модели, если уже считали.</summary>
        public string Worksets
        {
            get
            {
                if (WorksetNames == null)
                    return string.Empty;

                return WorksetNames.Count == 0 ? "нет" : WorksetNames.Count.ToString();
            }
        }

        /// <summary>Подпись столбца «Состояние».</summary>
        public string Status
        {
            get
            {
                if (Note.Length > 0)
                    return Note;

                return IsExisting ? "Уже в проекте" : "Новая";
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
