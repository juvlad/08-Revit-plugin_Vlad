namespace VladTools.UI
{
    /// <summary>
    /// What to do when the project already holds a schedule of that name. Revit gives no third way:
    /// view names are unique in a document, so either the existing one stays, or it goes, or the new
    /// one arrives under a name of its own.
    ///
    /// The order of the values is the order in the window drop-down.
    /// </summary>
    internal enum ScheduleAction
    {
        /// <summary>Leave the project's own schedule alone and do not insert this one.</summary>
        Skip,

        /// <summary>Delete the project's schedule and put the one from the set in its place.</summary>
        Replace,

        /// <summary>Insert alongside, under a free name ("Doors (2)").</summary>
        AddCopy
    }

    /// <summary>Text for the drop-down and the reports — the window knows nothing about the Revit API, so it lives here.</summary>
    internal static class ScheduleActionText
    {
        public static string Caption(ScheduleAction action)
        {
            switch (action)
            {
                case ScheduleAction.Skip:
                    return "Skip";
                case ScheduleAction.Replace:
                    return "Replace";
                case ScheduleAction.AddCopy:
                    return "Insert as a copy";
                default:
                    return action.ToString();
            }
        }

        public static readonly ScheduleAction[] All =
        {
            ScheduleAction.Skip,
            ScheduleAction.Replace,
            ScheduleAction.AddCopy
        };
    }
}
