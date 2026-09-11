using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of one schedule for the window: everything needed to draw a row, and not a single
    /// Revit type — the windows of this add-in know nothing about the Revit API.
    ///
    /// The schedule is identified by its name alone, and that is not a shortcut: view names are
    /// unique within a document, so a name is a reliable key on both sides — the window hands names
    /// back, and the command finds the elements by them again, in the cache and in the project.
    /// </summary>
    internal sealed class ScheduleInfo
    {
        public ScheduleInfo(string name, string category, int fieldCount, bool isKeySchedule, bool isMaterialTakeoff)
        {
            Name = name ?? string.Empty;
            Category = category ?? string.Empty;
            FieldCount = fieldCount;
            IsKeySchedule = isKeySchedule;
            IsMaterialTakeoff = isMaterialTakeoff;
        }

        /// <summary>The schedule name as it stands in the browser.</summary>
        public string Name { get; }

        /// <summary>The category the schedule counts, in words; multi-category ones have none.</summary>
        public string Category { get; }

        /// <summary>How many fields (columns) the schedule has — a rough sense of its size in the table.</summary>
        public int FieldCount { get; }

        /// <summary>
        /// A key schedule. Worth telling apart from an ordinary one: its rows are elements, and
        /// copying one carries data into the project, not merely a view.
        /// </summary>
        public bool IsKeySchedule { get; }

        public bool IsMaterialTakeoff { get; }

        /// <summary>The kind of schedule in words — a column of its own in the table.</summary>
        public string Kind
        {
            get
            {
                if (IsKeySchedule)
                    return "Key schedule";

                return IsMaterialTakeoff ? "Material takeoff" : "Schedule";
            }
        }

        public string Caption => Category.Length == 0 ? Name : Name + " (" + Category + ")";
    }

    /// <summary>
    /// Picks, out of the schedules found in the source model, the ones to put into the set.
    ///
    /// Choosing is the window's business and reading the model is the command's, yet the model must
    /// not be opened twice for it — a base model takes minutes to open. So the choice is handed to
    /// the reading side as a call-back: the command opens the model once, asks, copies and closes it.
    /// Returning null means the user backed out, and nothing at all should be reported afterwards.
    /// </summary>
    internal delegate IReadOnlyList<ScheduleInfo> ScheduleChooser(IReadOnlyList<ScheduleInfo> found);
}
