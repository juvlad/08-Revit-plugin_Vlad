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
        private bool _removalAllowed;

        public CoordinationChangeRow(
            CoordinationChangeKind kind,
            bool isLevel,
            string name,
            string detail,
            string note,
            DatumUpdate update,
            int dependents = 0)
        {
            Kind = kind;
            IsLevel = isLevel;
            Name = name;
            Detail = detail;
            Note = note;
            Update = update;
            Dependents = dependents;
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

        /// <summary>
        /// The command can apply this; the other rows have their check box disabled.
        ///
        /// A removal is applicable only while the window's "Delete…" box is on: deleting a level
        /// takes everything standing on it with it, and that must be switched on deliberately
        /// rather than arrived at by clicking "select all".
        /// </summary>
        public bool CanApply =>
            CoordinationChangeKinds.CanApply(Kind) && (!IsRemoval || _removalAllowed);

        /// <summary>Applying this row deletes the project element — see <see cref="CoordinationChangeKinds.IsRemoval"/>.</summary>
        public bool IsRemoval => CoordinationChangeKinds.IsRemoval(Kind);

        /// <summary>
        /// Whether the window currently permits deletions. Set on every row at once when the box is
        /// toggled; on a row that is not a removal it changes nothing.
        /// </summary>
        public bool RemovalAllowed
        {
            get { return _removalAllowed; }
            set
            {
                if (_removalAllowed == value)
                    return;

                _removalAllowed = value;
                Raise(nameof(RemovalAllowed));
                Raise(nameof(CanApply));
            }
        }

        /// <summary>
        /// How many other elements Revit would delete together with this one — its own count of
        /// what is standing on the level. Read while scanning, because the whole point of showing it
        /// is that the decision is made **before** the deletion, not explained after it.
        /// </summary>
        public int Dependents { get; }

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
