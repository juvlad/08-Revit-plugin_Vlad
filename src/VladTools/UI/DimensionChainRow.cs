using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>
    /// Строка таблицы ниток в окне «Авторазмеры»: вид нитки, смещение от грани стены,
    /// тип размера и состояние (годна ли нитка к расстановке — заполняется при «Расставить»
    /// или при загрузке образца/шаблона).
    ///
    /// Ряды заполняются как разбором образца (<c>DimensionSampleReader</c>), так и руками
    /// в самом окне — поэтому все поля читаются/пишутся свободно, без валидации на уровне
    /// строки: проверка целиком лежит на окне (см. правило «пустое правило не выбирает всё»
    /// в CLAUDE.md — здесь тот же принцип: строка не может незаметно стать «правильной»).
    ///
    /// <see cref="StatusText"/>/<see cref="HasError"/> — это результат проверки строки окном
    /// (смещение положительное, тип размера найден); <see cref="Note"/> — отдельная, не цветная
    /// подсказка «откуда взялась строка» (например, из какого образцового размера), её незачем
    /// стирать при каждой проверке.
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

        /// <summary>Галочка «использовать эту нитку при расстановке».</summary>
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

        /// <summary>Смещение линии размера от грани стороны, в миллиметрах. Может быть отрицательным (наружу).</summary>
        public double OffsetMm
        {
            get { return _offsetMm; }
            set { Set(ref _offsetMm, value, nameof(OffsetMm)); }
        }

        /// <summary>Имя типа размера — не Id: шаблон должен переноситься между проектами.</summary>
        public string DimensionTypeName
        {
            get { return _dimensionTypeName; }
            set { Set(ref _dimensionTypeName, value ?? string.Empty, nameof(DimensionTypeName)); }
        }

        /// <summary>Откуда взялась строка (например, «из размера 3925 мм») — только текст, без проверки.</summary>
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
