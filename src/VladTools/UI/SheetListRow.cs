using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Lays the sheets' folder paths out as folder lines, every folder counting all its sheets. The
        /// sheets arrive sorted by their paths, so a folder's sheets are consecutive, and a new folder
        /// line is needed only where a sheet's path parts from the one before it. Shared by the window
        /// and by a batch run, whose reports are written straight from these lines.
        /// </summary>
        public static List<SheetListRow> Tree(IReadOnlyList<SheetInfo> sheets)
        {
            var rows = new List<SheetListRow>();
            var open = new List<SheetListRow>();

            foreach (var sheet in sheets)
            {
                var path = sheet.Folders;

                var common = 0;
                while (common < open.Count && common < path.Count && string.Equals(open[common].FolderName, path[common], StringComparison.Ordinal))
                    common++;

                open.RemoveRange(common, open.Count - common);

                for (var level = common; level < path.Count; level++)
                {
                    var folder = Folder(level == 0 ? null : open[level - 1], level, path[level]);
                    open.Add(folder);
                    rows.Add(folder);
                }

                foreach (var folder in open)
                    folder._shownSheets++;

                rows.Add(ForSheet(open.Count == 0 ? null : open[open.Count - 1], path.Count, sheet));
            }

            return rows;
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

        public string ChangedText => IsFolder ? string.Empty : Sheet.ChangedText;

        /// <summary>The tooltip of the date; null — no tooltip — for a folder line.</summary>
        public string ChangeNote => IsFolder || Sheet.ChangeNote.Length == 0 ? null : Sheet.ChangeNote;

        /// <summary>Bold for a date this very check found: what changed since the previous one stands out.</summary>
        public FontWeight ChangedWeight => !IsFolder && Sheet.ChangedAtThisCheck ? FontWeights.SemiBold : FontWeights.Normal;

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
