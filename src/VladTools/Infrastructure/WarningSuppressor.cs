using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Гасит предупреждения Revit при фиксации транзакции и запоминает их текст.
    ///
    /// Нужен там, где за одну транзакцию правится много элементов: без него Revit
    /// показывал бы модальное окно на каждое предупреждение и пакетная работа
    /// превращалась бы в щёлканье по диалогам. Предупреждения не пропадают молча —
    /// команда достаёт их из <see cref="Messages"/> и печатает в итоговом отчёте.
    ///
    /// Ошибки (severity выше предупреждения) не трогаются: их разбирает сам Revit.
    /// </summary>
    internal sealed class WarningSuppressor : IFailuresPreprocessor
    {
        private readonly List<string> _messages = new List<string>();
        private readonly HashSet<string> _seen = new HashSet<string>();

        /// <summary>Тексты погашенных предупреждений, без повторов и в порядке появления.</summary>
        public IReadOnlyList<string> Messages => _messages;

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() != FailureSeverity.Warning)
                    continue;

                var text = failure.GetDescriptionText();
                if (!string.IsNullOrEmpty(text) && _seen.Add(text))
                    _messages.Add(text);
            }

            failuresAccessor.DeleteAllWarnings();

            // Continue, а не ProceedWithCommit: оставшиеся ошибки должны обрабатываться как обычно.
            return FailureProcessingResult.Continue;
        }
    }
}
