using System;
using System.Collections.Generic;
using System.Globalization;

namespace VladTools.UI
{
    /// <summary>How many things of each kind stand on one sheet — the lines of <see cref="SheetInfo.Contents"/>, counted.</summary>
    internal sealed class SheetCounts
    {
        public static readonly SheetCounts None = new SheetCounts(0, 0, 0, 0);

        public SheetCounts(int views, int legends, int schedules, int other)
        {
            Views = views;
            Legends = legends;
            Schedules = schedules;
            Other = other;
        }

        /// <summary>Viewports of every view but a legend.</summary>
        public int Views { get; }

        public int Legends { get; }

        /// <summary>Schedules and panel schedules; a split schedule counts once, as it is one line.</summary>
        public int Schedules { get; }

        /// <summary>Images and CAD files.</summary>
        public int Other { get; }
    }

    /// <summary>
    /// A snapshot of one sheet for the "Sheets" window and its reports: what identifies it, where the
    /// Project Browser files it, what is placed on it and when a change was last found on it — plain
    /// strings, counts and dates, no Revit types.
    /// </summary>
    internal sealed class SheetInfo
    {
        public SheetInfo(string uniqueId, string number, string name, IReadOnlyList<string> folders, IReadOnlyList<string> contents, bool canOpen,
            DateTime? changed, string changeNote, bool changedAtThisCheck, bool isHiddenByFilter, SheetCounts counts)
        {
            UniqueId = uniqueId;
            Number = number ?? string.Empty;
            Name = name ?? string.Empty;
            Folders = folders;
            Contents = contents;
            CanOpen = canOpen;
            Changed = changed;
            ChangeNote = changeNote ?? string.Empty;
            ChangedAtThisCheck = changedAtThisCheck;
            IsHiddenByFilter = isHiddenByFilter;
            Counts = counts ?? SheetCounts.None;
        }

        /// <summary>The way back to the element — a UniqueId rather than an ElementId, which the window has no type for.</summary>
        public string UniqueId { get; }

        public string Number { get; }

        public string Name { get; }

        /// <summary>"A101 - Floor Plan", the way the Project Browser itself writes a sheet.</summary>
        public string Title => Number.Length == 0 ? Name : Name.Length == 0 ? Number : Number + " - " + Name;

        /// <summary>The Project Browser folders the sheet sits in, outermost first; empty when the organization has none.</summary>
        public IReadOnlyList<string> Folders { get; }

        /// <summary>One line per thing placed on the sheet: "Floor plan: Level 1", "Schedule: Doors"… — with the date of a change found in it.</summary>
        public IReadOnlyList<string> Contents { get; }

        /// <summary>False for a placeholder sheet: it exists only as a line in sheet lists and has nothing to open.</summary>
        public bool CanOpen { get; }

        /// <summary>A sheet the browser organization's filter leaves out — still a sheet of the project.</summary>
        public bool IsHiddenByFilter { get; }

        public SheetCounts Counts { get; }

        /// <summary>A real sheet with nothing on it. A placeholder is not one: it never holds anything.</summary>
        public bool IsEmpty => CanOpen && Contents.Count == 0;

        /// <summary>
        /// The latest day a change was found in the sheet itself or in a view on it; null when none was
        /// found since tracking began, or nothing is tracked.
        /// </summary>
        public DateTime? Changed { get; }

        /// <summary>"2026-10-07" — a form that sorts and filters the same in Excel whatever the locale.</summary>
        public string ChangedText => FormatDate(Changed);

        /// <summary>What changed and between which checks — the tooltip of the date.</summary>
        public string ChangeNote { get; }

        /// <summary>True when the check made by opening the window this time found a change on the sheet.</summary>
        public bool ChangedAtThisCheck { get; }

        public static string FormatDate(DateTime? date)
        {
            return date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    /// <summary>Where the "Sheets" change history stands, for the window's explanation line and its "Show" list.</summary>
    internal sealed class SheetTracking
    {
        public SheetTracking(bool isTracked, DateTime? since, DateTime? previousCheck, string problem)
        {
            IsTracked = isTracked;
            Since = since;
            PreviousCheck = previousCheck;
            Problem = problem ?? string.Empty;
        }

        /// <summary>False for a model never saved, or a history that could not be read.</summary>
        public bool IsTracked { get; }

        /// <summary>The first check; null when this one is the first.</summary>
        public DateTime? Since { get; }

        /// <summary>The check before this one; null when this one is the first.</summary>
        public DateTime? PreviousCheck { get; }

        public string Problem { get; }

        public bool HasDates => IsTracked && PreviousCheck != null;
    }
}
