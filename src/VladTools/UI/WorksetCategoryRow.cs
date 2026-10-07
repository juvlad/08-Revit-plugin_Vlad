using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// A row of the "Move to Workset" table: the elements of one category in one workset, and the
    /// check box that takes them along.
    ///
    /// The check box means "these elements" rather than "move these": it feeds both "Move" and
    /// "Select in model", so it is never disabled — a row with nothing movable in it (all inside
    /// groups, already in the target) is still worth selecting to look at. What each checked row
    /// will actually do is written in <see cref="StatusText"/>, filled in by the window, which alone
    /// knows the chosen target — the same rule as in <c>ProjectWorksetRow</c>.
    /// </summary>
    internal sealed class WorksetCategoryRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _statusText = string.Empty;
        private bool _isWarning;

        public WorksetCategoryRow(WorksetCategoryInfo info)
        {
            Info = info;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public WorksetCategoryInfo Info { get; }

        public string Workset => Info.WorksetName;

        public string Kind => Info.KindCaption;

        public string Category => Info.Category;

        /// <summary>The "Elements" column — every element of the row, movable or not.</summary>
        public int Elements => Info.Total;

        public bool IsSelected
        {
            get { return _isSelected; }
            set { Set(ref _isSelected, value, nameof(IsSelected)); }
        }

        /// <summary>What happens to the row's elements, in words — the "State" column.</summary>
        public string StatusText
        {
            get { return _statusText; }
            set { Set(ref _statusText, value ?? string.Empty, nameof(StatusText)); }
        }

        /// <summary>Something in the row is wrong or will not be carried out — the state is coloured, not merely written.</summary>
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
