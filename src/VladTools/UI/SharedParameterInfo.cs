using System;

namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of one definition read out of a shared parameter file: enough to add it to a set and
    /// to show it in the "Add from shared file…" picker, but not the Revit <c>ExternalDefinition</c>
    /// itself — the window knows nothing about the Revit API, the same rule as everywhere else in the
    /// project (see CLAUDE.md, "Windows know nothing about Revit").
    /// </summary>
    internal sealed class SharedParameterInfo
    {
        public SharedParameterInfo(Guid guid, string name, string groupName, string dataType)
        {
            Guid = guid;
            Name = name ?? string.Empty;
            GroupName = groupName ?? string.Empty;
            DataType = dataType ?? string.Empty;
        }

        /// <summary>The parameter's permanent identity — what a set actually stores and looks up by.</summary>
        public Guid Guid { get; }

        public string Name { get; }

        /// <summary>The group inside the shared parameter file itself (not a Revit parameter group).</summary>
        public string GroupName { get; }

        /// <summary>A short label of the parameter's data type ("Text", "Length", …), for information only.</summary>
        public string DataType { get; }
    }
}
