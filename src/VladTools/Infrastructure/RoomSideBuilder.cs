using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Builds the straight sides of a room boundary out of the <c>GetBoundarySegments</c> loops.
    ///
    /// Each loop is a polyline of segments, one per element the boundary runs along (a piece of wall
    /// between two openings is a separate segment too).
    /// Neighbouring segments are merged into one side when they point the same way and lie on the
    /// same line — otherwise a single wall would turn into several parallel chains instead of one
    /// long one. The direction of the inward normal is not taken from the loop order as given — it is
    /// verified through <see cref="Room.IsPointInRoom"/>: relying on Revit always walking its loops
    /// in the same direction is not safe (see CLAUDE.md).
    /// </summary>
    internal static class RoomSideBuilder
    {
        // cos(0.06°) — after the small inaccuracies of boundary construction, neighbouring segments of
        // one wall are almost always perfectly parallel; the threshold is deliberately tight so that a
        // stray 1-2° kink (another wall meeting this one) is not glued into a single side.
        private const double DirectionDotTolerance = 1e-6;

        private const double CollinearToleranceMm = 1.0;
        private const double MinSideLengthMm = 10.0;
        private const double NormalTestOffsetMm = 50.0;

        public static List<RoomSide> Build(Room room, SpatialElementBoundaryOptions options)
        {
            var sides = new List<RoomSide>();

            IList<IList<BoundarySegment>> loops;
            try
            {
                loops = room.GetBoundarySegments(options);
            }
            catch (Exception)
            {
                return sides;
            }

            if (loops == null)
                return sides;

            for (var loopIndex = 0; loopIndex < loops.Count; loopIndex++)
                sides.AddRange(BuildLoop(room, loops[loopIndex], loopIndex));

            return sides;
        }

        private static List<RoomSide> BuildLoop(Room room, IList<BoundarySegment> loop, int loopIndex)
        {
            var pieces = new List<Piece>();

            foreach (var segment in loop)
            {
                Curve curve;
                try
                {
                    curve = segment.GetCurve();
                }
                catch (Exception)
                {
                    continue;
                }

                if (curve == null)
                    continue;

                var line = curve as Line;
                var start = curve.GetEndPoint(0);
                var end = curve.GetEndPoint(1);

                if (start.DistanceTo(end) < FeetOf(MinSideLengthMm))
                    continue; // a degenerate segment — Revit sometimes returns these at joints

                pieces.Add(new Piece
                {
                    Start = Flat(start),
                    End = Flat(end),
                    ElementId = segment.ElementId,
                    IsCurved = line == null
                });
            }

            var result = new List<RoomSide>();
            if (pieces.Count == 0)
                return result;

            var runs = GroupIntoRuns(pieces);
            var index = 0;

            foreach (var run in runs)
            {
                var side = BuildSide(room, run, loopIndex, ref index);
                if (side != null)
                    result.Add(side);
            }

            return result;
        }

        private static RoomSide BuildSide(Room room, List<Piece> run, int loopIndex, ref int index)
        {
            var start = run[0].Start;
            var end = run[run.Count - 1].End;
            var isCurved = run.Count == 1 && run[0].IsCurved;

            XYZ direction;
            XYZ inwardNormal;

            if (isCurved)
            {
                // A curved segment has no straight direction — we take the chord only so the side can
                // be shown in the report; no chains are built along it
                // (see DimensionReferenceCollector — IsCurved is filtered out earlier).
                if (start.DistanceTo(end) < FeetOf(MinSideLengthMm))
                    return null;

                direction = (end - start).Normalize();
                inwardNormal = XYZ.BasisZ.CrossProduct(direction).Normalize();
            }
            else
            {
                if (start.DistanceTo(end) < FeetOf(MinSideLengthMm))
                    return null;

                direction = (end - start).Normalize();
                inwardNormal = ComputeInwardNormal(room, start, end, direction);
            }

            var wallIds = new List<ElementId>();
            var seen = new HashSet<long>();

            foreach (var piece in run)
            {
                var id = piece.ElementId;
                if (id == null || id == ElementId.InvalidElementId)
                    continue;

                if (seen.Add(id.IntegerValue))
                    wallIds.Add(id);
            }

            var jointOffsets = new List<double>();
            for (var i = 1; i < run.Count; i++)
            {
                var prevId = run[i - 1].ElementId;
                var currId = run[i].ElementId;

                // A joint counts only where the neighbour is a different wall: between two pieces of
                // the same wall (split by an opening) there is a jamb face, not a wall joint.
                var samePiece = prevId != null && currId != null && prevId == currId;
                if (samePiece)
                    continue;

                jointOffsets.Add((run[i].Start - start).DotProduct(direction));
            }

            index++;

            var side = new RoomSide(start, end, direction, inwardNormal, wallIds, isCurved, loopIndex, index, jointOffsets);
            side.HasWall = wallIds.Any(id => room.Document.GetElement(id) is Wall);
            return side;
        }

        /// <summary>
        /// The inward normal is not taken from the loop direction as given: the raw
        /// <c>BasisZ × direction</c> product is checked with a point 50 mm off the side through
        /// <see cref="Room.IsPointInRoom"/>, and flipped if the answer is negative.
        /// If the check fails (the point landed exactly on a face, say) the raw value is returned:
        /// being wrong here means placing the dimension outside, not breaking the model.
        /// </summary>
        private static XYZ ComputeInwardNormal(Room room, XYZ start, XYZ end, XYZ direction)
        {
            var raw = XYZ.BasisZ.CrossProduct(direction);
            if (raw.GetLength() < 1e-9)
                return XYZ.BasisX;

            raw = raw.Normalize();

            var mid = (start + end) * 0.5;
            var offset = FeetOf(NormalTestOffsetMm);

            try
            {
                if (room.IsPointInRoom(mid + raw.Multiply(offset)))
                    return raw;

                var flipped = raw.Negate();
                if (room.IsPointInRoom(mid + flipped.Multiply(offset)))
                    return flipped;
            }
            catch (Exception)
            {
                // could not check — hand back the raw value as it is
            }

            return raw;
        }

        private static List<List<Piece>> GroupIntoRuns(List<Piece> pieces)
        {
            var runs = new List<List<Piece>>();
            var n = pieces.Count;

            if (n == 1)
            {
                runs.Add(new List<Piece> { pieces[0] });
                return runs;
            }

            var splitStart = -1;
            for (var i = 0; i < n; i++)
            {
                var prev = pieces[(i - 1 + n) % n];
                var curr = pieces[i];
                if (!CanMerge(prev, curr))
                {
                    splitStart = i;
                    break;
                }
            }

            if (splitStart == -1)
            {
                // Every segment merged into one — a degenerate case (a loop without a single kink);
                // it does not occur on a closed room in practice, but it must not hang.
                runs.Add(new List<Piece>(pieces));
                return runs;
            }

            var current = new List<Piece> { pieces[splitStart] };
            for (var offset = 1; offset < n; offset++)
            {
                var i = (splitStart + offset) % n;
                var prev = pieces[(i - 1 + n) % n];
                var curr = pieces[i];

                if (CanMerge(prev, curr))
                {
                    current.Add(curr);
                }
                else
                {
                    runs.Add(current);
                    current = new List<Piece> { curr };
                }
            }

            runs.Add(current);
            return runs;
        }

        private static bool CanMerge(Piece prev, Piece next)
        {
            if (prev.IsCurved || next.IsCurved)
                return false;

            var dir1 = prev.End - prev.Start;
            var dir2 = next.End - next.Start;

            if (dir1.GetLength() < 1e-9 || dir2.GetLength() < 1e-9)
                return false;

            dir1 = dir1.Normalize();
            dir2 = dir2.Normalize();

            if (dir1.DotProduct(dir2) < 1 - DirectionDotTolerance)
                return false;

            var toNext = next.Start - prev.Start;
            var along = toNext.DotProduct(dir1);
            var perpendicular = toNext - dir1.Multiply(along);

            return perpendicular.GetLength() <= FeetOf(CollinearToleranceMm);
        }

        private static XYZ Flat(XYZ point)
        {
            return new XYZ(point.X, point.Y, point.Z);
        }

        private static double FeetOf(double millimeters)
        {
            return UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);
        }

        private sealed class Piece
        {
            public XYZ Start;
            public XYZ End;
            public ElementId ElementId;
            public bool IsCurved;
        }
    }
}
