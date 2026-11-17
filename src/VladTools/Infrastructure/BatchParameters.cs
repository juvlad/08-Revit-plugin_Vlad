using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>One model of a "Parameters" run: what became of every parameter in it.</summary>
    internal sealed class BatchParameterResult
    {
        public BatchParameterResult(LinkEntry entry)
        {
            Entry = entry;
        }

        public LinkEntry Entry { get; }

        public string Name => Entry.Name;

        /// <summary>Why nothing could be done in the model, or why what was done could not be saved; empty otherwise.</summary>
        public string Failure { get; set; } = string.Empty;

        /// <summary>The run was stopped before this model was reached — it was never opened.</summary>
        public bool NotStarted { get; set; }

        /// <summary>The changes were written back — synchronised with central, or saved.</summary>
        public bool Saved { get; set; }

        /// <summary>"synchronised with central" or "saved" — how they were written back.</summary>
        public string SavedHow { get; set; } = string.Empty;

        public TimeSpan Duration { get; set; }

        /// <summary>Parameters bound in this model by the run and read back so.</summary>
        public List<string> Added { get; } = new List<string>();

        /// <summary>Parameters the model had, given what they lacked — "name — what changed".</summary>
        public List<string> Updated { get; } = new List<string>();

        /// <summary>Parameters the model had exactly as the file asks — left untouched.</summary>
        public List<string> Already { get; } = new List<string>();

        /// <summary>Parameters left out of this model by rule, each with the reason: a name taken, no category here.</summary>
        public List<string> Skipped { get; } = new List<string>();

        /// <summary>Where the model differs from the file in a way the run never changes — values would be lost.</summary>
        public List<string> Differs { get; } = new List<string>();

        /// <summary>What Revit refused, or what did not read back as asked.</summary>
        public List<string> Failures { get; } = new List<string>();

        public List<string> Notes { get; } = new List<string>();

        /// <summary>Revit's warnings dismissed while the model was worked on.</summary>
        public List<string> Warnings { get; } = new List<string>();

        public bool Changed => Added.Count + Updated.Count > 0;

        /// <summary>"added 12, updated 2, already as asked 3 — synchronised with central".</summary>
        public string Summary()
        {
            var parts = new List<string>();
            if (Added.Count > 0)
                parts.Add("added " + Added.Count);
            if (Updated.Count > 0)
                parts.Add("updated " + Updated.Count);
            if (Already.Count > 0)
                parts.Add("already as asked " + Already.Count);
            if (Skipped.Count > 0)
                parts.Add("left out " + Skipped.Count);
            if (Differs.Count > 0)
                parts.Add("differs, left " + Differs.Count);
            if (Failures.Count > 0)
                parts.Add("FAILED " + Failures.Count);

            var text = parts.Count == 0 ? "nothing to do" : string.Join(", ", parts);

            return Saved
                ? text + " — " + SavedHow
                : Changed ? text + " — NOT saved" : text + " — nothing changed, not saved";
        }
    }

    /// <summary>
    /// "Parameters" for one model: the parameters of a ParaManager file bound as project parameters in a model
    /// nobody has open.
    ///
    /// **The definitions come from a shared parameter file the run makes itself.** A shared parameter can be
    /// bound only from an <c>ExternalDefinition</c>, and the API makes one only inside a shared parameter file.
    /// The ParaManager file carries everything a definition needs — GUID, name, type, description, "user
    /// modifiable", "visible", "hide when no value" — so the run writes them into a file of its own in the Windows
    /// temp folder (<see cref="CreateDefinitionFile"/>) rather than asking for the office's shared parameter
    /// file and hoping it holds the same GUIDs. Revit's <c>SharedParametersFilename</c> points at it only while a
    /// model's parameters are bound (an <c>ExternalDefinition</c> is valid only while its file is the current one)
    /// and is put back in a <c>finally</c>: it is an application-wide setting, not the button's to keep.
    ///
    /// **Adding never takes anything away.** A parameter the model has already (by GUID) gets the categories it
    /// lacks and the file's group, and "values can vary by group instance" is turned on where the file asks;
    /// a category is never unbound, instance is never switched to type or back, and "can vary" is never turned
    /// off — each of those loses or overwrites values somebody filled in, and nobody is watching a batch. Such
    /// differences are listed instead. A parameter whose name the model already uses for another parameter (a
    /// shared one with another GUID, or one that is not shared) is left out of that model: two parameters of
    /// one name are never told apart in a schedule or a tag.
    ///
    /// **Each parameter goes in a transaction of its own, rolled back on an error.** A binding Revit refuses at
    /// commit — something to be borrowed that another user holds, as a rule — takes only itself back; the
    /// transaction's own failure handler rolls it back rather than leave a dialog waiting for a click in a run
    /// nobody watches, and the report gives Revit's words. Every binding is read back after its commit.
    /// </summary>
    internal static class BatchParameters
    {
        private const string DefinitionGroup = "VladTools";

        // ───────────────────────────── the definitions ─────────────────────────────

        /// <summary>
        /// Writes every parameter's definition into a new shared parameter file in the Windows temp folder and
        /// returns its path; the caller deletes it when the run is over. A parameter Revit will not define is
        /// given a <see cref="BatchParameter.Problem"/> and a line in <paramref name="refused"/>, and goes into no model.
        /// </summary>
        public static string CreateDefinitionFile(Application app, IReadOnlyList<BatchParameter> parameters, List<string> refused)
        {
            var path = Path.Combine(Path.GetTempPath(), "VladTools-parameters-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, string.Empty);

            var original = SharedParameterFile.CurrentPath(app);

            try
            {
                app.SharedParametersFilename = path;

                var file = app.OpenSharedParameterFile()
                           ?? throw new InvalidOperationException("Revit would not open a temporary shared parameter file in " + Path.GetTempPath());

                var group = file.Groups.Create(DefinitionGroup);

                foreach (var parameter in parameters)
                {
                    try
                    {
                        group.Definitions.Create(Options(parameter, true));
                    }
                    catch (Exception first)
                    {
                        // A description Revit will not take is no reason to leave the parameter out of every model.
                        if (parameter.Description.Length > 0 && TryCreate(group, parameter))
                        {
                            parameter.Notes.Add("its description was left out — Revit refused it: " + LinkCatalog.Short(first.Message));
                            continue;
                        }

                        parameter.Problem = "Revit would not create its definition: " + LinkCatalog.Short(first.Message);
                        refused.Add(parameter.Name + " — " + parameter.Problem);
                    }
                }
            }
            catch (Exception)
            {
                Delete(path);
                throw;
            }
            finally
            {
                Restore(app, original);
            }

            return path;
        }

        private static bool TryCreate(DefinitionGroup group, BatchParameter parameter)
        {
            try
            {
                group.Definitions.Create(Options(parameter, false));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ExternalDefinitionCreationOptions Options(BatchParameter parameter, bool withDescription)
        {
            return new ExternalDefinitionCreationOptions(parameter.Name, parameter.Spec)
            {
                GUID = parameter.Guid,
                Description = withDescription ? parameter.Description : string.Empty,
                UserModifiable = parameter.UserModifiable,
                Visible = parameter.Visible,
                HideWhenNoValue = parameter.HideWhenNoValue
            };
        }

        public static void Delete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
                // Left in the Windows temp folder — a few kilobytes, not worth a line in the report.
            }
        }

        private static void Restore(Application app, string original)
        {
            try { app.SharedParametersFilename = original ?? string.Empty; }
            catch (Exception) { }
        }

        // ───────────────────────────── one model ─────────────────────────────

        /// <summary>
        /// Opens, binds, writes back and closes one model, filling <paramref name="result"/>. Never throws for a
        /// reason the model itself gives: that is a line in the report, and the run takes the next one.
        /// </summary>
        public static void Run(Application app, BatchParameterResult result, IReadOnlyList<BatchParameter> parameters, string definitionFile)
        {
            var watch = Stopwatch.StartNew();

            string reason;
            var model = BatchCoordination.OpenToEdit(app, result.Entry, out reason);
            if (model == null)
            {
                result.Failure = reason;
                result.Duration = watch.Elapsed;
                return;
            }

            if (model.Note.Length > 0)
                result.Notes.Add(model.Note);

            try
            {
                var doc = model.Document;

                if (doc.IsFamilyDocument)
                {
                    result.Failure = "it is a family, not a project — project parameters go into projects";
                    return;
                }

                if (doc.IsReadOnly)
                {
                    result.Failure = "Revit opened it read-only, so nothing could be written into it";
                    return;
                }

                Bind(app, doc, parameters, definitionFile, result);

                var failures = new List<string>();

                if (result.Changed)
                {
                    // No comment, the way the other batches here synchronise: the server's history says who and when.
                    result.Saved = BatchCoordination.Commit(model, string.Empty, failures);
                    if (result.Saved)
                        result.SavedHow = model.NeedsSync ? "synchronised with central" : "saved";
                    else
                        result.Failure = ServerUpload.Strip(failures, model.Entry.Name);
                }
                else
                {
                    BatchCoordination.Relinquish(model, failures);
                    if (failures.Count > 0)
                        result.Notes.Add(ServerUpload.Strip(failures, model.Entry.Name));
                }
            }
            catch (Exception exception)
            {
                result.Failure = "the run broke off: " + LinkCatalog.Explain(exception);
            }
            finally
            {
                BatchCoordination.Release(model);
                result.Duration = watch.Elapsed;
            }
        }

        private static void Bind(Application app, Document doc, IReadOnlyList<BatchParameter> parameters, string definitionFile, BatchParameterResult result)
        {
            var original = SharedParameterFile.CurrentPath(app);

            // Category label → the parameters that wanted it: one note per model, not one per parameter.
            var absent = new Dictionary<string, List<string>>(StringComparer.CurrentCulture);

            try
            {
                app.SharedParametersFilename = definitionFile;

                var definitions = ReadDefinitions(app);
                var names = ParameterNames(doc);

                foreach (var parameter in parameters)
                {
                    try
                    {
                        BindOne(app, doc, parameter, definitions, names, absent, result);
                    }
                    catch (Exception exception)
                    {
                        result.Failures.Add(parameter.Name + " — " + LinkCatalog.Explain(exception));
                    }
                }
            }
            finally
            {
                Restore(app, original);
            }

            if (absent.Count > 0)
            {
                result.Notes.Add("categories this model does not have, so left out of the bindings: " +
                                 string.Join(", ", absent.Select(pair => pair.Key + " (" + pair.Value.Count + " parameter" + (pair.Value.Count == 1 ? ")" : "s)"))));
            }
        }

        private static void BindOne(
            Application app,
            Document doc,
            BatchParameter parameter,
            IReadOnlyDictionary<Guid, ExternalDefinition> definitions,
            IReadOnlyDictionary<string, List<ParameterElement>> names,
            Dictionary<string, List<string>> absent,
            BatchParameterResult result)
        {
            var here = new List<Category>();

            foreach (var wanted in parameter.Categories)
            {
                var category = CategoryIn(doc, wanted.BuiltIn);
                if (category != null)
                {
                    here.Add(category);
                    continue;
                }

                List<string> who;
                if (!absent.TryGetValue(wanted.Label, out who))
                    absent[wanted.Label] = who = new List<string>();
                who.Add(parameter.Name);
            }

            if (here.Count == 0)
            {
                result.Skipped.Add(parameter.Name + " — none of its categories is in this model");
                return;
            }

            SharedParameterElement element;
            try { element = SharedParameterElement.Lookup(doc, parameter.Guid); }
            catch (Exception) { element = null; }

            if (element == null)
            {
                var clash = Clash(names, parameter);
                if (clash.Length > 0)
                {
                    result.Skipped.Add(parameter.Name + " — " + clash);
                    return;
                }

                ExternalDefinition definition;
                if (!definitions.TryGetValue(parameter.Guid, out definition))
                {
                    result.Failures.Add(parameter.Name + " — its definition is missing from the run's own shared parameter file");
                    return;
                }

                Insert(app, doc, parameter, definition, here, true, result, string.Empty);
                return;
            }

            InternalDefinition own;
            try { own = element.GetDefinition(); }
            catch (Exception) { own = null; }

            if (own == null)
            {
                result.Failures.Add(parameter.Name + " — it is in the model, but its definition could not be read");
                return;
            }

            var note = Mismatch(own, parameter);

            ElementBinding binding;
            try { binding = doc.ParameterBindings.get_Item(own) as ElementBinding; }
            catch (Exception) { binding = null; }

            if (binding == null)
            {
                // In the model but bound to nothing: brought in by a loaded family, or left after a binding was removed.
                Insert(app, doc, parameter, own, here, false, result,
                    "it was in the model already, bound to no category" + (note.Length > 0 ? "; " + note : string.Empty));
                return;
            }

            if (binding is InstanceBinding != parameter.IsInstance)
            {
                result.Differs.Add(parameter.Name + " — bound per " + Kind(binding is InstanceBinding) + " in the model, per " +
                                   Kind(parameter.IsInstance) + " in the file; left as it is: switching it loses the values already filled in");
                return;
            }

            var current = new HashSet<ElementId>(Categories(binding.Categories).Select(category => category.Id));
            var adding = here.Where(category => !current.Contains(category.Id)).ToList();
            var regroup = !SameGroup(Safe(() => own.GetGroupTypeId()), parameter.Group);

            var varies = Safe(() => own.VariesAcrossGroups);
            var varyOn = parameter.IsInstance && parameter.VariesAcrossGroups && !varies;

            var alignWanted = parameter.IsInstance && !parameter.VariesAcrossGroups && varies;
            if (alignWanted)
            {
                result.Differs.Add(parameter.Name + " — its values can vary by group instance in the model, the file says aligned per group type; " +
                                   "left as it is: aligning overwrites the values of the group copies");
            }

            if (note.Length > 0)
                result.Notes.Add(parameter.Name + " — " + note);

            if (adding.Count == 0 && !regroup && !varyOn)
            {
                // One that differs in a way the run never changes is listed there, not also as "already as asked".
                if (!alignWanted)
                    result.Already.Add(parameter.Name);
                return;
            }

            var changes = new List<string>();
            if (adding.Count > 0)
                changes.Add("categories added: " + string.Join(", ", adding.Select(category => category.Name)));
            if (regroup)
                changes.Add("group " + GroupLabel(Safe(() => own.GetGroupTypeId())) + " → " + GroupLabel(parameter.Group));
            if (varyOn)
                changes.Add("values can now vary by group instance");

            var all = Categories(binding.Categories).Concat(adding).ToList();
            var expected = all;

            var failure = Transact(doc, "Update parameter " + parameter.Name, result, () =>
            {
                if (adding.Count > 0 || regroup)
                {
                    if (!doc.ParameterBindings.ReInsert(own, NewBinding(app, parameter.IsInstance, all), parameter.Group))
                        throw new InvalidOperationException("Revit refused the new binding");
                }

                return varyOn ? Vary(doc, parameter, true) : string.Empty;
            }, out var varyNote);

            if (failure.Length > 0)
            {
                result.Failures.Add(parameter.Name + " — " + failure);
                return;
            }

            var check = Verify(doc, parameter, expected, varyOn && varyNote.Length == 0 ? true : (bool?)null);
            if (check.Length > 0)
            {
                result.Failures.Add(parameter.Name + " — " + check);
                return;
            }

            if (varyNote.Length > 0)
                result.Notes.Add(parameter.Name + " — " + varyNote);

            result.Updated.Add(parameter.Name + " — " + string.Join("; ", changes));
        }

        /// <summary>
        /// A binding made from scratch — a parameter new to the model (<paramref name="isNew"/>), or one it held bound to
        /// nothing. A new one gets the file's "values can vary by group instance" either way, holding no values yet; one
        /// the model had may have values in its families' instances, so there it is only ever turned on.
        /// </summary>
        private static void Insert(
            Application app,
            Document doc,
            BatchParameter parameter,
            Definition definition,
            IReadOnlyList<Category> here,
            bool isNew,
            BatchParameterResult result,
            string addedNote)
        {
            bool? vary = null;
            if (parameter.IsInstance && (isNew || parameter.VariesAcrossGroups))
                vary = parameter.VariesAcrossGroups;

            var failure = Transact(doc, "Add parameter " + parameter.Name, result, () =>
            {
                if (!doc.ParameterBindings.Insert(definition, NewBinding(app, parameter.IsInstance, here), parameter.Group))
                    throw new InvalidOperationException("Revit refused the binding");

                return vary.HasValue ? Vary(doc, parameter, vary.Value) : string.Empty;
            }, out var varyNote);

            if (failure.Length > 0)
            {
                result.Failures.Add(parameter.Name + " — " + failure);
                return;
            }

            var check = Verify(doc, parameter, here, varyNote.Length == 0 ? vary : null);
            if (check.Length > 0)
            {
                result.Failures.Add(parameter.Name + " — " + check);
                return;
            }

            if (varyNote.Length > 0)
                result.Notes.Add(parameter.Name + " — " + varyNote);

            result.Added.Add(addedNote.Length > 0 ? parameter.Name + " (" + addedNote + ")" : parameter.Name);
        }

        /// <summary>
        /// Sets "values can vary by group instance" for a parameter bound a moment ago, in the same transaction — only
        /// where it differs. Revit refuses it for some categories; that is a note, not a reason to take the binding back.
        /// </summary>
        private static string Vary(Document doc, BatchParameter parameter, bool value)
        {
            var words = value ? "\"values can vary by group instance\"" : "\"values are aligned per group type\"";

            try
            {
                var definition = SharedParameterElement.Lookup(doc, parameter.Guid)?.GetDefinition();
                if (definition == null)
                    return words + " was not set: the parameter could not be found right after binding it";

                if (definition.VariesAcrossGroups != value)
                    definition.SetAllowVaryBetweenGroups(doc, value);

                return string.Empty;
            }
            catch (Exception exception)
            {
                return words + " was not set — Revit refused it: " + LinkCatalog.Short(exception.Message);
            }
        }

        /// <summary>
        /// The binding as Revit holds it after the commit: there, bound, of the right kind, on every category it was
        /// given — "Insert returned true" is not proof. Empty when all is as asked.
        /// </summary>
        private static string Verify(Document doc, BatchParameter parameter, IReadOnlyList<Category> expected, bool? vary)
        {
            SharedParameterElement element;
            try { element = SharedParameterElement.Lookup(doc, parameter.Guid); }
            catch (Exception) { element = null; }

            var definition = element == null ? null : Safe(() => element.GetDefinition());
            if (definition == null)
                return "Revit reported it bound, but the model does not hold it after the commit";

            ElementBinding binding;
            try { binding = doc.ParameterBindings.get_Item(definition) as ElementBinding; }
            catch (Exception) { binding = null; }

            if (binding == null)
                return "Revit reported it bound, but it is bound to no category after the commit";

            if (binding is InstanceBinding != parameter.IsInstance)
                return "it is bound per " + Kind(binding is InstanceBinding) + " after the commit, not per " + Kind(parameter.IsInstance);

            var bound = new HashSet<ElementId>(Categories(binding.Categories).Select(category => category.Id));
            var missing = expected.Where(category => !bound.Contains(category.Id)).Select(category => category.Name).ToList();
            if (missing.Count > 0)
                return "after the commit it is not bound to " + string.Join(", ", missing);

            if (vary.HasValue && Safe(() => definition.VariesAcrossGroups) != vary.Value)
            {
                return "bound, but " + (vary.Value ? "\"values can vary by group instance\"" : "\"values are aligned per group type\"") +
                       " did not take";
            }

            return string.Empty;
        }

        /// <summary>
        /// One edit in a transaction of its own, with Revit's warnings dismissed and its errors turning into a rollback
        /// with their words — never a dialog. Empty when it was committed; otherwise why not.
        /// </summary>
        private static string Transact(Document doc, string name, BatchParameterResult result, Func<string> edit, out string note)
        {
            note = string.Empty;
            var handler = new RollBackOnError();

            using (var transaction = new Transaction(doc, name))
            {
                var options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(handler);
                options.SetClearAfterRollback(true);
                transaction.SetFailureHandlingOptions(options);

                if (transaction.Start() != TransactionStatus.Started)
                    return "Revit would not open a transaction in the model";

                try
                {
                    note = edit() ?? string.Empty;
                }
                catch (Exception exception)
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                        transaction.RollBack();

                    return LinkCatalog.Explain(exception);
                }

                var status = transaction.Commit();

                foreach (var warning in handler.Warnings)
                {
                    if (!result.Warnings.Contains(warning))
                        result.Warnings.Add(warning);
                }

                if (status == TransactionStatus.Committed)
                    return string.Empty;

                note = string.Empty;
                return "Revit rolled it back" + (handler.Errors.Count > 0 ? ": " + string.Join("; ", handler.Errors) : string.Empty);
            }
        }

        // ───────────────────────────── what the model holds ─────────────────────────────

        /// <summary>The definitions of the run's file, by GUID — read while it is Revit's current shared parameter file.</summary>
        private static Dictionary<Guid, ExternalDefinition> ReadDefinitions(Application app)
        {
            var result = new Dictionary<Guid, ExternalDefinition>();

            var file = app.OpenSharedParameterFile();
            if (file == null)
                return result;

            foreach (DefinitionGroup group in file.Groups)
            {
                foreach (Definition definition in group.Definitions)
                {
                    if (definition is ExternalDefinition external && !result.ContainsKey(external.GUID))
                        result.Add(external.GUID, external);
                }
            }

            return result;
        }

        /// <summary>
        /// Every project and shared parameter element of the model by name — what a new parameter's name could clash
        /// with. Global parameters are left out: a global parameter of the same name is a different kind of thing,
        /// and Revit keeps them apart.
        /// </summary>
        private static Dictionary<string, List<ParameterElement>> ParameterNames(Document doc)
        {
            var result = new Dictionary<string, List<ParameterElement>>(StringComparer.Ordinal);
            var seen = new HashSet<ElementId>();

            // Both classes are asked for: whether a filter on the base class also passes the derived one is not
            // something to rest a name check on.
            var elements = new FilteredElementCollector(doc).OfClass(typeof(ParameterElement)).Cast<ParameterElement>()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<ParameterElement>());

            foreach (var element in elements)
            {
                if (element is GlobalParameter || !seen.Add(element.Id))
                    continue;

                var name = Safe(() => element.GetDefinition()?.Name) ?? string.Empty;
                if (name.Length == 0)
                    continue;

                List<ParameterElement> list;
                if (!result.TryGetValue(name, out list))
                    result[name] = list = new List<ParameterElement>();
                list.Add(element);
            }

            return result;
        }

        /// <summary>Why the name is taken in this model by another parameter; empty when it is free.</summary>
        private static string Clash(IReadOnlyDictionary<string, List<ParameterElement>> names, BatchParameter parameter)
        {
            List<ParameterElement> holders;
            if (!names.TryGetValue(parameter.Name, out holders))
                return string.Empty;

            var other = holders.FirstOrDefault(holder => !(holder is SharedParameterElement shared && shared.GuidValue == parameter.Guid));
            if (other == null)
                return string.Empty;

            if (other is SharedParameterElement namesake)
            {
                return "the model already has a shared parameter of that name with another GUID (" + namesake.GuidValue +
                       ") — two of one name are never told apart in a schedule or a tag; left out";
            }

            return "the model already has a project parameter of that name that is not shared — two of one name are never " +
                   "told apart in a schedule or a tag; left out";
        }

        /// <summary>What the model's own definition says differently from the file — things a binding cannot change.</summary>
        private static string Mismatch(InternalDefinition own, BatchParameter parameter)
        {
            var notes = new List<string>();

            if (!string.Equals(own.Name, parameter.Name, StringComparison.Ordinal))
                notes.Add("it is named \"" + own.Name + "\" in the model");

            var type = Safe(() => own.GetDataType());
            if (type != null && parameter.Spec != null && !type.Empty() && !type.NameEquals(parameter.Spec))
            {
                notes.Add("its type in the model is " + SpecLabel(type) + ", the file says " + SpecLabel(parameter.Spec) +
                          " — a parameter's type cannot be changed once it is in a model");
            }

            return string.Join("; ", notes);
        }

        private static Category CategoryIn(Document doc, BuiltInCategory builtIn)
        {
            try
            {
                var category = Category.GetCategory(doc, builtIn);
                return category != null && category.AllowsBoundParameters ? category : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Binding NewBinding(Application app, bool instance, IEnumerable<Category> categories)
        {
            var set = app.Create.NewCategorySet();
            foreach (var category in categories)
                set.Insert(category);

            return instance ? (Binding)app.Create.NewInstanceBinding(set) : app.Create.NewTypeBinding(set);
        }

        private static List<Category> Categories(CategorySet set)
        {
            var result = new List<Category>();
            if (set == null)
                return result;

            foreach (Category category in set)
                result.Add(category);

            return result;
        }

        private static bool SameGroup(ForgeTypeId first, ForgeTypeId second)
        {
            var firstEmpty = first == null || first.Empty();
            var secondEmpty = second == null || second.Empty();

            return firstEmpty || secondEmpty ? firstEmpty == secondEmpty : first.NameEquals(second);
        }

        private static string GroupLabel(ForgeTypeId group)
        {
            if (group == null || group.Empty())
                return "\"Other\"";

            return "\"" + (Safe(() => LabelUtils.GetLabelForGroup(group)) ?? group.TypeId) + "\"";
        }

        private static string SpecLabel(ForgeTypeId spec)
        {
            return "\"" + (Safe(() => LabelUtils.GetLabelForSpec(spec)) ?? spec.TypeId) + "\"";
        }

        private static string Kind(bool instance)
        {
            return instance ? "instance" : "type";
        }

        private static T Safe<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception) { return default(T); }
        }
    }

    /// <summary>
    /// The failure handler of a "Parameters" transaction: warnings are dismissed and kept for the report, and an
    /// error rolls the transaction back, its text kept — the transaction is the run's own, one parameter's worth,
    /// and taking it back is the one answer that changes nothing nobody asked for. Left to Revit, the error would
    /// be a dialog waiting for a click in the middle of a batch.
    /// </summary>
    internal sealed class RollBackOnError : IFailuresPreprocessor
    {
        public List<string> Warnings { get; } = new List<string>();

        public List<string> Errors { get; } = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var error = false;

            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                var text = (failure.GetDescriptionText() ?? string.Empty).Trim();
                var severity = failure.GetSeverity();

                if (severity == FailureSeverity.Warning)
                {
                    if (text.Length > 0 && !Warnings.Contains(text))
                        Warnings.Add(text);
                }
                else if (severity != FailureSeverity.None)
                {
                    error = true;
                    if (text.Length > 0 && !Errors.Contains(text))
                        Errors.Add(text);
                }
            }

            if (error)
                return FailureProcessingResult.ProceedWithRollBack;

            failuresAccessor.DeleteAllWarnings();
            return FailureProcessingResult.Continue;
        }
    }

    /// <summary>
    /// The run's full report, written as it goes into <see cref="BatchParametersPreferences.ReportsFolder"/>: a run
    /// over a building's models takes a while, and if Revit falls over halfway the dialog at the end is never
    /// shown, while this file still says how far it got. A failure to write it is kept and said in that dialog,
    /// never allowed to stop the run.
    /// </summary>
    internal sealed class BatchParametersLog
    {
        private BatchParametersLog(string filePath)
        {
            FilePath = filePath;
        }

        public string FilePath { get; }

        /// <summary>Why the file could not be written; empty while it could.</summary>
        public string Error { get; private set; } = string.Empty;

        public static BatchParametersLog Start(string source, IReadOnlyList<BatchParameter> parameters, IReadOnlyList<string> refused, int models)
        {
            var now = DateTime.Now;
            var log = new BatchParametersLog(Path.Combine(BatchParametersPreferences.ReportsFolder,
                "Parameters " + now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".txt"));

            var lines = new List<string>
            {
                "VladTools — Parameters, " + now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                "From the file: " + source,
                "Parameters (" + parameters.Count + "):"
            };

            lines.AddRange(parameters.Select(parameter => "    " + ParaManagerFile.Describe(parameter) + " — " + parameter.Guid));

            if (refused.Count > 0)
            {
                lines.Add("Revit would not define these, so they went into no model (" + refused.Count + "):");
                lines.AddRange(refused.Select(line => "    " + line));
            }

            lines.Add("Models: " + models);
            lines.Add(string.Empty);
            lines.Add("MODELS");

            log.Write(true, lines);
            return log;
        }

        public void Model(BatchParameterResult result)
        {
            string line;

            if (result.NotStarted)
                line = result.Name + " — not started: the run was stopped before it";
            else if (result.Failure.Length > 0 && result.Changed && !result.Saved)
                line = result.Name + " — changed but NOT saved: " + result.Failure + Where(result.Entry);
            else if (result.Failure.Length > 0)
                line = result.Name + " — NOT done: " + result.Failure + Where(result.Entry);
            else
                line = result.Name + " — " + result.Summary() + " (" + IfcExportResult.Minutes(result.Duration) + ")";

            var lines = new List<string> { Stamp() + line };
            lines.AddRange(result.Added.Select(item => "    added: " + item));
            lines.AddRange(result.Updated.Select(item => "    updated: " + item));

            if (result.Already.Count > 0)
                lines.Add("    already as asked: " + string.Join(", ", result.Already));

            lines.AddRange(result.Skipped.Select(item => "    left out: " + item));
            lines.AddRange(result.Differs.Select(item => "    differs, left as it is: " + item));
            lines.AddRange(result.Failures.Select(item => "    FAILED: " + item));
            lines.AddRange(result.Notes.Select(item => "    note: " + item));
            lines.AddRange(result.Warnings.Select(item => "    Revit warning, dismissed: " + item));

            Write(false, lines);
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
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? BatchParametersPreferences.ReportsFolder);

                var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;

                // The BOM once, at the top — Notepad then reads the Cyrillic parameter and model names right.
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
