using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Dismisses every Revit warning raised while it lives — in any transaction, Revit's own included —
    /// and keeps what they said for the report.
    ///
    /// <see cref="WarningSuppressor"/> does this for a transaction of ours. "Link Updater" and "Upload to
    /// Server" open none: their work is <c>LoadFrom</c>, opening a model and synchronising it, each of
    /// which Revit runs in a transaction of its own that no <c>IFailuresPreprocessor</c> can be attached
    /// to. Their warnings came up as dialogs — above all "Instance of linked .rvt file needs Coordination
    /// Review" (<c>LinkInstanceNeedsReconcile</c>), posted when a link reloaded from the server differs
    /// from what the model copy-monitors: one per link, each waiting for a click, which stops a batch of a
    /// hundred models at the first. So the run listens to <c>Application.FailuresProcessing</c> itself and
    /// deletes the warnings, and the report says what they were. Only the dialog goes: the Coordination
    /// Review alert itself stays in the model, where Revit keeps it.
    ///
    /// Errors are left alone, as <see cref="WarningSuppressor"/> leaves them: resolving one means choosing
    /// what Revit deletes or rolls back, and that is not a choice to make for the user.
    ///
    /// One per run, and only one at a time: two would both see every failure, and the second would find
    /// the warnings already gone.
    /// </summary>
    internal sealed class WarningSilencer : IDisposable
    {
        private readonly Application _app;

        /// <summary>What was dismissed since the last <see cref="Take"/>, in order, with how many times.</summary>
        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);

        private bool _disposed;

        public WarningSilencer(Application app)
        {
            _app = app;
            _app.FailuresProcessing += OnFailuresProcessing;
        }

        /// <summary>
        /// What was dismissed since the last call, one line per distinct warning — a batch takes them after
        /// every model, so each lands in that model's report.
        /// </summary>
        public List<string> Take()
        {
            var lines = _order
                .Select(line => _counts[line] > 1 ? line + " (×" + _counts[line] + ")" : line)
                .ToList();

            _order.Clear();
            _counts.Clear();

            return lines;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _app.FailuresProcessing -= OnFailuresProcessing;
        }

        private void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            try
            {
                var accessor = e.GetFailuresAccessor();
                var warnings = accessor.GetFailureMessages()
                    .Where(message => message.GetSeverity() == FailureSeverity.Warning)
                    .ToList();

                if (warnings.Count == 0)
                    return;

                var doc = accessor.GetDocument();
                foreach (var warning in warnings)
                    Note(Describe(doc, warning));

                accessor.DeleteAllWarnings();

                // Continue, not ProceedWithCommit: any errors left are Revit's to handle, as usual.
                e.SetProcessingResult(FailureProcessingResult.Continue);
            }
            catch (Exception)
            {
                // Revit's own handling takes over — at worst, the dialog this was meant to spare.
            }
        }

        private void Note(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            int count;
            if (_counts.TryGetValue(line, out count))
            {
                _counts[line] = count + 1;
                return;
            }

            _counts[line] = 1;
            _order.Add(line);
        }

        /// <summary>
        /// A warning as a report line, in Revit's own words — the words of the dialog the user would have
        /// seen. The Coordination Review one, the one this class exists for, also names its links and where
        /// the alert is now.
        /// </summary>
        private static string Describe(Document doc, FailureMessageAccessor warning)
        {
            var text = (warning.GetDescriptionText() ?? string.Empty).Trim();

            if (warning.GetFailureDefinitionId().Guid != BuiltInFailures.LinkFailures.LinkInstanceNeedsReconcile.Guid)
                return text;

            var links = Names(doc, warning.GetFailingElementIds());

            return text + (links.Length > 0 ? " — " + links : string.Empty) +
                   " (what the model copy-monitors from the link differs from it now; the alert stays in " +
                   "Collaborate → Coordination Review)";
        }

        /// <summary>The links a warning is about, by their type's name — "AR.rvt", not "AR.rvt : 2 : location &lt;Not Shared&gt;".</summary>
        private static string Names(Document doc, ICollection<ElementId> ids)
        {
            if (doc == null || ids == null)
                return string.Empty;

            var names = new List<string>();

            foreach (var id in ids)
            {
                try
                {
                    var element = doc.GetElement(id);
                    var instance = element as RevitLinkInstance;
                    var name = instance != null ? doc.GetElement(instance.GetTypeId())?.Name : element?.Name;

                    var quoted = "\"" + name + "\"";
                    if (!string.IsNullOrEmpty(name) && !names.Contains(quoted))
                        names.Add(quoted);
                }
                catch (Exception)
                {
                    // A name Revit will not give is left out; the warning is still said.
                }
            }

            return string.Join(", ", names);
        }
    }
}
