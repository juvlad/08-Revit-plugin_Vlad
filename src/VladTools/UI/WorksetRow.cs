using System.ComponentModel;

namespace VladTools.UI
{
    /// <summary>
    /// A row of the workset list in the "Link Manager" window: the workset name and a check box that
    /// applies to every selected link at once.
    ///
    /// A workset here is a name, not the workset of a particular model: every model has its own
    /// workset ids, and the only thing "00_Shared levels and grids" has in common across all links
    /// is the name. That is why <see cref="LinkCount"/> exists: it shows how many of the selected
    /// models contain such a workset at all.
    /// </summary>
    internal sealed class WorksetRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private int _linkCount;
        private bool _isCounted;

        public WorksetRow(string name, bool isRemembered)
        {
            Name = name ?? string.Empty;
            IsRemembered = isRemembered;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string Name { get; }

        /// <summary>
        /// The name came from the saved settings rather than from a model that was read.
        /// Such a name may not occur in these links at all — that is not an error.
        /// </summary>
        public bool IsRemembered { get; }

        /// <summary>The check box: act on this workset in every selected link.</summary>
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

        /// <summary>In how many of the selected models the workset was found; 0 if the worksets were never read.</summary>
        public int LinkCount
        {
            get { return _linkCount; }
            set
            {
                if (_linkCount == value)
                    return;

                _linkCount = value;
                Raise(nameof(LinkCount));
                Raise(nameof(Where));
            }
        }

        /// <summary>
        /// The worksets of the checked links have already been read, so <see cref="LinkCount"/> is a
        /// count rather than "not looked at yet". Without this flag zero meant both.
        /// </summary>
        public bool IsCounted
        {
            get { return _isCounted; }
            set
            {
                if (_isCounted == value)
                    return;

                _isCounted = value;
                Raise(nameof(IsCounted));
                Raise(nameof(Where));
            }
        }

        /// <summary>The caption of the "Found in" column.</summary>
        public string Where
        {
            get
            {
                if (LinkCount > 0)
                    return "in " + LinkCount + " links";

                // Read and not found is nothing like "not read": a name that occurs in no link has
                // nothing to close, and that is the most common reason behind "the workset did not
                // close". The user has to see such a row.
                if (IsCounted)
                    return "in none of them";

                return IsRemembered ? "from last time" : string.Empty;
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
