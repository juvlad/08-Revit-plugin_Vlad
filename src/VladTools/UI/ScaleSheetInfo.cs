using System;
using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of one sheet for the "Sheet Scale" window: where the Project Browser files it, the
    /// drawing area of its titleblock, and every view standing on it — plain strings and numbers, no
    /// Revit types.
    /// </summary>
    internal sealed class ScaleSheetInfo
    {
        public ScaleSheetInfo(
            string uniqueId,
            string number,
            string name,
            IReadOnlyList<string> folders,
            double[] area,
            IReadOnlyList<ScaleViewportInfo> viewports)
        {
            UniqueId = uniqueId;
            Number = number ?? string.Empty;
            Name = name ?? string.Empty;
            Folders = folders;
            Area = area;
            Viewports = viewports;
        }

        public string UniqueId { get; }
        public string Number { get; }
        public string Name { get; }

        public string Title => Number + " - " + Name;

        /// <summary>The Project Browser folders, outermost first.</summary>
        public IReadOnlyList<string> Folders { get; }

        /// <summary>
        /// The drawing area — the titleblock less the margins "Views to Sheets" keeps for its type —
        /// as left, bottom, right, top in millimetres of the sheet; null when the sheet has no titleblock.
        /// </summary>
        public double[] Area { get; }

        public IReadOnlyList<ScaleViewportInfo> Viewports { get; }
    }

    /// <summary>One view standing on one sheet: the view, and where its viewport's box is on that sheet.</summary>
    internal sealed class ScaleViewportInfo
    {
        public ScaleViewportInfo(ScaleViewInfo view, double left, double bottom, double right, double top)
        {
            View = view;
            Left = left;
            Bottom = bottom;
            Right = right;
            Top = top;
        }

        public ScaleViewInfo View { get; }

        /// <summary>The viewport's box on the sheet, millimetres; all zeros when Revit could not say.</summary>
        public double Left { get; }
        public double Bottom { get; }
        public double Right { get; }
        public double Top { get; }

        public bool HasBox => Right > Left && Top > Bottom;

        /// <summary>
        /// How far the box would stick out of the sheet's drawing area at another scale, millimetres; 0
        /// or less when it stays inside. The middle of the box stays where it is — the button keeps it
        /// there — and the box grows or shrinks with the scale. Only an estimate: text and tags keep
        /// their size on paper; the report checks the real outline.
        /// </summary>
        public double OverhangAt(ScaleSheetInfo sheet, int scale)
        {
            var area = sheet.Area;
            if (area == null || !HasBox || scale <= 0 || View.Scale <= 0)
                return 0;

            var factor = (double)View.Scale / scale;
            var halfWidth = (Right - Left) / 2 * factor;
            var halfHeight = (Top - Bottom) / 2 * factor;
            var middleX = (Left + Right) / 2;
            var middleY = (Bottom + Top) / 2;

            return Math.Max(
                Math.Max(area[0] - (middleX - halfWidth), middleX + halfWidth - area[2]),
                Math.Max(area[1] - (middleY - halfHeight), middleY + halfHeight - area[3]));
        }
    }

    /// <summary>
    /// One view as "Sheet Scale" sees it. Shared by every line of the view — a legend stands on
    /// several sheets, and one check box stands for it on all of them, because it has one scale.
    /// </summary>
    internal sealed class ScaleViewInfo
    {
        public ScaleViewInfo(
            string uniqueId,
            string name,
            string kind,
            int scale,
            string problem,
            bool isLegend,
            int sheetCount,
            string scaleGroup,
            int groupSize,
            bool isDependent,
            string templateUniqueId,
            string templateName,
            bool templateSetsScale,
            int templateUsers)
        {
            UniqueId = uniqueId;
            Name = name ?? string.Empty;
            Kind = kind ?? string.Empty;
            Scale = scale;
            Problem = problem;
            IsLegend = isLegend;
            SheetCount = sheetCount;
            ScaleGroup = scaleGroup;
            GroupSize = groupSize;
            IsDependent = isDependent;
            TemplateUniqueId = templateUniqueId;
            TemplateName = templateName ?? string.Empty;
            TemplateSetsScale = templateSetsScale;
            TemplateUsers = templateUsers;
        }

        public string UniqueId { get; }

        public string Name { get; }

        /// <summary>"Floor plan", "Section"…</summary>
        public string Kind { get; }

        /// <summary>The view's scale, 100 for 1:100; 0 for a view without one (a perspective).</summary>
        public int Scale { get; }

        /// <summary>Why the scale cannot be changed at all; null when nothing stands in the way.</summary>
        public string Problem { get; }

        public bool IsLegend { get; }

        /// <summary>How many sheets the view stands on — more than one only for a legend.</summary>
        public int SheetCount { get; }

        /// <summary>
        /// Views sharing this key share one scale — a primary view and its dependents: a new scale for
        /// one is a new scale for all. Null for a view on its own.
        /// </summary>
        public string ScaleGroup { get; }

        /// <summary>How many views share the scale, this one included.</summary>
        public int GroupSize { get; }

        /// <summary>A dependent view: its scale is its primary's.</summary>
        public bool IsDependent { get; }

        /// <summary>The view template of the view holding the scale (the primary, for a dependent); null when there is none.</summary>
        public string TemplateUniqueId { get; }

        public string TemplateName { get; }

        /// <summary>The template's "Include" is ticked for "View Scale": the template, not the view, decides the scale.</summary>
        public bool TemplateSetsScale { get; }

        /// <summary>How many views of the project use that template.</summary>
        public int TemplateUsers { get; }
    }
}
