using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>What the "Export IFC" window asks for — the same for every model of the run.</summary>
    internal sealed class IfcExportRequest
    {
        /// <summary>The folder the .ifc files go into.</summary>
        public string Folder { get; set; } = string.Empty;

        public IfcSchema Schema { get; set; } = IfcSchema.Ifc2x3CoordinationView2;

        /// <summary>The 3D view whose visibility decides what is exported; empty — the whole model.</summary>
        public string ViewName { get; set; } = string.Empty;

        public bool RevitPropertySets { get; set; }

        /// <summary>The dialog's "Export material property sets" — property sets on the IFC materials themselves.</summary>
        public bool MaterialPropertySets { get; set; }

        public bool BaseQuantities { get; set; }

        public bool SplitWallsAndColumns { get; set; }

        /// <summary>The request in words, for the report's header.</summary>
        public IEnumerable<string> Describe()
        {
            yield return "Into: " + Folder;
            yield return "IFC version: " + IfcSchemaText.Caption(Schema);
            yield return "What is exported: " + (ViewName.Length == 0
                ? "the whole model"
                : "what the 3D view \"" + ViewName + "\" shows (a model without such a view is not exported)");
            yield return "Revit property sets: " + (RevitPropertySets ? "yes" : "no") +
                         "; material property sets: " + (MaterialPropertySets ? "yes" : "no") +
                         "; base quantities: " + (BaseQuantities ? "yes" : "no") +
                         "; walls and columns split by level: " + (SplitWallsAndColumns ? "yes" : "no");
        }
    }

    /// <summary>One model of an "Export IFC" run: what became of it.</summary>
    internal sealed class IfcExportResult
    {
        public IfcExportResult(LinkEntry entry)
        {
            Entry = entry;
        }

        public LinkEntry Entry { get; }

        public string Name => Entry.Name;

        /// <summary>The .ifc file's name in the folder, unique within the run (<see cref="IfcExport.FileName"/>).</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>The file written and read back; empty unless the model was exported.</summary>
        public string FilePath { get; set; } = string.Empty;

        public bool Exported => FilePath.Length > 0;

        /// <summary>A file of that name was in the folder before, and this run wrote over it.</summary>
        public bool Replaced { get; set; }

        public long Bytes { get; set; }

        public TimeSpan Duration { get; set; }

        /// <summary>The model was open in Revit already and was exported as it stands there.</summary>
        public bool WasOpen { get; set; }

        /// <summary>Why the model was not exported; empty when it was.</summary>
        public string Failure { get; set; } = string.Empty;

        /// <summary>The run was stopped before this model was reached — it was never opened.</summary>
        public bool NotStarted { get; set; }

        public List<string> Notes { get; } = new List<string>();

        /// <summary>Revit's warnings dismissed while the model was open (<see cref="WarningSilencer"/>).</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>"AR.ifc, 84.2 MB, 3 min".</summary>
        public string Summary()
        {
            return FileName + ", " + Size(Bytes) + ", " + Minutes(Duration) +
                   (Replaced ? " — replaced the file that was there" : string.Empty) +
                   (WasOpen ? " (exported as it stands open in Revit)" : string.Empty);
        }

        public static string Size(long bytes)
        {
            return bytes >= 1024 * 1024
                ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
                : Math.Max(1, (int)Math.Round(bytes / 1024.0)) + " KB";
        }

        public static string Minutes(TimeSpan duration)
        {
            return duration.TotalMinutes >= 1
                ? (int)Math.Round(duration.TotalMinutes) + " min"
                : Math.Max(1, (int)Math.Round(duration.TotalSeconds)) + " s";
        }
    }

    /// <summary>
    /// "Export IFC" for one model: open it, export it, close it — the same steps as Revit's own
    /// File → Export → IFC, with the dialog's choices made once for the whole list.
    ///
    /// **The export runs inside a transaction that is always rolled back.** RevitAPI.xml (2022, 2024,
    /// 2025): "Exporting to IFC requires that document is modifiable, therefore there must be a
    /// transaction already open". What the exporter changes in the model while it works — IFC GUIDs
    /// written into parameters, among other things — is nothing anybody asked for, and a model already
    /// open on the user's screen must come out of the run exactly as it went in; the file is written to
    /// disk during the call, so rolling back takes nothing from it.
    ///
    /// **The file is written into a temporary folder first and copied over only when it is whole.** A
    /// failed or cut-short export never replaces last week's good file with nothing, a file locked by a
    /// viewer is a clear line in the report rather than a half-written file, and the copy is read back
    /// before the model counts as exported — "Revit returned true" is not proof there is a file.
    /// </summary>
    internal static class IfcExport
    {
        /// <summary>
        /// The option name the IFC exporter shipped with Revit reads for "Export Revit property sets"
        /// (seen in Revit.IFC.Export.dll of IFC for Revit 2022, 22.6.2; Revit's own dialog passes it the same way).
        /// </summary>
        private const string RevitPropertySetsOption = "ExportInternalRevitPropertySets";

        /// <summary>
        /// The same for "Export material property sets" — the name the exporter reads and its own dialog
        /// passes (Revit.IFC.Export.dll and IFCExporterUIOverride.dll of IFC for Revit 2022, 22.6.2).
        /// </summary>
        private const string MaterialPropertySetsOption = "ExportMaterialPsets";

        /// <summary>
        /// "AR.ifc" — the model's own name, made unique within one run: two models called "AR" from
        /// different folders must not write over each other's file.
        /// </summary>
        public static string FileName(string modelName, ISet<string> taken)
        {
            var raw = Path.GetFileNameWithoutExtension(modelName ?? string.Empty);
            var stem = SheetReport.SafeFileName(string.IsNullOrWhiteSpace(raw) ? "Model" : raw);
            var name = stem + ".ifc";

            for (var number = 2; !taken.Add(name); number++)
                name = stem + " (" + number + ").ifc";

            return name;
        }

        /// <summary>
        /// Opens, exports and closes one model, filling <paramref name="result"/>. Never throws for a
        /// reason the model itself gives: that is a line in the report, and the run takes the next one.
        /// </summary>
        public static void ExportOne(Application app, IfcExportRequest request, IfcExportResult result)
        {
            var watch = Stopwatch.StartNew();

            // Detached, with every workset open: the file in the list is never written, and nothing the
            // model holds is left out of the export (see BatchCoordination.OpenToRead).
            var model = BatchCoordination.OpenToRead(app, result.Entry, out var reason, everyWorkset: true);
            if (model == null)
            {
                result.Failure = reason;
                result.Duration = watch.Elapsed;
                return;
            }

            try
            {
                var doc = model.Document;
                result.WasOpen = model.WasOpen;

                if (doc.IsLinked)
                {
                    // Revit opens no transaction in a linked document, and IFC is exported only inside one.
                    result.Failure = "the model is loaded as a link in a project open in Revit, and Revit exports no linked model — " +
                                     "close that project (or unload the link) and run the button again";
                    return;
                }

                NoteClosedWorksets(doc, model.WasOpen, result);

                string failure;
                var options = Options(doc, request, out failure);
                if (options == null)
                {
                    result.Failure = failure;
                    return;
                }

                Write(doc, options, request.Folder, result);
            }
            catch (Exception exception)
            {
                result.Failure = "the export broke off: " + LinkCatalog.Explain(exception);
            }
            finally
            {
                BatchCoordination.Release(model);
                result.Duration = watch.Elapsed;
            }
        }

        /// <summary>
        /// The export options. Null — the model cannot be exported as asked, with the reason in
        /// <paramref name="failure"/>: a model without the named 3D view is not exported whole instead,
        /// because a file with everything in it is not what was asked for and would not look different.
        /// </summary>
        private static IFCExportOptions Options(Document doc, IfcExportRequest request, out string failure)
        {
            failure = string.Empty;

            var options = new IFCExportOptions
            {
                FileVersion = Version(request.Schema),
                ExportBaseQuantities = request.BaseQuantities,
                WallAndColumnSplitting = request.SplitWallsAndColumns,

                // Revit's own Coordination View setups export no space boundaries; the API's default is
                // the first level, which only energy analysis reads.
                SpaceBoundaryLevel = 0
            };

            options.AddOption(RevitPropertySetsOption, request.RevitPropertySets ? "true" : "false");
            options.AddOption(MaterialPropertySetsOption, request.MaterialPropertySets ? "true" : "false");

            if (request.ViewName.Length == 0)
                return options;

            var view = Find3DView(doc, request.ViewName);
            if (view == null)
            {
                var names = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .Where(candidate => !candidate.IsTemplate)
                    .Select(candidate => candidate.Name)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                failure = "there is no 3D view named \"" + request.ViewName + "\" in it" +
                          (names.Count == 0
                              ? " (it has no 3D views at all)"
                              : " — its 3D views: " + string.Join(", ", names.Take(10).Select(name => "\"" + name + "\"")) +
                                (names.Count > 10 ? " and " + (names.Count - 10) + " more" : string.Empty));
                return null;
            }

            // RevitAPI.xml: "Id of the view whose visibility settings will govern the contents in the
            // exported IFC file" — the dialog's "Export only elements visible in view".
            options.FilterViewId = view.Id;
            return options;
        }

        private static IFCVersion Version(IfcSchema schema)
        {
            switch (schema)
            {
                case IfcSchema.Ifc4ReferenceView:
                    return IFCVersion.IFC4RV;
                case IfcSchema.Ifc4DesignTransferView:
                    return IFCVersion.IFC4DTV;
                case IfcSchema.Ifc2x3CoordinationView:
                    return IFCVersion.IFC2x3;
                case IfcSchema.Ifc2x3BasicFmHandover:
                    return IFCVersion.IFC2x3BFM;
                default:
                    return IFCVersion.IFC2x3CV2;
            }
        }

        /// <summary>The 3D view of that name, case aside — an exact spelling first, should both exist.</summary>
        private static View3D Find3DView(Document doc, string name)
        {
            var wanted = (name ?? string.Empty).Trim();

            var matches = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(view => !view.IsTemplate && string.Equals(view.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return matches.FirstOrDefault(view => string.Equals(view.Name.Trim(), wanted, StringComparison.Ordinal)) ??
                   matches.FirstOrDefault();
        }

        /// <summary>
        /// Says which user worksets are closed, if any: their elements are not in the document, so not in
        /// the file. A model the run opened asked for every one; a model already open is exported as it is.
        /// </summary>
        private static void NoteClosedWorksets(Document doc, bool wasOpen, IfcExportResult result)
        {
            if (!doc.IsWorkshared)
                return;

            List<string> closed;

            try
            {
                closed = new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset)
                    .Where(workset => !workset.IsOpen)
                    .Select(workset => workset.Name)
                    .ToList();
            }
            catch (Exception)
            {
                return;
            }

            if (closed.Count == 0)
                return;

            result.Notes.Add((wasOpen
                                 ? "closed in the model open in Revit"
                                 : "still closed although every workset was asked for") +
                             ", so not in the file: " + string.Join(", ", closed.Take(10)) +
                             (closed.Count > 10 ? " and " + (closed.Count - 10) + " more" : string.Empty));
        }

        /// <summary>Exports into a temporary folder, then copies the file into place and reads it back.</summary>
        private static void Write(Document doc, IFCExportOptions options, string folder, IfcExportResult result)
        {
            var temporary = Path.Combine(Path.GetTempPath(), "VladTools-ifc-" + Guid.NewGuid().ToString("N"));
            var stem = Path.GetFileNameWithoutExtension(result.FileName);

            try
            {
                Directory.CreateDirectory(temporary);

                bool done;

                using (var transaction = new Transaction(doc, "Export IFC"))
                {
                    if (transaction.Start() != TransactionStatus.Started)
                    {
                        result.Failure = "Revit would not open a transaction in the model, and IFC is exported only inside one";
                        return;
                    }

                    try
                    {
                        done = doc.Export(temporary, stem, options);
                    }
                    finally
                    {
                        // Whatever the exporter changed in the model goes; the file on disk stays.
                        if (transaction.GetStatus() == TransactionStatus.Started)
                            transaction.RollBack();
                    }
                }

                var produced = Produced(temporary, stem);

                if (!done)
                {
                    result.Failure = "Revit reported the export as failed" +
                                     (produced != null ? " (the file it began is not taken)" : string.Empty);
                    return;
                }

                if (produced == null)
                {
                    result.Failure = "Revit reported the export as done, but wrote no .ifc file";
                    return;
                }

                var length = new FileInfo(produced).Length;
                if (length == 0)
                {
                    result.Failure = "the file Revit wrote is empty";
                    return;
                }

                var target = Path.Combine(folder, result.FileName);
                var existed = File.Exists(target);

                try
                {
                    File.Copy(produced, target, true);
                }
                catch (Exception exception)
                {
                    result.Failure = "exported, but the file could not be put into the folder" +
                                     (existed ? " over the one there (open in a viewer?)" : string.Empty) +
                                     ": " + LinkCatalog.Short(exception.Message);
                    return;
                }

                var copy = new FileInfo(target);
                if (!copy.Exists || copy.Length != length)
                {
                    result.Failure = "the file in the folder does not match the one exported — " + target;
                    return;
                }

                result.Replaced = existed;
                result.Bytes = length;
                result.FilePath = target;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(temporary))
                        Directory.Delete(temporary, true);
                }
                catch (Exception)
                {
                    // A file still held is left in the Windows temp folder — not worth a line in the report.
                }
            }
        }

        /// <summary>
        /// The .ifc file the export wrote: the one of the asked-for name, else the only one there — the
        /// folder is new and holds nothing else, and the name Revit makes of a model name with a dot in it
        /// is not documented.
        /// </summary>
        private static string Produced(string folder, string stem)
        {
            var files = Directory.GetFiles(folder, "*.ifc");
            if (files.Length == 0)
                return null;

            return files.FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), stem, StringComparison.OrdinalIgnoreCase)) ??
                   files.OrderByDescending(file => new FileInfo(file).Length).First();
        }
    }

    /// <summary>
    /// The run's full report, written as it goes into <see cref="IfcExportPreferences.ReportsFolder"/> — a
    /// run over a building's models takes hours, and if Revit falls over halfway, the dialog at the end is
    /// never shown while this file still says how far it got. A failure to write it is kept and said in
    /// that dialog, never allowed to stop the run.
    /// </summary>
    internal sealed class IfcExportLog
    {
        private IfcExportLog(string filePath)
        {
            FilePath = filePath;
        }

        public string FilePath { get; }

        /// <summary>Why the file could not be written; empty while it could.</summary>
        public string Error { get; private set; } = string.Empty;

        public static IfcExportLog Start(IfcExportRequest request, int models)
        {
            var now = DateTime.Now;
            var log = new IfcExportLog(Path.Combine(IfcExportPreferences.ReportsFolder,
                "IFC " + now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".txt"));

            log.Write(true, new[] { "VladTools — Export IFC, " + now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) }
                .Concat(request.Describe())
                .Concat(new[] { "Models: " + models, string.Empty, "MODELS" }));

            return log;
        }

        public void Model(IfcExportResult result)
        {
            var line = result.NotStarted
                ? result.Name + " — not started: the run was stopped before it"
                : result.Exported
                ? result.Name + " — exported → " + result.FilePath + " (" + IfcExportResult.Size(result.Bytes) + ", " +
                  IfcExportResult.Minutes(result.Duration) + (result.Replaced ? ", REPLACED the file that was there" : string.Empty) +
                  (result.WasOpen ? ", as it stands open in Revit" : string.Empty) + ")"
                : result.Name + " — NOT exported: " + result.Failure + Where(result.Entry);

            Write(false, new[] { Stamp() + line }
                .Concat(result.Notes.Select(note => "    note: " + note))
                .Concat(result.Warnings.Select(warning => "    Revit warning, dismissed: " + warning)));
        }

        public void Finish(IEnumerable<string> lines)
        {
            Write(false, new[] { string.Empty, Stamp() + "Done." }.Concat(lines));
        }

        private static string Where(LinkEntry entry)
        {
            return entry.Path.Length > 0 ? " (" + entry.Path + ")" : string.Empty;
        }

        private static string Stamp()
        {
            return "[" + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + "] ";
        }

        private void Write(bool create, IEnumerable<string> lines)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? IfcExportPreferences.ReportsFolder);

                var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;

                // The BOM once, at the top — Notepad then reads Cyrillic model names right.
                if (create)
                    File.WriteAllText(FilePath, text, new UTF8Encoding(true));
                else
                    File.AppendAllText(FilePath, text, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                if (Error.Length == 0)
                    Error = exception.Message;
            }
        }
    }
}
