namespace VladTools.UI
{
    /// <summary>
    /// What differs between a project element and the coordination file.
    ///
    /// A single element may produce two table rows — one about position and one about the name:
    /// in Revit's own "Coordination Review" these are also two separate changes, and the user must
    /// be able to accept them separately.
    /// </summary>
    internal enum CoordinationChangeKind
    {
        /// <summary>The grid is shifted or rotated, or the level has a different elevation.</summary>
        Position,

        /// <summary>The element is named differently in the coordination file.</summary>
        Name,

        /// <summary>Something monitors the link, but no matching element was found in it.</summary>
        Missing,

        /// <summary>The element exists in the coordination file, but nothing in the project monitors it.</summary>
        New,

        /// <summary>There is a difference, but it cannot be expressed as a move and a rotation.</summary>
        Unsupported
    }

    /// <summary>Captions and rules for <see cref="CoordinationChangeKind"/>.</summary>
    internal static class CoordinationChangeKinds
    {
        public static string Label(CoordinationChangeKind kind)
        {
            switch (kind)
            {
                case CoordinationChangeKind.Position:
                    return "Position";
                case CoordinationChangeKind.Name:
                    return "Name";
                case CoordinationChangeKind.Missing:
                    return "Deleted from the coordination file";
                case CoordinationChangeKind.New:
                    return "New in the coordination file";
                default:
                    return "Cannot be applied";
            }
        }

        /// <summary>
        /// The command is able to apply this change. <see cref="CoordinationChangeKind.New"/> never
        /// can be: setting up monitoring on a new link element is not in the Revit API at all, that
        /// is done by hand through "Copy/Monitor".
        ///
        /// <see cref="CoordinationChangeKind.Missing"/> can — but it is the one kind that
        /// **deletes**, and a deletion is never offered on its own: the row is only made checkable
        /// once the window's "Delete…" box is on (see <see cref="CoordinationChangeRow.CanApply"/>).
        /// </summary>
        public static bool CanApply(CoordinationChangeKind kind)
        {
            return kind == CoordinationChangeKind.Position ||
                   kind == CoordinationChangeKind.Name ||
                   kind == CoordinationChangeKind.Missing;
        }

        /// <summary>
        /// Applying this change removes the element from the project. Everything else here only ever
        /// moves or relabels something — this is the one kind that cannot be undone by looking at
        /// the model afterwards, so it is treated apart everywhere: it is not checked by default,
        /// it needs its own switch, and it has its own section in the report.
        /// </summary>
        public static bool IsRemoval(CoordinationChangeKind kind)
        {
            return kind == CoordinationChangeKind.Missing;
        }
    }
}
