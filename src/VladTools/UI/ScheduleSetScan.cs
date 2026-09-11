using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// What came of working with one cache set: what is in it now, what was just added to it, and
    /// what could not be done.
    ///
    /// One type for reading, capturing and removing on purpose: all three end the same way — the
    /// window has to show the set's current contents and say what went wrong. <see cref="Added"/> is
    /// empty for a plain read, and that is the only difference between them.
    /// </summary>
    internal sealed class ScheduleSetScan
    {
        public ScheduleSetScan(
            string setName,
            string filePath,
            IReadOnlyList<ScheduleInfo> schedules,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> added)
        {
            SetName = setName ?? string.Empty;
            FilePath = filePath ?? string.Empty;
            Schedules = schedules ?? new List<ScheduleInfo>();
            Failures = failures ?? new List<string>();
            Added = added ?? new List<string>();
        }

        public string SetName { get; }

        /// <summary>The cache file itself — shown to the user so the set can be found and copied by hand.</summary>
        public string FilePath { get; }

        /// <summary>The schedules the set holds right now.</summary>
        public IReadOnlyList<ScheduleInfo> Schedules { get; }

        /// <summary>What could not be read or copied. Never silent, never thrown to the surface.</summary>
        public IReadOnlyList<string> Failures { get; }

        /// <summary>The names just put into the set; empty on a plain read.</summary>
        public IReadOnlyList<string> Added { get; }
    }
}
