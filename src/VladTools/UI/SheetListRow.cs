using System.ComponentModel;
using System.Windows;

namespace VladTools.UI
{
    /// <summary>
    /// A line of the "Sheets" table: either a Project Browser folder or a sheet inside one. The table
    /// is the browser's tree laid flat — a folder line, then everything under it one step deeper —
    /// rather than WPF's own grouped collection view: the export writes the table to Excel line for
    /// line, and a grouped view would have to be laid flat a second time for that anyway.
    /// </summary>
    internal sealed class SheetListRow : INotifyPropertyChanged
    {
        public const string NothingPlaced = "(nothing placed)";

        private const double IndentStep = 16;

        private bool _isExpanded = true;
        private int _shownSheets;

        private SheetListRow(SheetListRow parent, int level, string folderName, SheetInfo sheet)
        {
            Parent = parent;
            Level = level;
            FolderName = folderName ?? string.Empty;
            Sheet = sheet;
        }

        public static SheetListRow Folder(SheetListRow parent, int level, string name)
        {
            return new SheetListRow(parent, level, name, null);
        }

        public static SheetListRow ForSheet(SheetListRow parent, int level, SheetInfo sheet)
        {
            return new SheetListRow(parent, level, null, sheet);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The folder this line sits in; null at the top.</summary>
        public SheetListRow Parent { get; }

        /// <summary>The depth in the tree: 0 for a top folder, and for a sheet of an organization with no folders.</summary>
        public int Level { get; }

        public string FolderName { get; }

        /// <summary>Null for a folder line.</summary>
        public SheetInfo Sheet { get; }

        public bool IsFolder => Sheet == null;

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                if (_isExpanded == value)
                    return;

                _isExpanded = value;
                Notify(nameof(IsExpanded));
                Notify(nameof(SheetText));
            }
        }

        /// <summary>How many sheets under this folder the search leaves in the table — counted by the window.</summary>
        public int ShownSheets
        {
            get { return _shownSheets; }
            set
            {
                if (_shownSheets == value)
                    return;

                _shownSheets = value;
                Notify(nameof(ShownSheets));
                Notify(nameof(ContentsText));
            }
        }

        public string SheetText => IsFolder ? (IsExpanded ? "▾  " : "▸  ") + FolderName : Sheet.Title;

        public string ContentsText => IsFolder
            ? Count(ShownSheets)
            : Sheet.Contents.Count == 0 ? NothingPlaced : string.Join("\n", Sheet.Contents);

        public Thickness Indent => new Thickness(4 + Level * IndentStep, 3, 4, 3);

        public FontWeight Weight => IsFolder ? FontWeights.SemiBold : FontWeights.Normal;

        /// <summary>Drawn grey: a folder's sheet count, a sheet with nothing on it.</summary>
        public bool IsMuted => IsFolder || Sheet.Contents.Count == 0;

        public static string Count(int sheets)
        {
            return sheets == 1 ? "1 sheet" : sheets + " sheets";
        }

        private void Notify(string property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
