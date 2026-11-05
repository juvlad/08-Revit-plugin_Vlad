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
    /// The "Export IFC" button: a batch of models — a folder of them, files, Revit Server, BIM360, a saved
    /// set — exported to IFC one after another, with the choices Revit's own IFC export dialog asks for
    /// made once for the whole list.
    ///
    /// Each model is the unit of work (<see cref="IfcExport.ExportOne"/>): opened detached with every
    /// workset open, exported inside a transaction that is rolled back, closed without saving. One that
    /// fails is a line in the report and the next one is taken, and the report is written into a file as
    /// the run goes, for a run that takes hours and may not reach its end. Nothing in any model is changed.
    ///
    /// The button needs no open document — it opens its models itself — and stays pressable with none
    /// (<see cref="AlwaysAvailable"/>). While it runs, <see cref="BatchProgressWindow"/> says which model of
    /// how many it is on and takes "Stop after this model"; it is brought up to date between models, on the
    /// one thread the Revit API has.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportIfcCommand : IExternalCommand
    {
        private const string DialogTitle = "Export IFC";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var application = commandData?.Application;
            if (application == null)
                return Result.Cancelled;

            try
            {
                var window = new IfcExportWindow(IfcExportPreferences.Load(), LinkPreferences.Load());
                new WindowInteropHelper(window).Owner = application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                Run(application.Application, application.MainWindowHandle, window.Selected, window.Request);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        private static void Run(
            Autodesk.Revit.ApplicationServices.Application app,
            IntPtr revitWindow,
            IReadOnlyList<BatchModelRow> selected,
            IfcExportRequest request)
        {
            var results = selected.Select(row => new IfcExportResult(row.Entry)).ToList();

            // The names are settled before anything is opened, the same way the window settled them for its
            // question about the files already in the folder.
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in results)
                result.FileName = IfcExport.FileName(result.Name, taken);

            var log = IfcExportLog.Start(request, results.Count);

            var stopped = false;

            // Warnings raised while a model opens would each wait for a click; the run dismisses them and the
            // report says what they were. The progress window is closed — and Revit's window enabled again —
            // before the report: whatever happens to the run, by the "using".
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
                        result.Failure = "the run was stopped before it";
                        log.Model(result);
                        continue;
                    }

                    progress.Begin(index, result.Name);

                    try
                    {
                        IfcExport.ExportOne(app, request, result);
                    }
                    catch (Exception exception)
                    {
                        // Nothing in ExportOne is meant to throw; a model that does anyway is a line, not the
                        // end of a run hours long.
                        result.Failure = "the export broke off: " + LinkCatalog.Explain(exception);
                    }

                    result.Warnings.AddRange(warnings.Take());
                    log.Model(result);

                    progress.Done(result.Exported, result.Exported
                        ? result.Summary()
                        : result.Name + " — not exported: " + result.Failure);
                }
            }

            log.Finish(new[] { (stopped ? "STOPPED by the user. " : string.Empty) + Totals(results) });

            Report(results, request, log, stopped);
        }

        private static string Totals(IReadOnlyList<IfcExportResult> results)
        {
            var done = results.Where(result => result.Exported).ToList();
            var notStarted = results.Count(result => result.NotStarted);

            return "Exported: " + done.Count + " of " + results.Count +
                   " (" + IfcExportResult.Size(done.Sum(result => result.Bytes)) + " in all)" +
                   "; replaced files that were there: " + done.Count(result => result.Replaced) +
                   "; not exported: " + results.Count(result => !result.Exported && !result.NotStarted) +
                   (notStarted > 0 ? "; not started (the run was stopped): " + notStarted : string.Empty) + ".";
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(IReadOnlyList<IfcExportResult> results, IfcExportRequest request, IfcExportLog log, bool stopped)
        {
            var done = results.Where(result => result.Exported).ToList();

            var text = "Into: " + request.Folder + "\n" + IfcSchemaText.Caption(request.Schema) +
                       (request.ViewName.Length > 0 ? ", what the 3D view \"" + request.ViewName + "\" shows" : ", the whole model");

            text += Section("Exported", done.Select(result => result.Name + " → " + result.Summary()).ToList());
            text += Section("Not exported — an older file of the same name in the folder is left as it was",
                results.Where(result => !result.Exported && !result.NotStarted).Select(result => result.Name + " — " + result.Failure).ToList());
            text += Section("Not started — the run was stopped before them",
                results.Where(result => result.NotStarted).Select(result => result.Name).ToList());
            text += Section("Notes", results.SelectMany(result => result.Notes.Select(note => result.Name + " — " + note)).ToList());
            text += Section("Revit warnings — dismissed so the run would not stop",
                results.SelectMany(result => result.Warnings.Select(warning => result.Name + " — " + warning)).ToList());

            text += log.Error.Length > 0
                ? "\n\nTHE FULL REPORT COULD NOT BE WRITTEN: " + log.Error + "\n" + log.FilePath
                : "\n\nThe full report: " + log.FilePath;

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = "Exported " + done.Count + " of " + results.Count + " models" + (stopped ? " — stopped" : string.Empty),
                MainContent = text.Trim(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close
            };

            if (done.Count > 0)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the folder with the IFC files", request.Folder);

            if (log.Error.Length == 0)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the full report", log.FilePath);

            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1:
                    Start(request.Folder);
                    break;
                case TaskDialogResult.CommandLink2:
                    Start(log.FilePath);
                    break;
            }
        }

        private static string Section(string title, IReadOnlyList<string> lines)
        {
            const int limit = 15;

            if (lines.Count == 0)
                return string.Empty;

            var text = "\n\n" + title + " (" + lines.Count + "):\n" + string.Join("\n", lines.Take(limit).Select(line => "• " + line));

            return lines.Count > limit ? text + "\n… and " + (lines.Count - limit) + " more — see the full report" : text;
        }

        /// <summary>Opens a file in its program, or a folder in Explorer; a failure is said, not swallowed.</summary>
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
