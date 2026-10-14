using System.Collections.Generic;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// What the "Views to Sheets" window hands back: the titleblock, the drawing area cut out of it,
    /// how to position, the sample when positioning like one, and the views — each with the new sheet
    /// it goes on or the sheet it is moved on, and the scale it gets — in the order the sheets are made.
    /// </summary>
    internal sealed class ViewPlacementRequest
    {
        private readonly IReadOnlyDictionary<string, double[]> _marginsByTitleblock;

        public ViewPlacementRequest(
            TitleblockInfo titleblock,
            double[] margins,
            ViewCentering centering,
            SampleViewportInfo sample,
            IReadOnlyDictionary<string, double[]> marginsByTitleblock,
            IReadOnlyList<ViewPlacementItem> items)
        {
            Titleblock = titleblock;
            MarginLeft = margins[0];
            MarginTop = margins[1];
            MarginRight = margins[2];
            MarginBottom = margins[3];
            Centering = centering;
            Sample = sample;
            _marginsByTitleblock = marginsByTitleblock;
            Items = items;
        }

        /// <summary>The titleblock of the new sheets; may be null when every view is only moved.</summary>
        public TitleblockInfo Titleblock { get; }

        /// <summary>Millimetres from the titleblock's outer edge to the drawing area.</summary>
        public double MarginLeft { get; }
        public double MarginTop { get; }
        public double MarginRight { get; }
        public double MarginBottom { get; }

        public ViewCentering Centering { get; }

        /// <summary>The viewport to repeat, when <see cref="Centering"/> is <see cref="ViewCentering.Sample"/>; null otherwise.</summary>
        public SampleViewportInfo Sample { get; }

        public IReadOnlyList<ViewPlacementItem> Items { get; }

        public double AreaLeft => Titleblock.Left + MarginLeft;
        public double AreaRight => Titleblock.Right - MarginRight;
        public double AreaBottom => Titleblock.Bottom + MarginBottom;
        public double AreaTop => Titleblock.Top - MarginTop;

        /// <summary>
        /// Left, top, right, bottom for a titleblock type by its name — the drawing area of a sheet that
        /// already exists, for checking that a moved view stays inside it.
        /// </summary>
        public double[] MarginsFor(string titleblock)
        {
            return titleblock != null && _marginsByTitleblock != null && _marginsByTitleblock.TryGetValue(titleblock, out var margins)
                ? margins
                : ViewsToSheetsPreferences.DefaultMargins;
        }
    }

    /// <summary>One view, the sheet it goes on or stays on, and the scale it gets.</summary>
    internal sealed class ViewPlacementItem
    {
        public ViewPlacementItem(PlaceableViewInfo view, string sheetNumber, int scale)
        {
            View = view;
            SheetNumber = sheetNumber;
            Scale = scale;
        }

        public PlaceableViewInfo View { get; }

        /// <summary>The new sheet's number; null when the view is moved on the sheet it already stands on.</summary>
        public string SheetNumber { get; }

        public bool IsMove => SheetNumber == null;

        /// <summary>The scale to give the view, 100 for 1:100; 0 or the view's own scale leaves it alone.</summary>
        public int Scale { get; }

        public bool ChangesScale => Scale > 0 && Scale != View.Scale;

        /// <summary>The new sheet is named after the view.</summary>
        public string SheetName => View.Name;

        public string SheetTitle => IsMove ? View.PlacedOn : SheetNumber + " - " + SheetName;
    }
}
