namespace VladTools.UI
{
    /// <summary>
    /// One distinct Russian text of the open project, with where it is used — a snapshot the command
    /// hands to the "Translate" window.
    ///
    /// The table has one row per *text*, not per place, and that is the whole idea of the button:
    /// "Отверстия" in a view parameter, in a view filter's rule and in a schedule filter is one row,
    /// translated once and replaced everywhere at once. Translated place by place, the filter would
    /// keep looking for "Отверстия" after the parameter had become "Openings", and stop matching
    /// without a word.
    /// </summary>
    internal sealed class TranslationText
    {
        public TranslationText(
            string original,
            int changeableCount,
            int fixedCount,
            string places,
            string placesDetail,
            string fixedNote,
            bool hasNames)
        {
            Original = original ?? string.Empty;
            ChangeableCount = changeableCount;
            FixedCount = fixedCount;
            Places = places ?? string.Empty;
            PlacesDetail = placesDetail ?? string.Empty;
            FixedNote = fixedNote ?? string.Empty;
            HasNames = hasNames;
        }

        /// <summary>The text itself, normalised: line breaks as "\n", no surrounding whitespace.</summary>
        public string Original { get; }

        /// <summary>The places the button can change.</summary>
        public int ChangeableCount { get; }

        /// <summary>The places the Revit API will not let anyone change — a shared parameter's name, say.</summary>
        public int FixedCount { get; }

        /// <summary>A one-line summary for the "Where" column: <c>view name ×3; value of "Раздел" ×12</c>.</summary>
        public string Places { get; }

        /// <summary>The places one per line, for the tooltip.</summary>
        public string PlacesDetail { get; }

        /// <summary>Which places will stay as they are, and why; empty when there are none.</summary>
        public string FixedNote { get; }

        /// <summary>At least one place is an element name — Revit's forbidden characters apply.</summary>
        public bool HasNames { get; }
    }
}
