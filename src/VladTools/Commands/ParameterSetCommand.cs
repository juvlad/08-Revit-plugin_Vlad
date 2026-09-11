using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VladTools.Infrastructure;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// Applies a saved "Parameter Sets" bundle to the open project: shared parameters that are missing
    /// are bound, ones already bound are checked against the set and brought in line with it —
    /// categories, instance/type, parameter group, "varies across groups".
    ///
    /// A routine job for every new project of a discipline: a handful of the office's own shared
    /// parameters (a mass, a code, a status) have to be present with exactly the same settings
    /// everywhere, and building that by hand in "Manage → Project Parameters" one project at a time is
    /// exactly the kind of batch work this add-in exists for.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ParameterSetCommand : IExternalCommand
    {
        private const string DialogTitle = "Parameter Sets";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData?.Application;
            var uidoc = uiApp?.ActiveUIDocument;

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
                    "In the family editor, shared parameters are managed through the family's own parameter list.");
                return Result.Cancelled;
            }

            try
            {
                var categories = CollectCategories(doc);
                var groups = CollectGroups();
                var validGroups = ParameterUtils.GetAllBuiltInGroups();

                // Both the comparison and the binding work off the same two lookups, built once:
                // which categories this project can take at all, and what they are called in it.
                var available = new HashSet<string>(categories.Select(info => info.BuiltInName), StringComparer.OrdinalIgnoreCase);
                var labels = categories.ToDictionary(info => info.BuiltInName, info => info.DisplayName, StringComparer.OrdinalIgnoreCase);

                var window = new ParameterSetWindow(
                    categories,
                    groups,
                    entries => DescribeStatus(doc, available, labels, entries),
                    path => ReadSharedParameterFile(uiApp.Application, path),
                    () => SafeCurrentFile(uiApp.Application),
                    doc.Title);

                new WindowInteropHelper(window).Owner = uiApp.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var applied = new List<string>();
                var updated = new List<string>();
                var unchanged = new List<string>();
                var failures = new List<string>();
                var warnings = new WarningSuppressor();

                // A parameter missing from the project can only be created out of a shared parameter
                // file, and it has to be the file the set was actually built from — not whatever
                // Revit happens to be pointed at right now. The property is swapped for the whole
                // run (the ExternalDefinitions read out of it stay valid only while it is) and put
                // back in the finally: it is an application-wide setting, not this button's to keep.
                var originalFile = SafeCurrentFile(uiApp.Application);
                var setFile = window.SharedParameterFile;
                var swapped = false;

                try
                {
                    if (setFile.Length > 0
                        && File.Exists(setFile)
                        && !string.Equals(setFile, originalFile, StringComparison.OrdinalIgnoreCase))
                    {
                        uiApp.Application.SharedParametersFilename = setFile;
                        swapped = true;
                    }

                    var fromFile = ReadDefinitions(uiApp.Application);

                    using (var transaction = new Transaction(doc, "Apply parameter set"))
                    {
                        transaction.Start();

                        // Binding a shared parameter can raise Revit warnings just as freely as
                        // deleting one (a schedule field, a view filter that used the old
                        // categories) — suppressed and gathered into the final report, the same as
                        // every other project command.
                        var options = transaction.GetFailureHandlingOptions();
                        options.SetFailuresPreprocessor(warnings);
                        transaction.SetFailureHandlingOptions(options);

                        foreach (var row in window.Selected)
                        {
                            try
                            {
                                ApplyOne(doc, uiApp.Application, validGroups, available, labels, fromFile,
                                    row.Entry, applied, updated, unchanged, failures);
                            }
                            catch (Exception exception)
                            {
                                failures.Add(row.Name + " — " + exception.Message);
                            }
                        }

                        if (applied.Count == 0 && updated.Count == 0)
                            transaction.RollBack();
                        else
                            transaction.Commit();
                    }
                }
                finally
                {
                    if (swapped)
                    {
                        try { uiApp.Application.SharedParametersFilename = originalFile; }
                        catch (Exception) { }
                    }
                }

                Report(applied, updated, unchanged, failures, warnings.Messages);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── the data snapshot ─────────────────────────────

        /// <summary>
        /// Every category the open project can bind a shared parameter to. Kept as
        /// <see cref="BuiltInCategory"/> names, the same handle a saved set stores — a custom,
        /// per-document category (no such enum value at all) cannot be addressed by a set and is
        /// left out rather than saved as something nothing could ever read back.
        /// </summary>
        private static List<CategoryInfo> CollectCategories(Document doc)
        {
            var result = new List<CategoryInfo>();

            foreach (Category category in doc.Settings.Categories)
            {
                bool allowsBound;
                try { allowsBound = category.AllowsBoundParameters; }
                catch (Exception) { allowsBound = false; }

                if (!allowsBound)
                    continue;

                BuiltInCategory builtIn;
                try
                {
                    // The long-standing idiom for going from a Category back to its BuiltInCategory:
                    // every built-in category's ElementId is exactly the enum's own integer value.
                    // The same IntegerValue this project already keeps deliberately elsewhere for
                    // 2022 compatibility (see CLAUDE.md, "Porting to another Revit version").
                    builtIn = (BuiltInCategory)category.Id.IntegerValue;
                }
                catch (Exception)
                {
                    continue;
                }

                if (!Enum.IsDefined(typeof(BuiltInCategory), builtIn))
                    continue;

                result.Add(new CategoryInfo(builtIn.ToString(), category.Name));
            }

            return result.OrderBy(info => info.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>
        /// Every parameter group Revit offers, for the "Group" column — the same
        /// <c>ParameterUtils.GetAllBuiltInGroups</c> / <c>LabelUtils.GetLabelForGroup</c> pair already
        /// used, and confirmed identical across every supported year, in <c>DeleteProjectParametersCommand</c>.
        /// </summary>
        private static List<ParameterGroupInfo> CollectGroups()
        {
            var result = new List<ParameterGroupInfo>();

            foreach (var group in ParameterUtils.GetAllBuiltInGroups())
            {
                string label;
                try { label = LabelUtils.GetLabelForGroup(group); }
                catch (Exception) { label = group.TypeId; }

                result.Add(new ParameterGroupInfo(group.TypeId, label));
            }

            return result.OrderBy(info => info.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ───────────────────────────── comparing against the project ─────────────────────────────

        private static IReadOnlyList<ParameterStatusInfo> DescribeStatus(
            Document doc,
            ISet<string> available,
            IReadOnlyDictionary<string, string> labels,
            IReadOnlyList<ParameterEntry> entries)
        {
            return entries.Select(entry => Describe(Compare(doc, available, entry), labels)).ToList();
        }

        /// <summary>
        /// One entry next to the open project: what is there now, and what would have to change. A
        /// read-only look — no transaction, nothing is touched here. Both the "State" column and the
        /// applying itself go through this, so the table can never promise one thing and the button
        /// do another.
        /// </summary>
        private static Comparison Compare(Document doc, ISet<string> available, ParameterEntry entry)
        {
            var result = new Comparison { Entry = entry };

            // A category the set asks for but this project does not offer cannot be bound here, and
            // must not count as a difference either — otherwise a set built in an MEP project would
            // read "will be updated" in an architectural one forever, and re-bind on every run.
            foreach (var category in entry.Categories)
            {
                if (available.Contains(category))
                    result.Desired.Add(category);
                else
                    result.Unavailable.Add(category);
            }

            try { result.Element = SharedParameterElement.Lookup(doc, entry.Guid); }
            catch (Exception) { result.Element = null; }

            if (result.Element == null)
                return result;

            try { result.Definition = result.Element.GetDefinition(); }
            catch (Exception) { result.Definition = null; }

            if (result.Definition == null)
            {
                result.Failure = "the parameter is in the project but its definition could not be read";
                return result;
            }

            // ElementBinding, not the bare Binding the map hands back: Categories only lives on the
            // subtype — the same cast DeleteProjectParametersCommand already relies on.
            ElementBinding binding;
            try { binding = doc.ParameterBindings.get_Item(result.Definition) as ElementBinding; }
            catch (Exception) { binding = null; }

            if (binding == null)
                return result;

            result.IsBound = true;

            var current = CategoryNames(binding.Categories);
            result.Removed.AddRange(current.Except(result.Desired, StringComparer.OrdinalIgnoreCase));
            result.Added.AddRange(result.Desired.Except(current, StringComparer.OrdinalIgnoreCase));

            result.KindChanged = binding is InstanceBinding != (entry.Binding == ParameterBindingKind.Instance);

            string currentGroup;
            try { currentGroup = result.Definition.GetGroupTypeId()?.TypeId ?? string.Empty; }
            catch (Exception) { currentGroup = string.Empty; }

            result.GroupChanged = !string.Equals(currentGroup, entry.GroupTypeId ?? string.Empty, StringComparison.Ordinal);

            bool currentVary;
            try { currentVary = result.Definition.VariesAcrossGroups; }
            catch (Exception) { currentVary = false; }

            result.VaryChanged = entry.Binding == ParameterBindingKind.Instance && currentVary != entry.VariesAcrossGroups;

            return result;
        }

        /// <summary>The "State" column caption for one comparison.</summary>
        private static ParameterStatusInfo Describe(Comparison comparison, IReadOnlyDictionary<string, string> labels)
        {
            var note = comparison.Unavailable.Count == 0
                ? string.Empty
                : " (not in this project: " + Names(comparison.Unavailable, labels) + ")";

            if (comparison.Failure != null)
                return new ParameterStatusInfo(Capitalise(comparison.Failure), true);

            if (comparison.Desired.Count == 0)
            {
                return new ParameterStatusInfo(
                    "None of its categories exist in this project — nothing to bind it to" + note, true);
            }

            if (comparison.Element == null)
                return new ParameterStatusInfo("Not in the project yet — will be added" + note, false);

            if (!comparison.IsBound)
                return new ParameterStatusInfo("Already in the project but not bound to any category — will be added" + note, false);

            if (comparison.Matches)
                return new ParameterStatusInfo("Already matches" + note, false);

            // Losing values is the one outcome worth a red flag: shrinking the categories, or
            // switching instance <-> type, both throw away whatever is stored on what falls out of
            // the new shape. Everything else (a wider category list, a different group, "varies
            // across groups") only ever adds or relabels, never drops data.
            if (comparison.KindChanged || comparison.Removed.Count > 0)
            {
                var reason = comparison.KindChanged
                    ? (comparison.Entry.Binding == ParameterBindingKind.Instance ? "type → instance" : "instance → type") +
                      " — existing values are lost"
                    : "no longer bound to " + Names(comparison.Removed, labels) + " — their values are lost";

                return new ParameterStatusInfo("Will be updated — " + reason + note, true);
            }

            var changes = new List<string>();
            if (comparison.Added.Count > 0)
                changes.Add("adds " + Names(comparison.Added, labels));
            if (comparison.GroupChanged)
                changes.Add("group changes");
            if (comparison.VaryChanged)
                changes.Add("\"varies across groups\" changes");

            return new ParameterStatusInfo("Will be updated — " + string.Join("; ", changes) + note, false);
        }

        /// <summary>Category names as this project shows them — an English enum name would read as a defect.</summary>
        private static string Names(IEnumerable<string> builtInNames, IReadOnlyDictionary<string, string> labels)
        {
            return string.Join(", ", builtInNames.Select(name =>
            {
                string label;
                return labels != null && labels.TryGetValue(name, out label) ? label : name;
            }));
        }

        private static string Capitalise(string text)
        {
            return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        /// <summary>One set entry weighed against the open project: what is there, and what has to change.</summary>
        private sealed class Comparison
        {
            public ParameterEntry Entry;

            /// <summary>The shared parameter element already in the document; null when there is none.</summary>
            public SharedParameterElement Element;

            public InternalDefinition Definition;

            /// <summary>The element exists and sits in the binding map — that is, it is a project parameter.</summary>
            public bool IsBound;

            /// <summary>The entry's categories this project actually has, and the ones it does not.</summary>
            public List<string> Desired = new List<string>();
            public List<string> Unavailable = new List<string>();

            public List<string> Removed = new List<string>();
            public List<string> Added = new List<string>();

            public bool KindChanged;
            public bool GroupChanged;
            public bool VaryChanged;

            /// <summary>Set when the project's own parameter could not be read at all.</summary>
            public string Failure;

            /// <summary>The project already holds exactly what the set asks for — nothing to do.</summary>
            public bool Matches => Failure == null
                                   && IsBound
                                   && !KindChanged
                                   && !GroupChanged
                                   && !VaryChanged
                                   && Removed.Count == 0
                                   && Added.Count == 0;
        }

        private static HashSet<string> CategoryNames(CategorySet categories)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (categories == null)
                return result;

            foreach (Category category in categories)
            {
                try
                {
                    var builtIn = (BuiltInCategory)category.Id.IntegerValue;
                    if (Enum.IsDefined(typeof(BuiltInCategory), builtIn))
                        result.Add(builtIn.ToString());
                }
                catch (Exception)
                {
                    // A category with no BuiltInCategory representation cannot be compared by name;
                    // it is simply left out — the same rule as when the set itself is built.
                }
            }

            return result;
        }

        // ───────────────────────────── the shared parameter file ─────────────────────────────

        /// <summary>
        /// Reads a shared parameter file's definitions, flattened out of its groups. The file Revit is
        /// already configured with is swapped out for the duration and restored right after — the API
        /// gives no way to open an arbitrary path directly, only "the current one", and this is an
        /// application-wide Revit setting that is not this button's to keep changed.
        /// </summary>
        private static IReadOnlyList<SharedParameterInfo> ReadSharedParameterFile(Application app, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("The shared parameter file was not found.", path);

            var original = app.SharedParametersFilename;

            try
            {
                app.SharedParametersFilename = path;
                var file = app.OpenSharedParameterFile();

                if (file == null)
                    throw new InvalidOperationException("Revit could not open this file as a shared parameter file.");

                var result = new List<SharedParameterInfo>();

                foreach (DefinitionGroup group in file.Groups)
                {
                    foreach (Definition definition in group.Definitions)
                    {
                        var external = definition as ExternalDefinition;
                        if (external == null)
                            continue;

                        result.Add(new SharedParameterInfo(external.GUID, external.Name, group.Name, DataTypeLabel(external)));
                    }
                }

                return result;
            }
            finally
            {
                try { app.SharedParametersFilename = original; }
                catch (Exception) { }
            }
        }

        private static string DataTypeLabel(ExternalDefinition external)
        {
            try
            {
                var dataType = external.GetDataType();
                return dataType != null && SpecUtils.IsSpec(dataType) ? LabelUtils.GetLabelForSpec(dataType) : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string SafeCurrentFile(Application app)
        {
            try { return app.SharedParametersFilename ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>
        /// Every definition of the shared parameter file Revit is pointed at right now, keyed by GUID —
        /// read once for the whole run rather than re-parsing the file for each parameter. An empty
        /// result is not a failure: a set whose parameters are all already in the project needs no
        /// shared parameter file at all.
        /// </summary>
        private static Dictionary<Guid, ExternalDefinition> ReadDefinitions(Application app)
        {
            var result = new Dictionary<Guid, ExternalDefinition>();

            DefinitionFile file;
            try { file = app.OpenSharedParameterFile(); }
            catch (Exception) { return result; }

            if (file == null)
                return result;

            try
            {
                foreach (DefinitionGroup group in file.Groups)
                {
                    foreach (Definition definition in group.Definitions)
                    {
                        var external = definition as ExternalDefinition;
                        if (external != null && !result.ContainsKey(external.GUID))
                            result.Add(external.GUID, external);
                    }
                }
            }
            catch (Exception)
            {
                // A file that cannot be read through to the end still yields whatever was reached:
                // the parameters found so far can be bound, the rest land in the report as missing.
            }

            return result;
        }

        // ───────────────────────────── applying ─────────────────────────────

        /// <summary>
        /// Binds or rebinds one parameter. <c>Insert</c> for one never bound before, <c>ReInsert</c> —
        /// which can freely swap instance for type and back — for one that is; both take the group
        /// directly, so there is no separate call needed just to set it. "Varies across groups" is
        /// entirely outside the binding and is set on its own, in its own try/catch: it cannot be
        /// reached through the binding, only through the definition itself.
        ///
        /// A row the project already matches is left completely untouched — re-binding it would be a
        /// real document edit for no gain, and would make the report claim an update that never
        /// happened. The decision comes from the same <see cref="Compare"/> the "State" column shows.
        /// </summary>
        private static void ApplyOne(
            Document doc,
            Application app,
            IList<ForgeTypeId> validGroups,
            ISet<string> available,
            IReadOnlyDictionary<string, string> labels,
            IReadOnlyDictionary<Guid, ExternalDefinition> fromFile,
            ParameterEntry entry,
            List<string> applied,
            List<string> updated,
            List<string> unchanged,
            List<string> failures)
        {
            var comparison = Compare(doc, available, entry);

            if (comparison.Failure != null)
            {
                failures.Add(entry.Name + " — " + comparison.Failure);
                return;
            }

            var note = comparison.Unavailable.Count == 0
                ? string.Empty
                : " (categories not in this project, skipped: " + Names(comparison.Unavailable, labels) + ")";

            if (comparison.Desired.Count == 0)
            {
                failures.Add(entry.Name + " — none of its categories exist in this project");
                return;
            }

            if (comparison.Matches)
            {
                unchanged.Add(entry.Name + note);
                return;
            }

            var create = app.Create;
            var categorySet = BuildCategorySet(doc, create, comparison.Desired);

            if (categorySet.IsEmpty)
            {
                failures.Add(entry.Name + " — none of its categories could be resolved in this project");
                return;
            }

            bool groupSubstituted;
            var groupTypeId = ResolveGroup(validGroups, entry.GroupTypeId, out groupSubstituted);

            Definition definition;

            if (comparison.Element != null)
            {
                definition = comparison.Definition;
            }
            else
            {
                ExternalDefinition external;
                if (!fromFile.TryGetValue(entry.Guid, out external))
                {
                    failures.Add(entry.Name +
                        " — its GUID is not in the shared parameter file, and the parameter is not yet in the project");
                    return;
                }

                definition = external;
            }

            Binding desired = entry.Binding == ParameterBindingKind.Instance
                ? (Binding)create.NewInstanceBinding(categorySet)
                : create.NewTypeBinding(categorySet);

            var ok = comparison.IsBound
                ? doc.ParameterBindings.ReInsert(definition, desired, groupTypeId)
                : doc.ParameterBindings.Insert(definition, desired, groupTypeId);

            if (!ok)
            {
                failures.Add(entry.Name + " — Revit refused the binding");
                return;
            }

            // Insert only hands the definition its InternalDefinition identity once it is actually
            // bound — re-fetching is the only way to reach SetAllowVaryBetweenGroups afterwards.
            if (entry.Binding == ParameterBindingKind.Instance)
            {
                InternalDefinition internalDefinition = null;

                try
                {
                    var justBound = SharedParameterElement.Lookup(doc, entry.Guid);
                    internalDefinition = justBound?.GetDefinition();
                }
                catch (Exception)
                {
                }

                if (internalDefinition != null)
                {
                    try
                    {
                        internalDefinition.SetAllowVaryBetweenGroups(doc, entry.VariesAcrossGroups);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(entry.Name + " — \"varies across groups\" was not applied: " + exception.Message);
                    }
                }
            }

            if (groupSubstituted)
                note += " (its saved group is not offered by this Revit — set to the default group instead)";

            (comparison.IsBound ? updated : applied).Add(entry.Name + note);
        }

        /// <summary>
        /// The categories to bind to. Only names already found in this project are passed in (see
        /// <see cref="Compare"/>), so anything that still fails to resolve here is a genuine surprise
        /// and simply drops out — the caller checks the set is not left empty.
        /// </summary>
        private static CategorySet BuildCategorySet(
            Document doc,
            Autodesk.Revit.Creation.Application create,
            IReadOnlyList<string> names)
        {
            var set = create.NewCategorySet();

            foreach (var name in names)
            {
                BuiltInCategory builtIn;
                if (!Enum.TryParse(name, out builtIn))
                    continue;

                Category category;
                try { category = Category.GetCategory(doc, builtIn); }
                catch (Exception) { category = null; }

                if (category != null)
                    set.Insert(category);
            }

            return set;
        }

        /// <summary>
        /// The saved group, if this Revit still recognises it; the first group offered otherwise. A
        /// substitution is noted in the report rather than left to fail the whole row over a cosmetic
        /// setting — see CLAUDE.md's caution about <c>ForgeTypeId</c> group strings across Revit years.
        /// </summary>
        private static ForgeTypeId ResolveGroup(IList<ForgeTypeId> validGroups, string typeId, out bool substituted)
        {
            substituted = false;

            if (!string.IsNullOrEmpty(typeId))
            {
                var match = validGroups.FirstOrDefault(group => string.Equals(group.TypeId, typeId, StringComparison.Ordinal));
                if (match != null)
                    return match;
            }

            substituted = true;
            return validGroups.FirstOrDefault() ?? new ForgeTypeId();
        }

        private static void Report(
            IReadOnlyList<string> applied,
            IReadOnlyList<string> updated,
            IReadOnlyList<string> unchanged,
            IReadOnlyList<string> failures,
            IReadOnlyList<string> warnings)
        {
            // "Already as required" is reported separately and never folded into "updated": a run
            // that changed nothing because everything was already right is a different answer from
            // one that rewrote every binding, and the user is entitled to tell them apart.
            var text = applied.Count == 0 && updated.Count == 0
                ? unchanged.Count > 0
                    ? "Nothing needed changing: all " + unchanged.Count + " parameter(s) are already as the set asks."
                    : "No parameter was applied."
                : "Added: " + applied.Count + ". Updated: " + updated.Count +
                  (unchanged.Count > 0 ? ". Already as required: " + unchanged.Count : string.Empty) + ".";

            if (failures.Count > 0)
            {
                const int limit = 15;
                text += "\n\nCould not be applied (" + failures.Count + "):\n• " +
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
