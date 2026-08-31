using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Одна прямая сторона границы помещения — итог слияния соседних отрезков одной петли
    /// <c>GetBoundarySegments</c> в один прямолинейный участок (см. <see cref="RoomSideBuilder"/>).
    ///
    /// Хранит только геометрию и список стен вдоль стороны; какие ссылки на этой стороне
    /// собирать под нитку — знает <c>DimensionReferenceCollector</c>, сторона об этом не знает.
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

        /// <summary>Начало стороны — крайняя точка по направлению <see cref="Direction"/>.</summary>
        public XYZ Start { get; }

        /// <summary>Конец стороны.</summary>
        public XYZ End { get; }

        /// <summary>Единичный вектор вдоль стороны (в плане, Z = 0).</summary>
        public XYZ Direction { get; }

        /// <summary>Единичная нормаль, указывающая внутрь помещения.</summary>
        public XYZ InwardNormal { get; }

        /// <summary>Стены (и другие элементы границы — двери в проёме без стены и т.п.) вдоль стороны, по порядку.</summary>
        public IReadOnlyList<ElementId> WallIds { get; }

        /// <summary>Сторона составлена из криволинейного отрезка — авторазмер её пропускает.</summary>
        public bool IsCurved { get; }

        /// <summary>Номер петли границы (петель может быть несколько — помещение с отверстием).</summary>
        public int LoopIndex { get; }

        /// <summary>Порядковый номер стороны внутри своей петли — для отчёта («восточная сторона» и т.п.).</summary>
        public int Index { get; }

        public double Length
        {
            get { return Start.DistanceTo(End); }
        }

        /// <summary>Хотя бы одна настоящая стена (не разделитель помещений) на стороне — иначе снимать размер не с чего.</summary>
        public bool HasWall { get; set; }

        /// <summary>
        /// Смещения (от <see cref="Start"/> вдоль <see cref="Direction"/>) точек стыка между разными
        /// стенами внутри стороны — сторона может быть склеена из нескольких коллинеарных стен
        /// (например, ступенчатая стена или два разных типа стены в одну линию). Используется
        /// только нитью «Грани стен» — остальные виды нитки о стыках не спрашивают.
        /// </summary>
        public IReadOnlyList<double> WallJointOffsets { get; }

        /// <summary>Проекция точки на ось стороны (0 — начало, Length — конец).</summary>
        public double ProjectOnAxis(XYZ point)
        {
            return (point - Start).DotProduct(Direction);
        }

        /// <summary>Знаковое расстояние точки от прямой стороны вдоль внутренней нормали (0 — на стороне, >0 — внутрь).</summary>
        public double SignedOffset(XYZ point)
        {
            return (point - Start).DotProduct(InwardNormal);
        }
    }
}
