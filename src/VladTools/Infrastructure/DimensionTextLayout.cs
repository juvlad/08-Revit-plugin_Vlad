using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Разводит подписи готовой нитки: значение, которому не хватает места между засечками,
    /// сдвигается с линии в сторону от стены — Revit сам дорисовывает к нему выноску.
    ///
    /// Зачем: на кладочном плане простенок в 120 мм — обычное дело, а подпись «120» при
    /// масштабе 1:50 занимает на модели около 250 мм. Оставленные на месте, такие подписи
    /// налезают друг на друга и на соседние значения, и нитка становится нечитаемой (это и
    /// было вторым замечанием проектировщика). В образцовом кладочном плане ровно эти мелкие
    /// значения вынесены на выноски, крупные стоят на линии.
    ///
    /// **Это эвристика, а не расчёт** — того же статуса, что <see cref="FormulaParser"/> и
    /// разбор образца: ширину отрисованного текста Revit API не отдаёт, её приходится оценивать
    /// по высоте шрифта из типа размера, коэффициенту ширины и числу знаков. Оценка намеренно
    /// с запасом: лишний раз вынести подпись не страшно, оставить наложение — страшно.
    ///
    /// Работает **после** создания всех размеров и одного <c>Document.Regenerate()</c>: до
    /// регенерации у только что созданного размера ещё не заполнены сегменты, и разводить
    /// нечего (см. CLAUDE.md, две фазы расстановки).
    /// </summary>
    internal static class DimensionTextLayout
    {
        /// <summary>
        /// Средняя ширина знака в долях высоты шрифта. Точного числа не существует — оно зависит
        /// от гарнитуры; 0.62 взято с запасом относительно типичных для чертежей узких шрифтов,
        /// чтобы оценка ошибалась в сторону «вынести», а не «оставить наложение».
        /// </summary>
        private const double GlyphWidthPerHeight = 0.62;

        /// <summary>Запас по краям подписи, в знаках: между двумя значениями должен остаться просвет.</summary>
        private const double PaddingInGlyphs = 0.8;

        /// <summary>Первая полка — на такой высоте (в высотах шрифта) над линией размера.</summary>
        private const double FirstLevelInHeights = 1.2;

        /// <summary>Шаг между полками, если подписи не разошлись и на первой.</summary>
        private const double LevelStepInHeights = 1.0;

        /// <summary>Больше трёх полок не бывает: дальше подпись улетает от своей засечки и путается с соседней ниткой.</summary>
        private const int MaxLevels = 3;

        /// <summary>
        /// Разводит подписи одной нитки. <paramref name="awayNormal"/> — куда сдвигать (единичный
        /// вектор от стены, тот же, которым нитка отодвинута от стороны). Возвращает, сколько
        /// подписей вынесено; ноль — всем хватило места, и это нормальный исход.
        /// </summary>
        public static int Arrange(Dimension dimension, XYZ awayNormal, int viewScale)
        {
            if (dimension == null || awayNormal == null)
                return 0;

            var line = SafeCurve(dimension) as Line;
            if (line == null)
                return 0;

            var textHeight = ModelTextHeight(dimension, viewScale);
            if (textHeight <= 0)
                return 0;

            var direction = line.Direction;
            var moved = 0;

            double lastT = 0;
            double lastHalfWidth = 0;
            var lastLevel = 0;

            foreach (var item in Items(dimension))
            {
                var text = item.ValueString;
                if (string.IsNullOrEmpty(text) || !item.Length.HasValue)
                    continue;

                var halfWidth = (text.Length + PaddingInGlyphs) * textHeight * GlyphWidthPerHeight * 0.5;
                if (item.Length.Value >= halfWidth * 2.0)
                {
                    // Место есть — подпись остаётся на линии, и следующая вынесенная считает себя первой.
                    lastLevel = 0;
                    continue;
                }

                var origin = item.TextPosition;
                if (origin == null)
                    continue;

                var t = origin.DotProduct(direction);

                // Полка та же, что у предыдущей вынесенной подписи, только если они не налезут
                // друг на друга уже на ней; иначе — следующая, по кругу.
                var level = lastLevel == 0 || Math.Abs(t - lastT) >= halfWidth + lastHalfWidth
                    ? 1
                    : lastLevel % MaxLevels + 1;

                var distance = textHeight * (FirstLevelInHeights + LevelStepInHeights * (level - 1));

                if (!item.TryMove(origin + awayNormal.Multiply(distance)))
                    continue;

                lastT = t;
                lastHalfWidth = halfWidth;
                lastLevel = level;
                moved++;
            }

            if (moved > 0)
                TryShowLeader(dimension);

            return moved;
        }

        /// <summary>
        /// Высота подписи в единицах модели: размер шрифта хранится в бумажных единицах,
        /// на плане он растянут масштабом вида — 2.5 мм на бумаге при 1:50 это 125 мм в модели.
        /// </summary>
        private static double ModelTextHeight(Dimension dimension, int viewScale)
        {
            DimensionType type;
            try
            {
                type = dimension.DimensionType;
            }
            catch (Exception)
            {
                return 0;
            }

            if (type == null)
                return 0;

            var paperHeight = ParameterValue(type, BuiltInParameter.TEXT_SIZE, 0);
            if (paperHeight <= 0)
                return 0;

            var widthFactor = ParameterValue(type, BuiltInParameter.TEXT_WIDTH_SCALE, 1.0);
            if (widthFactor <= 0)
                widthFactor = 1.0;

            var scale = viewScale > 0 ? viewScale : 1;
            return paperHeight * widthFactor * scale;
        }

        private static double ParameterValue(Element element, BuiltInParameter id, double fallback)
        {
            try
            {
                var parameter = element.get_Parameter(id);
                if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.Double)
                    return fallback;

                return parameter.AsDouble();
            }
            catch (Exception)
            {
                return fallback;
            }
        }

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

        /// <summary>
        /// Подписи размера единым списком: у нитки из трёх и более засечек это сегменты,
        /// у размера всего с двумя — сам размер (сегментов у него нет вовсе).
        /// </summary>
        private static IEnumerable<TextItem> Items(Dimension dimension)
        {
            DimensionSegmentArray segments;
            try
            {
                segments = dimension.NumberOfSegments > 1 ? dimension.Segments : null;
            }
            catch (Exception)
            {
                segments = null;
            }

            if (segments == null)
            {
                yield return TextItem.ForWhole(dimension);
                yield break;
            }

            foreach (DimensionSegment segment in segments)
            {
                if (segment != null)
                    yield return TextItem.ForSegment(segment);
            }
        }

        /// <summary>Одна подпись — сегмент нитки или размер целиком; сводит их к общему виду.</summary>
        private sealed class TextItem
        {
            private Dimension _dimension;
            private DimensionSegment _segment;

            public string ValueString { get; private set; }
            public double? Length { get; private set; }
            public XYZ TextPosition { get; private set; }

            public static TextItem ForWhole(Dimension dimension)
            {
                return new TextItem
                {
                    _dimension = dimension,
                    ValueString = Safe(() => dimension.ValueString),
                    Length = Safe(() => dimension.Value),
                    TextPosition = Safe(() => dimension.TextPosition)
                };
            }

            public static TextItem ForSegment(DimensionSegment segment)
            {
                return new TextItem
                {
                    _segment = segment,
                    ValueString = Safe(() => segment.ValueString),
                    Length = Safe(() => segment.Value),
                    TextPosition = Safe(() => segment.TextPosition)
                };
            }

            /// <summary>
            /// Сдвигает подпись. Отказ Revit (размер под шаблоном вида, заблокированный размер)
            /// не должен ронять расстановку: нитка уже стоит, просто подпись осталась на месте.
            /// </summary>
            public bool TryMove(XYZ position)
            {
                try
                {
                    if (_segment != null)
                        _segment.TextPosition = position;
                    else
                        _dimension.TextPosition = position;

                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            private static T Safe<T>(Func<T> read) where T : class
            {
                try
                {
                    return read();
                }
                catch (Exception)
                {
                    return null;
                }
            }

            private static double? Safe(Func<double?> read)
            {
                try
                {
                    return read();
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Просит Revit показать выноску к сдвинутой подписи. У большинства типов размеров это
        /// и так настроено («выноска при отводе текста»), поэтому отказ здесь не ошибка — просто
        /// подпись останется без полки.
        /// </summary>
        private static void TryShowLeader(Dimension dimension)
        {
            try
            {
                dimension.HasLeader = true;
            }
            catch (Exception)
            {
                // Тип размера выносок не поддерживает — подпись всё равно сдвинута, этого достаточно.
            }
        }
    }
}
