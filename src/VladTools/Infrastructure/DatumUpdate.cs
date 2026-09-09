using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// A ready-made edit for a single grid or level: exactly what to do so that the project element
    /// matches the coordination file.
    ///
    /// It is computed while comparing and applied by the command inside a transaction — the same
    /// scheme as in "Auto Dimensions": all reads first, all writes afterwards. Here there is a second
    /// reason for it: between opening the window and pressing "Apply" the user looks at the numbers,
    /// and exactly what they saw must be what gets applied.
    /// </summary>
    internal sealed class DatumUpdate
    {
        /// <summary>The project element being edited.</summary>
        public ElementId HostId { get; set; }

        /// <summary>Rotation of the grid about the vertical axis, in radians; 0 — no rotation needed.</summary>
        public double Angle { get; set; }

        /// <summary>
        /// The point to rotate about — the midpoint of the grid before the edit. The translation is
        /// measured from it as well: after rotating about that point the grid runs through it in the
        /// right direction, and all that is left is to shift it sideways.
        /// </summary>
        public XYZ Center { get; set; }

        /// <summary>Translation of the grid in plan after the rotation; null — no translation needed.</summary>
        public XYZ Translation { get; set; }

        /// <summary>The new level elevation in internal units; NaN — leave the level alone.</summary>
        public double Elevation { get; set; } = double.NaN;

        /// <summary>The new name; null — do not rename.</summary>
        public string NewName { get; set; }
    }
}
