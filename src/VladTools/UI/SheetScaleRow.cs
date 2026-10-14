using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// A line of the "Sheet Scale" table: a Project Browser folder, a sheet, or a view standing on that
    /// sheet — the browser's tree laid flat, with the sheets as one more level of folders
    /// (<see cref="FlatTree"/>).
    ///
    /// A folder's or a sheet's check box stands for the views under it, so its value is worked out by
    /// the window from theirs (checked, cleared, or partly — <c>null</c>) and a click on it is handed
    /// to the window through <see cref="Toggled"/>. Whether a view can be checked depends on the
    /// window's settings — the scale to change from, what to do with templates — so
    /// <see cref="IsCheckable"/> is set by the window, not worked out here.
    /// </summary>
    internal sealed class SheetScaleRow : INotifyPropertyChanged
    {
        private const double IndentStep = 16;

        private bool? _isChecked = false;
        private bool _isCheckable;
        private bool _isExpanded = true;
        private string _newScaleText = string.Empty;
        private string _statusText = string.Empty;
        private bool _isWarning;

        private SheetScaleRow(SheetScaleRow parent, int level, string folderName, ScaleSheetInfo sheet, ScaleViewportInfo viewport)
        {
            Parent = parent;
            Level = level;
            FolderName = folderName ?? string.Empty;
            Sheet = sheet;
            Viewport = viewport;
        }

        public static SheetScaleRow Folder(SheetScaleRow parent, int level, string name)
        {
            return new SheetScaleRow(parent, level, name, null, null);
        }

        public static SheetScaleRow ForSheet(SheetScaleRow parent, int level, ScaleSheetInfo sheet)
        {
            return new SheetScaleRow(parent, level, null, sheet, null);
        }

        public static SheetScaleRow ForView(SheetScaleRow parent, int level, ScaleSheetInfo sheet, ScaleViewportInfo viewport)
        {
            return new SheetScaleRow(parent, level, null, sheet, viewport);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The check box was clicked; the argument is the value it had before.</summary>
        public event Action<SheetScaleRow, bool?> Toggled;

        /// <summary>The folder or the sheet this line sits in; null at the top.</summary>
        public SheetScaleRow Parent { get; }

        public int Level { get; }

        public string FolderName { get; }

        /// <summary>The sheet of a sheet line and of a view line; null for a folder line.</summary>
        public ScaleSheetInfo Sheet { get; }

        /// <summary>Null for a folder or a sheet line.</summary>
        public ScaleViewportInfo Viewport { get; }

        public ScaleViewInfo View => Viewport?.View;

        public bool IsView => Viewport != null;

        public bool IsSheet => Viewport == null && Sheet != null;

        public bool IsFolder => Viewport == null && Sheet == null;

        /// <summary>A folder or a sheet: a line that folds and whose check box stands for the views under it.</summary>
        public bool IsGroup => Viewport == null;

        /// <summary>Every view line under a folder or a sheet, at any depth — filled in by the window as it lays the tree out.</summary>
        public List<SheetScaleRow> Views { get; } = new List<SheetScaleRow>();

        public bool? IsChecked
        {
            get { return _isChecked; }
            set
            {
                if (Equals(_isChecked, value))
                    return;

                var before = _isChecked;
                _isChecked = value;
                Raise(nameof(IsChecked));
                Toggled?.Invoke(this, before);
            }
        }

        /// <summary>Sets the value without telling the window — for the window's own bookkeeping.</summary>
        public void SetChecked(bool? value)
        {
            if (Equals(_isChecked, value))
                return;

            _isChecked = value;
            Raise(nameof(IsChecked));
        }

        public bool IsCheckable
        {
            get { return _isCheckable; }
            set
            {
                if (Set(ref _isCheckable, value, nameof(IsCheckable)))
                    Raise(nameof(NameBrush));
            }
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                if (_isExpanded == value)
                    return;

                _isExpanded = value;
                Raise(nameof(IsExpanded));
                Raise(nameof(Text));
            }
        }

        public string Text
        {
            get
            {
                if (IsView)
                    return View.Kind + ": " + View.Name;

                return (IsExpanded ? "▾  " : "▸  ") + (IsSheet ? Sheet.Title : FolderName);
            }
        }

        /// <summary>The scale now: the view's, or every scale on a sheet — what Revit's own "Scale" of a sheet would read.</summary>
        public string ScaleText
        {
            get
            {
                if (IsView)
                    return View.Scale > 0 ? ViewPlacementRow.ScaleCaption(View.Scale) : "—";

                if (!IsSheet)
                    return string.Empty;

                return string.Join(", ", Views
                    .Where(row => row.View.Scale > 0)
                    .Select(row => row.View.Scale)
                    .Distinct()
                    .OrderBy(scale => scale)
                    .Select(ViewPlacementRow.ScaleCaption));
            }
        }

        /// <summary>The scale the view gets — or, on a sheet line, that something on the sheet changes; empty otherwise.</summary>
        public string NewScaleText
        {
            get { return _newScaleText; }
            set { Set(ref _newScaleText, value ?? string.Empty, nameof(NewScaleText)); }
        }

        public string StatusText
        {
            get { return _statusText; }
            set { Set(ref _statusText, value ?? string.Empty, nameof(StatusText)); }
        }

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

        public Thickness Indent => new Thickness(4 + Level * IndentStep, 0, 4, 0);

        public FontWeight Weight => IsFolder ? FontWeights.SemiBold : FontWeights.Normal;

        /// <summary>Drawn grey: a view that cannot be checked with the settings chosen.</summary>
        public Brush NameBrush => IsView && !IsCheckable ? SystemColors.GrayTextBrush : SystemColors.ControlTextBrush;

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
