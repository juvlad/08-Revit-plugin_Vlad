namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of one viewport already on a sheet, offered as the sample for "Like a sample sheet":
    /// which sheet, which view, and where its box stands — millimetres of paper from the sheet's origin.
    /// </summary>
    internal sealed class SampleViewportInfo
    {
        public SampleViewportInfo(
            string viewportUniqueId,
            string viewUniqueId,
            string sheetTitle,
            string viewCaption,
            string titleblock,
            string alignGroup,
            double left,
            double bottom,
            double right,
            double top)
        {
            ViewportUniqueId = viewportUniqueId;
            ViewUniqueId = viewUniqueId;
            SheetTitle = sheetTitle ?? string.Empty;
            ViewCaption = viewCaption ?? string.Empty;
            Titleblock = titleblock ?? string.Empty;
            AlignGroup = alignGroup;
            Left = left;
            Bottom = bottom;
            Right = right;
            Top = top;
        }

        public string ViewportUniqueId { get; }

        public string ViewUniqueId { get; }

        /// <summary>"A101 - Level 1".</summary>
        public string SheetTitle { get; }

        /// <summary>"Floor plan: Level 1".</summary>
        public string ViewCaption { get; }

        /// <summary>The sheet's titleblock type, "Family : Type"; empty for a sheet without one.</summary>
        public string Titleblock { get; }

        /// <summary>The sample view's <see cref="PlaceableViewInfo.AlignGroup"/>: views sharing it line up with the sample through the model.</summary>
        public string AlignGroup { get; }

        /// <summary>The viewport's box on its sheet, title excluded.</summary>
        public double Left { get; }
        public double Bottom { get; }
        public double Right { get; }
        public double Top { get; }

        public string Caption => SheetTitle + "   ·   " + ViewCaption;

        public override string ToString()
        {
            return Caption;
        }
    }
}
