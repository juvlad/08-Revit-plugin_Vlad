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
    /// The "Parameters" button: the parameters of a DiRoots ParaManager Excel file added to a batch of models —
    /// a folder of them, files, Revit Server, BIM360, a saved set — as project parameters, the way ParaManager
    /// would add them to the one open model.
    ///
    /// The file is read and its words resolved into this Revit's ids before anything is opened
    /// (<see cref="ParaManagerFile"/>); the definitions are written once into a shared parameter file of the
    /// run's own; then each model is the unit of work (<see cref="BatchParameters.Run"/>): opened with every
    /// workset closed, every parameter bound in a transaction of its own and read back, the model synchronised
    /// or saved, closed. One that fails is a line in the report and the next one is taken; the report is written
    /// into a file as the run goes.
    ///
    /// The button needs no open document — it opens its models itself — and stays pressable with none
    /// (<see cref="AlwaysAvailable"/>). While it runs, <see cref="BatchProgressWindow"/> says which model of how
    /// many it is on and takes "Stop after this model".
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BatchParametersCommand : IExternalCommand
    {
        private const string DialogTitle = "Parameters";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var application = commandData?.Application;
            if (application == null)
                return Result.Cancelled;

            try
            {
                // The scan behind every view the window was shown, by path: the run takes the parameters from the
                // very reading the user looked at, not from a second one.
                var scans = new Dictionary<string, ParameterFileScan>(StringComparer.OrdinalIgnoreCase);

                var window = new BatchParametersWindow(BatchParametersPreferences.Load(), LinkPreferences.Load(), path =>
                {
                    var scan = ParaManagerFile.Read(path);
                    scans[path] = scan;
                    return View(scan);
                });

                new WindowInteropHelper(window).Owner = application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                ParameterFileScan chosen;
                if (!scans.TryGetValue(window.FilePath, out chosen))
                    return Result.Cancelled;

                var wanted = new HashSet<Guid>(window.SelectedParameters);
                var parameters = chosen.Parameters.Where(parameter => parameter.IsUsable && wanted.Contains(parameter.Guid)).ToList();

                Run(application.Application, application.MainWindowHandle, window.SelectedModels, parameters, chosen.Path);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        /// <summary>The file as the window shows it: words only, the ids stay here.</summary>
        private static BatchParameterFileView View(ParameterFileScan scan)
        {
            var view = new BatchParameterFileView(scan.Path) { Error = scan.Error };
            view.Notes.AddRange(scan.Notes);

            foreach (var parameter in scan.Parameters)
            {
                string state;
                if (!parameter.IsUsable)
                    state = Capitalise(parameter.Problem);
                else if (parameter.Notes.Count > 0)
                    state = Capitalise(string.Join("; ", parameter.Notes));
                else
                    state = "Ready";

                view.Rows.Add(new BatchParameterRow(
                    parameter.Guid,
                    parameter.Row,
                    parameter.Name,
                    parameter.TypeLabel,
                    parameter.IsInstance,
                    parameter.GroupLabel,
                    parameter.Categories.Select(category => category.Label).ToList(),
                    parameter.VariesAcrossGroups,
                    state,
                    parameter.IsUsable,
                    parameter.Notes.Count > 0));
            }

            return view;
        }

        private static string Capitalise(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        // ───────────────────────────── the run ─────────────────────────────

        private static void Run(
            Autodesk.Revit.ApplicationServices.Application app,
            IntPtr revitWindow,
            IReadOnlyList<BatchModelRow> selected,
            List<BatchParameter> parameters,
            string source)
        {
            var refused = new List<string>();
            string definitions;

            try
            {
                definitions = BatchParameters.CreateDefinitionFile(app, parameters, refused);
            }
            catch (Exception exception)
            {
                TaskDialog.Show(DialogTitle, "The parameters' definitions could not be written into a temporary shared parameter file:\n\n" +
                                             exception.Message + "\n\nNo model was opened.");
                return;
            }

            try
            {
                var usable = parameters.Where(parameter => parameter.IsUsable).ToList();
                if (usable.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "Revit would not define any of the checked parameters, so no model was opened:\n\n• " +
                                                 string.Join("\n• ", refused.Take(15)));
                    return;
                }

                var results = selected.Select(row => new BatchParameterResult(row.Entry)).ToList();
                var log = BatchParametersLog.Start(source, usable, refused, results.Count);
                var stopped = false;

                // Warnings raised while a model opens or synchronises would each wait for a click; the run dismisses
                // them and the report says what they were. The progress window is closed — and Revit's window enabled
                // again — before the report, whatever happens to the run, by the "using".
                using (var warnings = new WarningSilencer(app))
                using (var progress = BatchProgressWindow.Start(revitWindow, DialogTitle, results.Count))
                {
                    for (var index = 0; index < results.Count; index++)
                    {
                        var result = results[index];

                        if (progress.StopRequested)
                        {
                            stopped = true;
                            result.NotStarted = true;
                            log.Model(result);
                            continue;
                        }

                        progress.Begin(index, result.Name);

                        try
                        {
                            BatchParameters.Run(app, result, usable, definitions);
                        }
                        catch (Exception exception)
                        {
                            // Nothing in Run is meant to throw; a model that does anyway is a line, not the end of the run.
                            result.Failure = "the run broke off: " + LinkCatalog.Explain(exception);
                        }

                        foreach (var warning in warnings.Take())
                        {
                            if (!result.Warnings.Contains(warning))
                                result.Warnings.Add(warning);
                        }

                        log.Model(result);

                        progress.Done(result.Failure.Length == 0, result.Failure.Length == 0
                            ? result.Name + " — " + result.Summary()
                            : result.Name + " — not done: " + result.Failure);
                    }
                }

                log.Finish(new[] { (stopped ? "STOPPED by the user. " : string.Empty) + Totals(results) });

                Report(results, usable, refused, log, stopped);
            }
            finally
            {
                BatchParameters.Delete(definitions);
            }
        }

        private static string Totals(IReadOnlyList<BatchParameterResult> results)
        {
            var done = results.Where(result => !result.NotStarted && result.Failure.Length == 0).ToList();
            var notStarted = results.Count(result => result.NotStarted);

            return "Models done: " + done.Count + " of " + results.Count +
                   " (changed and written back: " + done.Count(result => result.Saved) +
                   ", nothing to change: " + done.Count(result => !result.Changed) + ")" +
                   "; parameters added: " + results.Sum(result => result.Added.Count) +
                   ", updated: " + results.Sum(result => result.Updated.Count) +
                   ", failed: " + results.Sum(result => result.Failures.Count) +
                   "; models not done: " + results.Count(result => !result.NotStarted && result.Failure.Length > 0) +
                   (notStarted > 0 ? "; not started (the run was stopped): " + notStarted : string.Empty) + ".";
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(
            IReadOnlyList<BatchParameterResult> results,
            IReadOnlyList<BatchParameter> parameters,
            IReadOnlyList<string> refused,
            BatchParametersLog log,
            bool stopped)
        {
            var done = results.Where(result => !result.NotStarted && result.Failure.Length == 0).ToList();

            var text = parameters.Count + " parameter(s) from the file.";

            text += Section("Done", done.Select(result => result.Name + " — " + result.Summary()).ToList());
            text += Section("Changed, but NOT saved — the model is as it was",
                results.Where(result => result.Failure.Length > 0 && result.Changed && !result.Saved)
                    .Select(result => result.Name + " — " + result.Failure).ToList());
            text += Section("Not done",
                results.Where(result => !result.NotStarted && result.Failure.Length > 0 && !(result.Changed && !result.Saved))
                    .Select(result => result.Name + " — " + result.Failure).ToList());
            text += Section("Not started — the run was stopped before them",
                results.Where(result => result.NotStarted).Select(result => result.Name).ToList());
            text += Section("Revit would not define these, so they went into no model", refused.ToList());
            text += Section("Could not be added", Lines(results, result => result.Failures));
            text += Section("Left out", Lines(results, result => result.Skipped));
            text += Section("Differs from the file — left as it is", Lines(results, result => result.Differs));
            text += Section("Notes", Lines(results, result => result.Notes));
            text += Section("Revit warnings — dismissed so the run would not stop", Lines(results, result => result.Warnings));

            text += log.Error.Length > 0
                ? "\n\nTHE FULL REPORT COULD NOT BE WRITTEN: " + log.Error + "\n" + log.FilePath
                : "\n\nThe full report: " + log.FilePath;

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = "Done in " + done.Count + " of " + results.Count + " models" + (stopped ? " — stopped" : string.Empty),
                MainContent = text.Trim(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close
            };

            if (log.Error.Length == 0)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the full report", log.FilePath);

            if (dialog.Show() == TaskDialogResult.CommandLink1)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(log.FilePath) { UseShellExecute = true });
                }
                catch (Exception exception)
                {
                    TaskDialog.Show(DialogTitle, log.FilePath + "\n\ncould not be opened (" + LinkCatalog.Short(exception.Message) + ") — open it by hand.");
                }
            }
        }

        private static List<string> Lines(IEnumerable<BatchParameterResult> results, Func<BatchParameterResult, IEnumerable<string>> pick)
        {
            return results.SelectMany(result => pick(result).Select(line => result.Name + ": " + line)).ToList();
        }

        private static string Section(string title, IReadOnlyList<string> lines)
        {
            const int limit = 15;

            if (lines.Count == 0)
                return string.Empty;

            var text = "\n\n" + title + " (" + lines.Count + "):\n" + string.Join("\n", lines.Take(limit).Select(line => "• " + line));

            return lines.Count > limit ? text + "\n… and " + (lines.Count - limit) + " more — see the full report" : text;
        }
    }
}
