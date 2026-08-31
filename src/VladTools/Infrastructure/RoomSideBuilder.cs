using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Собирает прямые стороны границы помещения из петель <c>GetBoundarySegments</c>.
    ///
    /// Каждая петля — это ломаная из отрезков, по одному на каждый элемент, к которому
    /// примыкает граница (кусок стены между двумя проёмами — тоже отдельный отрезок).
    /// Соседние отрезки сливаются в одну сторону, если они сонаправлены и лежат на одной
    /// прямой — иначе одна и та же стена превращалась бы в несколько параллельных ниток
    /// вместо одной длинной. Направление нормали внутрь помещения не берётся из порядка
    /// петли «как есть» — оно проверяется через <see cref="Room.IsPointInRoom"/>: полагаться
    /// на то, что Revit всегда обходит петли в одну сторону, нельзя (см. CLAUDE.md, этап 0 плана).
    /// </summary>
    internal static class RoomSideBuilder
    {
        // cos(0.06°) — соседние отрезки одной стены после мелких неточностей построения
        // границы почти всегда идеально сонаправлены; порог жёсткий специально, чтобы
        // случайный залом в 1-2° (примыкание другой стены) не склеился в одну сторону.
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
                    continue; // вырожденный отрезок — Revit иногда отдаёт такие в стыках

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
                // У кривого отрезка нет прямого направления — берём хорду только для того,
                // чтобы сторону можно было показать в отчёте; нитки по ней не строятся
                // (см. DimensionReferenceCollector — IsCurved отсекается раньше).
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

                // Стык считается только там, где сосед — другая стена: между двумя кусками
                // одной и той же стены (разрезанными проёмом) стоит грань откоса, а не стык стен.
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
        /// Нормаль «внутрь» не берётся из направления петли как данность: сырое произведение
        /// <c>BasisZ × direction</c> проверяется точкой в 50 мм от стороны через
        /// <see cref="Room.IsPointInRoom"/>, и при отрицательном ответе разворачивается.
        /// Не удалось проверить (например, точка легла ровно на грань) — возвращается сырое
        /// значение: ошибиться тут значит поставить размер снаружи, а не сломать модель.
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
                // не смогли проверить — отдаём сырое значение как есть
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
                // Все отрезки слились в один — вырожденный случай (петля без единого залома),
                // на практике у замкнутого помещения не встречается, но не должен виснуть.
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
