using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Spreads out the labels of a finished chain: a value that does not fit between its ticks is
    /// moved off the line away from the wall — Revit draws the leader to it by itself.
    ///
    /// Why: a 120 mm pier is an everyday thing on a masonry plan, while the "120" label at 1:50 takes
    /// about 250 mm in model space. Left where they are, such labels overlap each other and the
    /// neighbouring values, and the chain becomes unreadable (that was the designer's second
    /// complaint). On the reference masonry plan it is exactly these small values that are pulled out
    /// onto leaders, while the large ones stay on the line.
    ///
    /// **This is a heuristic, not a calculation** — the same status as <see cref="FormulaParser"/> and
    /// sample parsing: the Revit API does not report the rendered text width, so it has to be
    /// estimated from the font height of the dimension type, the width factor and the character count.
    /// The estimate deliberately errs high: pulling a label out needlessly is harmless, leaving an overlap is not.
    ///
    /// It runs **after** every dimension is created and after a single <c>Document.Regenerate()</c>:
    /// before the regeneration a freshly created dimension has no segments filled in yet, and there is
    /// nothing to spread out (see CLAUDE.md, the two placement phases).
    /// </summary>
    internal static class DimensionTextLayout
    {
        /// <summary>
        /// The average character width as a fraction of the font height. No exact number exists — it
        /// depends on the typeface; 0.62 is taken generously relative to the narrow fonts typical of
        /// drawings, so that the estimate errs towards "pull it out" rather than "leave an overlap".
        /// </summary>
        private const double GlyphWidthPerHeight = 0.62;

        /// <summary>The margin at the edges of a label, in characters: two values must keep a gap between them.</summary>
        private const double PaddingInGlyphs = 0.8;

        /// <summary>The first tier sits this high (in font heights) above the dimension line.</summary>
        private const double FirstLevelInHeights = 1.2;

        /// <summary>The step between tiers if the labels still clash on the first one.</summary>
        private const double LevelStepInHeights = 1.0;

        /// <summary>Never more than three tiers: beyond that a label flies away from its own tick and gets confused with the neighbouring chain.</summary>
        private const int MaxLevels = 3;

        /// <summary>
        /// Spreads out the labels of one chain. <paramref name="awayNormal"/> is where to move them (the
        /// unit vector away from the wall, the same one the chain was offset from the side by). Returns
        /// how many labels were pulled out; zero means everything fitted, and that is a normal outcome.
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
                    // There is room — the label stays on the line, and the next pulled-out one counts itself as the first.
                    lastLevel = 0;
                    continue;
                }

                var origin = item.TextPosition;
                if (origin == null)
                    continue;

                var t = origin.DotProduct(direction);

                // The tier is the same as the previous pulled-out label's only if the two will not
                // overlap on it; otherwise the next one, cycling round.
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
        /// The label height in model units: the font size is stored in paper units, and on the plan it
        /// is stretched by the view scale — 2.5 mm on paper at 1:50 is 125 mm in the model.
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
        /// The labels of a dimension as one list: on a chain of three or more ticks these are the
        /// segments; on a dimension with only two it is the dimension itself (it has no segments at all).
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

        /// <summary>One label — a chain segment or a whole dimension; this brings them to a common shape.</summary>
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
            /// Moves the label. A refusal from Revit (a dimension under a view template, a locked
            /// dimension) must not bring the placement down: the chain is already there, the label just stayed put.
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
        /// Asks Revit to show a leader to the moved label. Most dimension types already have that set
        /// up ("leader when text is moved"), so a refusal here is not an error — the label will simply
        /// be left without a leader.
        /// </summary>
        private static void TryShowLeader(Dimension dimension)
        {
            try
            {
                dimension.HasLeader = true;
            }
            catch (Exception)
            {
                // The dimension type does not support leaders — the label is moved anyway, and that is enough.
            }
        }
    }
}
