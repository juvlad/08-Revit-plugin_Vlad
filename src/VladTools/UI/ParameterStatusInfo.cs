namespace VladTools.UI
{
    /// <summary>
    /// What "Parameter Sets" found when it compared one set entry against the open project: whether it
    /// is already bound exactly as asked, missing, or bound differently — filled in by the command
    /// (the window cannot read the document itself, see CLAUDE.md, "Windows know nothing about Revit").
    /// </summary>
    internal sealed class ParameterStatusInfo
    {
        public ParameterStatusInfo(string text, bool isWarning)
        {
            Text = text ?? string.Empty;
            IsWarning = isWarning;
        }

        /// <summary>The "State" column caption.</summary>
        public string Text { get; }

        /// <summary>Something needs attention: the GUID is missing from the shared file, say.</summary>
        public bool IsWarning { get; }
    }
}
