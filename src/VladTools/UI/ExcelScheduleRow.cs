using System.ComponentModel;

namespace VladTools.UI
{
    /// <summary>A row of the "Export to Excel" list: one schedule of the project and the check box that sends it.</summary>
    internal sealed class ExcelScheduleRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ExcelScheduleRow(ScheduleInfo info, bool isSelected)
        {
            Info = info;
            _isSelected = isSelected;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ScheduleInfo Info { get; }

        public string Name => Info.Name;

        public string Kind => Info.Kind;

        public string Category => Info.Category;

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
