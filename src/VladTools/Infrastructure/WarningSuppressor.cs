using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Suppresses Revit warnings when a transaction is committed and remembers their text.
    ///
    /// It is needed wherever a single transaction edits many elements: without it Revit would show
    /// a modal dialog for every warning and batch work would turn into clicking through dialogs.
    /// The warnings do not disappear silently — the command takes them from <see cref="Messages"/>
    /// and prints them in the final report.
    ///
    /// Errors (a severity above warning) are left alone: Revit handles those itself.
    /// </summary>
    internal sealed class WarningSuppressor : IFailuresPreprocessor
    {
        private readonly List<string> _messages = new List<string>();
        private readonly HashSet<string> _seen = new HashSet<string>();

        /// <summary>The text of the suppressed warnings, deduplicated and in order of appearance.</summary>
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

            // Continue, not ProceedWithCommit: any remaining errors must be handled as usual.
            return FailureProcessingResult.Continue;
        }
    }
}
