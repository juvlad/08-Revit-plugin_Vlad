using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace VladTools.UI
{
    /// <summary>
    /// The result of comparing the project against a single link: these grids and levels monitor it,
    /// and this is what differs.
    ///
    /// A project may contain several links that something monitors (the building base file and the
    /// overall site placement), so the window lists them in a drop-down and the command computes all
    /// of them at once: reading the grids and levels of a link is cheap, and switching between links
    /// must be instant.
    /// </summary>
    internal sealed class CoordinationScan
    {
        public CoordinationScan(ElementId linkId, string linkName, int monitoredCount)
        {
            LinkId = linkId;
            LinkName = linkName;
            MonitoredCount = monitoredCount;
            Rows = new List<CoordinationChangeRow>();
        }

        /// <summary>The link instance in the project.</summary>
        public ElementId LinkId { get; }

        public string LinkName { get; }

        /// <summary>How many project grids and levels monitor this link.</summary>
        public int MonitoredCount { get; }

        /// <summary>The link is loaded and its contents could be read.</summary>
        public bool IsLoaded { get; set; }

        public IReadOnlyList<CoordinationChangeRow> Rows { get; set; }

        /// <summary>How many changes the command is able to apply.</summary>
        public int ApplicableCount => Rows.Count(row => row.CanApply);

        /// <summary>The caption of the link in the window drop-down.</summary>
        public string Caption
        {
            get
            {
                if (!IsLoaded)
                    return LinkName + " — link is not loaded";

                if (Rows.Count == 0)
                    return LinkName + " — no differences (monitoring: " + MonitoredCount + ")";

                return LinkName + " — differences: " + Rows.Count + " (monitoring: " + MonitoredCount + ")";
            }
        }
    }
}
