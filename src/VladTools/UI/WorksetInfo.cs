using System;

namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of one user workset of the open project, taken when the window opens.
    ///
    /// The identity carried into the window is <see cref="UniqueId"/>, not the project's
    /// <c>WorksetId</c>: by Autodesk's own documentation a workset's id changes on synchronising
    /// with the central model and only the GUID is stable — the same reason "Link Manager" re-reads
    /// workset ids right before loading instead of trusting the window's cache. The command
    /// resolves the GUID back to a <c>WorksetId</c> at the moment it deletes.
    /// </summary>
    internal sealed class WorksetInfo
    {
        public WorksetInfo(
            Guid uniqueId,
            string name,
            int elementCount,
            bool isCounted,
            bool isOpen,
            bool isActive,
            bool isEditable,
            string owner)
        {
            UniqueId = uniqueId;
            Name = name ?? string.Empty;
            ElementCount = elementCount;
            IsCounted = isCounted;
            IsOpen = isOpen;
            IsActive = isActive;
            IsEditable = isEditable;
            Owner = owner ?? string.Empty;
        }

        /// <summary>The workset's stable identity — what the command looks it up by when deleting.</summary>
        public Guid UniqueId { get; }

        public string Name { get; }

        /// <summary>How many elements the workset holds; meaningless unless <see cref="IsCounted"/>.</summary>
        public int ElementCount { get; }

        /// <summary>
        /// The contents really were counted. A **closed** workset is invisible to
        /// <c>FilteredElementCollector</c>, so its count comes back as a perfectly plausible zero —
        /// and there is no API to open a workset in an already-open document. Reporting that zero as
        /// "empty" would be the worst possible lie on a button that offers to delete the contents,
        /// so the two cases are kept apart and the window says "not counted" instead.
        /// </summary>
        public bool IsCounted { get; }

        /// <summary>The workset is open in this session; a closed one cannot have its contents counted.</summary>
        public bool IsOpen { get; }

        /// <summary>The workset new elements currently go into — it has to be left behind before it can be deleted.</summary>
        public bool IsActive { get; }

        /// <summary>The current user owns the workset (it is checked out) and may edit it.</summary>
        public bool IsEditable { get; }

        /// <summary>Who owns the workset; empty when nobody does. Somebody else's workset cannot be deleted.</summary>
        public string Owner { get; }
    }
}
