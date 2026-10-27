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
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LinkUpdaterCommand : IExternalCommand
    {
        private const string DialogTitle = "Link Updater";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
                return Result.Cancelled;

            var doc = uidoc.Document;
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "The command works only in a project.\n" +
                    "A family holds no Revit links to repoint.");
                return Result.Cancelled;
            }

            try
            {
                int nested;
                var links = LinkRepointing.Collect(doc, out nested);

                if (links.Count == 0)
                {
                    TaskDialog.Show(DialogTitle,
                        "The model has no Revit links — there is nothing to repoint." +
                        (nested > 0 ? "\n\n(Nested links follow the models they are nested in.)" : string.Empty));
                    return Result.Cancelled;
                }

                var window = new LinkUpdaterWindow(links, LinkCatalog.Host(doc), nested);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var targets = window.Selected.ToDictionary(row => row.Info.TypeId, row => row.Target.RsnPath);
                var outcome = new LinkRepointOutcome();

                LinkRepointing.Run(doc, window.Selected.Select(row => row.Info).ToList(), link => targets[link.TypeId], outcome);

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
    }
}
