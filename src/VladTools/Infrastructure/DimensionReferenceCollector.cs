using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>The result of collecting references for one chain: either a ready set for <c>NewDimension</c>, or the reason it failed.</summary>
    internal sealed class DimensionReferenceResult
    {
        private DimensionReferenceResult(ReferenceArray references, string failureReason, string warning)
        {
            References = references;
            FailureReason = failureReason;
            Warning = warning;
        }

        public ReferenceArray References { get; }
        public string FailureReason { get; }

        /// <summary>
        /// The chain was built, but not entirely honestly — the corner of the side, say, was not
        /// found and the nearest face was taken instead. An empty string means everything is fine.
        /// This goes into a report section of its own: silently handing over a shortened chain is
        /// worse than not handing one over at all (see CLAUDE.md).
        /// </summary>
        public string Warning { get; }

        public bool Success => References != null;

        public static DimensionReferenceResult Ok(ReferenceArray references, string warning)
        {
            return new DimensionReferenceResult(references, null, warning);
        }

        public static DimensionReferenceResult Fail(string reason)
        {
            return new DimensionReferenceResult(null, reason, null);
        }
    }

    /// <summary>
    /// Collects a <see cref="ReferenceArray"/> for a dimension chain, from the catalogue of chain
    /// kinds (<see cref="DimensionChainKind"/>). One instance lives for the whole run of the
    /// command: wall geometry is read once and cached by <c>ElementId</c>, otherwise the command
    /// would grind to a halt on a model with hundreds of rooms.
    ///
    /// The shared trick behind every kind: a wall's solid geometry is searched for the planar
    /// faces whose normal is parallel to the side's direction. For a straight wall these are
    /// exactly its ends and the jamb faces of its openings — cutting an opening always adds a pair
    /// of such faces (see CLAUDE.md, "Key decisions" — confirmed by the stage-0 investigation). The
    /// points are projected onto the side's axis and sorted; duplicates closer than 1 mm collapse
    /// into one.
    /// </summary>
    internal sealed class DimensionReferenceCollector
    {
        private const double SameOffsetToleranceMm = 1.0;
        private const double JointToleranceMm = 5.0;
        private const double NormalDotTolerance = 0.99; // ~8°, margin for wall-geometry inaccuracies
        private const double PerpendicularDotTolerance = 0.05; // ~87–93° is treated as perpendicular

        /// <summary>
        /// How far from the geometric end of a side a face can still count as a "corner". Not
        /// zero, because a room boundary need not lie on a wall face: at
        /// <c>SpatialElementBoundaryLocation.Center</c> it runs along the centreline, and the
        /// adjoining wall's face sits half its thickness away from the end of the side. But not
        /// infinite either — it was precisely an unbounded search ("take whatever end face there
        /// is") that produced a shortened chain: on a wall with a mitred end, the nearest face
        /// turned out to be the jamb of the first opening, half a metre from the corner, and the
        /// chain started at the door rather than at the wall.
        /// </summary>
        private const double CornerWindowMm = 600.0;

        private readonly Document _doc;
        private readonly Options _geometryOptions;
        private readonly Dictionary<long, List<PlanarFace>> _faceCache = new Dictionary<long, List<PlanarFace>>();

        public DimensionReferenceCollector(Document doc)
        {
            _doc = doc;
            _geometryOptions = new Options
            {
                ComputeReferences = true,
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = false
            };
        }

        /// <summary>
        /// Collects the references for one side. <paramref name="loopSides"/> is every side of the
        /// same loop of the same room: the chain's corner and the "Partitions" chain are found
        /// through the neighbouring sides. <paramref name="includeAdjacentThickness"/> says whether
        /// the end ticks should pick up both faces of the adjoining wall, that is, show its
        /// thickness (see <see cref="BuildCorners"/>).
        /// </summary>
        public DimensionReferenceResult Collect(
            RoomSide side,
            IReadOnlyList<RoomSide> loopSides,
            DimensionChainKind kind,
            bool includeAdjacentThickness)
        {
            if (side.IsCurved)
                return DimensionReferenceResult.Fail("the side is curved — auto dimensions skip it");

            if (!side.HasWall)
                return DimensionReferenceResult.Fail("the side has no wall at all");

            var corners = BuildCorners(side, loopSides, includeAdjacentThickness);

            switch (kind)
            {
                case DimensionChainKind.Overall:
                    return Build(corners.Hits, corners);

                case DimensionChainKind.OpeningEdges:
                    return Build(Merge(corners.Hits, corners.Interior(OwnWallHits(side))), corners);

                case DimensionChainKind.WallFaces:
                    return CollectWallFaces(side, corners);

                case DimensionChainKind.OpeningCenters:
                    return CollectOpeningCenters(side, corners);

                case DimensionChainKind.Partitions:
                    return CollectPartitions(side, loopSides, corners);

                case DimensionChainKind.Combined:
                    return CollectCombined(side, loopSides, corners);

                default:
                    return DimensionReferenceResult.Fail("unknown chain kind");
            }
        }

        // ───────────────────────────── chain kinds ─────────────────────────────

        private DimensionReferenceResult CollectWallFaces(RoomSide side, CornerSet corners)
        {
            if (side.WallJointOffsets.Count == 0)
                return DimensionReferenceResult.Fail("the side is made of a single wall with no joints — the \"Wall faces\" chain is not needed here");

            var joints = corners.Interior(OwnWallHits(side))
                .Where(hit => side.WallJointOffsets.Any(offset => Math.Abs(offset - hit.T) <= FeetOf(JointToleranceMm)))
                .ToList();

            return Build(Merge(corners.Hits, joints), corners);
        }

        private DimensionReferenceResult CollectOpeningCenters(RoomSide side, CornerSet corners)
        {
            var centers = corners.Interior(OpeningCenterHits(side));

            if (centers.Count == 0)
            {
                return DimensionReferenceResult.Fail(
                    "the openings on this side have no centre plane (the family does not publish it) — " +
                    "the \"Opening centres\" chain is unavailable");
            }

            return Build(Merge(corners.Hits, centers), corners);
        }

        private DimensionReferenceResult CollectPartitions(RoomSide side, IReadOnlyList<RoomSide> loopSides, CornerSet corners)
        {
            var neighborHits = corners.Interior(NeighborPartitionHits(side, loopSides));

            if (neighborHits.Count == 0)
                return DimensionReferenceResult.Fail("no partition meets this side");

            return Build(Merge(corners.Hits, neighborHits), corners);
        }

        /// <summary>
        /// "All combined" — what a masonry-plan chain is made of: the thickness of the adjoining
        /// wall, piers, opening jambs, and the thickness of the adjoining partitions, all on one line.
        ///
        /// Opening centres are deliberately absent here, even though the name might suggest
        /// otherwise: a centre line splits every opening in half and adds a tick in the middle of
        /// every door — a masonry plan never has such a tick (checked against the "Masonry plan.
        /// Fragment 2" sample), and it would make the chain twice as dense and unreadable. Whoever
        /// needs centres takes the separate "Opening centres" chain alongside this one.
        /// </summary>
        private DimensionReferenceResult CollectCombined(RoomSide side, IReadOnlyList<RoomSide> loopSides, CornerSet corners)
        {
            var extra = new List<Hit>();
            extra.AddRange(OwnWallHits(side));
            extra.AddRange(NeighborPartitionHits(side, loopSides));

            var result = Merge(corners.Hits, corners.Interior(extra));

            if (Dedup(result).Count < 2)
                return DimensionReferenceResult.Fail("nothing to pick up on this side — no faces, no openings, no partitions");

            return Build(result, corners);
        }

        // ───────────────────────────── the corners of a side ─────────────────────────────

        /// <summary>
        /// The two (or four — with thicknesses) corner points of a side.
        ///
        /// A corner is taken **not** from the end face of the side's own wall: at a real corner
        /// Revit almost always builds a mitred end so that the neighbouring walls meet without a
        /// gap, and such a face's normal is no longer parallel to the side's axis — <see
        /// cref="FaceHitsForWall"/> will not find it (see the <see cref="NormalDotTolerance"/>
        /// tolerance). The longitudinal face of the neighbouring (perpendicular) wall, on the other
        /// hand, is never mitred — it is a flat face running the wall's whole length, so the corner
        /// is found through it instead.
        ///
        /// When <paramref name="includeThickness"/> is on, each end takes not one face of the
        /// neighbouring wall but two — the near one (the corner itself) and the far one, beyond the
        /// corner. The first and last link of the chain then becomes the adjoining wall's
        /// thickness: that is exactly how every chain on a masonry plan is built ("120 | 3775 |
        /// 120"), and that was exactly what was missing — the thickness would show up on one side
        /// or on neither, depending on whether the own wall's end happened to fall within the
        /// normal tolerance.
        ///
        /// The far face is taken only if it lies **beyond** the corner, outside the span of the
        /// side: at an internal (concave) corner both of the neighbour's faces sit inside the span,
        /// and there the thickness is not a chain link but an ordinary partition — <see
        /// cref="NeighborPartitionHits"/> will find that instead.
        /// </summary>
        private CornerSet BuildCorners(RoomSide side, IReadOnlyList<RoomSide> loopSides, bool includeThickness)
        {
            var own = Dedup(OwnWallHits(side));

            var start = FindEndCorner(side, loopSides, own, previous: true, includeThickness: includeThickness);
            var end = FindEndCorner(side, loopSides, own, previous: false, includeThickness: includeThickness);

            var set = new CornerSet();

            if (start.Outer != null)
                set.Hits.Add(start.Outer);
            if (start.Inner != null)
                set.Hits.Add(start.Inner);
            if (end.Inner != null)
                set.Hits.Add(end.Inner);
            if (end.Outer != null)
                set.Hits.Add(end.Outer);

            set.HasRange = start.Inner != null && end.Inner != null;
            if (set.HasRange)
            {
                set.Lo = Math.Min(start.Inner.T, end.Inner.T);
                set.Hi = Math.Max(start.Inner.T, end.Inner.T);
            }

            var approximate = new List<string>();
            if (start.IsApproximate)
                approximate.Add("start");
            if (end.IsApproximate)
                approximate.Add("end");

            if (approximate.Count > 0)
            {
                set.Warning = "the corner of the side was not found (" + string.Join(" and ", approximate) + ") — " +
                              "the chain was built from the nearest face, check it by hand";
            }

            return set;
        }

        /// <summary>
        /// The corner of one end of a side: the near face (the corner itself) and, if asked for and
        /// available, the far one beyond the corner (gives the thickness of the adjoining wall).
        /// The search order goes from reliable to fallback, and the last resort is honestly marked
        /// as approximate.
        /// </summary>
        private EndCorner FindEndCorner(
            RoomSide side,
            IReadOnlyList<RoomSide> loopSides,
            List<Hit> own,
            bool previous,
            bool includeThickness)
        {
            var target = previous ? 0.0 : side.Length;
            var window = FeetOf(CornerWindowMm);
            var margin = FeetOf(SameOffsetToleranceMm);

            var candidates = new List<Hit>();
            var neighbor = LoopNeighbor(side, loopSides, previous, walk: true);
            if (neighbor != null)
            {
                foreach (var wallId in neighbor.WallIds)
                    candidates.AddRange(FaceHitsForWall(wallId, side.Direction, side));
            }

            var inner = Nearest(candidates, target, window, previous);
            Hit outer = null;

            if (inner != null && includeThickness)
            {
                // "Beyond the corner" — outward from the span of the side: smaller T for the start, larger T for the end.
                var beyond = candidates
                    .Where(hit => previous ? hit.T < inner.T - margin : hit.T > inner.T + margin)
                    .Where(hit => Math.Abs(hit.T - inner.T) <= window)
                    .ToList();

                outer = beyond.Count == 0
                    ? null
                    : (previous ? beyond.OrderBy(hit => hit.T).First() : beyond.OrderByDescending(hit => hit.T).First());
            }

            if (inner != null)
                return new EndCorner { Inner = inner, Outer = outer };

            // No neighbour (a room boundary with no wall, a kink that is not 90°) or its faces
            // cannot be read — we try the own wall's end, but only if it really does sit at the
            // end of the side.
            inner = Nearest(own, target, window, previous);
            if (inner != null)
                return new EndCorner { Inner = inner };

            // Neither one: the side starts in the middle of a wall (a corridor beyond a room
            // separator) or the end is mitred and unreadable. No reference exists at this point —
            // we take the nearest face available, but mark the chain as approximate.
            if (own.Count == 0)
                return new EndCorner();

            return new EndCorner
            {
                Inner = previous ? own[0] : own[own.Count - 1],
                IsApproximate = true
            };
        }

        /// <summary>
        /// The point nearest to <paramref name="target"/>, but no farther than
        /// <paramref name="window"/> from it.
        ///
        /// On a tie, the one lying inward of the side's span wins over the one lying outward. The
        /// tie is not contrived: at <c>SpatialElementBoundaryLocation.Center</c> the boundary runs
        /// along the centreline, and both faces of the adjoining wall sit exactly half its
        /// thickness from the end of the side — without an explicit rule the choice would depend
        /// on the order of faces in the geometry, that is, it would differ between identical corners.
        /// </summary>
        private static Hit Nearest(List<Hit> hits, double target, double window, bool previous)
        {
            return hits
                .Where(hit => Math.Abs(hit.T - target) <= window)
                .OrderBy(hit => Math.Abs(hit.T - target))
                .ThenBy(hit => previous ? -hit.T : hit.T)
                .FirstOrDefault();
        }

        /// <summary>The near and far faces of one end of a side — the result of <see cref="FindEndCorner"/>.</summary>
        private sealed class EndCorner
        {
            /// <summary>The face right at the corner — the one the side's length is measured from.</summary>
            public Hit Inner;

            /// <summary>The face beyond the corner (gives the adjoining wall's thickness); null if not asked for or absent.</summary>
            public Hit Outer;

            /// <summary>The corner was not found by geometry, the nearest face was taken — the chain needs checking.</summary>
            public bool IsApproximate;
        }

        /// <summary>
        /// A side's corner points and the range between them — everything the other chain kinds
        /// need so as not to rebuild the corners a second time and argue with them about boundaries.
        /// </summary>
        private sealed class CornerSet
        {
            public readonly List<Hit> Hits = new List<Hit>();
            public bool HasRange;
            public double Lo;
            public double Hi;
            public string Warning;

            /// <summary>
            /// Leaves only the points strictly between the two corner ones — anything outside this
            /// range is not an opening and not a joint, but the corner face itself (or the
            /// adjoining wall's face beyond the corner), simply found a second time through the
            /// side's own geometry.
            ///
            /// Without this filter, a wall not perfectly trimmed at the corner would give its own
            /// corner face a second time, on top of the real corner from the neighbouring wall —
            /// and a short spurious tick, roughly the neighbouring wall's thickness, would appear
            /// next to the corner. The tolerance is the same one <see cref="Dedup"/> uses to
            /// collapse matching points, so the edge of the range does not cut off a point that
            /// essentially coincides with the corner anyway.
            /// </summary>
            public List<Hit> Interior(List<Hit> hits)
            {
                if (!HasRange)
                    return hits;

                var margin = FeetOf(SameOffsetToleranceMm);
                return hits.Where(hit => hit.T > Lo + margin && hit.T < Hi - margin).ToList();
            }
        }

        // ───────────────────────────── shared helpers ─────────────────────────────

        /// <summary>The faces of a side's own walls whose normal is parallel to the side's axis — ends and jambs at once.</summary>
        private List<Hit> OwnWallHits(RoomSide side)
        {
            var hits = new List<Hit>();
            foreach (var wallId in side.WallIds)
                hits.AddRange(FaceHitsForWall(wallId, side.Direction, side));

            return hits;
        }

        /// <summary>The centre planes of doors and windows in a side's walls — only for the "Opening centres" chain.</summary>
        private List<Hit> OpeningCenterHits(RoomSide side)
        {
            var centers = new List<Hit>();

            foreach (var wallId in side.WallIds)
            {
                var wall = _doc.GetElement(wallId) as Wall;
                if (wall == null)
                    continue;

                foreach (var insertId in SafeInserts(wall))
                {
                    var instance = _doc.GetElement(insertId) as FamilyInstance;
                    if (instance != null && IsDoorOrWindow(instance))
                        centers.AddRange(CenterHits(instance, side));
                }
            }

            return centers;
        }

        /// <summary>
        /// Perpendicular sides of the same loop meeting ours from the left or the right — that is,
        /// partitions that make the boundary step into the room and back out (see
        /// RoomSideBuilder: such a step always splits the merge into separate sides), or, at an
        /// ordinary right-angle corner, the neighbouring wall of the same room. Their faces are
        /// found the same way as the side's own, but the normal is checked against our side's
        /// axis — for a perpendicular wall that is exactly its longitudinal faces.
        /// </summary>
        private List<Hit> NeighborPartitionHits(RoomSide side, IReadOnlyList<RoomSide> loopSides)
        {
            var result = new List<Hit>();

            foreach (var previous in new[] { true, false })
            {
                var neighbor = LoopNeighbor(side, loopSides, previous, walk: false);
                if (neighbor == null)
                    continue;

                foreach (var wallId in neighbor.WallIds)
                    result.AddRange(FaceHitsForWall(wallId, side.Direction, side));
            }

            return result;
        }

        /// <summary>
        /// The neighbouring side of the loop (towards the start or the end) able to give a corner:
        /// not curved, with its own wall, and perpendicular to ours.
        ///
        /// <paramref name="walk"/> allows stepping past the first neighbour along the loop. The
        /// corner search needs this: between the two walls of a corner there is often a short
        /// stretch of boundary with no wall (a room separator, a door in an opening with no wall)
        /// or a collinear continuation — and the search used to stop there, falling back to the
        /// approximate case. Sides parallel to ours are skipped as the same line along the way; the
        /// first one that is neither parallel nor perpendicular (a kink that is not 90°) stops the
        /// search — there is no corner past it.
        ///
        /// The partition search, in contrast, **must not** walk: a corner is also checked by
        /// distance (see <see cref="CornerWindowMm"/>), but a partition's tick is not, and a far
        /// wall projected onto our axis somewhere in the middle of the span would become a tick out
        /// of nowhere.
        /// </summary>
        private static RoomSide LoopNeighbor(RoomSide side, IReadOnlyList<RoomSide> loopSides, bool previous, bool walk)
        {
            var sameLoop = loopSides.Where(s => s.LoopIndex == side.LoopIndex).OrderBy(s => s.Index).ToList();
            if (sameLoop.Count < 2)
                return null;

            var position = sameLoop.FindIndex(s => s.Index == side.Index);
            if (position < 0)
                return null;

            var limit = walk ? sameLoop.Count : 2;

            for (var step = 1; step < limit; step++)
            {
                var offset = previous ? -step : step;
                var index = ((position + offset) % sameLoop.Count + sameLoop.Count) % sameLoop.Count;

                var neighbor = sameLoop[index];
                if (ReferenceEquals(neighbor, side))
                    return null;

                if (neighbor.IsCurved)
                    return null; // no corner past a rounded one

                var dot = Math.Abs(neighbor.Direction.DotProduct(side.Direction));

                if (dot <= PerpendicularDotTolerance)
                    return neighbor.HasWall ? neighbor : null; // perpendicular — this is the corner

                if (dot >= NormalDotTolerance)
                    continue; // a continuation of the same line (an insert with no wall) — keep looking

                return null; // a kink that is not 90° — no corner here
            }

            return null;
        }

        /// <summary>Merges several lists into one — just so AddRange does not have to be repeated at every call site.</summary>
        private static List<Hit> Merge(List<Hit> first, List<Hit> second)
        {
            var result = new List<Hit>(first);
            result.AddRange(second);
            return result;
        }

        private List<Hit> CenterHits(FamilyInstance instance, RoomSide projectionSide)
        {
            var hits = new List<Hit>();

            IList<Reference> references;
            try
            {
                references = instance.GetReferences(FamilyInstanceReferenceType.CenterLeftRight);
            }
            catch (Exception)
            {
                return hits;
            }

            if (references == null || references.Count == 0)
                return hits;

            var point = RepresentativeLocation(instance);
            if (point == null)
                return hits;

            var t = projectionSide.ProjectOnAxis(point);
            foreach (var reference in references)
            {
                if (reference != null)
                    hits.Add(new Hit { T = t, Reference = reference });
            }

            return hits;
        }

        private static XYZ RepresentativeLocation(FamilyInstance instance)
        {
            try
            {
                var locationPoint = instance.Location as LocationPoint;
                if (locationPoint != null)
                    return locationPoint.Point;
            }
            catch (Exception)
            {
                // fall through to the bounding box below
            }

            try
            {
                var box = instance.get_BoundingBox(null);
                if (box != null)
                    return (box.Min + box.Max) * 0.5;
            }
            catch (Exception)
            {
                // could not — simply no point
            }

            return null;
        }

        private static bool IsDoorOrWindow(Element element)
        {
            try
            {
                var category = element.Category;
                if (category == null)
                    return false;

                var id = category.Id.IntegerValue;
                return id == (int)BuiltInCategory.OST_Doors || id == (int)BuiltInCategory.OST_Windows;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private IList<ElementId> SafeInserts(Wall wall)
        {
            try
            {
                // No shared inserts — those belong to the host of the source model; no links — the open document only.
                return wall.FindInserts(true, false, true, false) ?? new List<ElementId>();
            }
            catch (Exception)
            {
                return new List<ElementId>();
            }
        }

        private List<Hit> FaceHitsForWall(ElementId wallId, XYZ direction, RoomSide projectionSide)
        {
            var hits = new List<Hit>();

            foreach (var face in GetCachedFaces(wallId))
            {
                XYZ normal;
                try
                {
                    normal = face.FaceNormal;
                }
                catch (Exception)
                {
                    continue;
                }

                if (Math.Abs(normal.DotProduct(direction)) < NormalDotTolerance)
                    continue;

                var point = RepresentativeFacePoint(face);
                if (point == null)
                    continue;

                Reference reference;
                try
                {
                    reference = face.Reference;
                }
                catch (Exception)
                {
                    continue;
                }

                if (reference == null)
                    continue;

                hits.Add(new Hit { T = projectionSide.ProjectOnAxis(point), Reference = reference });
            }

            return hits;
        }

        private static XYZ RepresentativeFacePoint(PlanarFace face)
        {
            try
            {
                var bbox = face.GetBoundingBox();
                var uv = new UV((bbox.Min.U + bbox.Max.U) / 2.0, (bbox.Min.V + bbox.Max.V) / 2.0);
                return face.Evaluate(uv);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private List<PlanarFace> GetCachedFaces(ElementId wallId)
        {
            List<PlanarFace> faces;
            if (_faceCache.TryGetValue(wallId.IntegerValue, out faces))
                return faces;

            faces = new List<PlanarFace>();

            try
            {
                var wall = _doc.GetElement(wallId) as Wall;
                if (wall != null)
                {
                    var geometry = wall.get_Geometry(_geometryOptions);
                    if (geometry != null)
                        CollectPlanarFaces(geometry, faces);
                }
            }
            catch (Exception)
            {
                faces = new List<PlanarFace>();
            }

            _faceCache[wallId.IntegerValue] = faces;
            return faces;
        }

        private static void CollectPlanarFaces(GeometryElement geometry, List<PlanarFace> faces)
        {
            foreach (var obj in geometry)
            {
                var solid = obj as Solid;
                if (solid != null)
                {
                    if (solid.Faces == null || solid.Volume <= 1e-9)
                        continue;

                    foreach (Face face in solid.Faces)
                    {
                        var planar = face as PlanarFace;
                        if (planar != null)
                            faces.Add(planar);
                    }

                    continue;
                }

                var instance = obj as GeometryInstance;
                if (instance == null)
                    continue;

                GeometryElement nested;
                try
                {
                    nested = instance.GetInstanceGeometry();
                }
                catch (Exception)
                {
                    continue;
                }

                if (nested != null)
                    CollectPlanarFaces(nested, faces);
            }
        }

        // ───────────────────────────── building the result ─────────────────────────────

        private static DimensionReferenceResult Build(List<Hit> hits, CornerSet corners)
        {
            var distinct = Dedup(hits);
            if (distinct.Count < 2)
                return DimensionReferenceResult.Fail("nothing to pick up — fewer than two points for a chain");

            var array = new ReferenceArray();
            foreach (var hit in distinct)
                array.Append(hit.Reference);

            return DimensionReferenceResult.Ok(array, corners.Warning);
        }

        /// <summary>Sorts along the side's axis and collapses points closer than 1 mm — the same face, found twice.</summary>
        private static List<Hit> Dedup(List<Hit> hits)
        {
            var tolerance = FeetOf(SameOffsetToleranceMm);
            var result = new List<Hit>();

            foreach (var hit in hits.OrderBy(h => h.T))
            {
                if (result.Count > 0 && Math.Abs(result[result.Count - 1].T - hit.T) <= tolerance)
                    continue;

                result.Add(hit);
            }

            return result;
        }

        private static double FeetOf(double millimeters)
        {
            return UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);
        }

        private sealed class Hit
        {
            public double T;
            public Reference Reference;
        }
    }
}
