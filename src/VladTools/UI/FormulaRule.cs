using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>Результат проверки строки — от него зависит текст и цвет столбца «Статус».</summary>
    internal enum FormulaStatus
    {
        /// <summary>Пустая строка таблицы: ни имени, ни формулы.</summary>
        Blank,

        /// <summary>Всё на месте, формулу можно применять.</summary>
        Ready,

        /// <summary>Применить можно, но у параметра уже есть формула — она будет заменена.</summary>
        Replace,

        /// <summary>Применить нельзя: нет параметра, нет формулы, формула ссылается на несуществующее имя.</summary>
        Error
    }

    /// <summary>
    /// Строка таблицы в окне «Добавить формулы»: галочка, параметр, формула и результат проверки.
    /// Правится прямо в таблице, поэтому сообщает об изменениях.
    /// </summary>
    internal sealed class FormulaRule : INotifyPropertyChanged
    {
        private bool _isEnabled = true;
        private string _parameterName = string.Empty;
        private string _formula = string.Empty;
        private string _statusText = string.Empty;
        private FormulaStatus _status = FormulaStatus.Blank;

        /// <summary>Нужен таблице: последнюю (пустую) строку она создаёт сама.</summary>
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

        /// <summary>Галочка «применять эту формулу».</summary>
        public bool IsEnabled
        {
            get { return _isEnabled; }
            set { Set(ref _isEnabled, value, nameof(IsEnabled)); }
        }

        /// <summary>Имя параметра семейства, которому задаётся формула.</summary>
        public string ParameterName
        {
            get { return _parameterName; }
            set { Set(ref _parameterName, value ?? string.Empty, nameof(ParameterName)); }
        }

        /// <summary>Текст формулы — так же, как он пишется в диалоге параметров Revit.</summary>
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

        /// <summary>Строка, в которой ничего не заполнено, — её не сохраняем и не применяем.</summary>
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
