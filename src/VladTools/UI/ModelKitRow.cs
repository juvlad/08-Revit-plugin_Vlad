using System.ComponentModel;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// A row of the "Building Kit" window table: one discipline model that was found.
    ///
    /// It knows nothing about Revit — like every window in the add-in: everything needed is already
    /// inside <see cref="LinkEntry"/>, and that is the only thing that leaves here.
    /// </summary>
    internal sealed class ModelKitRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ModelKitRow(ModelKitHit hit, bool isSelected, string status)
        {
            Hit = hit;
            _isSelected = isSelected;
            Status = status ?? string.Empty;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ModelKitHit Hit { get; }

        public LinkEntry Entry => Hit.Entry;

        /// <summary>The "add this model to the link list" check box.</summary>
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

        public string Discipline => Hit.Discipline;

        public string Name => Hit.Entry.Name;

        /// <summary>The folder the model was found in — it is what shows the guess did not miss.</summary>
        public string Folder => Hit.Folder;

        /// <summary>Why the row is not checked: several models in the discipline, model already listed.</summary>
        public string Status { get; }
    }
}
