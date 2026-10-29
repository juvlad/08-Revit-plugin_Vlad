using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Which worksets inside a link are to be open and which closed — one setup for a whole batch of
    /// links: the names checked in a list, a name rule, and a base mode for everything else.
    ///
    /// "Link Manager" applies it to the links of the open project, "Insert Links" to the links it puts
    /// into a batch of models. Both build the request and check what Revit did through this class, so the
    /// two can never phrase the request differently or judge the result by different rules — the phrasing
    /// is exactly what decides whether Revit carries it out (see <see cref="Configuration"/>).
    ///
    /// Names, not ids: every model has ids of its own, and the only thing "00_Shared levels and grids" has
    /// in common across links is its name. The ids are looked up per link, right before it loads — by
    /// Autodesk's documentation <c>WorksetPreview.Id</c> changes on synchronising with central, and a stale
    /// id is silently ignored by both Open and Close.
    /// </summary>
    internal sealed class LinkWorksetRule
    {
        public LinkWorksetRule(LinkWorksetMode mode, IEnumerable<string> names, string pattern, bool patternContains)
        {
            Mode = mode;
            Names = (names ?? Enumerable.Empty<string>())
                .Select(LinkPreferences.NormalizeWorkset)
                .Where(name => name.Length > 0)
                .ToList();
            Pattern = (pattern ?? string.Empty).Trim();
            PatternContains = patternContains;
        }

        /// <summary>The rule "Link Manager" keeps in its settings.</summary>
        public static LinkWorksetRule From(LinkPreferences preferences)
        {
            return new LinkWorksetRule(
                preferences.WorksetMode,
                preferences.Worksets,
                preferences.WorksetPattern,
                preferences.WorksetPatternContains);
        }

        public LinkWorksetMode Mode { get; }

        /// <summary>The names checked in the window's list.</summary>
        public IReadOnlyList<string> Names { get; }

        /// <summary>The name rule; empty — there is none.</summary>
        public string Pattern { get; }

        /// <summary>The rule looks for a substring rather than the start of the name.</summary>
        public bool PatternContains { get; }

        /// <summary>Whether any workset is asked for by name or by rule — otherwise there is nothing to read.</summary>
        public bool Requested => Names.Count > 0 || Pattern.Length > 0;

        /// <summary>
        /// Whether a link's workset is one the rule names. Leading and trailing spaces are trimmed both
        /// here and in the rule: a consultant's workset name with a trailing space would otherwise never
        /// close at all — see <see cref="LinkPreferences.NormalizeWorkset"/>.
        /// </summary>
        public bool Matches(string name)
        {
            var trimmed = LinkPreferences.NormalizeWorkset(name);

            if (Names.Any(chosen => LinkPreferences.SameWorkset(chosen, trimmed)))
                return true;

            if (Pattern.Length == 0)
                return false;

            return PatternContains
                ? trimmed.IndexOf(Pattern, StringComparison.CurrentCultureIgnoreCase) >= 0
                : trimmed.StartsWith(Pattern, StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>
        /// The base mode when there is nothing to list by name: nothing is checked, or the link's
        /// workset list could not be read.
        ///
        /// It matters here that "open all" is specifically <c>OpenAllWorksets</c>: substituting
        /// <c>CloseAllWorksets</c> here, as in the main path, would mean loading the link empty on
        /// any read failure, silently.
        /// </summary>
        public WorksetConfigurationOption BaseOption()
        {
            switch (Mode)
            {
                case LinkWorksetMode.CloseAll:
                    return WorksetConfigurationOption.CloseAllWorksets;
                case LinkWorksetMode.LastViewed:
                    return WorksetConfigurationOption.OpenLastViewed;
                default:
                    return WorksetConfigurationOption.OpenAllWorksets;
            }
        }

        /// <summary>The base mode as a configuration — what a link loads with when its worksets are not listed.</summary>
        public WorksetConfiguration Base()
        {
            return new WorksetConfiguration(BaseOption());
        }

        /// <summary>
        /// Builds the workset setup for one link out of its own worksets, read just before: the base mode
        /// plus the names checked in the window plus the name rule. The rule is applied here rather than in
        /// the window, exactly because every model's worksets are its own: "00_" catches both "00_Shared
        /// Levels and Grids" and its Russian-named equivalent, though neither appears in the window's list.
        ///
        /// The request is always phrased as "close all, open the listed ones" rather than "open all, close
        /// the listed ones". By the documentation both phrasings are equivalent, but CloseAllWorksets + Open
        /// is the only one Autodesk's own example uses specifically for links (Developers Guide → Linked
        /// Files → Revit Links), and the only one with no reports of silently doing nothing.
        /// OpenAllWorksets + Close on links leaves the worksets open while reporting success — exactly what
        /// made "Link Manager" "fail at closing". So "close the checked ones" is computed as a complement:
        /// open every link workset except the checked ones.
        ///
        /// The caller reads the worksets and deals with a failed or empty read itself (each says so in its
        /// own report); this takes the list as read.
        /// </summary>
        public WorksetConfiguration Configuration(IReadOnlyList<ModelWorkset> worksets)
        {
            if (!Requested || worksets == null || worksets.Count == 0)
                return Base();

            var marked = worksets.Where(workset => Matches(workset.Name)).Select(workset => workset.Id).ToList();
            var rest = worksets.Where(workset => !Matches(workset.Name)).Select(workset => workset.Id).ToList();

            // Nothing checked matches this link — nothing to close, hand back a plain base mode.
            // This also sidesteps an ambiguity in the Open method's XML doc: "if all worksets are
            // set to open, the configuration will be unchanged". Finding out on a live project
            // whether that means CloseAll + Open(all) would leave the link entirely closed is not
            // a price worth paying for the answer.
            if (marked.Count == 0)
                return Base();

            // "As last opened" cannot be expressed through Open: only Revit knows what was open
            // last time, and there is no complement to compute here. Close is what is left — with
            // the very unreliability described above; if Revit does not carry it out, Check will
            // catch that and say so in the report rather than swallow it.
            if (Mode == LinkWorksetMode.LastViewed)
            {
                var lastViewed = new WorksetConfiguration(WorksetConfigurationOption.OpenLastViewed);
                lastViewed.Close(marked);

                return lastViewed;
            }

            // Under "close all", a check mark means the opposite: open only the checked ones.
            var open = Mode == LinkWorksetMode.CloseAll ? marked : rest;

            var configuration = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);

            if (open.Count > 0)
                configuration.Open(open);

            return configuration;
        }

        /// <summary>
        /// What a workset with this name should become after loading: <c>true</c> — open,
        /// <c>false</c> — closed, <c>null</c> — nothing was promised about it, and it cannot be
        /// checked. A mirror of <see cref="Configuration"/>: edit one, edit the other.
        /// </summary>
        public bool? Expected(string name)
        {
            var marked = Matches(name);

            switch (Mode)
            {
                case LinkWorksetMode.CloseAll:
                    return marked;

                case LinkWorksetMode.OpenAll:
                    return !marked;

                default:
                    // "As last opened": only Revit knows what happened to the unchecked ones.
                    return marked ? (bool?)false : null;
            }
        }

        /// <summary>
        /// Re-reads an already-loaded link document and checks what Revit actually did with the
        /// worksets against what was asked. A WorksetConfiguration only conveys a wish; without this,
        /// finding out whether Revit obeyed could only be done by hand — through its own "Manage
        /// Worksets".
        ///
        /// There are three outcomes, not two, and that is the whole point. "Link Manager" once returned
        /// "could not be checked" as "everything as asked": the link would not hand over its document,
        /// the collector would fail — and the report read "Loaded" with not a word about worksets. That
        /// is precisely how a closing failure stayed unnoticed, so <see cref="LinkWorksetVerdict.Unknown"/>
        /// reaches every report as a line of its own.
        /// </summary>
        public LinkWorksetCheck Check(Document linkDocument)
        {
            if (!Requested)
                return LinkWorksetCheck.NotRequested();

            if (linkDocument == null)
                return LinkWorksetCheck.Unknown("the link did not hand over its document — the worksets could not be checked");

            try
            {
                // A non-workshared link has no worksets at all: nothing to close, and that is not a failure.
                if (!linkDocument.IsWorkshared)
                    return LinkWorksetCheck.NotRequested();

                var worksets = new FilteredWorksetCollector(linkDocument)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToWorksets()
                    .ToList();

                if (worksets.Count == 0)
                    return LinkWorksetCheck.Unknown("the workshared link has no worksets at all — nothing to check against");

                var wrong = new List<string>();

                foreach (var workset in worksets)
                {
                    var expected = Expected(workset.Name);

                    if (expected.HasValue && workset.IsOpen != expected.Value)
                        wrong.Add(workset.Name);
                }

                return LinkWorksetCheck.Verified(wrong);
            }
            catch (Exception exception)
            {
                return LinkWorksetCheck.Unknown("could not check the worksets: " + LinkCatalog.Explain(exception));
            }
        }

        /// <summary>
        /// The checked names that were not found in any link of the run, given the names that were.
        ///
        /// A checked name found in no link is the most common reason behind "the workset did not close":
        /// there is simply nothing to close, <see cref="Matches"/> never finds such a workset, and nothing
        /// else would say so. An empty <paramref name="seen"/> means the worksets could not be read for any
        /// link at all — then "not found" has nothing to say: the reason is a different one, and it is
        /// already in the report, on a line of its own.
        /// </summary>
        /// <param name="seen">The normalised workset names read from the links, compared ignoring case.</param>
        public List<string> Missing(ISet<string> seen)
        {
            if (seen == null || seen.Count == 0)
                return new List<string>();

            return Names.Where(name => !seen.Contains(LinkPreferences.NormalizeWorkset(name))).ToList();
        }

        /// <summary>The rule in words, for a report: "open all, close \"00_…\" and whatever starts with \"00_\"".</summary>
        public string Describe()
        {
            var names = string.Join(", ", Names.Select(name => "\"" + name + "\""));
            var rule = Pattern.Length == 0
                ? string.Empty
                : (Names.Count > 0 ? " and " : string.Empty) + "whatever " + (PatternContains ? "contains" : "starts with") +
                  " \"" + Pattern + "\"";

            switch (Mode)
            {
                case LinkWorksetMode.CloseAll:
                    return Requested ? "close all, open " + names + rule : "close all";

                case LinkWorksetMode.LastViewed:
                    return Requested ? "as last opened, close " + names + rule : "as last opened";

                default:
                    return Requested ? "open all, close " + names + rule : "open all — left alone";
            }
        }
    }

    /// <summary>What was found out about the worksets of an already-loaded link.</summary>
    internal enum LinkWorksetVerdict
    {
        /// <summary>Nothing was requested — nothing to check.</summary>
        NotRequested,

        /// <summary>Everything is as asked.</summary>
        Satisfied,

        /// <summary>Some worksets are not in the right state — Revit did not carry out the request.</summary>
        Mismatched,

        /// <summary>Could not be checked. This is not the same thing as "all is well".</summary>
        Unknown
    }

    /// <summary>
    /// The result of checking a link's worksets against what was requested. It exists so that
    /// <see cref="LinkWorksetVerdict.Unknown"/> can never be mistaken for a success: an earlier check
    /// returned an empty list both when it checked and everything matched, and when it could not
    /// check at all.
    /// </summary>
    internal sealed class LinkWorksetCheck
    {
        private static readonly string[] None = new string[0];

        private LinkWorksetCheck(LinkWorksetVerdict verdict, IReadOnlyList<string> names, string reason)
        {
            Verdict = verdict;
            Names = names ?? None;
            Reason = reason ?? string.Empty;
        }

        public LinkWorksetVerdict Verdict { get; }

        /// <summary>The names of the worksets whose state did not match what was requested.</summary>
        public IReadOnlyList<string> Names { get; }

        /// <summary>
        /// Why it could not be checked, or — on a mismatch — why the reload meant to fix it failed;
        /// empty otherwise.
        /// </summary>
        public string Reason { get; }

        /// <summary>The same outcome, with the reason the reload left it as it was.</summary>
        public LinkWorksetCheck Because(string reason)
        {
            return new LinkWorksetCheck(Verdict, Names, reason);
        }

        public static LinkWorksetCheck NotRequested()
        {
            return new LinkWorksetCheck(LinkWorksetVerdict.NotRequested, None, null);
        }

        public static LinkWorksetCheck Unknown(string reason)
        {
            return new LinkWorksetCheck(LinkWorksetVerdict.Unknown, None, reason);
        }

        public static LinkWorksetCheck Verified(IReadOnlyList<string> wrong)
        {
            return wrong.Count == 0
                ? new LinkWorksetCheck(LinkWorksetVerdict.Satisfied, None, null)
                : new LinkWorksetCheck(LinkWorksetVerdict.Mismatched, wrong, null);
        }
    }
}
