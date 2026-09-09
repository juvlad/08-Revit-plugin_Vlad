using System.ComponentModel;
using Autodesk.Revit.DB;

namespace VladTools.UI
{
    /// <summary>
    /// A table row in the "Delete Parameters" window: the check box, what the user sees, plus a
    /// reference to the family parameter itself so it can be deleted.
    /// The check box is set both by hand and by the rule, so the row reports its changes.
    /// </summary>
    internal sealed class SharedParameterRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public SharedParameterRow(
            FamilyParameter parameter,
            string name,
            string guid,
            string binding,
            string group,
            bool usedInDimensions)
        {
            Parameter = parameter;
            Name = name;
            Guid = guid;
            Binding = binding;
            Group = group;
            UsedInDimensions = usedInDimensions;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The "delete this parameter" check box.</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;

                var handler = PropertyChanged;
                if (handler != null)
                    handler(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        /// <summary>The family parameter behind the row.</summary>
        public FamilyParameter Parameter { get; }

        public string Name { get; }

        public string Guid { get; }

        /// <summary>"Instance" or "Type".</summary>
        public string Binding { get; }

        /// <summary>The parameter group as it is labelled in the Revit interface.</summary>
        public string Group { get; }

        /// <summary>
        /// The parameter labels a dimension. Deleting such a parameter drops the label and breaks
        /// the family parametrics, so the window hides those rows by default.
        /// </summary>
        public bool UsedInDimensions { get; }

        /// <summary>The caption for the "Dimensions" column: empty for parameters that are not involved.</summary>
        public string DimensionUse => UsedInDimensions ? "Dimension label" : string.Empty;
    }
}
