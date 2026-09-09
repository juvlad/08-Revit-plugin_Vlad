namespace VladTools.UI
{
    /// <summary>
    /// What exactly the "Cleanup" command removes from the project.
    ///
    /// The order of the values is the order of the rows in the window. The execution order is
    /// different and is defined in the command itself: presentation first, families last.
    /// </summary>
    internal enum CleanupTarget
    {
        /// <summary>Loaded families and types that no model element uses.</summary>
        UnusedFamilies,

        /// <summary>All sheets.</summary>
        Sheets,

        /// <summary>View filters and selection filters.</summary>
        Filters,

        /// <summary>Graphical views: plans, sections, elevations, 3D, callouts, drafting views.</summary>
        Views,

        /// <summary>Legend views.</summary>
        Legends,

        /// <summary>Schedules, material and note takeoffs, panel schedules.</summary>
        Schedules,

        /// <summary>Model groups: they are ungrouped, the contents stay in place.</summary>
        ModelGroups,

        /// <summary>Group types that are not placed in the model.</summary>
        UnusedGroups
    }
}
