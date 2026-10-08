using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>How the lines of the file are paired with the rows of the schedule.</summary>
    internal enum ExcelMatchMode
    {
        /// <summary>By the elements each line was exported with — only a file this add-in exported has them.</summary>
        ElementIds,

        /// <summary>By the value of one column that tells the rows apart: a mark, a room number, a sheet number.</summary>
        KeyColumn,

        /// <summary>The first line of data to the first row, the second to the second — the way ModPlus does it.</summary>
        RowOrder
    }

    /// <summary>
    /// What the import window asks for. Anything left at its "work it out" value (0, -1, null) is
    /// decided by the planner, and the preview hands back what it decided — the window then shows it.
    /// </summary>
    internal sealed class ExcelImportRequest
    {
        public string SheetName { get; set; } = string.Empty;

        public string ScheduleName { get; set; } = string.Empty;

        /// <summary>The first line of data, as Excel numbers rows; 0 = find the headings and start below them.</summary>
        public int FirstDataRow { get; set; }

        /// <summary>Null = by element where the file has them, by row order otherwise.</summary>
        public ExcelMatchMode? MatchMode { get; set; }

        /// <summary>The schedule column whose values pair the rows, for <see cref="ExcelMatchMode.KeyColumn"/>; -1 = pick one.</summary>
        public int KeyColumn { get; set; } = -1;

        /// <summary>For every schedule column, the file column feeding it (-1 = not imported); null = match by what the columns show and by heading.</summary>
        public int[] ColumnMap { get; set; }

        /// <summary>An empty cell in the file clears the value in Revit, rather than leaving it as it is.</summary>
        public bool EmptyClears { get; set; }

        public ExcelImportRequest Copy()
        {
            var copy = (ExcelImportRequest)MemberwiseClone();
            copy.ColumnMap = ColumnMap == null ? null : (int[])ColumnMap.Clone();
            return copy;
        }
    }

    /// <summary>What a cell of the preview is: the colour and the tooltip follow from it.</summary>
    internal enum ExcelCellState
    {
        /// <summary>The file and the schedule agree, or the column is not imported.</summary>
        Same,

        /// <summary>The file holds a new value, and it will be written.</summary>
        Changed,

        /// <summary>The file's cell is empty, and "Empty cells clear values" is on.</summary>
        Cleared,

        /// <summary>The file holds a new value that cannot be written — the tooltip says why.</summary>
        Blocked,

        /// <summary>The file's cell is empty and is left alone ("Empty cells clear values" is off).</summary>
        Kept,

        /// <summary>The column the rows are paired by; never written.</summary>
        Key,

        /// <summary>A line of the file with no schedule row: the file's text, for reference only.</summary>
        Absent
    }

    internal sealed class ExcelPreviewCell
    {
        // Excel's own "Neutral" and "Bad" cell colours — a spreadsheet user reads them without a legend.
        public static readonly Brush ChangedBrush = Frozen(Color.FromRgb(0xFF, 0xEB, 0x9C));
        public static readonly Brush BlockedBrush = Frozen(Color.FromRgb(0xFF, 0xC7, 0xCE));
        public static readonly Brush KeyBrush = Frozen(Color.FromRgb(0xDD, 0xEB, 0xF7));

        public ExcelPreviewCell(string text, ExcelCellState state, string tooltip)
        {
            Text = text ?? string.Empty;
            State = state;
            Tooltip = string.IsNullOrEmpty(tooltip) ? null : tooltip;
        }

        public string Text { get; }

        public ExcelCellState State { get; }

        public string Tooltip { get; }

        public Brush Background
        {
            get
            {
                switch (State)
                {
                    case ExcelCellState.Changed:
                    case ExcelCellState.Cleared:
                        return ChangedBrush;
                    case ExcelCellState.Blocked:
                        return BlockedBrush;
                    case ExcelCellState.Key:
                        return KeyBrush;
                    default:
                        return Brushes.Transparent;
                }
            }
        }

        public Brush Foreground => State == ExcelCellState.Absent || State == ExcelCellState.Kept
            ? SystemColors.GrayTextBrush
            : SystemColors.ControlTextBrush;

        public FontStyle FontStyle => State == ExcelCellState.Cleared ? FontStyles.Italic : FontStyles.Normal;

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>One schedule column in the preview, and which column of the file feeds it.</summary>
    internal sealed class ExcelPreviewColumn
    {
        public ExcelPreviewColumn(string heading, int sourceColumn, string readOnlyReason, bool isKey)
        {
            Heading = heading ?? string.Empty;
            SourceColumn = sourceColumn;
            ReadOnlyReason = readOnlyReason;
            IsKey = isKey;
        }

        public string Heading { get; }

        /// <summary>The file column feeding it, zero-based; -1 when nothing does.</summary>
        public int SourceColumn { get; }

        /// <summary>Why the column is never written; null for a parameter.</summary>
        public string ReadOnlyReason { get; }

        /// <summary>The column the rows are paired by.</summary>
        public bool IsKey { get; }
    }

    /// <summary>One column of the file, as offered in a heading's drop-down.</summary>
    internal sealed class ExcelSourceColumn
    {
        public ExcelSourceColumn(int index, string caption)
        {
            Index = index;
            Caption = caption;
        }

        /// <summary>Zero-based; -1 for "not imported".</summary>
        public int Index { get; }

        public string Caption { get; }

        public override string ToString()
        {
            return Caption;
        }
    }

    internal enum ExcelRowKind
    {
        /// <summary>A schedule row paired with a line of the file.</summary>
        Matched,

        /// <summary>A schedule row no line of the file was paired with.</summary>
        ScheduleOnly,

        /// <summary>A line of the file no schedule row was paired with.</summary>
        FileOnly
    }

    /// <summary>
    /// One line of the preview: a schedule row and the line of the file paired with it, cell by cell.
    /// The check box is the only thing the window changes; everything else is the planner's verdict.
    /// </summary>
    internal sealed class ExcelPreviewRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ExcelPreviewRow(
            ExcelRowKind kind,
            int scheduleRow,
            int fileRow,
            int elementCount,
            IReadOnlyList<ExcelPreviewCell> cells,
            int changeCount,
            int blockedCount,
            string statusText)
        {
            Kind = kind;
            ScheduleRow = scheduleRow;
            FileRow = fileRow;
            ElementCount = elementCount;
            Cells = cells;
            ChangeCount = changeCount;
            BlockedCount = blockedCount;
            StatusText = statusText ?? string.Empty;
            _isSelected = CanApply;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ExcelRowKind Kind { get; }

        /// <summary>The schedule row, counted from 1 among element rows; 0 for a line of the file only.</summary>
        public int ScheduleRow { get; }

        /// <summary>The line of the file as Excel numbers it; 0 for a schedule row only.</summary>
        public int FileRow { get; }

        /// <summary>How many elements the schedule row stands for; 0 when it is not tied to any.</summary>
        public int ElementCount { get; }

        public IReadOnlyList<ExcelPreviewCell> Cells { get; }

        /// <summary>Values that will be written if the row is checked.</summary>
        public int ChangeCount { get; }

        /// <summary>New values in the file that cannot be written.</summary>
        public int BlockedCount { get; }

        public string StatusText { get; }

        public bool CanApply => ChangeCount > 0;

        public bool HasChanges => ChangeCount > 0 || BlockedCount > 0;

        public bool IsProblem => Kind == ExcelRowKind.FileOnly || BlockedCount > 0;

        public string ScheduleRowText => ScheduleRow > 0 ? ScheduleRow.ToString() : "—";

        public string FileRowText => FileRow > 0 ? FileRow.ToString() : "—";

        public string ElementCountText => Kind == ExcelRowKind.FileOnly ? string.Empty : ElementCount > 0 ? ElementCount.ToString() : "—";

        public Brush StatusBrush => IsProblem ? Brushes.Firebrick : SystemColors.GrayTextBrush;

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                var next = value && CanApply;
                if (_isSelected == next)
                    return;

                _isSelected = next;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    /// <summary>Everything the import window shows for one choice of sheet, schedule and matching.</summary>
    internal sealed class ExcelImportPreview
    {
        public ExcelImportPreview(
            ExcelImportRequest request,
            IReadOnlyList<ExcelPreviewColumn> columns,
            IReadOnlyList<ExcelSourceColumn> sourceColumns,
            IReadOnlyList<int> keyCandidates,
            IReadOnlyList<ExcelPreviewRow> rows,
            IReadOnlyList<string> notes,
            bool fileHasIds,
            string error)
        {
            Request = request;
            Columns = columns ?? new List<ExcelPreviewColumn>();
            SourceColumns = sourceColumns ?? new List<ExcelSourceColumn>();
            KeyCandidates = keyCandidates ?? new List<int>();
            Rows = rows ?? new List<ExcelPreviewRow>();
            Notes = notes ?? new List<string>();
            FileHasIds = fileHasIds;
            Error = error;
        }

        /// <summary>The request with every "work it out" value worked out.</summary>
        public ExcelImportRequest Request { get; }

        public IReadOnlyList<ExcelPreviewColumn> Columns { get; }

        public IReadOnlyList<ExcelSourceColumn> SourceColumns { get; }

        /// <summary>Schedule columns whose values tell every row apart — what "By a key column" may use.</summary>
        public IReadOnlyList<int> KeyCandidates { get; }

        public IReadOnlyList<ExcelPreviewRow> Rows { get; }

        /// <summary>Things worth knowing before importing, in sentences.</summary>
        public IReadOnlyList<string> Notes { get; }

        public bool FileHasIds { get; }

        /// <summary>Why there is no preview at all; null when there is one.</summary>
        public string Error { get; }

        public static ExcelImportPreview Failed(ExcelImportRequest request, string error)
        {
            return new ExcelImportPreview(request, null, null, null, null, null, false, error);
        }
    }
}
