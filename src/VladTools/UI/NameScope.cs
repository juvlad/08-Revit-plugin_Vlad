using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// A set of names Revit keeps unique — the views of one kind, the types of one family, the
    /// levels — with every name currently in it, Russian or not.
    ///
    /// The "Translate" window checks a translation against these before anything is applied: two
    /// different Russian names can easily become one English one ("План этажа", "План эт." → "Floor
    /// plan"), and an English name may already be taken by an element nobody is renaming. Revit
    /// would refuse the second rename either way; the window says so while the translation can still
    /// be changed.
    ///
    /// The scopes are a deliberately narrow reading of Revit's rules: a clash the window misses still
    /// comes back from Revit as a line in the report, while a clash it imagined would block a
    /// translation that would have gone through.
    /// </summary>
    internal sealed class NameScope
    {
        public NameScope(string caption, IReadOnlyList<string> names)
        {
            Caption = caption ?? string.Empty;
            Names = names ?? new List<string>();
        }

        /// <summary>What the names are, for a sentence: "views (Floor plan)", "types of family \"Door\"".</summary>
        public string Caption { get; }

        /// <summary>Every name in the scope, normalised the way the dictionary keys are.</summary>
        public IReadOnlyList<string> Names { get; }
    }
}
