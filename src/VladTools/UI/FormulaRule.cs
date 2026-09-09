using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>The result of validating a row — the text and colour of the "Status" column depend on it.</summary>
    internal enum FormulaStatus
    {
        /// <summary>An empty table row: neither a name nor a formula.</summary>
        Blank,

        /// <summary>Everything is in place, the formula can be applied.</summary>
        Ready,

        /// <summary>It can be applied, but the parameter already has a formula — it will be replaced.</summary>
        Replace,

        /// <summary>It cannot be applied: no parameter, no formula, or the formula refers to a name that does not exist.</summary>
        Error
    }

    /// <summary>
    /// A table row in the "Add Formulas" window: the check box, the parameter, the formula and the
    /// validation result. It is edited right in the table, so it reports its changes.
    /// </summary>
    internal sealed class FormulaRule : INotifyPropertyChanged
    {
        private bool _isEnabled = true;
        private string _parameterName = string.Empty;
        private string _formula = string.Empty;
        private string _statusText = string.Empty;
        private FormulaStatus _status = FormulaStatus.Blank;

        /// <summary>The table needs it: it creates the last (empty) row itself.</summary>
        public FormulaRule()
        {
        }

        public FormulaRule(string parameterName, string formula, bool isEnabled)
        {
            _parameterName = parameterName ?? string.Empty;
            _formula = formula ?? string.Empty;
            _isEnabled = isEnabled;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The "apply this formula" check box.</summary>
        public bool IsEnabled
        {
            get { return _isEnabled; }
            set { Set(ref _isEnabled, value, nameof(IsEnabled)); }
        }

        /// <summary>The name of the family parameter the formula is assigned to.</summary>
        public string ParameterName
        {
            get { return _parameterName; }
            set { Set(ref _parameterName, value ?? string.Empty, nameof(ParameterName)); }
        }

        /// <summary>The formula text — exactly as it is written in the Revit parameter dialog.</summary>
        public string Formula
        {
            get { return _formula; }
            set { Set(ref _formula, value ?? string.Empty, nameof(Formula)); }
        }

        public string StatusText
        {
            get { return _statusText; }
        }

        public FormulaStatus Status
        {
            get { return _status; }
        }

        public Brush StatusBrush
        {
            get
            {
                switch (_status)
                {
                    case FormulaStatus.Ready:
                        return Brushes.SeaGreen;
                    case FormulaStatus.Replace:
                        return Brushes.DarkOrange;
                    case FormulaStatus.Error:
                        return Brushes.Firebrick;
                    default:
                        return SystemColors.GrayTextBrush;
                }
            }
        }

        /// <summary>A row with nothing filled in — it is neither saved nor applied.</summary>
        public bool IsBlank
        {
            get { return string.IsNullOrWhiteSpace(_parameterName) && string.IsNullOrWhiteSpace(_formula); }
        }

        public string TrimmedName
        {
            get { return _parameterName.Trim(); }
        }

        public string TrimmedFormula
        {
            get { return _formula.Trim(); }
        }

        public void SetStatus(FormulaStatus status, string text)
        {
            _status = status;
            _statusText = text ?? string.Empty;

            Raise(nameof(Status));
            Raise(nameof(StatusText));
            Raise(nameof(StatusBrush));
        }

        private void Set<T>(ref T field, T value, string propertyName)
        {
            if (Equals(field, value))
                return;

            field = value;
            Raise(propertyName);
        }

        private void Raise(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
