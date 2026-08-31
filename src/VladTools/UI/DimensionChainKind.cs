namespace VladTools.UI
{
    /// <summary>
    /// Каталог видов ниток авторазмеров — фиксированный перечень того, что может засекать
    /// одна нитка размеров вдоль стороны помещения. Разбор образца (см. <c>DimensionSampleReader</c>)
    /// подбирает для каждого образцового размера один из этих видов; ошибка подбора не страшна —
    /// строка в окне правится руками.
    ///
    /// Порядок значений — это порядок в выпадающем списке окна.
    /// </summary>
    internal enum DimensionChainKind
    {
        /// <summary>Только два крайних угла стороны — общий габарит.</summary>
        Overall,

        /// <summary>Углы стороны + грани откосов проёмов (двери, окна).</summary>
        OpeningEdges,

        /// <summary>Углы стороны + оси проёмов (середины между откосами).</summary>
        OpeningCenters,

        /// <summary>Углы стороны + обе грани перегородок, примыкающих к стороне изнутри помещения.</summary>
        Partitions,

        /// <summary>Углы + стыки стен внутри стороны (для ступенчатых стен, собранных из нескольких).</summary>
        WallFaces,

        /// <summary>Объединение проёмов, осей проёмов и перегородок на одной нитке.</summary>
        Combined
    }

    /// <summary>Текст для выпадающего списка и отчётов — окно про Revit API не знает, поэтому текст здесь.</summary>
    internal static class DimensionChainKindText
    {
        public static string Caption(DimensionChainKind kind)
        {
            switch (kind)
            {
                case DimensionChainKind.Overall:
                    return "Габарит";
                case DimensionChainKind.OpeningEdges:
                    return "Проёмы";
                case DimensionChainKind.OpeningCenters:
                    return "Оси проёмов";
                case DimensionChainKind.Partitions:
                    return "Перегородки";
                case DimensionChainKind.WallFaces:
                    return "Грани стен";
                case DimensionChainKind.Combined:
                    return "Всё вместе";
                default:
                    return kind.ToString();
            }
        }

        public static readonly DimensionChainKind[] All =
        {
            DimensionChainKind.Overall,
            DimensionChainKind.OpeningEdges,
            DimensionChainKind.OpeningCenters,
            DimensionChainKind.Partitions,
            DimensionChainKind.WallFaces,
            DimensionChainKind.Combined
        };
    }
}
