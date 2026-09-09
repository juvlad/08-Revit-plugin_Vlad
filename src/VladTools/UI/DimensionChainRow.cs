using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// A row of the chain table in the "Auto Dimensions" window: the chain kind, the offset from the
    /// wall face, the dimension type and the state (whether the chain is fit to be placed — filled in
    /// on "Place" or when a sample or template is loaded).
    ///
    /// Rows are filled in both by sample parsing (<c>DimensionSampleReader</c>) and by hand in the
    /// window itself — so every field is read and written freely, without validation at the row
    /// level: validation belongs entirely to the window (see the "an empty rule does not select
    /// everything" rule in CLAUDE.md — the same principle applies here: a row cannot quietly become "valid").
    ///
    /// <see cref="StatusText"/> and <see cref="HasError"/> hold the result of the window validating the
    /// row (the offset is positive, the dimension type was found); <see cref="Note"/> is a separate,
    /// uncoloured hint about where the row came from (which sample dimension, say), and there is no
    /// reason to wipe it on every validation.
    /// </summary>
    internal sealed class DimensionChainRow : INotifyPropertyChanged
    {
        private bool _isEnabled = true;
        private DimensionChainKind _kind = DimensionChainKind.Overall;
        private double _offsetMm = 300;
        private string _dimensionTypeName = string.Empty;
        private string _note = string.Empty;
        private string _statusText = string.Empty;
        private bool _hasError;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The "use this chain when placing" check box.</summary>
        public bool IsEnabled
        {
            get { return _isEnabled; }
            set { Set(ref _isEnabled, value, nameof(IsEnabled)); }
        }

        public DimensionChainKind Kind
        {
            get { return _kind; }
            set { Set(ref _kind, value, nameof(Kind)); }
        }

        /// <summary>The offset of the dimension line from the side face, in millimetres. May be negative (outwards).</summary>
        public double OffsetMm
        {
            get { return _offsetMm; }
            set { Set(ref _offsetMm, value, nameof(OffsetMm)); }
        }

        /// <summary>The dimension type name, not its id: a template has to travel between projects.</summary>
        public string DimensionTypeName
        {
            get { return _dimensionTypeName; }
            set { Set(ref _dimensionTypeName, value ?? string.Empty, nameof(DimensionTypeName)); }
        }

        /// <summary>Where the row came from ("from the 3925 mm dimension", say) — plain text, not validated.</summary>
        public string Note
        {
            get { return _note; }
            set { Set(ref _note, value ?? string.Empty, nameof(Note)); }
        }

        public string StatusText
        {
            get { return _statusText; }
            private set { Set(ref _statusText, value ?? string.Empty, nameof(StatusText)); }
        }

        public bool HasError
        {
            get { return _hasError; }
            private set { Set(ref _hasError, value, nameof(HasError)); }
        }

        public Brush StatusBrush
        {
            get { return _hasError ? Brushes.Firebrick : SystemColors.GrayTextBrush; }
        }

        public void SetStatus(string text, bool isError)
        {
            StatusText = text;
            HasError = isError;
            Raise(nameof(StatusBrush));
        }

        private void Set<T>(ref T field, T value, string property)
        {
            if (Equals(field, value))
                return;

            field = value;
            Raise(property);
        }

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
