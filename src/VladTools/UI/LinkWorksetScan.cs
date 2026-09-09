using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// The result of reading the worksets of the selected links: the workset names themselves
    /// and how many models could not be read.
    ///
    /// Reading does not open the models, but it goes over the network and takes seconds on a dozen
    /// links — so it sits behind a button instead of running by itself, and returns a summary like
    /// the family scan in the "Delete Shared Parameters" window.
    /// </summary>
    internal sealed class LinkWorksetScan
    {
        public LinkWorksetScan(IReadOnlyList<string> names, int scanned, IReadOnlyList<string> failures)
        {
            Names = names ?? new List<string>();
            Scanned = scanned;
            Failures = failures ?? new List<string>();
        }

        /// <summary>Every workset name found in at least one model that was read.</summary>
        public IReadOnlyList<string> Names { get; }

        /// <summary>How many models were read successfully.</summary>
        public int Scanned { get; }

        /// <summary>Models that could not be read, with the reason.</summary>
        public IReadOnlyList<string> Failures { get; }
    }
}
