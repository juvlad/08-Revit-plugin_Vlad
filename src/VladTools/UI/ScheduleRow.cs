using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// A row of the "Schedule Library" table: one schedule out of the set, its check box, and — when
    /// the project already holds a schedule of that name — what to do about it.
    ///
    /// The row knows nothing of its own fate: <see cref="ResultName"/> and <see cref="StatusText"/>
    /// are filled in by the window, which alone can see the other rows and the project's names. The
    /// same rule as in <c>DimensionChainRow</c>: validation belongs to the window, so a row cannot
    /// quietly declare itself valid.
    /// </summary>
    internal sealed class ScheduleRow : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private ScheduleAction _action;
        private string _resultName = string.Empty;
        private string _statusText = string.Empty;
        private bool _isWarning;

        public ScheduleRow(ScheduleInfo info, int sheetCount, bool hasConflict, ScheduleAction action)
        {
            Info = info;
            SheetCount = sheetCount;
            HasConflict = hasConflict;
            _action = action;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ScheduleInfo Info { get; }

        public string Name => Info.Name;

        public string Category => Info.Category;

        public string Kind => Info.Kind;

        public int FieldCount => Info.FieldCount;

        /// <summary>A schedule of this name is already in the open project.</summary>
        public bool HasConflict { get; }

        /// <summary>How many sheets the project's own schedule of this name sits on; 0 when there is none.</summary>
        public int SheetCount { get; }

        /// <summary>The "insert this one" check box.</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set { Set(ref _isSelected, value, nameof(IsSelected)); }
        }

        /// <summary>
        /// What to do about the name clash. Meaningless without <see cref="HasConflict"/>, and the
        /// drop-down in that cell is disabled — there is nothing to decide.
        /// </summary>
        public ScheduleAction Action
        {
            get { return _action; }
            set { Set(ref _action, value, nameof(Action)); }
        }

        /// <summary>What the schedule will be called in the project once inserted.</summary>
        public string ResultName
        {
            get { return _resultName; }
            set { Set(ref _resultName, value ?? string.Empty, nameof(ResultName)); }
        }

        /// <summary>What will happen to this row, in words — the "State" column.</summary>
        public string StatusText
        {
            get { return _statusText; }
            set { Set(ref _statusText, value ?? string.Empty, nameof(StatusText)); }
        }

        /// <summary>Something in the project will be lost by this row — the state is coloured, not merely written.</summary>
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
