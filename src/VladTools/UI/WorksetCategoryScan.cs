using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// What "Move to Workset" found in one scope — the current selection or the whole model: the
    /// table rows plus what was left out of them, so the window can say why the table is shorter
    /// than the selection.
    /// </summary>
    internal sealed class WorksetCategoryScan
    {
        public WorksetCategoryScan(
            IReadOnlyList<WorksetCategoryInfo> rows,
            int elementCount,
            int leftOut,
            IReadOnlyList<string> closedWorksets)
        {
            Rows = rows ?? new List<WorksetCategoryInfo>();
            ElementCount = elementCount;
            LeftOut = leftOut;
            ClosedWorksets = closedWorksets ?? new List<string>();
        }

        public IReadOnlyList<WorksetCategoryInfo> Rows { get; }

        /// <summary>How many elements the scope held before anything was left out — for the selection, what the user selected.</summary>
        public int ElementCount { get; }

        /// <summary>
        /// Elements of the scope not in any row: Revit decides their workset itself (types, views,
        /// annotation belonging to a view, parts of another element). Only meaningful for a selection —
        /// on the whole model this is most of the document and says nothing.
        /// </summary>
        public int LeftOut { get; }

        /// <summary>
        /// User worksets closed in this session. A collector does not see inside a closed workset, so
        /// whatever stands there is in no row — said out loud rather than passed off as "nothing found",
        /// the same rule the "Worksets" button keeps.
        /// </summary>
        public IReadOnlyList<string> ClosedWorksets { get; }
    }
}
