using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// Deletes from the open (host) family the shared parameters whose names match the rule:
    /// "starts with …" or "contains …".
    /// It first shows the list of every shared parameter and what the rule will catch.
    /// It does not touch nested families — only the open document itself.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DeleteSharedParametersCommand : IExternalCommand
    {
        private const string DialogTitle = "Delete Parameters";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "There is no active document.";
                return Result.Cancelled;
            }

            var doc = uidoc.Document;
            if (!doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "The command works only in the family editor.\n" +
                    "Open a family (.rfa) and try again.");
                return Result.Cancelled;
            }

            try
            {
                var manager = doc.FamilyManager;
                var rows = CollectSharedParameters(manager, CollectDimensionLabels(doc));

                if (rows.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "This family has no shared parameters.");
                    return Result.Cancelled;
                }

                var window = new DeleteParametersWindow(rows);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var deleted = new List<string>();
                var failures = new List<string>();

                using (var transaction = new Transaction(doc, "Delete shared parameters"))
                {
                    transaction.Start();

                    Remove(manager, window.Selected, deleted, failures);

                    if (deleted.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                Report(deleted, failures);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        private static List<SharedParameterRow> CollectSharedParameters(
            FamilyManager manager,
            ICollection<ElementId> dimensionLabels)
        {
            return manager.Parameters
                .Cast<FamilyParameter>()
                .Where(parameter => parameter.IsShared)
                .Select(parameter => Describe(parameter, dimensionLabels))
                .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static SharedParameterRow Describe(FamilyParameter parameter, ICollection<ElementId> dimensionLabels)
        {
            var definition = parameter.Definition;

            return new SharedParameterRow(
                parameter,
                definition?.Name ?? "(unnamed)",
                GuidText(parameter),
                parameter.IsInstance ? "Instance" : "Type",
                GroupName(definition),
                dimensionLabels.Contains(parameter.Id));
        }

        /// <summary>
        /// The parameters that label dimensions: deleting one means dropping the label and breaking
        /// the parametrics. A label exists only on a dimension inside a family document and can be
        /// obtained in exactly one way — <c>Dimension.FamilyLabel</c>, whose getter throws instead of
        /// returning null on a dimension that cannot be labelled.
        /// </summary>
        private static HashSet<ElementId> CollectDimensionLabels(Document doc)
        {
            var result = new HashSet<ElementId>();

            foreach (var dimension in new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                try
                {
                    var label = dimension.FamilyLabel;
                    if (label != null)
                        result.Add(label.Id);
                }
                catch (Exception)
                {
                    // A dimension that cannot be labelled carries no label either.
                }
            }

            return result;
        }

        private static string GuidText(FamilyParameter parameter)
        {
            try
            {
                return parameter.GUID.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string GroupName(Definition definition)
        {
            if (definition == null)
                return string.Empty;

            // GetGroupTypeId instead of ParameterGroup: in Revit 2025 both BuiltInParameterGroup
            // itself and Definition.ParameterGroup are gone entirely. The new pair already exists in
            // 2022, so the code stays shared across all three years.
            ForgeTypeId group;
            try
            {
                group = definition.GetGroupTypeId();
            }
            catch (Exception)
            {
                return string.Empty;
            }

            // A parameter with no group has an empty ForgeTypeId, and GetLabelForGroup throws on it.
            if (group == null || string.IsNullOrEmpty(group.TypeId))
                return string.Empty;

            try
            {
                return LabelUtils.GetLabelForGroup(group);
            }
            catch (Exception)
            {
                return group.TypeId;
            }
        }

        /// <summary>
        /// Deletes the parameters in several passes: a parameter referenced by another parameter's
        /// formula cannot be deleted while that other one is alive. We repeat while there is progress,
        /// and only then record the errors that are left.
        /// </summary>
        private static void Remove(
            FamilyManager manager,
            IReadOnlyList<SharedParameterRow> rows,
            List<string> deleted,
            List<string> failures)
        {
            var pending = rows.ToList();
            var lastErrors = new Dictionary<string, string>();

            while (pending.Count > 0)
            {
                var stuck = new List<SharedParameterRow>();

                foreach (var row in pending)
                {
                    try
                    {
                        manager.RemoveParameter(row.Parameter);
                        deleted.Add(row.Name);
                    }
                    catch (Exception exception)
                    {
                        lastErrors[row.Name] = exception.Message;
                        stuck.Add(row);
                    }
                }

                // Not a single parameter was deleted in a whole pass — nothing will change from here on.
                if (stuck.Count == pending.Count)
                {
                    failures.AddRange(stuck.Select(row => row.Name + " — " + lastErrors[row.Name]));
                    return;
                }

                pending = stuck;
            }
        }

        private static void Report(IReadOnlyList<string> deleted, IReadOnlyList<string> failures)
        {
            var text = deleted.Count > 0
                ? "Parameters deleted: " + deleted.Count + "."
                : "No parameter was deleted.";

            if (failures.Count > 0)
            {
                const int limit = 15;
                text += "\n\nCould not be deleted (" + failures.Count + "):\n• " +
                        string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… and " + (failures.Count - limit) + " more";
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
