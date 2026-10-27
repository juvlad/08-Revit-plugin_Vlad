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
    /// The "Upload to Server" button: a batch of models — downloaded from BIM360 and detached, as a
    /// rule — put on Revit Server as central models, and then the links in each pointed at the server
    /// models of the same name, the way "Link Updater" points them in one open model.
    ///
    /// Two passes over the list (<see cref="ServerUpload"/>): every upload first, then every model's
    /// links, because a link can be pointed only at a model that is on the server already. Each model is
    /// the unit of work: one that fails is a line in the report and the next one is taken, and the report
    /// is written into a file as the run goes, for a run that takes hours and may not reach its end.
    ///
    /// The button needs no open document — it opens its models itself — and stays pressable with none
    /// (<see cref="AlwaysAvailable"/>). Nothing is ever written into the files in the list.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ServerUploadCommand : IExternalCommand
    {
        private const string DialogTitle = "Upload to Server";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var application = commandData?.Application;
            if (application == null)
                return Result.Cancelled;

            try
            {
                var window = new ServerUploadWindow(ServerUploadPreferences.Load(), LinkPreferences.Load());
                new WindowInteropHelper(window).Owner = application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                Run(application, window);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        private static void Run(UIApplication application, ServerUploadWindow window)
        {
            var app = application.Application;
            var destination = window.Destination;
            var results = window.Selected.Select(row => new ServerUploadResult(row.Entry)).ToList();
            var files = results.Where(result => result.Entry.Origin == LinkOrigin.File).ToList();

            // What is in the folder now, not when the window opened: the run decides by it what to skip.
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (destination != null && files.Count > 0)
            {
                try
                {
                    taken = ServerUpload.ModelNames(destination);
                }
                catch (Exception exception)
                {
                    TaskDialog.Show(DialogTitle, "The Revit Server folder could not be read, so nothing was uploaded.\n\n" +
                                                 destination.Display + "\n\n" + exception.Message);
                    return;
                }
            }

            var log = ServerUploadLog.Start(destination, files.Count, results.Count(result => result.LinksOnly),
                window.UpdateLinks, window.Replace);

            var uploaded = new Dictionary<string, ServerUploadResult>(StringComparer.OrdinalIgnoreCase);

            // Two files of one name from different folders: the second would land on the first — with
            // "Replace", write over the model this very run had just put there. The first one goes.
            var first = new Dictionary<string, ServerUploadResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                ServerUploadResult earlier;
                if (first.TryGetValue(file.Name, out earlier))
                    file.Failure = "another file of this name is earlier in the list (" + earlier.Entry.Path + ") — only the first goes up";
                else
                    first[file.Name] = file;
            }

            foreach (var result in results)
            {
                switch (result.Entry.Origin)
                {
                    case LinkOrigin.File:
                        if (result.Failure.Length > 0)
                        {
                            log.Upload(result);
                            break;
                        }

                        try
                        {
                            ServerUpload.Upload(app, result, destination, window.Replace, taken, uploaded);
                        }
                        catch (Exception exception)
                        {
                            // Nothing in Upload is meant to throw; a model that does anyway is a line,
                            // not the end of a run hours long.
                            result.Failure = "the upload broke off: " + LinkCatalog.Explain(exception);
                        }

                        log.Upload(result);
                        break;

                    case LinkOrigin.Server:
                        result.ServerPath = result.Entry.Path;
                        break;

                    default:
                        result.Failure = "a BIM360 model — download it first and add the file";
                        log.Upload(result);
                        break;
                }
            }

            if (window.UpdateLinks)
            {
                var targets = new ServerLinkTargets(uploaded);
                var queue = results.Where(result => result.Uploaded || result.Skipped || result.LinksOnly).ToList();

                if (queue.Count > 0)
                    log.Section("LINKS");

                foreach (var result in queue)
                {
                    try
                    {
                        ServerUpload.UpdateLinks(app, result, targets);
                    }
                    catch (Exception exception)
                    {
                        result.LinksFailure = "the links broke off: " + LinkCatalog.Explain(exception);
                    }

                    log.Links(result);
                }

                if (targets.LimitReached)
                    log.Lines(new[] { "The server search stopped after " + ServerModelIndex.FolderLimit + " folders: a link left \"not on the server\" may still be there." });

                if (targets.Failures.Count > 0)
                    log.Lines(new[] { "Server folders that could not be read:" }.Concat(targets.Failures.Select(line => "    " + line)));
            }

            log.Finish(Totals(results, window.UpdateLinks));

            Report(results, destination, window.UpdateLinks, log);
        }

        private static IEnumerable<string> Totals(IReadOnlyList<ServerUploadResult> results, bool links)
        {
            var files = results.Where(result => !result.LinksOnly).ToList();

            yield return "Uploaded: " + files.Count(result => result.Uploaded) + " of " + files.Count +
                         "; already there: " + files.Count(result => result.Skipped) +
                         "; not uploaded: " + files.Count(result => !result.Uploaded && !result.Skipped) + ".";

            if (links)
            {
                var tried = results.Where(result => result.LinksTried).ToList();
                yield return "Links: models where links were pointed at the server: " +
                             tried.Count(result => result.Links != null && result.Links.Count > 0 && result.LinksFailure.Length == 0) +
                             " of " + tried.Count + "; models whose links could not be updated: " +
                             tried.Count(result => result.LinksFailure.Length > 0) + ".";
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(IReadOnlyList<ServerUploadResult> results, ModelFolder destination, bool links, ServerUploadLog log)
        {
            var files = results.Where(result => !result.LinksOnly).ToList();
            var done = files.Where(result => result.Uploaded).ToList();

            var text = destination != null ? "Into: " + destination.Display : string.Empty;

            text += Section("Uploaded", done.Select(result => result.Name + (result.Replaced ? " — replaced the model that was there" : string.Empty)).ToList());
            text += Section("Already in the folder — not uploaded again", files.Where(result => result.Skipped).Select(result => result.Name).ToList());
            text += Section("Not uploaded", files.Where(result => !result.Uploaded && !result.Skipped).Select(result => result.Name + " — " + result.Failure).ToList());

            if (links)
            {
                var tried = results.Where(result => result.LinksTried).ToList();

                text += Section("Links", tried.Where(result => result.LinksFailure.Length == 0)
                    .Select(result => result.Name + " — " + (result.LinkCount == 0 ? "no links" : ServerUploadLog.LinkSummary(result)))
                    .ToList());

                text += Section("Links left as they are — finish them in \"Link Updater\"", tried
                    .SelectMany(result => result.LinksLeft.Select(line => result.Name + ": " + line))
                    .ToList());

                text += Section("Links that failed", tried
                    .Where(result => result.Links != null)
                    .SelectMany(result => result.Links.Failures.Select(line => result.Name + ": " + line))
                    .ToList());

                text += Section("Links could not be updated", tried.Where(result => result.LinksFailure.Length > 0)
                    .Select(result => result.Name + " — " + result.LinksFailure)
                    .ToList());
            }

            text += Section("Notes", results
                .SelectMany(result => result.Notes.Concat(result.LinksNotes).Select(note => result.Name + " — " + note))
                .ToList());

            text += log.Error.Length > 0
                ? "\n\nTHE FULL REPORT COULD NOT BE WRITTEN: " + log.Error + "\n" + log.FilePath
                : "\n\nThe full report, workset checks of every link included: " + log.FilePath;

            var instruction = files.Count > 0
                ? "Uploaded " + done.Count + " of " + files.Count + " models"
                : "Links updated";

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = instruction,
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
