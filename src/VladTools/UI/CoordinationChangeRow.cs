using System.ComponentModel;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// A table row in the "Accept Changes" window: one difference between the project and the
    /// coordination file.
    ///
    /// Besides what the user sees, the row carries a ready-made edit (<see cref="Update"/>) — just as
    /// <c>SharedParameterRow</c> carries a <c>FamilyParameter</c>. The window never looks inside it:
    /// <c>CoordinationCatalog</c> computes the edit, the command applies it.
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

        /// <summary>The "accept this change" check box.</summary>
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

        /// <summary>The command can apply this; the other rows have their check box disabled.</summary>
        public bool CanApply => CoordinationChangeKinds.CanApply(Kind);

        /// <summary>A level or a grid — both the caption and the way it is edited depend on this.</summary>
        public bool IsLevel { get; }

        /// <summary>"Level" or "Grid".</summary>
        public string Type => IsLevel ? "Level" : "Grid";

        /// <summary>The element name in the project; for a new link element — its name in the link.</summary>
        public string Name { get; }

        /// <summary>What exactly differs — the caption of the change kind.</summary>
        public string What => CoordinationChangeKinds.Label(Kind);

        /// <summary>Before and after: "shift 150 mm", "+3000 → +3150", "\"1\" → \"1a\"".</summary>
        public string Detail { get; }

        /// <summary>Why the row is not applied, or what to check by eye.</summary>
        public string Note { get; }

        /// <summary>How to name the element in the report: "Grid \"1\"".</summary>
        public string Title => Type + " \"" + Name + "\"";

        /// <summary>The ready-made edit; null on rows that cannot be applied.</summary>
        public DatumUpdate Update { get; }

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
