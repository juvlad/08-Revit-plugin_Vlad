using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// A row of the "Parameters" table: one parameter of the ParaManager file, as words the window can show —
    /// the resolving into Revit's ids stays with the command.
    /// </summary>
    internal sealed class BatchParameterRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public BatchParameterRow(
            Guid guid,
            int row,
            string name,
            string type,
            bool isInstance,
            string group,
            IReadOnlyList<string> categories,
            bool varies,
            string state,
            bool canSelect,
            bool hasNote)
        {
            Guid = guid;
            Row = row;
            Name = name ?? string.Empty;
            TypeText = type ?? string.Empty;
            BindingText = isInstance ? "Instance" : "Type";
            GroupText = group ?? string.Empty;
            Categories = categories ?? new List<string>();
            VaryText = isInstance ? (varies ? "Can vary" : "Aligned") : "—";
            State = state ?? string.Empty;
            CanSelect = canSelect;
            HasNote = hasNote;
            _isSelected = canSelect;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public Guid Guid { get; }

        /// <summary>The Excel row, from 1 — how the user finds the line in the file.</summary>
        public int Row { get; }

        public string Name { get; }

        public string TypeText { get; }

        public string BindingText { get; }

        public string GroupText { get; }

        public IReadOnlyList<string> Categories { get; }

        public string CategoriesText => string.Join(", ", Categories);

        /// <summary>"Values can vary by group instance" in a word; a dash for a type parameter, where it means nothing.</summary>
        public string VaryText { get; }

        /// <summary>Why the row cannot be added, what is worth knowing about it, or that it is ready.</summary>
        public string State { get; }

        /// <summary>The row can go into the models at all — a row that cannot has no check box to tick.</summary>
        public bool CanSelect { get; }

        public bool HasNote { get; }

        public Brush StateBrush => !CanSelect ? Brushes.Firebrick : HasNote ? Brushes.DarkGoldenrod : Brushes.DarkGreen;

        /// <summary>The "add this parameter" check box; never on for a row that cannot be added.</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                var effective = value && CanSelect;
                if (_isSelected == effective)
                    return;

                _isSelected = effective;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    /// <summary>What the window is shown of one ParaManager file — the rows, and what is said about the file as a whole.</summary>
    internal sealed class BatchParameterFileView
    {
        public BatchParameterFileView(string path)
        {
            Path = path ?? string.Empty;
        }

        public string Path { get; }

        /// <summary>Why the file gave no parameters; empty when it was read.</summary>
        public string Error { get; set; } = string.Empty;

        public List<BatchParameterRow> Rows { get; } = new List<BatchParameterRow>();

        public List<string> Notes { get; } = new List<string>();
    }
}
