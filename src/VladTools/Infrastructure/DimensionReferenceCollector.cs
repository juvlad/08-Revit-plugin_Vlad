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
        private DimensionReferenceResult(ReferenceArray references, string failureReason, string warning)
        {
            References = references;
            FailureReason = failureReason;
            Warning = warning;
        }

        public ReferenceArray References { get; }
        public string FailureReason { get; }

        /// <summary>
        /// Нитку построить удалось, но не полностью честно — например, угол стороны не нашёлся
        /// и взята ближайшая грань. Пустая строка — всё в порядке. Уходит отдельным разделом
        /// отчёта: молча отдать укороченную нитку хуже, чем не отдать её вовсе (см. CLAUDE.md).
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

        /// <summary>
        /// Насколько далеко от геометрического конца стороны ещё можно считать грань «углом».
        /// Не ноль, потому что граница помещения не обязана лежать на грани стены: при
        /// <c>SpatialElementBoundaryLocation.Center</c> она идёт по осевой, и грань примыкающей
        /// стены отстоит от конца стороны на половину её толщины. Но и не бесконечность —
        /// именно бесконечный поиск («возьми крайнюю грань, какая есть») и давал укороченную
        /// нитку: у стены со скошенным торцом крайней гранью оказывался откос первого проёма
        /// в полуметре от угла, и нитка начиналась от двери, а не от стены.
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
        /// Собирает ссылки для одной стороны. <paramref name="loopSides"/> — все стороны той же
        /// петли того же помещения: угол нитки и нитка «Перегородки» ищутся по соседним сторонам.
        /// <paramref name="includeAdjacentThickness"/> — захватывать ли крайними засечками обе
        /// грани примыкающей стены, то есть показывать её толщину (см. <see cref="BuildCorners"/>).
        /// </summary>
        public DimensionReferenceResult Collect(
            RoomSide side,
            IReadOnlyList<RoomSide> loopSides,
            DimensionChainKind kind,
            bool includeAdjacentThickness)
        {
            if (side.IsCurved)
                return DimensionReferenceResult.Fail("сторона криволинейна — авторазмеры её пропускают");

            if (!side.HasWall)
                return DimensionReferenceResult.Fail("на стороне нет ни одной стены");

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
                    return DimensionReferenceResult.Fail("неизвестный вид нитки");
            }
        }

        // ───────────────────────────── виды ниток ─────────────────────────────

        private DimensionReferenceResult CollectWallFaces(RoomSide side, CornerSet corners)
        {
            if (side.WallJointOffsets.Count == 0)
                return DimensionReferenceResult.Fail("сторона собрана из одной стены без стыков — нитка «Грани стен» тут не нужна");

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
                    "у проёмов на этой стороне нет осевой плоскости (семейство её не публикует) — " +
                    "нитка «Оси проёмов» недоступна");
            }

            return Build(Merge(corners.Hits, centers), corners);
        }

        private DimensionReferenceResult CollectPartitions(RoomSide side, IReadOnlyList<RoomSide> loopSides, CornerSet corners)
        {
            var neighborHits = corners.Interior(NeighborPartitionHits(side, loopSides));

            if (neighborHits.Count == 0)
                return DimensionReferenceResult.Fail("к этой стороне не примыкает ни одна перегородка");

            return Build(Merge(corners.Hits, neighborHits), corners);
        }

        /// <summary>
        /// «Всё вместе» — то, из чего состоит нитка кладочного плана: толщина примыкающей стены,
        /// простенки, откосы проёмов и толщины примыкающих перегородок, всё на одной линии.
        ///
        /// Осей проёмов здесь намеренно нет, хотя по имени вида их можно было бы ждать: ось
        /// делит каждый проём пополам и добавляет засечку посреди каждой двери — на кладочном
        /// плане такой засечки нет ни разу (сверено с образцом «Кладочный план. Фрагмент 2»),
        /// а нитку она делает вдвое гуще и нечитаемой. Кому нужны оси — берёт отдельную
        /// нитку «Оси проёмов» рядом.
        /// </summary>
        private DimensionReferenceResult CollectCombined(RoomSide side, IReadOnlyList<RoomSide> loopSides, CornerSet corners)
        {
            var extra = new List<Hit>();
            extra.AddRange(OwnWallHits(side));
            extra.AddRange(NeighborPartitionHits(side, loopSides));

            var result = Merge(corners.Hits, corners.Interior(extra));

            if (Dedup(result).Count < 2)
                return DimensionReferenceResult.Fail("на стороне нечего засекать — ни граней, ни проёмов, ни перегородок");

            return Build(result, corners);
        }

        // ───────────────────────────── углы стороны ─────────────────────────────

        /// <summary>
        /// Две (или четыре — с толщинами) угловые точки стороны.
        ///
        /// Угол берётся **не** с торцевой грани самой стороны: у стены в реальном углу Revit
        /// почти всегда строит скошенный (митрованный) торец, чтобы соседние стены сходились
        /// без зазора, и нормаль такой грани уже не параллельна оси стороны — её не находит
        /// <see cref="FaceHitsForWall"/> (см. допуск <see cref="NormalDotTolerance"/>).
        /// Продольная грань соседней (перпендикулярной) стены, наоборот, не митруется никогда —
        /// это плоская грань на всю длину стены, поэтому угол ищется через неё.
        ///
        /// Когда <paramref name="includeThickness"/> включён, с каждого конца берётся не одна
        /// грань соседней стены, а обе — ближняя (собственно угол) и дальняя, за углом. Первым
        /// и последним звеном нитки тогда становится толщина примыкающей стены: ровно так
        /// устроена каждая нитка на кладочном плане («120 | 3775 | 120»), и ровно этого не
        /// хватало — толщина появлялась то с одной стороны, то ни с одной, в зависимости от
        /// того, попал ли торец своей стены под допуск нормали.
        ///
        /// Дальняя грань берётся только если она лежит **за** углом, вне пролёта стороны:
        /// во внутреннем (вогнутом) углу обе грани соседа стоят внутри пролёта, и там толщина
        /// не звено нитки, а обычная перегородка — её найдёт <see cref="NeighborPartitionHits"/>.
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
                approximate.Add("начало");
            if (end.IsApproximate)
                approximate.Add("конец");

            if (approximate.Count > 0)
            {
                set.Warning = "не нашёлся угол стороны (" + string.Join(" и ", approximate) + ") — " +
                              "нитка построена от ближайшей грани, проверьте её вручную";
            }

            return set;
        }

        /// <summary>
        /// Угол одного конца стороны: ближняя грань (сам угол) и, если просили и она есть,
        /// дальняя за углом (даёт толщину примыкающей стены). Порядок поиска — от надёжного
        /// к запасному, и последняя ступень честно помечается как приблизительная.
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
                // «За углом» — наружу от пролёта стороны: для начала это меньшие T, для конца большие.
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

            // Соседа нет (граница помещения без стены, залом не на 90°) или его грани не читаются —
            // пробуем торец собственной стены, но только если он и правда стоит у конца стороны.
            inner = Nearest(own, target, window, previous);
            if (inner != null)
                return new EndCorner { Inner = inner };

            // Ни того, ни другого: сторона начинается посреди стены (коридор за разделителем
            // помещений) или торец скошен и не читается. Ссылки в этой точке не существует —
            // берём крайнюю доступную грань, но помечаем нитку как приблизительную.
            if (own.Count == 0)
                return new EndCorner();

            return new EndCorner
            {
                Inner = previous ? own[0] : own[own.Count - 1],
                IsApproximate = true
            };
        }

        /// <summary>
        /// Ближайшая к <paramref name="target"/> точка, но не дальше <paramref name="window"/> от неё.
        ///
        /// При равном расстоянии берётся та, что лежит внутрь пролёта стороны, а не наружу. Ничья
        /// тут не выдумана: при <c>SpatialElementBoundaryLocation.Center</c> граница идёт по осевой,
        /// и обе грани примыкающей стены отстоят от конца стороны ровно на половину её толщины —
        /// без явного правила выбор зависел бы от порядка граней в геометрии, то есть был бы разным
        /// на одинаковых углах.
        /// </summary>
        private static Hit Nearest(List<Hit> hits, double target, double window, bool previous)
        {
            return hits
                .Where(hit => Math.Abs(hit.T - target) <= window)
                .OrderBy(hit => Math.Abs(hit.T - target))
                .ThenBy(hit => previous ? -hit.T : hit.T)
                .FirstOrDefault();
        }

        /// <summary>Ближняя и дальняя грани одного конца стороны — итог работы <see cref="FindEndCorner"/>.</summary>
        private sealed class EndCorner
        {
            /// <summary>Грань в самом углу — та, от которой считается длина стороны.</summary>
            public Hit Inner;

            /// <summary>Грань за углом (даёт толщину примыкающей стены); null — не просили или её нет.</summary>
            public Hit Outer;

            /// <summary>Угол не найден по геометрии, взята ближайшая грань — нитка требует проверки.</summary>
            public bool IsApproximate;
        }

        /// <summary>
        /// Угловые точки стороны и диапазон между ними — всё, что нужно остальным видам ниток,
        /// чтобы не пересобирать углы по второму разу и не спорить с ними о границах.
        /// </summary>
        private sealed class CornerSet
        {
            public readonly List<Hit> Hits = new List<Hit>();
            public bool HasRange;
            public double Lo;
            public double Hi;
            public string Warning;

            /// <summary>
            /// Оставляет только точки строго между двумя угловыми — то, что вне этого диапазона,
            /// не проём и не стык, а сама угловая грань (или грань примыкающей стены за углом),
            /// просто найденная вторично через геометрию стороны.
            ///
            /// Без отсева стена, не идеально подрезанная в углу, давала свою угловую грань ещё
            /// раз, вдобавок к настоящему углу от соседней стены, — и рядом с углом появлялась
            /// лишняя короткая засечка примерно в толщину соседней стены. Допуск тот же, каким
            /// <see cref="Dedup"/> схлопывает совпадающие точки, чтобы граница диапазона не резала
            /// точку, которая и так по сути совпадает с угловой.
            /// </summary>
            public List<Hit> Interior(List<Hit> hits)
            {
                if (!HasRange)
                    return hits;

                var margin = FeetOf(SameOffsetToleranceMm);
                return hits.Where(hit => hit.T > Lo + margin && hit.T < Hi - margin).ToList();
            }
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

        /// <summary>Осевые плоскости дверей и окон в стенах стороны — только для нитки «Оси проёмов».</summary>
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
                var neighbor = LoopNeighbor(side, loopSides, previous, walk: false);
                if (neighbor == null)
                    continue;

                foreach (var wallId in neighbor.WallIds)
                    result.AddRange(FaceHitsForWall(wallId, side.Direction, side));
            }

            return result;
        }

        /// <summary>
        /// Соседняя по петле сторона (в сторону начала или конца), способная дать угол:
        /// не криволинейная, со своей стеной и перпендикулярная нашей.
        ///
        /// <paramref name="walk"/> разрешает шагать по петле дальше первого соседа. Это нужно
        /// поиску угла: между двумя стенами угла нередко стоит короткий кусок границы без стены
        /// (разделитель помещений, дверь в проёме без стены) или коллинеарное продолжение — и на
        /// нём поиск раньше обрывался, а нитка уезжала на запасной вариант. Стороны, параллельные
        /// нашей, при этом пропускаются как та же линия; первая же непараллельная и
        /// неперпендикулярная (залом не на 90°) обрывает поиск — за ней угла уже нет.
        ///
        /// Поиску перегородок шагать, наоборот, **нельзя**: угол проверяется ещё и по расстоянию
        /// (см. <see cref="CornerWindowMm"/>), а засечка перегородки — нет, и дальняя стена,
        /// спроецированная на нашу ось где-то посреди пролёта, стала бы засечкой на пустом месте.
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
                    return null; // за скруглением угла нет

                var dot = Math.Abs(neighbor.Direction.DotProduct(side.Direction));

                if (dot <= PerpendicularDotTolerance)
                    return neighbor.HasWall ? neighbor : null; // перпендикулярна — это и есть угол

                if (dot >= NormalDotTolerance)
                    continue; // продолжение той же линии (вставка без стены) — смотрим дальше

                return null; // залом не на 90° — угла тут нет
            }

            return null;
        }

        /// <summary>Объединяет несколько списков в один — просто чтобы не плодить AddRange на местах вызова.</summary>
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

        private static DimensionReferenceResult Build(List<Hit> hits, CornerSet corners)
        {
            var distinct = Dedup(hits);
            if (distinct.Count < 2)
                return DimensionReferenceResult.Fail("засекать нечего — меньше двух точек на нитку");

            var array = new ReferenceArray();
            foreach (var hit in distinct)
                array.Append(hit.Reference);

            return DimensionReferenceResult.Ok(array, corners.Warning);
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
