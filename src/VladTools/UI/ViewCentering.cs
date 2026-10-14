namespace VladTools.UI
{
    /// <summary>
    /// How "Views to Sheets" decides where a view goes on its sheet. They answer different questions,
    /// which is why the window asks rather than picks: the first makes one sheet look balanced, the
    /// next two make a set of sheets line up with each other, the last repeats what the user did by
    /// hand on one sheet.
    ///
    /// The order of the values is the order of the options in the window.
    /// </summary>
    internal enum ViewCentering
    {
        /// <summary>Everything the viewport draws — crop region, annotation beyond it, the title — goes in the middle of the drawing area.</summary>
        Viewport,

        /// <summary>The view's crop region goes in the middle, whatever annotation sticks out of it.</summary>
        CropRegion,

        /// <summary>Views looking the same way put one model point in the same place on every sheet.</summary>
        ModelPoint,

        /// <summary>Every view goes where the view of a sample sheet stands — views already on sheets included.</summary>
        Sample
    }

    /// <summary>Text for the window and the reports — the window knows nothing about the Revit API, so it lives here.</summary>
    internal static class ViewCenteringText
    {
        public static readonly ViewCentering[] All =
        {
            ViewCentering.Viewport,
            ViewCentering.CropRegion,
            ViewCentering.ModelPoint,
            ViewCentering.Sample
        };

        public static string Caption(ViewCentering centering)
        {
            switch (centering)
            {
                case ViewCentering.Viewport:
                    return "The whole viewport, its title included";
                case ViewCentering.CropRegion:
                    return "The crop region";
                case ViewCentering.ModelPoint:
                    return "One model point in the same place on every sheet";
                case ViewCentering.Sample:
                    return "Like a sample sheet";
                default:
                    return centering.ToString();
            }
        }

        public static string Description(ViewCentering centering)
        {
            switch (centering)
            {
                case ViewCentering.Viewport:
                    return "What the sheet shows of the view — the crop region, annotation sticking out of it and the view title — " +
                           "sits in the middle of the drawing area. The sheet looks balanced; for sections, details, 3D views.";
                case ViewCentering.CropRegion:
                    return "The view's crop region sits in the middle, whatever annotation sticks out of it. Plans sharing a crop " +
                           "region (a scope box) land in exactly the same place on every sheet.";
                case ViewCentering.ModelPoint:
                    return "Views looking the same way put the same point of the model — the middle of all their crop regions " +
                           "together — in the middle of the drawing area, so a stack of floor plans does not jump as the sheets " +
                           "are flipped through, even where their crop regions differ.";
                case ViewCentering.Sample:
                    return "Place one view by hand, then repeat it: every checked view goes where the sample's view stands. A view " +
                           "looking the same way as the sample puts the same point of the model in the same place — a floor plan " +
                           "lands exactly over the sample's; any other view puts the middle of its viewport where the sample's is. " +
                           "Views already on sheets can be checked too: they are moved on their own sheets.";
                default:
                    return string.Empty;
            }
        }

        public static ViewCentering Parse(string text, ViewCentering fallback)
        {
            foreach (var centering in All)
            {
                if (string.Equals(centering.ToString(), (text ?? string.Empty).Trim(), System.StringComparison.OrdinalIgnoreCase))
                    return centering;
            }

            return fallback;
        }
    }
}
