using System.ComponentModel;
using Autodesk.Revit.DB;

namespace VladTools.UI
{
    /// <summary>
    /// A table row in the "Delete Project Shared Parameters" window: the check box, what the user
    /// sees, plus the id of the parameter element the command will delete from the document.
    /// The id is stored rather than the element: once deleted the element becomes invalid, whereas
    /// the id can still be used to ask the document whether the parameter is still alive.
    /// The check box is set both by hand and by the rule, so the row reports its changes.
    /// </summary>
    internal sealed class ProjectParameterRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _usedInDimensions;

        public ProjectParameterRow(
            ElementId id,
            string name,
            string guid,
            string binding,
            string group,
            string categories,
            bool isBound)
        {
            Id = id;
            Name = name;
            Guid = guid;
            Binding = binding;
            Group = group;
            Categories = categories;
            IsBound = isBound;
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
                Raise(nameof(IsSelected));
            }
        }

        /// <summary>
        /// The parameter labels a dimension in at least one loaded family.
        /// It is not filled in straight away: finding out requires opening every family,
        /// so the scan is started by a button in the window.
        /// </summary>
        public bool UsedInDimensions
        {
            get { return _usedInDimensions; }
            set
            {
                if (_usedInDimensions == value)
                    return;

                _usedInDimensions = value;
                Raise(nameof(UsedInDimensions));
                Raise(nameof(DimensionUse));
            }
        }

        /// <summary>The caption for the "Dimensions" column: empty for parameters that are not involved.</summary>
        public string DimensionUse => UsedInDimensions ? "Dimension label" : string.Empty;

        /// <summary>The id of the shared parameter element in the project document.</summary>
        public ElementId Id { get; }

        public string Name { get; }

        public string Guid { get; }

        /// <summary>"Instance", "Type" or "Not bound".</summary>
        public string Binding { get; }

        /// <summary>The parameter group as it is labelled in the Revit interface.</summary>
        public string Group { get; }

        /// <summary>The categories the parameter is bound to; empty when it is not bound.</summary>
        public string Categories { get; }

        /// <summary>
        /// The parameter is bound to categories, that is, it is visible in "Manage → Project Parameters".
        /// An unbound one is left over from loaded families or from a binding that was removed.
        /// </summary>
        public bool IsBound { get; }

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
