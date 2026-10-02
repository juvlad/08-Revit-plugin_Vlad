using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// A row of the "Translate" table: one Russian text, its English translation and the check box
    /// that lets it through.
    ///
    /// The row never judges itself: <see cref="StatusText"/>, <see cref="IsWarning"/> and
    /// <see cref="IsBlocked"/> are filled in by the window, which alone sees the other rows — whether
    /// a translated name clashes depends on what every other row turns into.
    /// </summary>
    internal sealed class TranslationRow : INotifyPropertyChanged
    {
        // Checked from the start: nothing is deleted here, and a row only does anything once it has a
        // translation — the check box is there to hold back a text that should stay as it is.
        private bool _isSelected = true;
        private string _translation = string.Empty;
        private string _statusText = string.Empty;
        private bool _isWarning;
        private bool _isBlocked;

        public TranslationRow(TranslationText info)
        {
            Info = info;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public TranslationText Info { get; }

        public string Original => Info.Original;

        public string Places => Info.Places;

        public string PlacesDetail => Info.PlacesDetail;

        /// <summary>At least one place of this text can be changed — otherwise there is nothing to check.</summary>
        public bool CanApply => Info.ChangeableCount > 0;

        public bool IsSelected
        {
            get { return _isSelected; }
            set { Set(ref _isSelected, value, nameof(IsSelected)); }
        }

        /// <summary>The English text, as typed, imported or taken from the dictionary.</summary>
        public string Translation
        {
            get { return _translation; }
            set { Set(ref _translation, value ?? string.Empty, nameof(Translation)); }
        }

        /// <summary>The translation in the form it is applied and stored in: line breaks as "\n", trimmed.</summary>
        public string CleanTranslation => TranslationDictionary.Normalize(Translation);

        /// <summary>A translation that actually changes something.</summary>
        public bool IsTranslated => CleanTranslation.Length > 0 && CleanTranslation != Original;

        /// <summary>The row goes into the run when "Translate" is pressed.</summary>
        public bool WillApply => IsSelected && CanApply && IsTranslated;

        /// <summary>The "State" column.</summary>
        public string StatusText
        {
            get { return _statusText; }
            set { Set(ref _statusText, value ?? string.Empty, nameof(StatusText)); }
        }

        /// <summary>Something about the row needs a look — the state is coloured, not merely written.</summary>
        public bool IsWarning
        {
            get { return _isWarning; }
            set
            {
                if (Set(ref _isWarning, value, nameof(IsWarning)))
                    Raise(nameof(StatusBrush));
            }
        }

        /// <summary>The translation cannot go in as it stands — a name clash or a forbidden character.</summary>
        public bool IsBlocked
        {
            get { return _isBlocked; }
            set { Set(ref _isBlocked, value, nameof(IsBlocked)); }
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
