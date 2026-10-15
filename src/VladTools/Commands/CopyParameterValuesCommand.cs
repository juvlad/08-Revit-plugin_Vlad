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
    /// The "Copy Parameters" button: the value of one parameter written into another, on every element
    /// of a category — in the selection, the active view or the whole model.
    ///
    /// The everyday case is a number that has to become a text for a tag or a schedule ("Length" into a
    /// text "Size"), a text that has to become a number for a filter, a family's reporting parameter
    /// carried into a shared one — which is why the two parameters do not have to store the same kind of
    /// value. <see cref="ParameterValueConverter"/> holds the rules; this command reads the model,
    /// plans every element, writes, and reads back.
    ///
    /// The plan and the preview the window shows come out of one pass (<see cref="Build"/>), and the
    /// command writes the plan behind the very preview the user confirmed — the window can never show one
    /// thing while the button does another. The same rule as the "Excel" import.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CopyParameterValuesCommand : IExternalCommand
    {
        private const string DialogTitle = "Copy Parameter Values";

        // Why a line is not written, worded once: the preview's "State" and the report's lines group by these.
        private const string NoTarget = "it does not have the parameter copied into";
        private const string ReadOnlyTarget = "the parameter copied into is read-only here — Revit computes it, or a formula or a reporting parameter in the family sets it";
        private const string InGroup = "it stands in a model group, and the parameter does not vary by group instance — change it with \"Edit Group\"";
        private const string NoSource = "it does not have the parameter copied from";
        private const string EmptySource = "the source is empty";
        private const string Filled = "the target already holds a value";
        private const string Disagree = "its elements give different values, and a type holds one";
        private const string NoType = "these elements have no type";
        private const string Emptied = "emptied, as the source is empty";

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
                    "In the family editor one parameter takes another's value through a formula — \"Add Formulas\" on the Families " +
                    "panel assigns formulas in a batch.");
                return Result.Cancelled;
            }

            try
            {
                var session = new Session(doc, uidoc.ActiveView, uidoc.Selection.GetElementIds());

                var window = new CopyParametersWindow(
                    session.SelectionCount,
                    session.ViewName,
                    session.Categories,
                    session.Parameters,
                    session.Preview,
                    CopyParameterPreferences.Load(),
                    doc.Title);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true || window.Confirmed == null)
                    return Result.Cancelled;

                var plan = session.PlanOf(window.Confirmed);
                if (plan == null)
                {
                    TaskDialog.Show(DialogTitle, "The preview confirmed in the window is no longer at hand, so nothing was copied. Press the button again.");
                    return Result.Cancelled;
                }

                Copy(doc, plan);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── what the scope holds ─────────────────────────────

        /// <summary>One element of a category in scope, with its type — the grouping a type parameter is written by.</summary>
        private sealed class Member
        {
            public Member(ElementId id, ElementId type)
            {
                Id = id;
                Type = type;
            }

            public ElementId Id { get; }

            public ElementId Type { get; }
        }

        private sealed class Bucket
        {
            public Bucket(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public List<Member> Members { get; } = new List<Member>();
        }

        /// <summary>
        /// Everything the window asks for while it is open, each read once: a scope on a large model is a
        /// pass over every element, and switching back and forth must not repeat it.
        /// </summary>
        private sealed class Session
        {
            private readonly Document _doc;
            private readonly View _view;
            private readonly List<ElementId> _selection;
            private readonly Dictionary<CopyScope, Dictionary<string, Bucket>> _scopes = new Dictionary<CopyScope, Dictionary<string, Bucket>>();
            private readonly Dictionary<string, IReadOnlyList<CopyParameterInfo>> _parameters = new Dictionary<string, IReadOnlyList<CopyParameterInfo>>(StringComparer.Ordinal);
            private Plan _last;

            public Session(Document doc, View view, ICollection<ElementId> selection)
            {
                _doc = doc;
                _selection = selection?.ToList() ?? new List<ElementId>();

                // A view template, or a view Revit will not list elements of, is not a scope.
                try
                {
                    if (view != null && !view.IsTemplate)
                    {
                        new FilteredElementCollector(doc, view.Id).Dispose();
                        _view = view;
                    }
                }
                catch (Exception)
                {
                    _view = null;
                }
            }

            public int SelectionCount => _selection.Count;

            public string ViewName => _view?.Name;

            public IReadOnlyList<CopyCategoryInfo> Categories(CopyScope scope)
            {
                return Scope(scope)
                    .Select(pair => new CopyCategoryInfo(pair.Key, pair.Value.Name, pair.Value.Members.Count))
                    .OrderBy(info => info.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            /// <summary>
            /// The category's parameters, read off one element of each type and that type — what the
            /// Properties palette shows for it. Reading every element would cost seconds on a large
            /// category for counts nobody needs exact; the copy asks every element again anyway.
            /// </summary>
            public IReadOnlyList<CopyParameterInfo> Parameters(CopyScope scope, string category)
            {
                var cacheKey = scope + "\n" + category;
                if (_parameters.TryGetValue(cacheKey, out var cached))
                    return cached;

                var gathered = new Dictionary<string, Gathered>(StringComparer.Ordinal);

                if (Scope(scope).TryGetValue(category, out var bucket))
                {
                    foreach (var group in bucket.Members.GroupBy(member => member.Type))
                    {
                        var count = group.Count();
                        Gather(gathered, _doc.GetElement(group.First().Id), false, count);

                        if (group.Key != ElementId.InvalidElementId)
                            Gather(gathered, _doc.GetElement(group.Key), true, count);
                    }
                }

                var list = gathered.Values
                    .Select(entry => entry.ToInfo())
                    .OrderBy(info => info.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(info => info.IsType)
                    .ThenBy(info => info.Kind, StringComparer.Ordinal)
                    .ToList();

                _parameters[cacheKey] = list;
                return list;
            }

            public CopyParameterPreview Preview(CopyParameterRequest request)
            {
                Scope(request.Scope).TryGetValue(request.Category, out var bucket);

                _last = Build(_doc, request, bucket);
                return _last.Preview;
            }

            /// <summary>The plan behind a preview — only the last one is kept: it is the only one the window can confirm.</summary>
            public Plan PlanOf(CopyParameterPreview preview)
            {
                return _last != null && ReferenceEquals(_last.Preview, preview) ? _last : null;
            }

            private Dictionary<string, Bucket> Scope(CopyScope scope)
            {
                if (_scopes.TryGetValue(scope, out var buckets))
                    return buckets;

                buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);

                foreach (var element in Elements(scope))
                {
                    var category = CategoryOf(element);
                    if (category == null)
                        continue;

                    var key = category.Id.ToString();
                    if (!buckets.TryGetValue(key, out var bucket))
                    {
                        bucket = new Bucket(category.Name);
                        buckets.Add(key, bucket);
                    }

                    bucket.Members.Add(new Member(element.Id, TypeIdOf(element)));
                }

                _scopes[scope] = buckets;
                return buckets;
            }

            private IEnumerable<Element> Elements(CopyScope scope)
            {
                switch (scope)
                {
                    case CopyScope.Selection:
                        return _selection.Select(id => _doc.GetElement(id)).Where(element => element != null);

                    case CopyScope.ActiveView:
                        return _view == null
                            ? Enumerable.Empty<Element>()
                            : new FilteredElementCollector(_doc, _view.Id).WhereElementIsNotElementType();

                    default:
                        return new FilteredElementCollector(_doc).WhereElementIsNotElementType();
                }
            }
        }

        /// <summary>
        /// The category an element is listed under, or <c>null</c> when it is not one to copy values on:
        /// types (the copy works on placed elements, and reaches a type through them), view templates, and
        /// categories that take no project parameters — sketch lines, Revit's own bookkeeping — which is
        /// exactly the list Revit's own "Project Parameters" dialog offers.
        /// </summary>
        private static Category CategoryOf(Element element)
        {
            try
            {
                if (element is ElementType || (element is View view && view.IsTemplate))
                    return null;

                var category = element.Category;
                if (category?.Parent != null)
                    category = category.Parent;

                if (category == null || category.CategoryType == CategoryType.Internal || category.CategoryType == CategoryType.Invalid)
                    return null;

                return category.AllowsBoundParameters ? category : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static ElementId TypeIdOf(Element element)
        {
            try
            {
                return element.GetTypeId() ?? ElementId.InvalidElementId;
            }
            catch (Exception)
            {
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>One parameter as it is met on the elements read: how many have it, how many can take a value.</summary>
        private sealed class Gathered
        {
            private readonly ParameterKey _key;
            private readonly string _name;
            private readonly List<string> _kinds = new List<string>();
            private int _elements;
            private int _writable;

            public Gathered(ParameterKey key, Parameter parameter)
            {
                _key = key;
                _name = parameter.Definition.Name;
            }

            public void Add(Parameter parameter, int count)
            {
                var kind = ParameterValueConverter.ValueKind(parameter);
                if (!_kinds.Contains(kind))
                    _kinds.Add(kind);

                _elements += count;
                if (!parameter.IsReadOnly)
                    _writable += count;
            }

            public CopyParameterInfo ToInfo()
            {
                var kind = _key.BuiltIn != BuiltInParameter.INVALID ? "Built-in" : _key.Guid != Guid.Empty ? "Shared" : "Project / family";
                return new CopyParameterInfo(_key.Text, _name, _key.IsType, kind, string.Join(" / ", _kinds), _elements, _writable);
            }
        }

        private static void Gather(Dictionary<string, Gathered> gathered, Element element, bool isType, int count)
        {
            if (element == null)
                return;

            IList<Parameter> parameters;
            try
            {
                parameters = element.GetOrderedParameters();
            }
            catch (Exception)
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var parameter in parameters)
            {
                try
                {
                    if (parameter?.Definition == null || parameter.StorageType == StorageType.None)
                        continue;

                    var key = ParameterKey.Of(parameter, isType);
                    if (key == null || !seen.Add(key.Text))
                        continue;

                    if (!gathered.TryGetValue(key.Text, out var entry))
                    {
                        entry = new Gathered(key, parameter);
                        gathered.Add(key.Text, entry);
                    }

                    entry.Add(parameter, count);
                }
                catch (Exception)
                {
                    // A parameter that cannot be read is not one to copy.
                }
            }
        }

        // ───────────────────────────── the plan ─────────────────────────────

        /// <summary>One value to write: on an element, or — for a type parameter — on a type.</summary>
        private sealed class Write
        {
            public ElementId Target { get; set; }

            public object Value { get; set; }

            public string Caption { get; set; } = string.Empty;

            public string After { get; set; } = string.Empty;

            public string Note { get; set; }
        }

        /// <summary>A preview and the writes behind it — the window sees the first, the command carries out the second.</summary>
        private sealed class Plan
        {
            public CopyParameterPreview Preview { get; set; }

            public ParameterKey Target { get; set; }

            public bool TargetIsType { get; set; }

            public string SourceName { get; set; } = string.Empty;

            public string TargetName { get; set; } = string.Empty;

            public string CategoryName { get; set; } = string.Empty;

            public List<Write> Writes { get; } = new List<Write>();

            public int Same { get; set; }

            public Tally Left { get; } = new Tally();

            public Tally Blocked { get; } = new Tally();
        }

        /// <summary>Reasons with how many lines each and one example: a hundred texts that are not numbers are one report line.</summary>
        private sealed class Tally
        {
            private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _examples = new Dictionary<string, string>(StringComparer.Ordinal);

            public int Total => _counts.Values.Sum();

            public void Add(string reason, string example)
            {
                _counts[reason] = _counts.TryGetValue(reason, out var count) ? count + 1 : 1;

                if (!_examples.ContainsKey(reason) && !string.IsNullOrEmpty(example))
                    _examples[reason] = example;
            }

            public IEnumerable<string> Lines()
            {
                return _counts
                    .OrderByDescending(pair => pair.Value)
                    .Select(pair => pair.Value + " — " + pair.Key +
                                    (_examples.TryGetValue(pair.Key, out var example) ? " (" + (pair.Value == 1 ? "" : "e.g. ") + example + ")" : string.Empty));
            }
        }

        /// <summary>What the copy would do to one line, before it becomes a preview row and, maybe, a write.</summary>
        private sealed class Cell
        {
            public CopyRowState State { get; set; } = CopyRowState.Write;

            public string Reason { get; set; } = string.Empty;

            public string Source { get; set; } = string.Empty;

            public string Before { get; set; } = string.Empty;

            public string After { get; set; } = string.Empty;

            public object Value { get; set; }

            /// <summary>What was done to the value on the way — rounded, taken out of a text; counted in the summary and the report.</summary>
            public string Note { get; set; }

            /// <summary>Anything else the line should say — for a type, how many of its elements gave no value.</summary>
            public string Remark { get; set; }

            public Cell Copy()
            {
                return (Cell)MemberwiseClone();
            }
        }

        /// <summary>
        /// Works out every line of the preview, and the writes behind it, in one pass. An instance
        /// parameter gives one line per element. A type parameter gives one line per type — writing it
        /// changes every element of that type — and when its value comes from an instance parameter, the
        /// elements of the type in scope must agree on it: a type holds one value, and which instance
        /// "wins" is not something to leave to the order of a list.
        /// </summary>
        private static Plan Build(Document doc, CopyParameterRequest request, Bucket bucket)
        {
            var source = ParameterKey.Parse(request.Source);
            var target = ParameterKey.Parse(request.Target);

            var plan = new Plan
            {
                Target = target,
                TargetIsType = target?.IsType == true,
                SourceName = request.SourceName,
                TargetName = request.TargetName,
                CategoryName = bucket?.Name ?? string.Empty
            };

            var rows = new List<CopyPreviewRow>();

            if (bucket == null || source == null || target == null || source.Text == target.Text)
            {
                plan.Preview = new CopyParameterPreview(request, rows, plan.TargetIsType, string.Empty,
                    new List<string> { bucket == null ? "The category has no elements in this scope." : "Pick two different parameters." });
                return plan;
            }

            var evaluator = new Evaluator(doc, request);

            if (!target.IsType)
            {
                foreach (var member in bucket.Members)
                {
                    var element = doc.GetElement(member.Id);
                    if (element == null)
                        continue;

                    var cell = evaluator.Evaluate(source.Find(doc, element), target.On(element), element);
                    rows.Add(Take(plan, cell, element.Id, Caption(doc, element), 1));
                }
            }
            else
            {
                foreach (var group in bucket.Members.GroupBy(member => member.Type))
                {
                    var elements = group.Select(member => doc.GetElement(member.Id)).Where(element => element != null).ToList();
                    if (elements.Count == 0)
                        continue;

                    var type = group.Key == ElementId.InvalidElementId ? null : doc.GetElement(group.Key);

                    Cell cell;
                    if (type == null)
                        cell = new Cell { State = CopyRowState.Blocked, Reason = NoType };
                    else if (source.IsType)
                        cell = evaluator.Evaluate(source.On(type), target.On(type), null);
                    else
                        cell = evaluator.ForType(elements.Select(element => source.Find(doc, element)).ToList(), target.On(type));

                    rows.Add(Take(plan, cell, type?.Id, type == null ? "(no type)" : Caption(doc, type), elements.Count));
                }
            }

            var notes = new List<string>();

            if (target.IsType)
                notes.Add("\"" + request.TargetName + "\" is a type parameter: every line is a type, and writing it changes every element of that type.");
            else if (source.IsType)
                notes.Add("\"" + request.SourceName + "\" is a type parameter: every element takes its own type's value.");

            foreach (var group in plan.Writes.Where(write => write.Note != null).GroupBy(write => write.Note))
                notes.Add(group.Count() + (group.Count() == 1 ? " value: " : " values: ") + group.Key + ".");

            var conversion = evaluator.FirstSource != null && evaluator.FirstTarget != null
                ? ParameterValueConverter.Describe(evaluator.FirstSource, evaluator.FirstTarget, request.NoUnitSymbol, request.NumberFromText)
                : string.Empty;

            plan.Preview = new CopyParameterPreview(request, rows, target.IsType, conversion, notes);
            return plan;
        }

        private static CopyPreviewRow Take(Plan plan, Cell cell, ElementId id, string caption, int elements)
        {
            string state;
            var extra = string.Join("; ", new[] { cell.Note, cell.Remark }.Where(text => !string.IsNullOrEmpty(text)));
            if (extra.Length > 0)
                extra = " — " + extra;

            switch (cell.State)
            {
                case CopyRowState.Write:
                    plan.Writes.Add(new Write { Target = id, Value = cell.Value, Caption = caption, After = cell.After, Note = cell.Note });
                    state = "Will be written" + extra;
                    break;

                case CopyRowState.Same:
                    plan.Same++;
                    state = "Already holds this value" + extra;
                    break;

                case CopyRowState.Skipped:
                    plan.Left.Add(cell.Reason, caption);
                    state = "Left as it is: " + cell.Reason;
                    break;

                default:
                    plan.Blocked.Add(cell.Reason, caption + (cell.Source.Length > 0 ? " — «" + cell.Source + "»" : string.Empty));
                    state = "Cannot be written: " + cell.Reason;
                    break;
            }

            return new CopyPreviewRow(id?.ToString() ?? string.Empty, caption, elements, cell.Source, cell.Before, cell.After, cell.State, state);
        }

        private sealed class Evaluator
        {
            private readonly Document _doc;
            private readonly CopyParameterRequest _request;

            public Evaluator(Document doc, CopyParameterRequest request)
            {
                _doc = doc;
                _request = request;
            }

            /// <summary>The first pair met — what the conversion rule above the preview is worded from.</summary>
            public Parameter FirstSource { get; private set; }

            public Parameter FirstTarget { get; private set; }

            /// <summary>
            /// One line: the source read, the target checked, the value converted. The order of the checks
            /// is the order of what matters: a target that cannot be written at all is said before a source
            /// that happens to be empty.
            /// </summary>
            /// <param name="member">The element written, when it is an element (not a type) — for the model group check.</param>
            public Cell Evaluate(Parameter source, Parameter target, Element member)
            {
                var cell = new Cell();

                if (FirstSource == null && source != null)
                    FirstSource = source;
                if (FirstTarget == null && target != null)
                    FirstTarget = target;

                SourceValue value = null;
                if (source != null)
                {
                    value = ParameterValueConverter.Read(_doc, source, _request.NoUnitSymbol);
                    cell.Source = value.Text;
                }

                if (target == null)
                    return Stop(cell, CopyRowState.Blocked, NoTarget);

                cell.Before = ParameterValueConverter.Current(target);
                cell.After = cell.Before;

                if (target.IsReadOnly)
                    return Stop(cell, CopyRowState.Blocked, ReadOnlyTarget);

                // Left to the commit, a refused group edit rolls back the whole batch, not just itself.
                if (member != null && member.GroupId != null && member.GroupId != ElementId.InvalidElementId && !VariesAcrossGroups(target))
                    return Stop(cell, CopyRowState.Blocked, InGroup);

                if (value == null)
                    return Stop(cell, CopyRowState.Skipped, NoSource);

                if (value.IsEmpty)
                {
                    if (_request.EmptyClears && !_request.OnlyEmptyTargets && target.StorageType == StorageType.String && !ParameterValueConverter.IsEmpty(target))
                    {
                        cell.Value = string.Empty;
                        cell.After = string.Empty;
                        cell.Note = Emptied;
                        return cell;
                    }

                    return Stop(cell, CopyRowState.Skipped, EmptySource);
                }

                if (_request.OnlyEmptyTargets && !ParameterValueConverter.IsEmpty(target))
                    return Stop(cell, CopyRowState.Skipped, Filled);

                if (!ParameterValueConverter.TryConvert(_doc, value, target, _request.NumberFromText, out var converted, out var note, out var error))
                    return Stop(cell, CopyRowState.Blocked, error);

                cell.Value = converted;
                cell.After = ParameterValueConverter.Show(_doc, target, converted);
                cell.Note = note;

                if (ParameterValueConverter.Holds(target, converted))
                {
                    cell.State = CopyRowState.Same;
                    cell.After = cell.Before;
                }

                return cell;
            }

            /// <summary>
            /// A type's line, its value coming from its elements' instance parameter: they must all give
            /// the same value. Elements that give none (an empty source, say) do not block the others — the
            /// line says how many they are.
            /// </summary>
            public Cell ForType(IReadOnlyList<Parameter> sources, Parameter target)
            {
                var cells = sources.Select(source => Evaluate(source, target, null)).ToList();
                var taken = cells.Where(cell => cell.State == CopyRowState.Write || cell.State == CopyRowState.Same).ToList();

                if (taken.Count == 0)
                {
                    return cells
                        .GroupBy(cell => cell.State + "\n" + cell.Reason)
                        .OrderByDescending(group => group.Count())
                        .First()
                        .First();
                }

                var values = new List<List<Cell>>();
                foreach (var cell in taken)
                {
                    var same = values.FirstOrDefault(group => ScheduleValues.Equal(group[0].Value, cell.Value));
                    if (same == null)
                        values.Add(new List<Cell> { cell });
                    else
                        same.Add(cell);
                }

                if (values.Count > 1)
                {
                    return new Cell
                    {
                        State = CopyRowState.Blocked,
                        Reason = Disagree,
                        Source = string.Join(", ", values.OrderByDescending(group => group.Count).Select(group => "«" + group[0].Source + "» ×" + group.Count)),
                        Before = taken[0].Before,
                        After = taken[0].Before
                    };
                }

                var result = (taken.FirstOrDefault(cell => cell.State == CopyRowState.Write) ?? taken[0]).Copy();

                var without = cells.Count - taken.Count;
                if (without > 0)
                {
                    var reason = cells.Where(cell => !taken.Contains(cell)).GroupBy(cell => cell.Reason).OrderByDescending(group => group.Count()).First().Key;
                    result.Remark = without + " of its " + cells.Count + " elements give no value (" + reason + ")";
                }

                return result;
            }

            private static Cell Stop(Cell cell, CopyRowState state, string reason)
            {
                cell.State = state;
                cell.Reason = reason;
                return cell;
            }

            private static bool VariesAcrossGroups(Parameter parameter)
            {
                try
                {
                    return (parameter.Definition as InternalDefinition)?.VariesAcrossGroups == true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>A line's name in the words of the Project Browser: a sheet by number, a view or a room by name, the rest by family and type.</summary>
        private static string Caption(Document doc, Element element)
        {
            try
            {
                if (element is ViewSheet sheet)
                    return sheet.SheetNumber + " - " + sheet.Name;

                if (element is View || element is SpatialElement || element is Level || element is Grid)
                    return element.Name;

                var type = element as ElementType ?? ParameterKey.TypeOf(doc, element) as ElementType;
                if (type != null)
                    return string.IsNullOrEmpty(type.FamilyName) ? type.Name : type.FamilyName + " : " + type.Name;

                return element.Name;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        // ───────────────────────────── copying ─────────────────────────────

        /// <summary>Everything one run found out.</summary>
        private sealed class Outcome
        {
            public WarningSuppressor Warnings { get; } = new WarningSuppressor();

            public List<Write> Done { get; } = new List<Write>();

            public Tally Failures { get; } = new Tally();

            public Dictionary<string, int> Held { get; } = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);

            public int Stale { get; set; }

            public List<string> Notes { get; } = new List<string>();

            /// <summary>The commit status when Revit rolled the whole batch back; <c>null</c> otherwise.</summary>
            public TransactionStatus? RolledBack { get; set; }
        }

        /// <summary>
        /// Writes the plan the user confirmed: checks the targets out, writes them all in one transaction,
        /// and reads every one of them back.
        /// </summary>
        private static void Copy(Document doc, Plan plan)
        {
            var outcome = new Outcome();
            var free = Borrow(doc, plan.Writes.Select(write => write.Target).Distinct().ToList(), outcome);

            var requested = new List<Write>();
            var status = TransactionStatus.Uninitialized;

            if (free.Count > 0)
            {
                using (var transaction = new Transaction(doc, "Copy \"" + plan.SourceName + "\" → \"" + plan.TargetName + "\""))
                {
                    transaction.Start();

                    var options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(outcome.Warnings);
                    transaction.SetFailureHandlingOptions(options);

                    foreach (var write in plan.Writes.Where(write => free.Contains(write.Target)))
                    {
                        if (Apply(doc, plan.Target, write, outcome))
                            requested.Add(write);
                    }

                    if (requested.Count == 0)
                        transaction.RollBack();
                    else
                        status = transaction.Commit();
                }
            }

            if (requested.Count > 0)
            {
                if (status == TransactionStatus.Committed)
                    Verify(doc, plan.Target, requested, outcome);
                else
                    outcome.RolledBack = status;
            }

            Report(plan, outcome);
        }

        /// <summary>
        /// Checks the targets out of the central model before writing them — the recipe of "Move to
        /// Workset": an element somebody else holds, or one changed in the central model since the last
        /// reload, would fail the whole transaction at the commit rather than itself, so both are taken
        /// out here and counted in the report.
        /// </summary>
        private static HashSet<ElementId> Borrow(Document doc, List<ElementId> wanted, Outcome outcome)
        {
            if (!doc.IsWorkshared || wanted.Count == 0)
                return new HashSet<ElementId>(wanted);

            HashSet<ElementId> owned = null;

            try
            {
                owned = new HashSet<ElementId>(WorksharingUtils.CheckoutElements(doc, wanted));
            }
            catch (Exception exception)
            {
                outcome.Notes.Add("The elements could not be checked out in one go (" + LinkCatalog.Short(exception.Message) +
                                  "). Whatever Revit lets through is still written.");
            }

            var free = new HashSet<ElementId>();

            foreach (var id in wanted)
            {
                var updates = ModelUpdatesStatus.CurrentWithCentral;
                try
                {
                    updates = WorksharingUtils.GetModelUpdatesStatus(doc, id);
                }
                catch (Exception)
                {
                    // Nothing could be found out — let the write itself decide.
                }

                if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral)
                {
                    outcome.Stale++;
                    continue;
                }

                if (owned != null && owned.Contains(id))
                {
                    free.Add(id);
                    continue;
                }

                var owner = string.Empty;
                var checkout = CheckoutStatus.NotOwned;

                try
                {
                    checkout = WorksharingUtils.GetCheckoutStatus(doc, id, out owner);
                }
                catch (Exception)
                {
                    // As above: the write decides.
                }

                if (checkout == CheckoutStatus.OwnedByOtherUser)
                {
                    var who = string.IsNullOrEmpty(owner) ? "another user" : owner;
                    outcome.Held[who] = outcome.Held.TryGetValue(who, out var count) ? count + 1 : 1;
                }
                else
                {
                    free.Add(id);
                }
            }

            return free;
        }

        /// <summary>
        /// Writes one value. The parameter is found again rather than trusted from the preview: the window
        /// may have been open a while, and the document is what counts, not the snapshot.
        /// </summary>
        private static bool Apply(Document doc, ParameterKey key, Write write, Outcome outcome)
        {
            try
            {
                var owner = doc.GetElement(write.Target);
                if (owner == null)
                {
                    outcome.Failures.Add("it is no longer in the model", write.Caption);
                    return false;
                }

                var parameter = key.On(owner);
                if (parameter == null || parameter.IsReadOnly)
                {
                    outcome.Failures.Add("Revit no longer lets the parameter be written", write.Caption);
                    return false;
                }

                if (!ParameterValueConverter.Write(parameter, write.Value))
                {
                    outcome.Failures.Add("Revit did not accept the value", write.Caption + " — «" + write.After + "»");
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                outcome.Failures.Add(LinkCatalog.Short(exception.Message), write.Caption + " — «" + write.After + "»");
                return false;
            }
        }

        /// <summary>
        /// Reads every value back after the commit. A <c>Set</c> that returned true is not yet a value
        /// in the model — the same rule "Move to Workset" and "Accept Changes" keep, and the only honest
        /// basis for the count in the report.
        /// </summary>
        private static void Verify(Document doc, ParameterKey key, IEnumerable<Write> requested, Outcome outcome)
        {
            foreach (var write in requested)
            {
                Parameter parameter = null;

                try
                {
                    parameter = key.On(doc.GetElement(write.Target));
                }
                catch (Exception)
                {
                    // Treated as not written below.
                }

                if (parameter != null && ParameterValueConverter.Holds(parameter, write.Value))
                    outcome.Done.Add(write);
                else if (parameter == null)
                    outcome.Failures.Add("it is gone from the model", write.Caption);
                else
                    outcome.Failures.Add("Revit took the value, but the parameter does not hold it",
                        write.Caption + " — reads «" + ParameterValueConverter.Current(parameter) + "»");
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(Plan plan, Outcome outcome)
        {
            var what = "\"" + plan.SourceName + "\" → \"" + plan.TargetName + "\"" + (plan.CategoryName.Length > 0 ? " (" + plan.CategoryName + ")" : string.Empty);
            var done = outcome.Done.Count;

            var text = done > 0
                ? "Copied " + what + ": " + done + " " + (plan.TargetIsType ? (done == 1 ? "type" : "types") : (done == 1 ? "element" : "elements")) + "." +
                  (plan.TargetIsType ? " Every element of those types changed with them." : string.Empty)
                : "Nothing was copied — " + what + ".";

            if (outcome.RolledBack.HasValue)
            {
                text += "\n\nRevit rolled the whole change back (" + outcome.RolledBack.Value + ") — not a single value was written. " +
                        "The reason is usually in the Revit messages below.";
            }

            var lines = new List<string>();

            if (plan.Same > 0)
                lines.Add("Already held the value: " + plan.Same + ".");

            foreach (var line in plan.Left.Lines())
                lines.Add("Left as they are: " + line + ".");

            foreach (var group in outcome.Done.Where(write => write.Note != null).GroupBy(write => write.Note))
                lines.Add("Of the copied, " + group.Key + ": " + group.Count() + ".");

            if (outcome.Stale > 0)
                lines.Add(outcome.Stale + " were changed in the central model after your last reload and cannot be edited yet — " +
                          "Collaborate → Reload Latest, then press the button again.");

            foreach (var held in outcome.Held.OrderByDescending(pair => pair.Value))
                lines.Add(held.Value + " are held by " + held.Key + " — they have to synchronise with central and relinquish them first.");

            lines.AddRange(outcome.Notes);

            if (lines.Count > 0)
                text += "\n\n" + string.Join("\n", lines);

            var failures = plan.Blocked.Lines().Concat(outcome.Failures.Lines()).ToList();
            if (failures.Count > 0)
            {
                const int limit = 15;
                text += "\n\nCould not be written:\n• " + string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… and " + (failures.Count - limit) + " more";
            }

            var warnings = outcome.Warnings.Messages;
            if (warnings.Count > 0)
            {
                const int limit = 5;
                text += "\n\nRevit warnings:\n• " + string.Join("\n• ", warnings.Take(limit));

                if (warnings.Count > limit)
                    text += "\n… and " + (warnings.Count - limit) + " more";
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
