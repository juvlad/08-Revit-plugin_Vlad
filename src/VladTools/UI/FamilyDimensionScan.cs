using System;
using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// The result of scanning the loaded families for dimension labels: which shared parameters
    /// drive geometry, and how many families could be opened along the way.
    ///
    /// The command returns it to the "Delete Shared Parameters" window. Dimension labels are
    /// invisible from the project — they have to be looked for inside every family — so the window
    /// gets a ready list of GUIDs rather than raw Revit elements.
    /// </summary>
    internal sealed class FamilyDimensionScan
    {
        public FamilyDimensionScan(
            ISet<string> parameterGuids,
            int openedFamilies,
            int reusedFamilies,
            IReadOnlyList<string> failures)
        {
            ParameterGuids = parameterGuids ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            OpenedFamilies = openedFamilies;
            ReusedFamilies = reusedFamilies;
            Failures = failures ?? new List<string>();
        }

        /// <summary>GUIDs of the shared parameters that label at least one dimension.</summary>
        public ISet<string> ParameterGuids { get; }

        /// <summary>How many families had to be opened again.</summary>
        public int OpenedFamilies { get; }

        /// <summary>How many were taken from the saved scan, without opening.</summary>
        public int ReusedFamilies { get; }

        /// <summary>Families that could not be opened, with the reason.</summary>
        public IReadOnlyList<string> Failures { get; }
    }
}
