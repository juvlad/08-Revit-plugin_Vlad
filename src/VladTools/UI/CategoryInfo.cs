namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of one bindable category of the open project, for the category picker: its display
    /// name (as Revit shows it, in whatever language the project runs in) and the
    /// <see cref="Autodesk.Revit.DB.BuiltInCategory"/> name it is saved and compared by.
    ///
    /// Gathered once when "Parameter Sets" opens (the window knows nothing about the Revit API — see
    /// CLAUDE.md, "Windows know nothing about Revit"), reused by every row's category picker.
    /// </summary>
    internal sealed class CategoryInfo
    {
        public CategoryInfo(string builtInName, string displayName)
        {
            BuiltInName = builtInName;
            DisplayName = displayName;
        }

        /// <summary>The <see cref="Autodesk.Revit.DB.BuiltInCategory"/> name — what a set stores and compares by.</summary>
        public string BuiltInName { get; }

        /// <summary>What the category is called in the Revit interface — what the user picks by.</summary>
        public string DisplayName { get; }
    }
}
