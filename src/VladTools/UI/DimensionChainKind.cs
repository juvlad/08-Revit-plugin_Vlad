namespace VladTools.UI
{
    /// <summary>
    /// The catalogue of auto-dimension chain kinds — a fixed list of what a single dimension chain
    /// along a room side can pick up. Sample parsing (see <c>DimensionSampleReader</c>) matches every
    /// sample dimension to one of these kinds; a wrong match is not a problem — the row in the window
    /// can be edited by hand.
    ///
    /// The order of the values is the order in the window drop-down.
    /// </summary>
    internal enum DimensionChainKind
    {
        /// <summary>The two end corners of the side only — the overall size.</summary>
        Overall,

        /// <summary>The side corners plus the jamb faces of the openings (doors, windows).</summary>
        OpeningEdges,

        /// <summary>The side corners plus the opening centre lines (midway between the jambs).</summary>
        OpeningCenters,

        /// <summary>The side corners plus both faces of the partitions meeting the side from inside the room.</summary>
        Partitions,

        /// <summary>The corners plus the wall joints inside the side (for stepped walls built from several walls).</summary>
        WallFaces,

        /// <summary>Openings, opening centres and partitions merged into one chain.</summary>
        Combined
    }

    /// <summary>Text for the drop-down and the reports — the window knows nothing about the Revit API, so the text lives here.</summary>
    internal static class DimensionChainKindText
    {
        public static string Caption(DimensionChainKind kind)
        {
            switch (kind)
            {
                case DimensionChainKind.Overall:
                    return "Overall";
                case DimensionChainKind.OpeningEdges:
                    return "Openings";
                case DimensionChainKind.OpeningCenters:
                    return "Opening centres";
                case DimensionChainKind.Partitions:
                    return "Partitions";
                case DimensionChainKind.WallFaces:
                    return "Wall faces";
                case DimensionChainKind.Combined:
                    return "All combined";
                default:
                    return kind.ToString();
            }
        }

        public static readonly DimensionChainKind[] All =
        {
            DimensionChainKind.Overall,
            DimensionChainKind.OpeningEdges,
            DimensionChainKind.OpeningCenters,
            DimensionChainKind.Partitions,
            DimensionChainKind.WallFaces,
            DimensionChainKind.Combined
        };
    }
}
