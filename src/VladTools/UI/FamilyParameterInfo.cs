namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of a family parameter for the formulas window. The window works without a Revit
    /// document; all it needs is the name and whether a formula can be assigned to that parameter.
    /// </summary>
    internal sealed class FamilyParameterInfo
    {
        public FamilyParameterInfo(string name, bool canAssignFormula, bool hasFormula)
        {
            Name = name;
            CanAssignFormula = canAssignFormula;
            HasFormula = hasFormula;
        }

        public string Name { get; }

        /// <summary>Revit forbids formulas on reporting parameters and on some built-in ones.</summary>
        public bool CanAssignFormula { get; }

        /// <summary>The parameter already has a formula — a new one will replace it.</summary>
        public bool HasFormula { get; }
    }
}
