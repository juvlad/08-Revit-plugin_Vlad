using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Points the open model's links at Revit Server: for every link, the model of the same name on the
    /// server the open model lies on, loaded through <c>RevitLinkType.LoadFrom</c>.
    ///
    /// Made for a move from BIM360 to Revit Server. The models arrive on the server, and every link in
    /// every one of them still points at the cloud — Revit's own way out is "Manage Links → Reload From"
    /// one link at a time, browsing the server tree for each. Here the server is searched once and the
    /// whole list is repointed in one go.
    ///
    /// The snapshot and the run are <see cref="LinkRepointing"/>'s, shared with the second pass of
    /// "Upload to Server"; what this command adds is the window — the user's choice of where each link
    /// goes — and the report.
    ///
    /// "Several models…" does it for a batch of models already on Revit Server — the whole server,
    /// folders, single models — with nobody choosing per link: that is the second pass of "Upload to
    /// Server" on its own (<see cref="ServerUpload"/>), so a link is pointed only where that pass is
    /// certain and the rest are listed. With no document open, or in a family, the batch is all the button
    /// can do, and it is offered straight away.
    ///
    /// Revit's warnings during either run — "needs Coordination Review" after a link is reloaded, above
    /// all — are dismissed and listed in the report rather than shown one dialog at a time
    /// (<see cref="WarningSilencer"/>).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LinkUpdaterCommand : IExternalCommand
    {
        private const string DialogTitle = "Link Updater";

        private const string WarningsTitle = "Revit warnings — dismissed so the run would not stop";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var application = commandData?.Application;
            if (application == null)
                return Result.Cancelled;

            try
            {
                // The button stays pressable with nothing open (see AlwaysAvailable): the batch opens its
                // models itself, and that is the one thing there is to do then.
                var uidoc = application.ActiveUIDocument;
                if (uidoc == null)
                    return Batch(application);

                var doc = uidoc.Document;
                if (doc.IsFamilyDocument)
                {
                    return Offer(application,
                        "The command works only in a project.\n" +
                        "A family holds no Revit links to repoint.");
                }

                int nested;
                var links = LinkRepointing.Collect(doc, out nested);

                if (links.Count == 0)
                {
                    return Offer(application,
                        "The model has no Revit links — there is nothing to repoint." +
                        (nested > 0 ? "\n\n(Nested links follow the models they are nested in.)" : string.Empty));
                }

                var window = new LinkUpdaterWindow(links, LinkCatalog.Host(doc), nested);
                new WindowInteropHelper(window).Owner = application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                if (window.WantsBatch)
                    return Batch(application);

                var targets = window.Selected.ToDictionary(row => row.Info.TypeId, row => row.Target.RsnPath);
                var outcome = new LinkRepointOutcome();

                using (var warnings = new WarningSilencer(application.Application))
                {
                    LinkRepointing.Run(doc, window.Selected.Select(row => row.Info).ToList(), link => targets[link.TypeId], outcome);
                    outcome.Warnings.AddRange(warnings.Take());
                }

                Report(doc, outcome);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(Document doc, LinkRepointOutcome outcome)
        {
            var text = outcome.Count == 0
                ? "Not a single link was repointed."
                : "Links pointed at Revit Server: " + outcome.Count + ".";

            text += Section(null, outcome.Repointed);

            text += Section("Repointed, but Revit did not load the model from there — check the path in Manage Links",
                outcome.NotLoaded);

            text += Section("The worksets open inside the link came back as before only on a second load", outcome.Healed);

            text += Section("The worksets open inside the link differ from before — set them in Manage Links → Manage Worksets",
                outcome.Mismatched);

            text += Section("Open before, but the server model has no workset of that name", outcome.Absent);

            text += Section("The worksets inside the link could not be compared with before — look in Manage Worksets",
                outcome.Unverified);

            // Said, not counted as checked: a link that was not loaded had nothing to compare with.
            text += Section("Were not loaded before, so their worksets are as Revit remembered them for the link",
                outcome.Remembered);

            text += Section("Could not be done", outcome.Failures);
            text += Section(WarningsTitle, outcome.Warnings);
            text += Section("Notes", outcome.Notes);

            if (outcome.Count > 0)
            {
                text += doc.IsWorkshared
                    ? "\n\nSynchronise with central: until then the new paths are only in your local copy."
                    : "\n\nSave the model: until then the new paths are only in this session.";
            }

            TaskDialog.Show(DialogTitle, text);
        }

        private static string Section(string title, IReadOnlyList<string> lines)
        {
            const int limit = 15;

            if (lines.Count == 0)
                return string.Empty;

            return "\n\n" + (title == null ? string.Empty : title + " (" + lines.Count + "):\n") +
                   "• " + string.Join("\n• ", lines.Take(limit)) +
                   (lines.Count > limit ? "\n… and " + (lines.Count - limit) + " more" : string.Empty);
        }

        // ───────────────────────────── a batch of models ─────────────────────────────

        /// <summary>
        /// The refusal that offers the batch instead of ending the conversation: a family, or a model with
        /// no links, is a usual enough place to start from when a whole project has moved to the server.
        /// </summary>
        private static Result Offer(UIApplication application, string text)
        {
            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = "Nothing to repoint here",
                MainContent = text,
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close
            };

            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Several models…",
                "Point the links of a batch of models on Revit Server at the server — the whole server, folders or " +
                "single models — without opening them yourself.");

            return dialog.Show() == TaskDialogResult.CommandLink1 ? Batch(application) : Result.Cancelled;
        }

        /// <summary>
        /// The batch: every checked model on Revit Server in turn — a local copy with the worksets its links
        /// stand in, each link with a certain namesake round the model's own folder pointed there, the model
        /// synchronised back with everything handed back. It is the second pass of "Upload to Server", run
        /// by the same code, and its report is written into a file as the run goes, model by model.
        /// </summary>
        private static Result Batch(UIApplication application)
        {
            var window = new LinkUpdaterBatchWindow(LinkUpdaterPreferences.Load(), LinkPreferences.Load());
            new WindowInteropHelper(window).Owner = application.MainWindowHandle;

            if (window.ShowDialog() != true)
                return Result.Cancelled;

            var app = application.Application;
            var results = window.Selected
                .Select(row => new ServerUploadResult(row.Entry) { ServerPath = row.Entry.Path })
                .ToList();

            var log = ServerUploadLog.StartLinks(results.Count);

            using (var warnings = new WarningSilencer(app))
                ServerUpload.UpdateLinks(app, results, new ServerLinkTargets(null), log, warnings);

            log.Finish(new[] { ServerUploadLog.LinkTotals(results) });

            ReportBatch(results, log);
            return Result.Succeeded;
        }

        private static void ReportBatch(IReadOnlyList<ServerUploadResult> results, ServerUploadLog log)
        {
            var done = results.Count(result => result.Links != null && result.Links.Count > 0 && result.LinksFailure.Length == 0);

            var text = string.Empty;

            foreach (var section in ServerUploadLog.LinkSections(results))
                text += Section(section.Key, section.Value);

            text += Section("Notes", results
                .SelectMany(result => result.LinksNotes.Select(note => result.Name + " — " + note))
                .ToList());

            text += Section(WarningsTitle, ServerUploadLog.WarningLines(results));

            text += log.Error.Length > 0
                ? "\n\nTHE FULL REPORT COULD NOT BE WRITTEN: " + log.Error + "\n" + log.FilePath
                : "\n\nThe full report, workset checks of every link included: " + log.FilePath;

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = "Links pointed at the server in " + done + " of " + results.Count + " models",
                MainContent = text.Trim(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close
            };

            if (log.Error.Length == 0)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the full report", log.FilePath);

            if (dialog.Show() == TaskDialogResult.CommandLink1)
                Start(log.FilePath);
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
