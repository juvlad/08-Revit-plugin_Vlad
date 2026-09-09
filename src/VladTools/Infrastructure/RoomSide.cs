using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// One straight side of a room boundary — the result of merging neighbouring segments of a single
    /// <c>GetBoundarySegments</c> loop into one straight stretch (see <see cref="RoomSideBuilder"/>).
    ///
    /// It stores only the geometry and the list of walls along the side; which references to collect
    /// on that side for a chain is up to <c>DimensionReferenceCollector</c> — the side knows nothing
    /// about that.
    /// </summary>
    internal sealed class RoomSide
    {
        public RoomSide(
            XYZ start,
            XYZ end,
            XYZ direction,
            XYZ inwardNormal,
            IReadOnlyList<ElementId> wallIds,
            bool isCurved,
            int loopIndex,
            int index,
            IReadOnlyList<double> wallJointOffsets)
        {
            Start = start;
            End = end;
            Direction = direction;
            InwardNormal = inwardNormal;
            WallIds = wallIds;
            IsCurved = isCurved;
            LoopIndex = loopIndex;
            Index = index;
            WallJointOffsets = wallJointOffsets ?? new List<double>();
        }

        /// <summary>The start of the side — the end point in the <see cref="Direction"/> sense.</summary>
        public XYZ Start { get; }

        /// <summary>The end of the side.</summary>
        public XYZ End { get; }

        /// <summary>The unit vector along the side (in plan, Z = 0).</summary>
        public XYZ Direction { get; }

        /// <summary>The unit normal pointing into the room.</summary>
        public XYZ InwardNormal { get; }

        /// <summary>Walls (and other boundary elements — a door in an opening without a wall, etc.) along the side, in order.</summary>
        public IReadOnlyList<ElementId> WallIds { get; }

        /// <summary>The side is made of a curved segment — auto-dimensioning skips it.</summary>
        public bool IsCurved { get; }

        /// <summary>The index of the boundary loop (there may be several — a room with a hole in it).</summary>
        public int LoopIndex { get; }

        /// <summary>The index of the side inside its own loop — used in the report ("the east side" and so on).</summary>
        public int Index { get; }

        public double Length
        {
            get { return Start.DistanceTo(End); }
        }

        /// <summary>At least one real wall (not a room separator) on the side — otherwise there is nothing to dimension.</summary>
        public bool HasWall { get; set; }

        /// <summary>
        /// The offsets (from <see cref="Start"/> along <see cref="Direction"/>) of the joints between
        /// different walls inside the side — a side can be glued together from several collinear walls
        /// (a stepped wall, say, or two different wall types in one line). Used only by the "Wall faces"
        /// chain — the other chain kinds never ask about joints.
        /// </summary>
        public IReadOnlyList<double> WallJointOffsets { get; }

        /// <summary>The projection of a point onto the side axis (0 — the start, Length — the end).</summary>
        public double ProjectOnAxis(XYZ point)
        {
            return (point - Start).DotProduct(Direction);
        }

        /// <summary>The signed distance from the side line along the inward normal (0 — on the side, &gt;0 — inwards).</summary>
        public double SignedOffset(XYZ point)
        {
            return (point - Start).DotProduct(InwardNormal);
        }
    }
}
