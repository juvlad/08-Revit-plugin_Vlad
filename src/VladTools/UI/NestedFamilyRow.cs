using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.DB;

namespace VladTools.UI
{
    /// <summary>What exactly stands behind the row: a nested family or one of its types.</summary>
    internal enum NestedKind
    {
        Family,
        Symbol
    }

    /// <summary>The result of validating the new name — the text and colour of the "Status" column depend on it.</summary>
    internal enum RenameStatus
    {
        /// <summary>The new name matches the old one: the row stays as it is.</summary>
        Unchanged,

        /// <summary>The name is valid and differs from the current one — it will be written.</summary>
        Ready,

        /// <summary>The name is empty, contains forbidden characters, or is already taken by another object.</summary>
        Error
    }

    /// <summary>
    /// A table row in the "Rename Nested" window: the check box, the current name, the new name and the validation.
    /// It holds the Revit element itself — the command needs something to assign the new name to.
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

        /// <summary>The Revit element the new name will be assigned to.</summary>
        public Element Element { get; }

        public NestedKind Kind { get; }

        public string KindText
        {
            get { return Kind == NestedKind.Family ? "Family" : "Type"; }
        }

        /// <summary>The name in the document as it stands now.</summary>
        public string CurrentName { get; }

        /// <summary>For a type — the name of the family it belongs to.</summary>
        public string OwnerName { get; }

        /// <summary>How many instances of this family are placed in the open family.</summary>
        public int Instances { get; }

        public string InstancesText
        {
            get { return Instances > 0 ? Instances.ToString() : "—"; }
        }

        /// <summary>The "rename this row" check box.</summary>
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
        /// The name that will result from the rename. The rule fills it in by itself, but the cell is
        /// editable — a hand edit outranks the rule and is never overwritten by it.
        /// </summary>
        public string NewName
        {
            get { return _newName; }
            set
            {
                var text = value ?? string.Empty;

                // Only the cell editor writes here: so the row was edited by hand.
                IsManual = true;

                if (_newName == text)
                    return;

                _newName = text;
                Raise(nameof(NewName));
            }
        }

        /// <summary>The name in this row was set by hand rather than by the rule.</summary>
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

        /// <summary>A row that will actually be renamed.</summary>
        public bool WillRename
        {
            get { return _isSelected && _status == RenameStatus.Ready; }
        }

        /// <summary>Filling the name in from the rule: unlike NewName it does not mark the row as hand-edited.</summary>
        public void SetPreview(string name)
        {
            var text = name ?? string.Empty;
            if (_newName == text)
                return;

            _newName = text;
            Raise(nameof(NewName));
        }

        /// <summary>Returns the row to its original name and clears the hand-edited flag.</summary>
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
