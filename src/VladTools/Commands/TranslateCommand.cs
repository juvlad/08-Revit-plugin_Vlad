using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VladTools.Infrastructure;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// The "Translate" button: replaces the Russian a person typed into the open project or template
    /// — names, parameter values, schedule headings and filters, view filter rules, text notes —
    /// with English, through one dictionary of original → translation.
    ///
    /// Revit has no language switch for this: it translates only what is its own (categories,
    /// built-in parameters, view types), and everything typed into a template stays as typed in any
    /// language Revit runs in. The work is replacing strings — and the one thing that makes it more
    /// than find-and-replace is that a string is often data, not a label: "Отверстия" in a view
    /// parameter is also what a view filter, a schedule filter and the Project Browser's grouping
    /// compare against. That is why the unit of work is a *text*, replaced in every place at once,
    /// and never a place on its own.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class TranslateCommand : IExternalCommand
    {
        private const string DialogTitle = "Translate";
        private const int ListLimit = 15;

        private enum Outcome
        {
            Done,
            AlreadyDone,
            Refused,
            Failed
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "There is no active document.";
                return Result.Cancelled;
            }

            var doc = uidoc.Document;
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "The command works only in a project or a project template (.rvt, .rte).\n" +
                    "A family keeps its own parameters, labels and types, and is translated inside the family itself.");
                return Result.Cancelled;
            }

            try
            {
                var scan = TextSiteCollector.Collect(doc);
                if (scan.Sites.Count == 0)
                {
                    TaskDialog.Show(DialogTitle,
                        "No text with Cyrillic letters was found in the project — there is nothing to translate.\n\n" +
                        "Not looked into: text inside loaded families (tag labels, titleblock text, family parameter names).");
                    return Result.Cancelled;
                }

                var window = new TranslateWindow(Summarize(scan.Sites), scan.Scopes, doc.Title);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                Apply(doc, scan.Sites, window.Selected);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── the snapshot for the window ─────────────────────────────

        /// <summary>One row per distinct text, with every place it is used in counted and listed.</summary>
        private static IReadOnlyList<TranslationText> Summarize(IReadOnlyList<TextSite> sites)
        {
            const int detailLimit = 25;

            return sites
                .GroupBy(site => site.Text, StringComparer.Ordinal)
                .Select(group =>
                {
                    var all = group.ToList();
                    var fixedSites = all.Where(site => site.FixedReason != null).ToList();

                    var places = string.Join("; ", all
                        .GroupBy(site => site.Kind)
                        .OrderByDescending(kind => kind.Count())
                        .ThenBy(kind => kind.Key, StringComparer.CurrentCulture)
                        .Select(kind => Counted(kind.Key, kind.Count())));

                    var detail = string.Join("\n", all.Take(detailLimit).Select(site => site.Owner + " — " + site.Kind));
                    if (all.Count > detailLimit)
                        detail += "\n… and " + (all.Count - detailLimit) + " more";

                    var fixedNote = string.Join("; ", fixedSites
                        .GroupBy(site => site.Kind)
                        .Select(kind => Counted(kind.Key, kind.Count()) + " stays as it is — " + kind.First().FixedReason));

                    return new TranslationText(
                        group.Key,
                        all.Count - fixedSites.Count,
                        fixedSites.Count,
                        places,
                        detail,
                        fixedNote,
                        all.Any(site => site.IsName));
                })
                .ToList();
        }

        private static string Counted(string kind, int count)
        {
            return count == 1 ? kind : kind + " ×" + count;
        }

        // ───────────────────────────── applying ─────────────────────────────

        private static void Apply(Document doc, IReadOnlyList<TextSite> sites, IReadOnlyDictionary<string, string> translations)
        {
            var plan = sites.Where(site => site.FixedReason == null && translations.ContainsKey(site.Text)).ToList();
            var kept = sites.Where(site => site.FixedReason != null && translations.ContainsKey(site.Text)).ToList();

            var done = new List<TextSite>();
            var failures = new List<string>();
            var notes = new List<string>();
            var warnings = new WarningSuppressor();

            var browser = BrowserWatch.Before(doc);

            using (var transaction = new Transaction(doc, "Translate texts"))
            {
                transaction.Start();

                // A batch of renames and value changes drags Revit warnings along; a modal dialog per
                // warning would wreck it, so they are gathered into the report instead.
                var options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(warnings);
                transaction.SetFailureHandlingOptions(options);

                WriteNames(doc, plan.Where(site => site.IsName).ToList(), translations, done, failures);

                foreach (var site in plan.Where(site => !site.IsName))
                {
                    if (Write(doc, site, translations[site.Text], out var error) == Outcome.Done)
                        done.Add(site);
                    else if (error != null)
                        failures.Add(error);
                }

                if (done.Count == 0)
                {
                    transaction.RollBack();
                }
                else
                {
                    notes.AddRange(browser.Lost(doc));

                    // Commit returns a status, and Revit can roll the whole batch back there without
                    // throwing — the same trap as in "Accept Changes". A rolled-back run must not
                    // report a single text as translated.
                    var status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        failures.Insert(0, "Revit rolled the whole translation back (" + status + ") — nothing was changed.");
                        done.Clear();
                        notes.Clear();
                    }
                }
            }

            Report(done, kept, failures, notes, warnings);
        }

        /// <summary>
        /// Names go in passes, the same way "Rename Nested" does it: while a name is still held by a
        /// neighbour that is being renamed too, Revit refuses it, and the next pass gets it once the
        /// neighbour has moved on. A refusal only counts as a failure in a pass where nothing at all
        /// went through.
        /// </summary>
        private static void WriteNames(
            Document doc,
            List<TextSite> sites,
            IReadOnlyDictionary<string, string> translations,
            List<TextSite> done,
            List<string> failures)
        {
            var pending = sites;
            var lastError = new Dictionary<TextSite, string>();

            while (pending.Count > 0)
            {
                var retry = new List<TextSite>();

                foreach (var site in pending)
                {
                    switch (Write(doc, site, translations[site.Text], out var error))
                    {
                        case Outcome.Done:
                            done.Add(site);
                            break;
                        case Outcome.Refused:
                            retry.Add(site);
                            lastError[site] = error;
                            break;
                        case Outcome.Failed:
                            failures.Add(error);
                            break;
                    }
                }

                if (retry.Count == pending.Count)
                {
                    failures.AddRange(retry.Select(site => lastError[site]));
                    break;
                }

                pending = retry;
            }
        }

        /// <summary>
        /// One place. Checked before (it still holds the original) and after (the translation is
        /// really there) — Revit taking a value without an error is not the same as the value landing.
        /// </summary>
        private static Outcome Write(Document doc, TextSite site, string translation, out string error)
        {
            error = null;

            try
            {
                if (!site.Holds(doc, site.Text))
                {
                    // A built-in parameter that mirrors a name already renamed in this run: done, not failed.
                    if (site.Holds(doc, translation))
                        return Outcome.AlreadyDone;

                    var current = site.Read(doc);
                    error = Line(site, translation, current == null
                        ? "it is no longer in the project"
                        : "it reads \"" + current + "\" now, not the text the window showed");
                    return Outcome.Failed;
                }

                site.Write(doc, translation);
            }
            catch (Exception exception) when (exception is ArgumentException || exception is Autodesk.Revit.Exceptions.ArgumentException)
            {
                // A name already taken comes back this way — possibly only for the moment (see WriteNames).
                error = Line(site, translation, LinkCatalog.Short(exception.Message));
                return Outcome.Refused;
            }
            catch (Exception exception)
            {
                error = Line(site, translation, LinkCatalog.Short(exception.Message) + (site.Hint != null ? " (" + site.Hint + ")" : string.Empty));
                return Outcome.Failed;
            }

            if (site.Holds(doc, translation))
                return Outcome.Done;

            error = Line(site, translation, "Revit took the change without an error, but the text still reads \"" + site.Read(doc) + "\"");
            return Outcome.Failed;
        }

        private static string Line(TextSite site, string translation, string reason)
        {
            return site.Owner + " — " + site.Kind + " → \"" + translation + "\": " + reason;
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(
            IReadOnlyList<TextSite> done,
            IReadOnlyList<TextSite> kept,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> notes,
            WarningSuppressor warnings)
        {
            string text;

            if (done.Count > 0)
            {
                var texts = done.Select(site => site.Text).Distinct(StringComparer.Ordinal).Count();

                text = "Translated: " + texts + (texts == 1 ? " text" : " texts") + " in " + done.Count +
                       (done.Count == 1 ? " place." : " places.") + "\n\n• " +
                       string.Join("\n• ", done
                           .GroupBy(site => site.Group)
                           .OrderByDescending(group => group.Count())
                           .Select(group => group.Key + " — " + group.Count()));
            }
            else
            {
                text = "Nothing was translated.";
            }

            var reset = done.OfType<TextNoteSite>().Count(note => note.FormattingReset);
            if (reset > 0)
                text += "\n\nText notes with bold, italic or underline on only part of the text: " + reset +
                        ". That part-formatting was reset; the text type's own formatting stays.";

            foreach (var note in notes)
                text += "\n\n" + note;

            if (kept.Count > 0)
            {
                text += "\n\nLeft as they are — the Revit API cannot change these (" + kept.Count + "):";

                foreach (var reason in kept.GroupBy(site => site.FixedReason))
                {
                    var owners = reason.Select(site => site.Owner).ToList();

                    text += "\n" + Capitalize(reason.Key) + ":\n• " + string.Join("\n• ", owners.Take(ListLimit));
                    if (owners.Count > ListLimit)
                        text += "\n… and " + (owners.Count - ListLimit) + " more";
                }
            }

            if (failures.Count > 0)
            {
                text += "\n\nCould not be done (" + failures.Count + "):\n• " + string.Join("\n• ", failures.Take(ListLimit));
                if (failures.Count > ListLimit)
                    text += "\n… and " + (failures.Count - ListLimit) + " more";
            }

            const int warningLimit = 5;

            foreach (var warning in warnings.Messages.Take(warningLimit))
                text += "\n\nRevit warning: " + warning;

            if (warnings.Messages.Count > warningLimit)
                text += "\n… and " + (warnings.Messages.Count - warningLimit) + " more Revit warnings";

            text += "\n\nNot looked into: text inside loaded families — tag labels, titleblock text, family parameter " +
                    "names. Those are edited in the family itself.";

            TaskDialog.Show(DialogTitle, text);
        }

        private static string Capitalize(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        // ───────────────────────────── the Project Browser ─────────────────────────────

        /// <summary>
        /// Watches the Project Browser's organizations through the translation.
        ///
        /// A browser organization can filter views by a parameter value ("Discipline equals
        /// Отверстия"), and the Revit API gives no way to read or edit that filter. Once the value is
        /// translated, every view carrying it silently drops out of that organization — the very
        /// "the folder disappeared" a translated template must not do without a word. What the API
        /// does give is <c>AreFiltersSatisfied</c>, so which views pass is recorded before the run and
        /// checked again inside the transaction, before the commit; a view that stopped passing goes
        /// into the report with where to fix the filter by hand.
        /// </summary>
        private sealed class BrowserWatch
        {
            private const int NameLimit = 5;

            private readonly List<KeyValuePair<BrowserOrganization, List<ElementId>>> _passing;

            private BrowserWatch(List<KeyValuePair<BrowserOrganization, List<ElementId>>> passing)
            {
                _passing = passing;
            }

            public static BrowserWatch Before(Document doc)
            {
                var passing = new List<KeyValuePair<BrowserOrganization, List<ElementId>>>();

                try
                {
                    var views = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(IsBrowserView)
                        .Select(view => view.Id)
                        .ToList();

                    foreach (var organization in Organizations(doc))
                        passing.Add(new KeyValuePair<BrowserOrganization, List<ElementId>>(
                            organization,
                            views.Where(id => Passes(organization, id)).ToList()));
                }
                catch (Exception)
                {
                    // Watching is a courtesy on top of the translation, never a reason to stop it.
                }

                return new BrowserWatch(passing);
            }

            /// <summary>One sentence per organization that lost views. Must run inside the transaction, before the commit.</summary>
            public IReadOnlyList<string> Lost(Document doc)
            {
                var lines = new List<string>();
                if (_passing.All(entry => entry.Value.Count == 0))
                    return lines;

                try
                {
                    doc.Regenerate();
                }
                catch (Exception)
                {
                    return lines;
                }

                foreach (var entry in _passing)
                {
                    var lost = entry.Value.Where(id => !Passes(entry.Key, id)).ToList();
                    if (lost.Count == 0)
                        continue;

                    var names = lost.Take(NameLimit).Select(id => "\"" + (doc.GetElement(id)?.Name ?? string.Empty) + "\"");

                    lines.Add("The Project Browser organization \"" + SafeName(entry.Key) + "\" filters by a value that " +
                              "was just translated: " + lost.Count + (lost.Count == 1 ? " view" : " views") +
                              " no longer pass its filter and will not show under it (" + string.Join(", ", names) +
                              (lost.Count > NameLimit ? ", …" : string.Empty) + "). The Revit API cannot edit a browser " +
                              "organization's filter — change it by hand: View → User Interface → Browser Organization → " +
                              "Edit → Filtering, or undo the translation with Ctrl+Z.");
                }

                return lines;
            }

            /// <summary>Every organization in the project; where Revit will not list them, at least the three in use.</summary>
            private static IReadOnlyList<BrowserOrganization> Organizations(Document doc)
            {
                try
                {
                    return new FilteredElementCollector(doc).OfClass(typeof(BrowserOrganization)).Cast<BrowserOrganization>().ToList();
                }
                catch (Exception)
                {
                    return new[]
                        {
                            BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc),
                            BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc),
                            BrowserOrganization.GetCurrentBrowserOrganizationForSchedules(doc)
                        }
                        .Where(organization => organization != null)
                        .ToList();
                }
            }

            private static bool Passes(BrowserOrganization organization, ElementId id)
            {
                try
                {
                    return organization.AreFiltersSatisfied(id);
                }
                catch (Exception)
                {
                    return false;
                }
            }

            private static bool IsBrowserView(View view)
            {
                if (view.IsTemplate)
                    return false;

                switch (view.ViewType)
                {
                    case ViewType.Internal:
                    case ViewType.ProjectBrowser:
                    case ViewType.SystemBrowser:
                    case ViewType.Undefined:
                        return false;
                    default:
                        return true;
                }
            }

            private static string SafeName(Element element)
            {
                try
                {
                    return element.Name;
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }
        }
    }
}
