using System.Globalization;

namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of one titleblock type for the "Views to Sheets" window: its name and the frame it
    /// draws on a sheet, in millimetres of paper, measured from the sheet's origin.
    ///
    /// The frame is not read from the family's parameters but measured: the command creates a sheet
    /// of every type in a transaction it rolls back and asks each titleblock for its extents. That is
    /// what the drawing area is cut out of, so it has to be the outline Revit actually draws.
    /// </summary>
    internal sealed class TitleblockInfo
    {
        public TitleblockInfo(string uniqueId, string name, double left, double bottom, double right, double top, string problem)
        {
            UniqueId = uniqueId;
            Name = name ?? string.Empty;
            Left = left;
            Bottom = bottom;
            Right = right;
            Top = top;
            Problem = problem;
        }

        /// <summary>The titleblock type's UniqueId — the way back to it for the command.</summary>
        public string UniqueId { get; }

        /// <summary>"Family : Type", the way Revit's own "Select a Titleblock" list writes it.</summary>
        public string Name { get; }

        public double Left { get; }
        public double Bottom { get; }
        public double Right { get; }
        public double Top { get; }

        public double Width => Right - Left;
        public double Height => Top - Bottom;

        /// <summary>Why the type could not be measured; null when it was.</summary>
        public string Problem { get; }

        public bool IsMeasured => Problem == null && Width > 0 && Height > 0;

        public string Size => IsMeasured ? Millimetres(Width, Height) : string.Empty;

        public string Caption => IsMeasured ? Name + "   (" + Size + ")" : Name + " — " + (Problem ?? "could not be measured");

        public override string ToString()
        {
            return Caption;
        }

        /// <summary>"841 × 594 mm".</summary>
        public static string Millimetres(double width, double height)
        {
            return width.ToString("0", CultureInfo.CurrentCulture) + " × " + height.ToString("0", CultureInfo.CurrentCulture) + " mm";
        }
    }
}
