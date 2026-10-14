using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of one view for the "Views to Sheets" window: where the Project Browser files it,
    /// whether it can go on a new sheet or be moved on its own, its scale and how big it is on paper —
    /// plain strings and numbers, no Revit types.
    /// </summary>
    internal sealed class PlaceableViewInfo
    {
        public PlaceableViewInfo(
            string uniqueId,
            string name,
            string kind,
            IReadOnlyList<string> folders,
            string placedOn,
            string viewportUniqueId,
            bool isLegend,
            string problem,
            int scale,
            string scaleLock,
            string scaleGroup,
            bool isDependent,
            double width,
            double height,
            string alignGroup,
            string notAlignable)
        {
            UniqueId = uniqueId;
            Name = name ?? string.Empty;
            Kind = kind ?? string.Empty;
            Folders = folders;
            PlacedOn = placedOn;
            ViewportUniqueId = viewportUniqueId;
            IsLegend = isLegend;
            Problem = problem;
            Scale = scale;
            ScaleLock = scaleLock;
            ScaleGroup = scaleGroup;
            IsDependent = isDependent;
            Width = width;
            Height = height;
            AlignGroup = alignGroup;
            NotAlignable = notAlignable;
        }

        public string UniqueId { get; }

        /// <summary>The view's name — and the name of the sheet it goes on.</summary>
        public string Name { get; }

        /// <summary>"Floor plan", "Section"…</summary>
        public string Kind { get; }

        /// <summary>The Project Browser folders, outermost first, the view-type group ("Floor Plans") last.</summary>
        public IReadOnlyList<string> Folders { get; }

        /// <summary>"A101 - Level 1" when the view already stands on a sheet; null when it does not.</summary>
        public string PlacedOn { get; }

        /// <summary>The viewport the view stands in, when it stands on exactly one sheet and is not a legend — what a move moves.</summary>
        public string ViewportUniqueId { get; }

        /// <summary>A legend may stand on any number of sheets; every other view on one only.</summary>
        public bool IsLegend { get; }

        /// <summary>Why the view cannot be placed at all (an empty view); null when nothing stands in the way.</summary>
        public string Problem { get; }

        /// <summary>Can go on a new sheet: not empty, and not already on one (a legend excepted).</summary>
        public bool CanPlace => Problem == null && (PlacedOn == null || IsLegend);

        /// <summary>Stands on a sheet already, in one viewport, so it can be moved on that sheet.</summary>
        public bool CanMove => Problem == null && ViewportUniqueId != null;

        /// <summary>The view's own scale, 100 for 1:100; 0 for a view without one (a perspective).</summary>
        public int Scale { get; }

        /// <summary>Why the scale cannot be changed here ("its view template … sets the scale"); null when it can.</summary>
        public string ScaleLock { get; }

        /// <summary>
        /// Views sharing this key share one scale — a primary view and its dependents: Revit keeps them
        /// alike, so a new scale for one is a new scale for all. Null for a view on its own.
        /// </summary>
        public string ScaleGroup { get; }

        /// <summary>A dependent view: its scale is its primary's.</summary>
        public bool IsDependent { get; }

        /// <summary>Millimetres of paper at the view's own scale, from the crop region; 0 when unknown until it is placed.</summary>
        public double Width { get; }

        public double Height { get; }

        public bool HasSize => Width > 0 && Height > 0;

        /// <summary>
        /// Views sharing this key look the same way, so one model point can be put in the same place on
        /// each of their sheets. Null when the view cannot be aligned through the model at all —
        /// <see cref="NotAlignable"/> says why.
        /// </summary>
        public string AlignGroup { get; }

        /// <summary>"no crop region", "a perspective view"… — why the view is centred by its viewport instead.</summary>
        public string NotAlignable { get; }

        /// <summary>The size on paper at another scale: the model stays the same size, the paper shrinks or grows with it.</summary>
        public double WidthAt(int scale)
        {
            return scale > 0 && Scale > 0 ? Width * Scale / scale : Width;
        }

        public double HeightAt(int scale)
        {
            return scale > 0 && Scale > 0 ? Height * Scale / scale : Height;
        }
    }
}
