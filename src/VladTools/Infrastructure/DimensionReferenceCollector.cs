using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>Итог сбора ссылок под одну нитку: либо готовый набор для <c>NewDimension</c>, либо причина отказа.</summary>
    internal sealed class DimensionReferenceResult
    {
        private DimensionReferenceResult(ReferenceArray references, string failureReason)
        {
            References = references;
            FailureReason = failureReason;
        }

        public ReferenceArray References { get; }
        public string FailureReason { get; }
        public bool Success => References != null;

        public static DimensionReferenceResult Ok(ReferenceArray references)
        {
            return new DimensionReferenceResult(references, null);
        }

        public static DimensionReferenceResult Fail(string reason)
        {
            return new DimensionReferenceResult(null, reason);
        }
    }

    /// <summary>
    /// Собирает <see cref="ReferenceArray"/> под нитку размеров по каталогу видов
    /// (<see cref="DimensionChainKind"/>). Один экземпляр — на весь запуск команды: геометрия
    /// стены читается один раз и кэшируется по <c>ElementId</c>, иначе на модели в сотни
    /// помещений команда встанет.
    ///
    /// Общий приём для всех видов: у стены (её solid-геометрии) собираются плоские грани,
    /// нормаль которых параллельна направлению стороны. Для прямой стены это ровно её торцы
    /// и грани откосов проёмов — вырезка проёма всегда добавляет пару таких граней (см. CLAUDE.md,
    /// «Ключевые решения» — проверено разведкой этапа 0). Точки проецируются на ось стороны
    /// и сортируются; дубликаты ближе 1 мм схлопываются.
    /// </summary>
    internal sealed class DimensionReferenceCollector
    {
        private const double SameOffsetToleranceMm = 1.0;
        private const double JointToleranceMm = 5.0;
        private const double NormalDotTolerance = 0.99; // ~8°, запас на неточности геометрии стены
        private const double PerpendicularDotTolerance = 0.05; // ~87–93° считаются перпендикулярными

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
        /// Собирает ссылки для одной стороны. <paramref name="loopSides"/> — все стороны той же
        /// петли того же помещения: нитке «Перегородки» нужны соседние (по петле) стороны,
        /// чтобы найти примыкающую перегородку.
        /// </summary>
        public DimensionReferenceResult Collect(RoomSide side, IReadOnlyList<RoomSide> loopSides, DimensionChainKind kind)
        {
            if (side.IsCurved)
                return DimensionReferenceResult.Fail("сторона криволинейна — авторазмеры её пропускают");

            if (!side.HasWall)
                return DimensionReferenceResult.Fail("на стороне нет ни одной стены");

            switch (kind)
            {
                case DimensionChainKind.Overall:
                    return Build(Corners(side, loopSides));

                case DimensionChainKind.OpeningEdges:
                {
                    var corners = Corners(side, loopSides);
                    return Build(Merge(corners, Interior(OwnWallHits(side), corners)));
                }

                case DimensionChainKind.WallFaces:
                    return CollectWallFaces(side, loopSides);

                case DimensionChainKind.OpeningCenters:
                    return CollectOpeningCenters(side, loopSides);

                case DimensionChainKind.Partitions:
                    return CollectPartitions(side, loopSides);

                case DimensionChainKind.Combined:
                    return CollectCombined(side, loopSides);

                default:
                    return DimensionReferenceResult.Fail("неизвестный вид нитки");
            }
        }

        // ───────────────────────────── виды ниток ─────────────────────────────

        private DimensionReferenceResult CollectWallFaces(RoomSide side, IReadOnlyList<RoomSide> loopSides)
        {
            if (side.WallJointOffsets.Count == 0)
                return DimensionReferenceResult.Fail("сторона собрана из одной стены без стыков — нитка «Грани стен» тут не нужна");

            var corners = Corners(side, loopSides);
            var own = Interior(OwnWallHits(side), corners);
            var joints = own
                .Where(hit => side.WallJointOffsets.Any(offset => Math.Abs(offset - hit.T) <= FeetOf(JointToleranceMm)))
                .ToList();

            var result = new List<Hit>(corners);
            result.AddRange(joints);

            return Build(result);
        }

        private DimensionReferenceResult CollectOpeningCenters(RoomSide side, IReadOnlyList<RoomSide> loopSides)
        {
            var corners = Corners(side, loopSides);
            var centers = new List<Hit>();

            foreach (var wallId in side.WallIds)
            {
                var wall = _doc.GetElement(wallId) as Wall;
                if (wall == null)
                    continue;

                foreach (var insertId in SafeInserts(wall))
                {
                    var instance = _doc.GetElement(insertId) as FamilyInstance;
                    if (instance == null || !IsDoorOrWindow(instance))
                        continue;

                    centers.AddRange(CenterHits(instance, side));
                }
            }

            centers = Interior(centers, corners);

            if (centers.Count == 0)
            {
                return DimensionReferenceResult.Fail(
                    "у проёмов на этой стороне нет осевой плоскости (семейство её не публикует) — " +
                    "нитка «Оси проёмов» недоступна");
            }

            var result = new List<Hit>(corners);
            result.AddRange(centers);
            return Build(result);
        }

        private DimensionReferenceResult CollectPartitions(RoomSide side, IReadOnlyList<RoomSide> loopSides)
        {
            var corners = Corners(side, loopSides);
            var neighborHits = Interior(NeighborPartitionHits(side, loopSides), corners);

            if (neighborHits.Count == 0)
                return DimensionReferenceResult.Fail("к этой стороне не примыкает ни одна перегородка");

            var result = new List<Hit>(corners);
            result.AddRange(neighborHits);
            return Build(result);
        }

        private DimensionReferenceResult CollectCombined(RoomSide side, IReadOnlyList<RoomSide> loopSides)
        {
            var corners = Corners(side, loopSides);
            var extra = new List<Hit>();
            extra.AddRange(OwnWallHits(side));

            foreach (var wallId in side.WallIds)
            {
                var wall = _doc.GetElement(wallId) as Wall;
                if (wall == null)
                    continue;

                foreach (var insertId in SafeInserts(wall))
                {
                    var instance = _doc.GetElement(insertId) as FamilyInstance;
                    if (instance != null && IsDoorOrWindow(instance))
                        extra.AddRange(CenterHits(instance, side));
                }
            }

            extra.AddRange(NeighborPartitionHits(side, loopSides));

            var result = new List<Hit>(corners);
            result.AddRange(Interior(extra, corners));

            if (Dedup(result).Count < 2)
                return DimensionReferenceResult.Fail("на стороне нечего засекать — ни граней, ни проёмов, ни перегородок");

            return Build(result);
        }

        // ───────────────────────────── общие помощники ─────────────────────────────

        /// <summary>Грани собственных стен стороны, чья нормаль параллельна оси стороны — торцы и откосы проёмов разом.</summary>
        private List<Hit> OwnWallHits(RoomSide side)
        {
            var hits = new List<Hit>();
            foreach (var wallId in side.WallIds)
                hits.AddRange(FaceHitsForWall(wallId, side.Direction, side));

            return hits;
        }

        /// <summary>
        /// Две угловые точки стороны. Берутся **не** с торцевых граней самой стороны — у стены
        /// в реальном углу Revit почти всегда строит скошенный (митрованный) торец, чтобы соседние
        /// стены сходились без зазора, и нормаль такой грани уже не параллельна оси стороны —
        /// её не находит <see cref="FaceHitsForWall"/> (см. допуск <see cref="NormalDotTolerance"/>),
        /// а следующая по счёту подходящая грань может оказаться где угодно в глубине стены —
        /// отсюда и разнобой точек на скриншоте, который это и вскрыл.
        ///
        /// Продольная грань соседней (перпендикулярной) стены, наоборот, никогда не митруется —
        /// это плоская грань на всю длину стены. Поэтому угол ищется через неё: у соседней стороны
        /// петли берётся её собственная стена, а среди её граней — ближайшая к общему углу
        /// (см. <see cref="CornerFromNeighbor"/>). Соседа нет, он не перпендикулярен или у него
        /// тоже нет подходящей грани — тогда используется старый способ (крайняя точка среди
        /// собственных граней стороны) как запасной вариант, а не отказ строить нитку целиком.
        /// </summary>
        private List<Hit> Corners(RoomSide side, IReadOnlyList<RoomSide> loopSides)
        {
            var own = Dedup(OwnWallHits(side));

            var start = CornerFromNeighbor(side, loopSides, previous: true)
                        ?? (own.Count > 0 ? own[0] : null);

            var end = CornerFromNeighbor(side, loopSides, previous: false)
                      ?? (own.Count > 0 ? own[own.Count - 1] : null);

            var result = new List<Hit>();
            if (start != null)
                result.Add(start);
            if (end != null)
                result.Add(end);

            return result;
        }

        /// <summary>Грань соседней (по петле) стены, ближайшая к общему с ней углу нашей стороны.</summary>
        private Hit CornerFromNeighbor(RoomSide side, IReadOnlyList<RoomSide> loopSides, bool previous)
        {
            var neighbor = LoopNeighbor(side, loopSides, previous);
            if (neighbor == null)
                return null;

            var candidates = new List<Hit>();
            foreach (var wallId in neighbor.WallIds)
                candidates.AddRange(FaceHitsForWall(wallId, side.Direction, side));

            if (candidates.Count == 0)
                return null;

            return previous
                ? candidates.OrderBy(hit => Math.Abs(hit.T)).First()
                : candidates.OrderBy(hit => Math.Abs(hit.T - side.Length)).First();
        }

        /// <summary>
        /// Перпендикулярные стороны той же петли, примыкающие слева/справа к нашей — то есть
        /// перегородки, из-за которых граница делает заход внутрь помещения и обратно
        /// (см. RoomSideBuilder: такой заход всегда рвёт слияние на отдельные стороны), а на
        /// обычном прямоугольном углу — соседняя стена того же помещения. Их грани ищутся тем же
        /// способом, что и свои, но нормаль проверяется относительно оси нашей стороны — у
        /// перпендикулярной стены это как раз её продольные грани.
        /// </summary>
        private List<Hit> NeighborPartitionHits(RoomSide side, IReadOnlyList<RoomSide> loopSides)
        {
            var result = new List<Hit>();

            foreach (var previous in new[] { true, false })
            {
                var neighbor = LoopNeighbor(side, loopSides, previous);
                if (neighbor == null)
                    continue;

                foreach (var wallId in neighbor.WallIds)
                    result.AddRange(FaceHitsForWall(wallId, side.Direction, side));
            }

            return result;
        }

        /// <summary>
        /// Соседняя по петле сторона (предыдущая или следующая по <see cref="RoomSide.Index"/>,
        /// с переходом через начало петли по кругу) — но только если она вообще может дать угол:
        /// не криволинейная, со своей стеной и перпендикулярная нашей. Иначе — null, и вызывающий
        /// код сам решает, чем заменить недостающий угол.
        /// </summary>
        private static RoomSide LoopNeighbor(RoomSide side, IReadOnlyList<RoomSide> loopSides, bool previous)
        {
            var sameLoop = loopSides.Where(s => s.LoopIndex == side.LoopIndex).OrderBy(s => s.Index).ToList();
            if (sameLoop.Count < 2)
                return null;

            var position = sameLoop.FindIndex(s => s.Index == side.Index);
            if (position < 0)
                return null;

            var neighbor = previous
                ? sameLoop[(position - 1 + sameLoop.Count) % sameLoop.Count]
                : sameLoop[(position + 1) % sameLoop.Count];

            if (ReferenceEquals(neighbor, side) || neighbor.IsCurved || !neighbor.HasWall)
                return null;

            if (Math.Abs(neighbor.Direction.DotProduct(side.Direction)) > PerpendicularDotTolerance)
                return null; // не перпендикулярна — не даёт угла (залом не на 90°, продолжение той же линии)

            return neighbor;
        }

        /// <summary>Объединяет несколько списков в один — просто чтобы не плодить AddRange на местах вызова.</summary>
        private static List<Hit> Merge(List<Hit> first, List<Hit> second)
        {
            var result = new List<Hit>(first);
            result.AddRange(second);
            return result;
        }

        /// <summary>
        /// Оставляет только точки строго между двумя угловыми — то, что вне этого диапазона,
        /// не проём и не стык, а сама угловая грань (или её сосед по стене), просто найденная
        /// вторично через собственную геометрию стороны.
        ///
        /// Без этого отсева стена, не идеально подрезанная в углу (нет чистого митра — торец
        /// остаётся плоским и параллельным оси), давала свою угловую грань ещё раз, вдобавок
        /// к настоящему углу от соседней стены (см. <see cref="Corners"/>): та же точка снаружи
        /// оказывалась чуть смещена — на её собственное продолжение за пределы угла — и рядом
        /// с настоящим углом появлялась лишняя короткая засечка размером примерно в толщину
        /// соседней стены. Отсекается по допуску <see cref="SameOffsetToleranceMm"/> — тому же,
        /// каким <see cref="Dedup"/> схлопывает совпадающие точки, чтобы граница диапазона
        /// не резала точку, которая и так по сути совпадает с угловой.
        /// </summary>
        private static List<Hit> Interior(List<Hit> hits, List<Hit> corners)
        {
            if (corners.Count < 2)
                return hits;

            var lo = Math.Min(corners[0].T, corners[corners.Count - 1].T);
            var hi = Math.Max(corners[0].T, corners[corners.Count - 1].T);
            var margin = FeetOf(SameOffsetToleranceMm);

            return hits.Where(hit => hit.T > lo + margin && hit.T < hi - margin).ToList();
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
                // падаем ниже, на bounding box
            }

            try
            {
                var box = instance.get_BoundingBox(null);
                if (box != null)
                    return (box.Min + box.Max) * 0.5;
            }
            catch (Exception)
            {
                // не смогли — просто нет точки
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
                // Без общих (Shared) — их считает хозяин исходной модели; без связей — только открытый документ.
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

        // ───────────────────────────── сборка результата ─────────────────────────────

        private static DimensionReferenceResult Build(List<Hit> hits)
        {
            var distinct = Dedup(hits);
            if (distinct.Count < 2)
                return DimensionReferenceResult.Fail("засекать нечего — меньше двух точек на нитку");

            var array = new ReferenceArray();
            foreach (var hit in distinct)
                array.Append(hit.Reference);

            return DimensionReferenceResult.Ok(array);
        }

        /// <summary>Сортирует по оси стороны и схлопывает точки ближе 1 мм — та же грань, найденная дважды.</summary>
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
