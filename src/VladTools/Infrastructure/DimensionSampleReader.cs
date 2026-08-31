using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>Итог разбора образцовых размеров: готовые строки для окна и заметки по тем, что не разобрались.</summary>
    internal sealed class DimensionSampleReadResult
    {
        public List<DimensionChainRow> Rows { get; } = new List<DimensionChainRow>();
        public List<string> Messages { get; } = new List<string>();

        /// <summary>Куда смотрело большинство образцовых ниток — используется как направление всего шаблона.</summary>
        public bool Outward { get; set; }
    }

    /// <summary>
    /// Разбирает размеры, поставленные пользователем вручную, в шаблон ниток — это подбор,
    /// а не парсер (см. CLAUDE.md, «Ключевые решения» — тот же статус, что у <c>FormulaParser</c>).
    /// Ошибка подбора не страшна: строка в окне правится руками.
    ///
    /// Для каждого образцового размера ищется помещение и сторона, которым он принадлежит
    /// (по стенам, на которые ссылается размер, и по направлению, совпадающему со стороной),
    /// дальше — знаковое смещение от стороны и вид нитки по составу ссылок.
    /// </summary>
    internal static class DimensionSampleReader
    {
        private const double DirectionDotTolerance = 0.99;
        private const double MergeToleranceMm = 2.0;

        public static DimensionSampleReadResult Read(
            Document doc,
            IReadOnlyList<Dimension> samples,
            SpatialElementBoundaryOptions boundaryOptions)
        {
            var result = new DimensionSampleReadResult();
            var index = BuildWallIndex(doc, boundaryOptions);

            var inward = 0;
            var outward = 0;

            foreach (var dimension in samples)
            {
                string failure;
                var parsed = ReadOne(dimension, index, out failure);

                if (parsed == null)
                {
                    result.Messages.Add(DimensionLabel(dimension) + " — " + failure);
                    continue;
                }

                if (parsed.IsOutward)
                    outward++;
                else
                    inward++;

                MergeInto(result.Rows, parsed);
            }

            result.Outward = outward > inward;

            foreach (var row in result.Rows)
            {
                if (row.OffsetMm < 0)
                    row.OffsetMm = -row.OffsetMm;
            }

            return result;
        }

        // ───────────────────────────── один образцовый размер ─────────────────────────────

        private sealed class ParsedSample
        {
            public DimensionChainKind Kind;
            public double OffsetMm;
            public bool IsOutward;
            public string DimensionTypeName;
            public string SourceLabel;
            public bool Approximate;
        }

        private static ParsedSample ReadOne(Dimension dimension, Dictionary<long, List<RoomSideRef>> index, out string failure)
        {
            failure = null;

            bool hasReferences;
            try
            {
                hasReferences = dimension.AreReferencesAvailable;
            }
            catch (Exception)
            {
                hasReferences = false;
            }

            if (!hasReferences)
            {
                failure = "ссылки размера потеряны (элементы, на которые он опирался, изменились)";
                return null;
            }

            var line = SafeCurve(dimension) as Line;
            if (line == null)
            {
                failure = "не прямолинейный размер (радиальный/угловой) — авторазмеры такие не разбирают";
                return null;
            }

            var references = SafeReferences(dimension);
            if (references.Count == 0)
            {
                failure = "у размера нет ни одной ссылки";
                return null;
            }

            var wallRefs = new List<Reference>();
            var centerRefs = new List<Reference>();

            foreach (var reference in references)
            {
                var element = SafeElement(dimension.Document, reference);

                var wall = element as Wall;
                if (wall != null)
                {
                    wallRefs.Add(reference);
                    continue;
                }

                var instance = element as FamilyInstance;
                if (instance != null && IsCenterReference(instance, reference))
                    centerRefs.Add(reference);
            }

            var direction = (line.GetEndPoint(1) - line.GetEndPoint(0));
            if (direction.GetLength() < 1e-9)
            {
                failure = "вырожденная линия размера";
                return null;
            }
            direction = new XYZ(direction.X, direction.Y, 0).Normalize();

            var match = MatchSide(wallRefs, direction, index);
            if (match == null)
            {
                failure = "не удалось определить помещение и сторону по ссылкам размера";
                return null;
            }

            var point = line.GetEndPoint(0);
            var offsetFeet = match.Side.SignedOffset(point);
            var offsetMm = UnitUtils.ConvertFromInternalUnits(offsetFeet, UnitTypeId.Millimeters);

            var ownCount = wallRefs.Count(r => match.Side.WallIds.Contains(r.ElementId));
            var foreignCount = wallRefs.Count - ownCount;
            var centerCount = centerRefs.Count;

            DimensionChainKind kind;
            var approximate = false;

            if (foreignCount > 0 && (ownCount > 2 || centerCount > 0))
                kind = DimensionChainKind.Combined;
            else if (foreignCount > 0)
                kind = DimensionChainKind.Partitions;
            else if (centerCount > 0 && ownCount <= 2)
                kind = DimensionChainKind.OpeningCenters;
            else if (centerCount > 0)
                kind = DimensionChainKind.Combined;
            else if (ownCount > 2)
                kind = DimensionChainKind.OpeningEdges;
            else if (ownCount == 2)
                kind = DimensionChainKind.Overall;
            else
            {
                kind = DimensionChainKind.Combined;
                approximate = true;
            }

            return new ParsedSample
            {
                Kind = kind,
                OffsetMm = offsetMm,
                IsOutward = offsetMm < 0,
                DimensionTypeName = SafeTypeName(dimension),
                SourceLabel = DimensionLabel(dimension),
                Approximate = approximate
            };
        }

        private static void MergeInto(List<DimensionChainRow> rows, ParsedSample sample)
        {
            var absOffset = Math.Abs(sample.OffsetMm);

            var existing = rows.FirstOrDefault(row =>
                row.Kind == sample.Kind && Math.Abs(Math.Abs(row.OffsetMm) - absOffset) <= MergeToleranceMm);

            if (existing != null)
            {
                existing.Note = existing.Note + "; " + sample.SourceLabel;
                return;
            }

            var row = new DimensionChainRow
            {
                Kind = sample.Kind,
                OffsetMm = sample.OffsetMm,
                DimensionTypeName = sample.DimensionTypeName ?? string.Empty
            };

            var note = "из " + sample.SourceLabel;
            if (sample.Approximate)
                note += " — вид нитки определён приблизительно, проверьте";

            row.Note = note;
            rows.Add(row);
        }

        // ───────────────────────────── поиск помещения и стороны ─────────────────────────────

        private sealed class RoomSideRef
        {
            public Room Room;
            public RoomSide Side;
        }

        private static Dictionary<long, List<RoomSideRef>> BuildWallIndex(Document doc, SpatialElementBoundaryOptions options)
        {
            var index = new Dictionary<long, List<RoomSideRef>>();

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(room => room.Area > 0);

            foreach (var room in rooms)
            {
                List<RoomSide> sides;
                try
                {
                    sides = RoomSideBuilder.Build(room, options);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var side in sides)
                {
                    if (side.IsCurved || !side.HasWall)
                        continue;

                    foreach (var wallId in side.WallIds)
                    {
                        List<RoomSideRef> list;
                        if (!index.TryGetValue(wallId.IntegerValue, out list))
                        {
                            list = new List<RoomSideRef>();
                            index[wallId.IntegerValue] = list;
                        }

                        list.Add(new RoomSideRef { Room = room, Side = side });
                    }
                }
            }

            return index;
        }

        private static RoomSideRef MatchSide(List<Reference> wallRefs, XYZ direction, Dictionary<long, List<RoomSideRef>> index)
        {
            var candidates = new List<RoomSideRef>();
            var seen = new HashSet<RoomSide>();

            foreach (var reference in wallRefs)
            {
                List<RoomSideRef> list;
                if (!index.TryGetValue(reference.ElementId.IntegerValue, out list))
                    continue;

                foreach (var candidate in list)
                {
                    if (Math.Abs(candidate.Side.Direction.DotProduct(direction)) < DirectionDotTolerance)
                        continue;

                    if (seen.Add(candidate.Side))
                        candidates.Add(candidate);
                }
            }

            if (candidates.Count == 0)
                return null;

            return candidates
                .OrderByDescending(c => wallRefs.Count(r => c.Side.WallIds.Contains(r.ElementId)))
                .ThenByDescending(c => c.Side.WallIds.Count)
                .First();
        }

        // ───────────────────────────── мелкие безопасные обёртки ─────────────────────────────

        private static Curve SafeCurve(Dimension dimension)
        {
            try
            {
                return dimension.Curve;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static List<Reference> SafeReferences(Dimension dimension)
        {
            var result = new List<Reference>();
            try
            {
                foreach (Reference reference in dimension.References)
                {
                    if (reference != null)
                        result.Add(reference);
                }
            }
            catch (Exception)
            {
                // ссылки недоступны — итог тот же, что и пустой список
            }

            return result;
        }

        private static Element SafeElement(Document doc, Reference reference)
        {
            try
            {
                return doc.GetElement(reference.ElementId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsCenterReference(FamilyInstance instance, Reference reference)
        {
            try
            {
                return instance.GetReferenceType(reference) == FamilyInstanceReferenceType.CenterLeftRight;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string SafeTypeName(Dimension dimension)
        {
            try
            {
                return dimension.DimensionType != null ? dimension.DimensionType.Name : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string DimensionLabel(Dimension dimension)
        {
            try
            {
                var value = dimension.Value;
                if (value.HasValue)
                {
                    var mm = UnitUtils.ConvertFromInternalUnits(value.Value, UnitTypeId.Millimeters);
                    return "размера " + mm.ToString("0", CultureInfo.InvariantCulture) + " мм";
                }
            }
            catch (Exception)
            {
                // берём запасной вариант ниже
            }

            return "размера id " + dimension.Id.IntegerValue;
        }
    }
}
