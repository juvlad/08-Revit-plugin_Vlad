using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VladTools.Infrastructure;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// The "Insert Links" button: a set of links put into a batch of models on Revit Server that nobody
    /// has open — each link into its project workset, named the same in every model, with "Link
    /// Manager"'s setup of the worksets inside the links.
    ///
    /// A routine job at the start of a stage or when a discipline joins: the same consultants' models have
    /// to be linked into every model of a building. "Link Manager" does it for the model on screen; here
    /// the list of models is the other half of the window, and the button opens each one itself.
    ///
    /// Each model is the unit of work (<see cref="LinkInsertion"/>): opened as a local copy, the links
    /// inserted and read back, synchronised — one that fails is a line in the report and the next one is
    /// taken, and the report is written into a file as the run goes. There is no transaction to undo the
    /// run with: the models are synchronised with central one by one, which is the point of a batch.
    ///
    /// The button needs no open document (<see cref="AlwaysAvailable"/>); an open project only lends its
    /// worksets to the window's drop-down, since it is most likely one of the very models.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class InsertLinksCommand : IExternalCommand
    {
        private const string DialogTitle = "Insert Links";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var application = commandData?.Application;
            if (application == null)
                return Result.Cancelled;

            try
            {
                var app = application.Application;
                var doc = application.ActiveUIDocument?.Document;
                var offered = doc != null && !doc.IsFamilyDocument ? LinkCatalog.HostWorksets(doc) : new List<string>();

                var linkPreferences = LinkPreferences.Load();
                var window = new InsertLinksWindow(
                    InsertLinksPreferences.Load(linkPreferences),
                    linkPreferences,
                    offered,
                    entries => ReadWorksets(app, entries));
                new WindowInteropHelper(window).Owner = application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                Run(app, window);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// The worksets of models on Revit Server, read without opening them (<c>GetUserWorksetInfo</c>) —
        /// the links' own for the list of worksets inside them, the target models' for the project worksets.
        /// </summary>
        private static LinkWorksetScan ReadWorksets(Application app, IReadOnlyList<LinkEntry> entries)
        {
            var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var byModel = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var failures = new List<string>();

            foreach (var entry in entries)
            {
                try
                {
                    var worksets = LinkCatalog.ModelWorksets(app, entry, false).Select(workset => workset.Name).ToList();
                    byModel[entry.Key] = worksets;

                    foreach (var name in worksets)
                        names.Add(LinkPreferences.NormalizeWorkset(name));
                }
                catch (Exception exception)
                {
                    failures.Add(entry.Name + " — " + LinkCatalog.Explain(exception));
                }
            }

            return new LinkWorksetScan(names.ToList(), byModel.Count, failures, byModel);
        }

        private static void Run(Application app, InsertLinksWindow window)
        {
            var preferences = window.Preferences;
            var request = new LinkInsertionRequest(
                window.Links,
                preferences.Placement,
                preferences.IsAttachment,
                preferences.Rule,
                preferences.CreateWorksets);

            var results = window.Models.Select(entry => new LinkInsertionResult(entry)).ToList();
            var log = LinkInsertionLog.Start(request, results.Count);
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

            // Revit's warnings — about coordinates, above all, on every link placed by shared coordinates —
            // would each wait for a click; the run dismisses them and the report says what they were.
            using (var warnings = new WarningSilencer(app))
            {
                foreach (var result in results)
                {
                    try
                    {
                        LinkInsertion.Run(app, result, request, seen);
                    }
                    catch (Exception exception)
                    {
                        // Nothing in Run is meant to throw; a model that does anyway is a line, not the end
                        // of a run hours long.
                        result.Failure = "the run broke off: " + LinkCatalog.Explain(exception);
                    }

                    result.Warnings.AddRange(warnings.Take());
                    log.Model(result);
                }
            }

            var missing = request.Inside.Missing(seen);
            log.Finish(Totals(results, missing));

            Report(results, missing, log);
        }

        private static IEnumerable<string> Totals(IReadOnlyList<LinkInsertionResult> results, IReadOnlyList<string> missing)
        {
            yield return "Models where links were inserted and synchronised: " + results.Count(result => result.Saved) +
                         " of " + results.Count + "; with nothing to insert: " + results.Count(Idle) +
                         "; not done: " + results.Count(result => result.Failure.Length > 0) + ".";

            yield return "Links inserted: " + results.Where(result => result.Saved).Sum(result => result.Inserted.Count) + ".";

            if (missing.Count > 0)
                yield return "Checked worksets inside the links that no link has: " + ServerUpload.Quote(missing) + ".";
        }

        /// <summary>Nothing to do there and nothing went wrong: every link already in it, or left out with a reason.</summary>
        private static bool Idle(LinkInsertionResult result)
        {
            return !result.Saved && result.Failure.Length == 0;
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(IReadOnlyList<LinkInsertionResult> results, IReadOnlyList<string> missing, LinkInsertionLog log)
        {
            var done = results.Where(result => result.Saved).ToList();

            var text = Section("Links inserted", done.Select(result => result.Name + " — " + LinkInsertionLog.Summary(result)).ToList());

            text += Section("Nothing to insert", results.Where(Idle)
                .Select(result => result.Name + " — " + LinkInsertionLog.Summary(result)).ToList());

            text += Section("Not done — the model is as it was", results.Where(result => result.Failure.Length > 0)
                .Select(result => result.Name + " — " + result.Failure).ToList());

            text += Section("Workset made", results.Where(result => result.Saved)
                .SelectMany(result => result.WorksetsMade.Select(name => result.Name + ": " + name)).ToList());

            text += Section("Left out", results
                .SelectMany(result => result.Skipped.Select(line => result.Name + ": " + line)).ToList());

            text += Section("The worksets inside the link are NOT as asked — set them in Manage Links → Manage Worksets", results
                .SelectMany(result => result.Mismatched.Select(line => result.Name + ": " + line)).ToList());

            text += Section("The worksets inside the link could not be checked — look in Manage Worksets", results
                .SelectMany(result => result.Unverified.Select(line => result.Name + ": " + line)).ToList());

            text += Section("Could not be done", results
                .SelectMany(result => result.Failures.Select(line => result.Name + ": " + line)).ToList());

            // A checked name found in no link is a typo or a different name on the consultant's side, not a
            // Revit refusal — worded apart, as "Link Manager" words it.
            if (missing.Count > 0)
                text += Section("Checked worksets inside the links that were found in no link — check the name", missing);

            text += Section("Notes", results.SelectMany(result => result.Notes.Select(note => result.Name + " — " + note)).ToList());

            text += Section("Revit warnings — dismissed so the run would not stop", results
                .SelectMany(result => result.Warnings.Select(warning => result.Name + " — " + warning)).ToList());

            text += log.Error.Length > 0
                ? "\n\nTHE FULL REPORT COULD NOT BE WRITTEN: " + log.Error + "\n" + log.FilePath
                : "\n\nThe full report, every link of every model: " + log.FilePath;

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = "Links inserted into " + done.Count + " of " + results.Count + " models",
                MainContent = text.Trim(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close
            };

            if (log.Error.Length == 0)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the full report", log.FilePath);

            if (dialog.Show() == TaskDialogResult.CommandLink1)
                Start(log.FilePath);
        }

        private static string Section(string title, IReadOnlyList<string> lines)
        {
            const int limit = 15;

            if (lines.Count == 0)
                return string.Empty;

            var text = "\n\n" + title + " (" + lines.Count + "):\n" + string.Join("\n", lines.Take(limit).Select(line => "• " + line));

            return lines.Count > limit ? text + "\n… and " + (lines.Count - limit) + " more — see the full report" : text;
        }

        /// <summary>Opens the report in its program; a failure is said, not swallowed.</summary>
        private static void Start(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                TaskDialog.Show(DialogTitle, path + "\n\ncould not be opened (" + LinkCatalog.Short(exception.Message) + ") — open it by hand.");
            }
        }
    }
}
