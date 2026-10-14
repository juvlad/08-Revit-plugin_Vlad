using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// A line of the "Views to Sheets" table: a Project Browser folder or a view inside one — the
    /// browser's tree laid flat, the same way the "Sheets" window lays out sheets (<see cref="FlatTree"/>).
    ///
    /// A folder's check box stands for the views under it, so its value is worked out by the window
    /// from theirs (checked, cleared, or partly — <c>null</c>) and a click on it is handed to the
    /// window through <see cref="Toggled"/>, which knows which of those views are shown. Whether a line
    /// can be checked at all depends on the positioning chosen — a view already on a sheet can only be
    /// moved there "like a sample" — so <see cref="IsCheckable"/> is set by the window, not worked out here.
    /// </summary>
    internal sealed class ViewPlacementRow : INotifyPropertyChanged
    {
        private const double IndentStep = 16;

        private bool? _isChecked = false;
        private bool _isCheckable;
        private bool _isExpanded = true;
        private string _scaleText;
        private string _sheetText = string.Empty;
        private string _statusText = string.Empty;
        private bool _isWarning;

        private ViewPlacementRow(ViewPlacementRow parent, int level, string folderName, PlaceableViewInfo view)
        {
            Parent = parent;
            Level = level;
            FolderName = folderName ?? string.Empty;
            View = view;
            _scaleText = view == null ? string.Empty : ScaleCaption(view.Scale);
        }

        public static ViewPlacementRow Folder(ViewPlacementRow parent, int level, string name)
        {
            return new ViewPlacementRow(parent, level, name, null);
        }

        public static ViewPlacementRow ForView(ViewPlacementRow parent, int level, PlaceableViewInfo view)
        {
            return new ViewPlacementRow(parent, level, null, view);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The check box was clicked; the argument is the value it had before.</summary>
        public event Action<ViewPlacementRow, bool?> Toggled;

        /// <summary>The folder this line sits in; null at the top.</summary>
        public ViewPlacementRow Parent { get; }

        public int Level { get; }

        public string FolderName { get; }

        /// <summary>Null for a folder line.</summary>
        public PlaceableViewInfo View { get; }

        public bool IsFolder => View == null;

        /// <summary>Every view line under a folder, at any depth — filled in by the window as it lays the tree out.</summary>
        public List<ViewPlacementRow> Views { get; } = new List<ViewPlacementRow>();

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
                {
                    Raise(nameof(NameBrush));
                    Raise(nameof(CanSetScale));
                }
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
                Raise(nameof(ViewText));
            }
        }

        public string ViewText => IsFolder ? (IsExpanded ? "▾  " : "▸  ") + FolderName : View.Name;

        /// <summary>The "Scale" cell: "1:100", typed or picked; the window reads it through <see cref="TargetScale"/>.</summary>
        public string ScaleText
        {
            get { return _scaleText; }
            set
            {
                if (Set(ref _scaleText, value ?? string.Empty, nameof(ScaleText)))
                    Raise(nameof(PaperSize));
            }
        }

        /// <summary>The scale the cell asks for, 100 for 1:100; 0 when the text is not a scale.</summary>
        public int TargetScale => ParseScale(ScaleText);

        /// <summary>The view has a scale, nothing locks it, and the line can be checked at all.</summary>
        public bool CanSetScale => !IsFolder && IsCheckable && View.Scale > 0 && View.ScaleLock == null;

        public Visibility ScaleVisibility => IsFolder ? Visibility.Collapsed : Visibility.Visible;

        public string PaperSize
        {
            get
            {
                if (IsFolder || !View.HasSize)
                    return string.Empty;

                var scale = TargetScale > 0 ? TargetScale : View.Scale;
                return "≈ " + TitleblockInfo.Millimetres(View.WidthAt(scale), View.HeightAt(scale));
            }
        }

        /// <summary>"A101 - Level 1" for a checked view — the sheet it will go on; empty otherwise.</summary>
        public string SheetText
        {
            get { return _sheetText; }
            set { Set(ref _sheetText, value ?? string.Empty, nameof(SheetText)); }
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

        /// <summary>Drawn grey: a view that cannot be checked in the positioning chosen.</summary>
        public Brush NameBrush => !IsFolder && !IsCheckable ? SystemColors.GrayTextBrush : SystemColors.ControlTextBrush;

        /// <summary>"1:100"; empty for no scale.</summary>
        public static string ScaleCaption(int scale)
        {
            return scale > 0 ? "1:" + scale.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        /// <summary>"1:100", "1 : 100" or plain "100" → 100; anything else → 0.</summary>
        public static int ParseScale(string text)
        {
            var value = (text ?? string.Empty).Replace(" ", string.Empty);
            var colon = value.IndexOf(':');

            if (colon >= 0)
            {
                if (value.Substring(0, colon) != "1")
                    return 0;

                value = value.Substring(colon + 1);
            }

            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var scale) && scale >= 1 && scale <= 100000 ? scale : 0;
        }

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
