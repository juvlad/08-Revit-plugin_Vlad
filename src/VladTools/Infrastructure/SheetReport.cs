using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>What a "Sheets" batch run made of one model: its sheets, or why there are none.</summary>
    internal sealed class SheetModelResult
    {
        public SheetModelResult(LinkEntry entry)
        {
            Entry = entry;
        }

        public LinkEntry Entry { get; }

        public string Name => Entry.Name;

        /// <summary>Why the model could not be read; empty when it was.</summary>
        public string Failure { get; set; } = string.Empty;

        public bool IsRead => Failure.Length == 0;

        /// <summary>The model was open in Revit already and was read as it stands — unsaved changes included.</summary>
        public bool WasOpen { get; set; }

        public string Organization { get; set; } = string.Empty;

        public IReadOnlyList<SheetInfo> Sheets { get; set; } = new List<SheetInfo>();

        public SheetTracking Tracking { get; set; }

        /// <summary>Which parameter the numbers in <see cref="Sheets"/> are, and whether the model had the one asked for.</summary>
        public SheetNumbering Numbering { get; set; } = SheetNumbering.Revit;

        /// <summary>
        /// This run compared the model with its history and saved the result. False for a model opened
        /// detached: its dates are the ones the history already held, as of its last check.
        /// </summary>
        public bool Checked { get; set; }

        /// <summary>The model's own report; empty when none was written.</summary>
        public string ReportFile { get; set; } = string.Empty;

        /// <summary>Why the model's own report could not be written; empty when it was, or when there was nothing to write.</summary>
        public string ReportError { get; set; } = string.Empty;

        /// <summary>This run checked the model, and the history has a check before this one to compare with.</summary>
        public bool HasDates => Checked && Tracking != null && Tracking.HasDates;
    }

    /// <summary>
    /// The summary of a "Sheets" batch run: one workbook over every model read, next to a folder with
    /// each model's own report. Two sheets — "Models", one line per model with its counts and a link to
    /// its report, and "All sheets", every sheet of every model in one table with filter buttons, so a
    /// question about the whole set ("which sheets have nothing on them", "where does A101 occur") is a
    /// filter rather than a dozen files opened one by one.
    ///
    /// Everything here is plain data in, Excel sheets out — no Revit types; the command reads the models.
    /// </summary>
    internal static class SheetReport
    {
        private const string Placeholder = "Placeholder — in sheet lists only";
        private const string Hidden = "Hidden by the Project Browser's filter";
        private const string NothingPlaced = "Nothing placed";

        /// <summary>
        /// The folder the models' own reports go into: next to the summary, named after it — one run is
        /// one file and one folder, and runs on different days never write over each other.
        /// </summary>
        public static string ReportsFolder(string summaryPath)
        {
            var directory = Path.GetDirectoryName(summaryPath) ?? string.Empty;
            return Path.Combine(directory, Path.GetFileNameWithoutExtension(summaryPath));
        }

        /// <summary>A file name Windows accepts, made of whatever a model or a project is called.</summary>
        public static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((name ?? string.Empty).Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
            return clean.Length == 0 ? "Sheets" : clean;
        }

        /// <summary>
        /// "AR - sheets.xlsx" — the name the export suggests for a model, made unique within one run:
        /// two models of the same name from different folders must not write over each other.
        /// </summary>
        public static string ModelFileName(string modelName, ISet<string> taken)
        {
            var stem = SafeFileName(Path.GetFileNameWithoutExtension(modelName ?? string.Empty) + " - sheets");
            var name = stem + ".xlsx";

            for (var number = 2; !taken.Add(name); number++)
                name = stem + " (" + number + ").xlsx";

            return name;
        }

        public static IReadOnlyList<XlsxSheet> Summary(IReadOnlyList<SheetModelResult> results, string summaryPath)
        {
            var repeats = Repeats(results);
            return new[] { Models(results, repeats, summaryPath), AllSheets(results, repeats) };
        }

        // ───────────────────────────── the "Models" sheet ─────────────────────────────

        private static XlsxSheet Models(IReadOnlyList<SheetModelResult> results, Dictionary<SheetInfo, List<string>> repeats, string summaryPath)
        {
            var headings = new[]
            {
                "Model", "Source", "Where it lives", "Result", "Sheet organization",
                "Sheets", "Hidden by the browser's filter", "Placeholders", "Nothing placed",
                "Views", "Legends", "Schedules", "Images and CAD",
                "Changed since the previous check", "Previous check", "Numbers also in another model", "Report"
            };

            const int resultColumn = 3;
            const int firstCount = 5;
            const int changedColumn = 13;
            const int previousColumn = 14;
            const int repeatsColumn = 15;
            const int reportColumn = 16;

            var sheet = new XlsxSheet("Models") { FrozenRows = 1 };
            for (var column = 0; column < headings.Length; column++)
                sheet.Set(0, column, headings[column], XlsxStyle.Heading);

            var totals = new long[repeatsColumn + 1];
            var line = 1;
            var summaryFolder = Path.GetDirectoryName(summaryPath) ?? string.Empty;

            foreach (var result in results)
            {
                sheet.Set(line, 0, result.Name);
                sheet.Set(line, 1, Source(result.Entry));
                sheet.Set(line, 2, Location(result.Entry));

                var problem = !result.IsRead || result.ReportError.Length > 0 || result.Numbering.Problem.Length > 0;
                sheet.Set(line, resultColumn, Outcome(result), problem ? XlsxStyle.Attention : XlsxStyle.Cell);

                if (result.IsRead)
                {
                    var sheets = result.Sheets;
                    sheet.Set(line, 4, result.Organization);

                    var counts = new long[]
                    {
                        sheets.Count,
                        sheets.Count(info => info.IsHiddenByFilter),
                        sheets.Count(info => !info.CanOpen),
                        sheets.Count(info => info.IsEmpty),
                        sheets.Sum(info => info.Counts.Views),
                        sheets.Sum(info => info.Counts.Legends),
                        sheets.Sum(info => info.Counts.Schedules),
                        sheets.Sum(info => info.Counts.Other)
                    };

                    for (var i = 0; i < counts.Length; i++)
                    {
                        sheet.SetNumber(line, firstCount + i, counts[i]);
                        totals[firstCount + i] += counts[i];
                    }

                    if (result.HasDates)
                    {
                        var changed = sheets.Count(info => info.ChangedAtThisCheck);
                        sheet.SetNumber(line, changedColumn, changed);
                        sheet.Set(line, previousColumn, SheetInfo.FormatDate(result.Tracking.PreviousCheck));
                        totals[changedColumn] += changed;
                    }
                    else
                    {
                        sheet.Set(line, changedColumn, string.Empty);
                        sheet.Set(line, previousColumn, CheckNote(result));
                    }

                    var repeated = sheets.Count(repeats.ContainsKey);
                    sheet.SetNumber(line, repeatsColumn, repeated);
                    totals[repeatsColumn] += repeated;
                }
                else
                {
                    for (var column = 4; column < reportColumn; column++)
                        sheet.Set(line, column, string.Empty);
                }

                if (result.ReportFile.Length > 0)
                {
                    sheet.Set(line, reportColumn, Path.GetFileName(result.ReportFile), XlsxStyle.Link);
                    sheet.Links.Add(new XlsxLink(line, reportColumn, Relative(summaryFolder, result.ReportFile)));
                }
                else
                {
                    sheet.Set(line, reportColumn, string.Empty);
                }

                line++;
            }

            var read = results.Count(result => result.IsRead);
            sheet.Set(line, 0, "All models: " + read + " read of " + results.Count, XlsxStyle.Group);
            for (var column = 1; column < firstCount; column++)
                sheet.Set(line, column, string.Empty, XlsxStyle.Group);

            for (var column = firstCount; column <= repeatsColumn; column++)
            {
                if (column == previousColumn || (column == changedColumn && !results.Any(result => result.HasDates)))
                    sheet.Set(line, column, string.Empty, XlsxStyle.Group);
                else
                    sheet.SetNumber(line, column, totals[column], true);
            }

            sheet.Set(line, reportColumn, string.Empty, XlsxStyle.Group);

            sheet.ColumnWidths[0] = Width(results.Select(result => result.Name), 16, 40);
            sheet.ColumnWidths[1] = 12;
            sheet.ColumnWidths[2] = Width(results.Select(result => Location(result.Entry)), 20, 60);
            sheet.ColumnWidths[resultColumn] = Width(results.Select(Outcome), 14, 60);
            sheet.ColumnWidths[4] = Width(results.Select(result => result.Organization), 14, 30);
            for (var column = firstCount; column <= repeatsColumn; column++)
                sheet.ColumnWidths[column] = column == previousColumn ? 24 : 11;
            sheet.ColumnWidths[reportColumn] = Width(results.Select(result => Path.GetFileName(result.ReportFile)), 16, 50);

            // The totals line stays out of the filter: sorting would carry it up among the models.
            sheet.AutoFilter = new XlsxRange(0, 0, line - 1, reportColumn);

            return sheet;
        }

        /// <summary>
        /// Why there is no "changed since the previous check" for a model. A model opened detached is
        /// read without a check (see SheetsCommand.Collect), so its dates are the history's own, as of
        /// the last check made by its window — said here, so a zero is never read as "nothing changed".
        /// </summary>
        public static string CheckNote(SheetModelResult result)
        {
            if (result.Tracking == null || !result.Tracking.IsTracked)
                return "not tracked";

            if (!result.Checked)
            {
                return result.Tracking.PreviousCheck == null
                    ? "not checked (opened detached); no history yet"
                    : "not checked (opened detached); dates as of " + SheetInfo.FormatDate(result.Tracking.PreviousCheck);
            }

            return "first check";
        }

        private static string Outcome(SheetModelResult result)
        {
            if (!result.IsRead)
                return "Not read: " + result.Failure;

            var text = result.Sheets.Count == 0 ? "No sheets" : "Read";

            if (result.WasOpen)
                text += " — as it stands open in Revit";

            if (result.ReportError.Length > 0)
                text += "; its report could not be written: " + result.ReportError;

            if (result.Tracking != null && result.Tracking.Problem.Length > 0)
                text += "; " + result.Tracking.Problem;

            if (result.Numbering.Problem.Length > 0)
                text += "; " + result.Numbering.Problem;

            return text;
        }

        // ───────────────────────────── the "All sheets" sheet ─────────────────────────────

        /// <summary>
        /// With a shared parameter as the number, "Number" is that parameter, headed by its name, and
        /// Revit's own Sheet Number gets a column of its own after it — the same as a model's own report.
        /// A model that did not have the parameter shows Revit's numbers in both, and says so in "Models".
        /// </summary>
        private static XlsxSheet AllSheets(IReadOnlyList<SheetModelResult> results, Dictionary<SheetInfo, List<string>> repeats)
        {
            var numbering = results.Where(result => result.IsRead).Select(result => result.Numbering).FirstOrDefault(item => item.UsesParameter);
            var withSheetNumber = numbering != null;

            var headings = new List<string> { "Model", numbering?.Heading ?? "Number" };
            if (withSheetNumber)
                headings.Add(SheetNumberParameter.RevitName);
            headings.AddRange(new[] { "Sheet name", "Browser folder", "Changed", "On the sheet", "Notes" });

            var shift = withSheetNumber ? 1 : 0;
            var nameColumn = 2 + shift;
            var folderColumn = 3 + shift;
            var changedColumn = 4 + shift;
            var contentsColumn = 5 + shift;
            var notesColumn = 6 + shift;

            var sheet = new XlsxSheet("All sheets") { FrozenRows = 1 };
            for (var column = 0; column < headings.Count; column++)
                sheet.Set(0, column, headings[column], XlsxStyle.Heading);

            var line = 1;
            var contentsWidth = 0;
            var folderWidth = 0;

            foreach (var result in results.Where(result => result.IsRead))
            {
                foreach (var info in result.Sheets)
                {
                    // A placeholder's and a hidden sheet's "folder" is the window's own grouping, not the browser's.
                    var folder = info.CanOpen && !info.IsHiddenByFilter ? string.Join(" / ", info.Folders) : string.Empty;
                    var contents = info.Contents.Count == 0 ? string.Empty : string.Join("\n", info.Contents);

                    sheet.Set(line, 0, result.Name);
                    sheet.Set(line, 1, info.Number);
                    if (withSheetNumber)
                        sheet.Set(line, 2, info.SheetNumber);
                    sheet.Set(line, nameColumn, info.Name);
                    sheet.Set(line, folderColumn, folder);
                    sheet.Set(line, changedColumn, info.ChangedText);
                    sheet.Set(line, contentsColumn, contents);

                    var notes = Notes(info, result.Numbering, repeats);
                    sheet.Set(line, notesColumn, notes, notes.Length > 0 ? XlsxStyle.Attention : XlsxStyle.Cell);

                    folderWidth = Math.Max(folderWidth, folder.Length);
                    contentsWidth = Math.Max(contentsWidth, info.Contents.Select(text => text.Length).DefaultIfEmpty(0).Max());
                    line++;
                }
            }

            var sheets = results.Where(result => result.IsRead).SelectMany(result => result.Sheets).ToList();
            sheet.ColumnWidths[0] = Width(results.Where(result => result.IsRead).Select(result => result.Name), 16, 40);
            sheet.ColumnWidths[1] = Width(sheets.Select(info => info.Number), 10, 24);
            if (withSheetNumber)
                sheet.ColumnWidths[2] = Width(sheets.Select(info => info.SheetNumber), 14, 24);
            sheet.ColumnWidths[nameColumn] = Width(sheets.Select(info => info.Name), 24, 60);
            sheet.ColumnWidths[folderColumn] = Math.Max(16, Math.Min(50, folderWidth + 4));
            sheet.ColumnWidths[changedColumn] = 12;
            sheet.ColumnWidths[contentsColumn] = Math.Max(30, Math.Min(90, contentsWidth + 4));
            sheet.ColumnWidths[notesColumn] = 36;

            sheet.AutoFilter = new XlsxRange(0, 0, Math.Max(0, line - 1), notesColumn);

            return sheet;
        }

        private static string Notes(SheetInfo info, SheetNumbering numbering, Dictionary<SheetInfo, List<string>> repeats)
        {
            var notes = new List<string>();

            if (!info.CanOpen)
                notes.Add(Placeholder);
            else if (info.IsEmpty)
                notes.Add(NothingPlaced);

            if (numbering.UsesParameter && info.Number.Length == 0)
                notes.Add("No " + numbering.Parameter.Name + " value");

            if (info.IsHiddenByFilter)
                notes.Add(Hidden);

            if (repeats.TryGetValue(info, out var models))
                notes.Add("Number also in " + string.Join(", ", models));

            return string.Join("\n", notes);
        }

        // ───────────────────────────── shared ─────────────────────────────

        /// <summary>
        /// Every sheet whose number turns up in another model too, with those models' names. Revit keeps
        /// numbers unique inside one model and knows nothing of the others; a set issued as one — a
        /// building split into discipline models — usually wants them unique across all of them.
        /// Compared as Revit compares numbers inside a model: trimmed, ignoring case.
        /// </summary>
        private static Dictionary<SheetInfo, List<string>> Repeats(IReadOnlyList<SheetModelResult> results)
        {
            var byNumber = results
                .Where(result => result.IsRead)
                .SelectMany(result => result.Sheets.Select(info => new { Result = result, Info = info }))
                .Where(item => item.Info.Number.Trim().Length > 0)
                .GroupBy(item => item.Info.Number.Trim(), StringComparer.OrdinalIgnoreCase);

            var repeats = new Dictionary<SheetInfo, List<string>>();

            foreach (var group in byNumber)
            {
                var models = group.Select(item => item.Result).Distinct().ToList();
                if (models.Count < 2)
                    continue;

                foreach (var item in group)
                    repeats[item.Info] = models.Where(model => model != item.Result).Select(model => model.Name).ToList();
            }

            return repeats;
        }

        private static string Source(LinkEntry entry)
        {
            switch (entry.Origin)
            {
                case LinkOrigin.Server:
                    return "Revit Server";
                case LinkOrigin.Cloud:
                    return "BIM360";
                default:
                    return "File";
            }
        }

        private static string Location(LinkEntry entry)
        {
            switch (entry.Origin)
            {
                case LinkOrigin.Cloud:
                    return entry.Region + " · project " + entry.ProjectGuid + " · model " + entry.ModelGuid;
                default:
                    return entry.Path;
            }
        }

        /// <summary>The report's path from the summary's folder — relative, so the two can be moved together.</summary>
        private static string Relative(string folder, string file)
        {
            if (folder.Length > 0 && file.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return file.Substring(folder.TrimEnd('\\').Length + 1);

            return file;
        }

        private static double Width(IEnumerable<string> texts, int minimum, int maximum)
        {
            var longest = texts.Select(text => (text ?? string.Empty).Length).DefaultIfEmpty(0).Max();
            return Math.Max(minimum, Math.Min(maximum, longest + 3));
        }
    }
}
