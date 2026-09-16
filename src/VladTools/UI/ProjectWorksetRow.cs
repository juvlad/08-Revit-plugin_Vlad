using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// A row of the workset table in the "Worksets" window: one user workset of the open project and
    /// the check box that marks it for removal.
    ///
    /// Not to be confused with <see cref="WorksetRow"/> — that one is a workset *name* shared across
    /// several links in "Link Manager". Here the row is a workset of the open project itself, with an
    /// identity, a size and an owner; the naming follows the same split as
    /// <c>ProjectParameterRow</c> against <c>SharedParameterRow</c>.
    ///
    /// The row never judges itself: <see cref="StatusText"/> and <see cref="CanRemove"/> are filled
    /// in by the window, which alone sees the other rows and the chosen destination — the same rule
    /// as in <c>ScheduleRow</c>.
    /// </summary>
    internal sealed class ProjectWorksetRow : INotifyPropertyChanged
    {
        // Unchecked from the start, and that is the rule rather than a preference: wherever a button
        // deletes, an empty filter must not mean "select everything" (see CLAUDE.md, "Conventions").
        private bool _isSelected;
        private bool _canRemove = true;
        private string _statusText = string.Empty;
        private bool _isWarning;

        public ProjectWorksetRow(WorksetInfo info)
        {
            Info = info;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public WorksetInfo Info { get; }

        public string Name => Info.Name;

        /// <summary>The "Elements" column: a count, or an honest admission that there is none.</summary>
        public string Contents
        {
            get
            {
                if (!Info.IsCounted)
                    return "not counted";

                return Info.ElementCount == 0 ? "empty" : Info.ElementCount.ToString();
            }
        }

        /// <summary>The "Remove" check box.</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set { Set(ref _isSelected, value, nameof(IsSelected)); }
        }

        /// <summary>
        /// The row can be marked at all. A workset owned by somebody else will never delete, and
        /// offering its check box would only produce a failure line in the report later.
        /// </summary>
        public bool CanRemove
        {
            get { return _canRemove; }
            set { Set(ref _canRemove, value, nameof(CanRemove)); }
        }

        /// <summary>What will happen to this workset, in words — the "State" column.</summary>
        public string StatusText
        {
            get { return _statusText; }
            set { Set(ref _statusText, value ?? string.Empty, nameof(StatusText)); }
        }

        /// <summary>Something will be lost by this row, or it cannot be carried out — the state is coloured, not merely written.</summary>
        public bool IsWarning
        {
            get { return _isWarning; }
            set
            {
                if (Set(ref _isWarning, value, nameof(IsWarning)))
                    Raise(nameof(StatusBrush));
            }
        }

        public Brush StatusBrush => IsWarning ? Brushes.Firebrick : SystemColors.GrayTextBrush;

        private bool Set<T>(ref T field, T value, string property)
        {
            if (Equals(field, value))
                return false;

            field = value;
            Raise(property);
            return true;
        }

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
