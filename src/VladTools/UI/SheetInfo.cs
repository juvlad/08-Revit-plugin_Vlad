using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of one sheet for the "Sheets" window: what identifies it, where the Project Browser
    /// files it, and what is placed on it — plain strings, no Revit types.
    /// </summary>
    internal sealed class SheetInfo
    {
        public SheetInfo(string uniqueId, string number, string name, IReadOnlyList<string> folders, IReadOnlyList<string> contents, bool canOpen)
        {
            UniqueId = uniqueId;
            Number = number ?? string.Empty;
            Name = name ?? string.Empty;
            Folders = folders;
            Contents = contents;
            CanOpen = canOpen;
        }

        /// <summary>The way back to the element — a UniqueId rather than an ElementId, which the window has no type for.</summary>
        public string UniqueId { get; }

        public string Number { get; }

        public string Name { get; }

        /// <summary>"A101 - Floor Plan", the way the Project Browser itself writes a sheet.</summary>
        public string Title => Number.Length == 0 ? Name : Name.Length == 0 ? Number : Number + " - " + Name;

        /// <summary>The Project Browser folders the sheet sits in, outermost first; empty when the organization has none.</summary>
        public IReadOnlyList<string> Folders { get; }

        /// <summary>One line per thing placed on the sheet: "Floor plan: Level 1", "Schedule: Doors"…</summary>
        public IReadOnlyList<string> Contents { get; }

        /// <summary>False for a placeholder sheet: it exists only as a line in sheet lists and has nothing to open.</summary>
        public bool CanOpen { get; }
    }
}
