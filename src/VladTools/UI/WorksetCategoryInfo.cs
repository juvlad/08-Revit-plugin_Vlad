using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of the elements of one category standing in one workset — a row of the
    /// "Move to Workset" table, taken when the scope is read.
    ///
    /// The ids are split three ways, and the split is the whole point of the row: what can be moved,
    /// what stands inside a model group, and what Revit will not let change workset at all. All three
    /// are kept as ids rather than counts, so "Select in model" can show the user every one of them —
    /// the last two are exactly the ones worth looking at.
    ///
    /// The workset is identified by <see cref="WorksetId"/> — its GUID, not its <c>WorksetId</c>, which
    /// by Autodesk's own documentation changes on synchronising (the same rule as in "Worksets").
    /// </summary>
    internal sealed class WorksetCategoryInfo
    {
        public WorksetCategoryInfo(
            Guid worksetId,
            string worksetName,
            string kindCaption,
            bool isUserWorkset,
            string category,
            IReadOnlyList<ElementId> movable,
            IReadOnlyList<ElementId> inGroups,
            IReadOnlyList<ElementId> locked)
        {
            WorksetId = worksetId;
            WorksetName = worksetName ?? string.Empty;
            KindCaption = kindCaption ?? string.Empty;
            IsUserWorkset = isUserWorkset;
            Category = category ?? string.Empty;
            Movable = movable ?? new List<ElementId>();
            InGroups = inGroups ?? new List<ElementId>();
            Locked = locked ?? new List<ElementId>();
        }

        /// <summary>The workset's stable identity.</summary>
        public Guid WorksetId { get; }

        public string WorksetName { get; }

        /// <summary>"User", "Project standards", "View", "Family" — the kind as Revit's Worksets dialog would call it.</summary>
        public string KindCaption { get; }

        /// <summary>
        /// A workset a person made. Anything else is one of Revit's own — project standards, a view,
        /// a family — and a model element standing in it is the anomaly this button exists for.
        /// </summary>
        public bool IsUserWorkset { get; }

        /// <summary>The top-level category, as Revit names it in the interface language.</summary>
        public string Category { get; }

        /// <summary>The elements whose workset can be changed.</summary>
        public IReadOnlyList<ElementId> Movable { get; }

        /// <summary>
        /// Members of a model group. Left alone: an edit to one instance of a group made outside
        /// "Edit Group" is a group change Revit may refuse at commit, and that refusal would take the
        /// whole batch down with it.
        /// </summary>
        public IReadOnlyList<ElementId> InGroups { get; }

        /// <summary>
        /// Elements standing in the model whose "Workset" parameter Revit reports as read-only. Only
        /// ever collected in Revit's own worksets: there they are the stray elements Revit refuses to
        /// let out, and staying silent about them would make "nothing found" look like "all is well".
        /// </summary>
        public IReadOnlyList<ElementId> Locked { get; }

        public int Total => Movable.Count + InGroups.Count + Locked.Count;
    }
}
