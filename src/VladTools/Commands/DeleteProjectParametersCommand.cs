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
    /// Deletes shared parameters from the open project — the ones checked in the window.
    /// Shows the whole list of shared parameters in the file: both project parameters (bound to
    /// categories) and the ones that arrived with loaded families.
    ///
    /// A routine job: after the DD stage a model is left with hundreds of somebody else's
    /// parameters, and they have to be swept away in a batch by a common name prefix.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DeleteProjectParametersCommand : IExternalCommand
    {
        private const string DialogTitle = "Delete Project Shared Parameters";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "There is no active document.";
                return Result.Cancelled;
            }

            var doc = uidoc.Document;
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "The command works only in a project.\n" +
                    "In the family editor, parameters are deleted by the \"Delete Parameters\" button.");
                return Result.Cancelled;
            }

            try
            {
                var rows = Collect(doc);

                if (rows.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "This project has no shared parameters.");
                    return Result.Cancelled;
                }

                var families = EditableFamilies(doc);

                // The saved scan is applied right away — that is exactly what it is kept for.
                var pending = ApplySavedScan(doc, families, rows);

                var window = new DeleteProjectParametersWindow(
                    rows,
                    families.Count,
                    pending,
                    DimensionLabelCache.SavedAt(doc.PathName),
                    force => ScanDimensionLabels(doc, families, force));

                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var deleted = new List<string>();
                var failures = new List<string>();
                var warnings = new WarningSuppressor();

                using (var transaction = new Transaction(doc, "Delete project shared parameters"))
                {
                    transaction.Start();

                    // Deleting a parameter drags Revit warnings along (schedule fields, view
                    // filters). On a hundred parameters, modal dialogs would wreck the batch job,
                    // so the warnings are suppressed and go into the final report.
                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(warnings);
                    transaction.SetFailureHandlingOptions(options);

                    Remove(doc, window.Selected, deleted, failures);

                    if (deleted.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                Report(deleted, failures, warnings.Messages);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        private static List<ProjectParameterRow> Collect(Document doc)
        {
            var bindings = CollectBindings(doc);

            return new FilteredElementCollector(doc)
                .OfClass(typeof(SharedParameterElement))
                .Cast<SharedParameterElement>()
                .Select(element => Describe(element, bindings))
                .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Project parameter bindings, keyed by the parameter element's own id: a shared parameter
        /// element cannot report its categories directly, only through the binding map.
        /// </summary>
        private static Dictionary<ElementId, ElementBinding> CollectBindings(Document doc)
        {
            var result = new Dictionary<ElementId, ElementBinding>();
            var iterator = doc.ParameterBindings.ForwardIterator();

            while (iterator.MoveNext())
            {
                var definition = iterator.Key as InternalDefinition;
                var binding = iterator.Current as ElementBinding;

                if (definition != null && binding != null)
                    result[definition.Id] = binding;
            }

            return result;
        }

        private static ProjectParameterRow Describe(
            SharedParameterElement element,
            IReadOnlyDictionary<ElementId, ElementBinding> bindings)
        {
            var definition = element.GetDefinition();

            ElementBinding binding;
            bindings.TryGetValue(element.Id, out binding);

            return new ProjectParameterRow(
                element.Id,
                definition?.Name ?? "(unnamed)",
                GuidText(element),
                BindingText(binding),
                GroupName(definition),
                CategoriesText(binding),
                binding != null);
        }

        private static string GuidText(SharedParameterElement element)
        {
            try
            {
                return element.GuidValue.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string BindingText(ElementBinding binding)
        {
            if (binding == null)
                return "Not bound";

            return binding is InstanceBinding ? "Instance" : "Type";
        }

        private static string CategoriesText(ElementBinding binding)
        {
            if (binding?.Categories == null)
                return string.Empty;

            try
            {
                var names = binding.Categories
                    .Cast<Category>()
                    .Select(category => category.Name)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);

                return string.Join(", ", names);
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
            // itself and Definition.ParameterGroup are gone entirely. The new pair already exists
            // in 2022, so the code stays shared across all three years.
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

        // ───────────────────────────── the family scan ─────────────────────────────

        /// <summary>
        /// Families that can be opened for editing at all: Revit will not hand over an in-place
        /// family or a non-editable one, and there is no point asking it about those.
        /// </summary>
        private static List<Family> EditableFamilies(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(family => family.IsEditable && !family.IsInPlace)
                .OrderBy(family => family.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Fills the rows with what the saved scan already knows, and returns how many families
        /// it does not cover — those are what the button in the window will offer to open.
        /// </summary>
        private static int ApplySavedScan(
            Document doc,
            IReadOnlyList<Family> families,
            IReadOnlyList<ProjectParameterRow> rows)
        {
            var saved = DimensionLabelCache.Load(doc.PathName);
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = 0;

            foreach (var family in families)
            {
                var record = Saved(saved, family);

                if (record == null)
                {
                    pending++;
                    continue;
                }

                foreach (var guid in record.ParameterGuids)
                    known.Add(guid);
            }

            foreach (var row in rows)
                row.UsedInDimensions = known.Contains(row.Guid);

            return pending;
        }

        /// <summary>
        /// A family record that can still be trusted. Revit changes an element's version on save
        /// and synchronisation, not on every edit, so it will not catch a family reloaded during
        /// this same session — for that case the window has "Scan again", which never looks in here.
        /// </summary>
        private static FamilyLabelRecord Saved(IReadOnlyDictionary<string, FamilyLabelRecord> saved, Family family)
        {
            var version = VersionOf(family);
            if (version.Length == 0)
                return null;

            FamilyLabelRecord record;
            return saved.TryGetValue(family.UniqueId, out record) && record.Version == version
                ? record
                : null;
        }

        /// <summary>
        /// Gathers the GUIDs of the shared parameters that label dimensions in the loaded families.
        /// There is no other way: a dimension label lives only inside the family document,
        /// invisible from the project.
        ///
        /// Only families missing from the saved scan or whose version has changed are opened;
        /// with <paramref name="force"/>, every one of them. The result is saved right away, so
        /// that next time the window opens already scanned.
        /// A family that could not be opened goes into the list of reasons, is not cached and
        /// does not bring the scan down.
        /// </summary>
        private static FamilyDimensionScan ScanDimensionLabels(Document doc, IReadOnlyList<Family> families, bool force)
        {
            var saved = force
                ? new Dictionary<string, FamilyLabelRecord>(StringComparer.Ordinal)
                : DimensionLabelCache.Load(doc.PathName);

            var fresh = new List<FamilyLabelRecord>();
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();
            var opened = 0;
            var reused = 0;

            foreach (var family in families)
            {
                var record = Saved(saved, family);

                if (record != null)
                {
                    reused++;
                }
                else
                {
                    record = Read(family, failures);
                    if (record == null)
                        continue;

                    opened++;
                }

                fresh.Add(record);

                foreach (var guid in record.ParameterGuids)
                    guids.Add(guid);
            }

            DimensionLabelCache.Save(doc.PathName, fresh);

            return new FamilyDimensionScan(guids, opened, reused, failures);
        }

        /// <summary>Opens a family and reads its dimension labels; on failure, null plus a line among the failures.</summary>
        private static FamilyLabelRecord Read(Family family, List<string> failures)
        {
            Document familyDoc = null;

            try
            {
                familyDoc = family.Document.EditFamily(family);
                return new FamilyLabelRecord(family.UniqueId, VersionOf(family), CollectDimensionLabels(familyDoc));
            }
            catch (Exception exception)
            {
                failures.Add(SafeName(family) + " — " + exception.Message);
                return null;
            }
            finally
            {
                if (familyDoc != null)
                {
                    // EditFamily hands back an independent copy: it has to be closed, otherwise it
                    // stays hanging in memory until the end of the Revit session.
                    try { familyDoc.Close(false); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// The GUIDs of the shared parameters that label the document's dimensions.
        /// <c>Dimension.FamilyLabel</c> throws instead of returning null on a dimension that cannot be labelled.
        /// </summary>
        private static List<string> CollectDimensionLabels(Document familyDoc)
        {
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dimension in new FilteredElementCollector(familyDoc).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                try
                {
                    var label = dimension.FamilyLabel;
                    if (label != null && label.IsShared)
                        guids.Add(label.GUID.ToString());
                }
                catch (Exception)
                {
                    // A dimension that cannot be labelled carries no label either.
                }
            }

            return guids.ToList();
        }

        private static string VersionOf(Family family)
        {
            try
            {
                return family.VersionGuid.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string SafeName(Family family)
        {
            try
            {
                return family.Name;
            }
            catch (Exception)
            {
                return "(unnamed family)";
            }
        }

        // ───────────────────────────── deletion ─────────────────────────────

        /// <summary>
        /// Deletes the parameters one by one: a failure on one must not wreck the whole batch.
        /// Revit can take several elements away at once (a parameter together with what references
        /// it), so before deleting we check whether the element is still alive.
        /// </summary>
        private static void Remove(
            Document doc,
            IReadOnlyList<ProjectParameterRow> rows,
            List<string> deleted,
            List<string> failures)
        {
            foreach (var row in rows)
            {
                try
                {
                    if (doc.GetElement(row.Id) == null)
                    {
                        deleted.Add(row.Name);
                        continue;
                    }

                    var removed = doc.Delete(row.Id);

                    if (removed != null && removed.Count > 0)
                        deleted.Add(row.Name);
                    else
                        failures.Add(row.Name + " — Revit would not release the parameter for deletion");
                }
                catch (Exception exception)
                {
                    failures.Add(row.Name + " — " + exception.Message);
                }
            }
        }

        private static void Report(
            IReadOnlyList<string> deleted,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
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

            if (warnings.Count > 0)
            {
                const int limit = 5;
                text += "\n\nRevit warnings (" + warnings.Count + "):\n• " +
                        string.Join("\n• ", warnings.Take(limit));

                if (warnings.Count > limit)
                    text += "\n… and " + (warnings.Count - limit) + " more";
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
