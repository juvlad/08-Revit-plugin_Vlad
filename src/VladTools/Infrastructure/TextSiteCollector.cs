using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Walks the open project for every place holding Cyrillic text (<see cref="TextSite"/>), and
    /// for the sets of names Revit keeps unique (<see cref="NameScope"/>) — the second only so the
    /// window can catch two Russian names turning into one English one before Revit refuses it.
    ///
    /// Names are read off a fixed list of element classes rather than off every element: a wall's
    /// or a door's <c>Name</c> is its type's, and setting it there is not renaming anything. What is
    /// left off the list but has a writable name parameter (a scope box, a room) is still caught by
    /// the parameter pass.
    ///
    /// The parameter pass reads what the Properties palette shows (<c>GetOrderedParameters</c>), not
    /// every parameter an element carries: Revit's hidden internal parameters are not something a
    /// person typed, and a translation has no business in them.
    /// </summary>
    internal sealed class TextSiteCollector
    {
        private const string SharedParameterReason =
            "a shared parameter's name is fixed by its GUID in the shared parameter file — Revit will not rename it even by hand";

        private const string LineStyleReason =
            "the Revit API cannot rename a line style — rename it by hand: Manage → Additional Settings → Line Styles";

        private const string SubcategoryReason =
            "the Revit API cannot rename a subcategory — rename it by hand in Manage → Object Styles " +
            "(one that came with a family is renamed inside the family)";

        /// <summary>
        /// The classes whose <c>Name</c> is a name a person gave, plus every <c>ElementType</c>
        /// (collected separately). Filter elements are listed by their concrete classes:
        /// <c>OfClass</c> refuses some abstract bases.
        /// </summary>
        private static readonly Type[] NamedClasses =
        {
            typeof(View),
            typeof(Family),
            typeof(Material),
            typeof(AppearanceAssetElement),
            typeof(PropertySetElement),
            typeof(FillPatternElement),
            typeof(LinePatternElement),
            typeof(ParameterFilterElement),
            typeof(SelectionFilterElement),
            typeof(Level),
            typeof(Grid),
            typeof(Phase),
            typeof(PhaseFilter),
            typeof(ViewSheetSet),
            typeof(AreaScheme),
            typeof(ProjectLocation),
            typeof(ParameterElement),
            typeof(SharedParameterElement),
            typeof(GlobalParameter),
            typeof(RevisionNumberingSequence),
            typeof(BrowserOrganization)
        };

        private readonly Document _doc;
        private readonly List<TextSite> _sites = new List<TextSite>();
        private readonly Dictionary<string, KeyValuePair<string, HashSet<string>>> _scopes =
            new Dictionary<string, KeyValuePair<string, HashSet<string>>>(StringComparer.Ordinal);

        // Elements whose name goes through Element.Name — their built-in "name" parameters are the
        // same string seen from another side, and must not become a second place of their own.
        private readonly HashSet<ElementId> _named = new HashSet<ElementId>();

        private TextSiteCollector(Document doc)
        {
            _doc = doc;
        }

        public IReadOnlyList<TextSite> Sites => _sites;

        public IReadOnlyList<NameScope> Scopes => _scopes.Values
            .Select(scope => new NameScope(scope.Key, scope.Value.ToList()))
            .ToList();

        public static TextSiteCollector Collect(Document doc)
        {
            var collector = new TextSiteCollector(doc);

            collector.CollectNames();
            collector.CollectSheetNumbers();
            collector.CollectWorksets();
            collector.CollectParameters();
            collector.CollectSchedules();
            collector.CollectViewFilters();
            collector.CollectTextNotes();
            collector.CollectSubcategories();

            return collector;
        }

        // ───────────────────────────── names ─────────────────────────────

        private void CollectNames()
        {
            foreach (var element in NamedElements())
            {
                string name;
                try
                {
                    name = element.Name;
                }
                catch (Exception)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(name))
                    continue;

                _named.Add(element.Id);

                var scope = ScopeOf(element);
                if (scope.HasValue)
                    AddToScope(scope.Value.Key, scope.Value.Value, name);

                if (!TextSite.HasCyrillic(name))
                    continue;

                var owner = What(element) + " \"" + name + "\"";

                _sites.Add(element is SharedParameterElement
                    ? (TextSite)new FixedTextSite(name, owner, "shared parameter name", SharedParameterReason)
                    : new ElementNameSite(element, name, owner, NameKind(element), HintFor(element)));
            }
        }

        private IEnumerable<Element> NamedElements()
        {
            var seen = new HashSet<ElementId>();
            var found = new List<Element>();

            foreach (var type in NamedClasses)
            {
                try
                {
                    found.AddRange(new FilteredElementCollector(_doc).OfClass(type).ToElements());
                }
                catch (Exception)
                {
                    // A class Revit will not filter by is skipped: its names are simply not offered.
                }
            }

            found.AddRange(new FilteredElementCollector(_doc).WhereElementIsElementType().ToElements());

            return found.Where(element => seen.Add(element.Id) && IsOwnName(element));
        }

        /// <summary>
        /// Views Revit keeps for itself are never offered: the browsers, the internal views, and the
        /// two schedules that live inside a titleblock rather than in the browser — the same set
        /// "Cleanup" never touches.
        /// </summary>
        private static bool IsOwnName(Element element)
        {
            if (!(element is View view))
                return true;

            switch (view.ViewType)
            {
                case ViewType.Internal:
                case ViewType.ProjectBrowser:
                case ViewType.SystemBrowser:
                case ViewType.Undefined:
                    return false;
            }

            return !(view is ViewSchedule schedule) || (!schedule.IsTitleblockRevisionSchedule && !schedule.IsInternalKeynoteSchedule);
        }

        /// <summary>
        /// The uniqueness scope of an element's name: key and caption. Sheets have none — two sheets
        /// may share a name; their numbers are what Revit keeps unique (<see cref="CollectSheetNumbers"/>).
        /// </summary>
        private static KeyValuePair<string, string>? ScopeOf(Element element)
        {
            try
            {
                switch (element)
                {
                    case BrowserOrganization _:
                        return Scope("browser organization", "browser organizations");
                    case ViewSheet _:
                        return null;
                    case View view when view.IsTemplate:
                        return Scope("view template", "view templates");
                    case View view:
                        return Scope("view:" + view.ViewType, "views (" + ViewKind(view) + ")");
                    case FamilySymbol symbol:
                        return Scope("type:" + symbol.Family.Id, "types of family \"" + symbol.Family.Name + "\"");
                    case ElementType type:
                        return Scope("type:" + type.Category?.Id + ":" + type.FamilyName, "types of \"" + type.FamilyName + "\"");
                    case Family family:
                        return Scope("family:" + family.FamilyCategory?.Id,
                            "families" + (family.FamilyCategory != null ? " (" + family.FamilyCategory.Name + ")" : string.Empty));
                    case Material _:
                        return Scope("material", "materials");
                    case FillPatternElement pattern:
                        return Scope("fill pattern:" + pattern.GetFillPattern().Target, "fill patterns");
                    case LinePatternElement _:
                        return Scope("line pattern", "line patterns");
                    case ParameterFilterElement _:
                    case SelectionFilterElement _:
                        return Scope("filter", "filters");
                    case Level _:
                        return Scope("level", "levels");
                    case Grid _:
                        return Scope("grid", "grids");
                    case Phase _:
                        return Scope("phase", "phases");
                    case PhaseFilter _:
                        return Scope("phase filter", "phase filters");
                    case ViewSheetSet _:
                        return Scope("print set", "print sets");
                    case GlobalParameter _:
                        return Scope("global parameter", "global parameters");
                    case ParameterElement _:
                        return Scope("parameter", "project and shared parameters");
                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static KeyValuePair<string, string> Scope(string key, string caption)
        {
            return new KeyValuePair<string, string>(key, caption);
        }

        private void AddToScope(string key, string caption, string name)
        {
            if (!_scopes.TryGetValue(key, out var scope))
            {
                scope = new KeyValuePair<string, HashSet<string>>(caption, new HashSet<string>(StringComparer.Ordinal));
                _scopes.Add(key, scope);
            }

            scope.Value.Add(TextSite.Normalize(name));
        }

        /// <summary>Sheet numbers are kept unique by Revit; a Cyrillic one ("АР-01") is a parameter value, found by the parameter pass.</summary>
        private void CollectSheetNumbers()
        {
            foreach (var sheet in new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                try
                {
                    AddToScope("sheet number", "sheet numbers", sheet.SheetNumber);
                }
                catch (Exception)
                {
                }
            }
        }

        private void CollectWorksets()
        {
            if (!_doc.IsWorkshared)
                return;

            foreach (var workset in new FilteredWorksetCollector(_doc).OfKind(WorksetKind.UserWorkset).ToWorksets())
            {
                AddToScope("workset", "worksets", workset.Name);

                if (TextSite.HasCyrillic(workset.Name))
                    _sites.Add(new WorksetNameSite(workset));
            }
        }

        // ───────────────────────────── parameter values ─────────────────────────────

        private void CollectParameters()
        {
            CollectParameters(new FilteredElementCollector(_doc).WhereElementIsNotElementType());
            CollectParameters(new FilteredElementCollector(_doc).WhereElementIsElementType());
        }

        private void CollectParameters(FilteredElementCollector elements)
        {
            foreach (var element in elements)
            {
                IList<Parameter> parameters;
                try
                {
                    parameters = element.GetOrderedParameters();
                }
                catch (Exception)
                {
                    continue;
                }

                if (parameters == null || parameters.Count == 0)
                    continue;

                var named = _named.Contains(element.Id);
                string owner = null;

                foreach (var parameter in parameters)
                {
                    string value;
                    BuiltInParameter builtIn;

                    try
                    {
                        if (parameter.StorageType != StorageType.String || parameter.IsReadOnly)
                            continue;

                        value = parameter.AsString();
                        builtIn = (parameter.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (!TextSite.HasCyrillic(value))
                        continue;

                    // A text note goes its own way, through FormattedText, so its formatting survives.
                    if (builtIn == BuiltInParameter.TEXT_TEXT)
                        continue;

                    // A view's "View Name", a level's "Name", a type's "Type Name": the name already
                    // collected, seen through a built-in parameter. A project or shared parameter is
                    // never skipped this way, whatever it holds.
                    if (named && builtIn != BuiltInParameter.INVALID && TextSite.Normalize(value) == TextSite.Normalize(SafeName(element)))
                        continue;

                    owner = owner ?? OwnerOf(element);
                    _sites.Add(new ParameterSite(element, parameter, value, owner));
                }
            }
        }

        // ───────────────────────────── schedules ─────────────────────────────

        private void CollectSchedules()
        {
            foreach (var schedule in new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
            {
                if (schedule.IsTemplate || !IsOwnName(schedule))
                    continue;

                ScheduleDefinition definition;
                try
                {
                    definition = schedule.Definition;
                }
                catch (Exception)
                {
                    continue;
                }

                if (definition == null)
                    continue;

                var owner = "Schedule \"" + schedule.Name + "\"";

                foreach (var fieldId in definition.GetFieldOrder())
                {
                    try
                    {
                        var heading = definition.GetField(fieldId).ColumnHeading;
                        if (TextSite.HasCyrillic(heading))
                            _sites.Add(new ColumnHeadingSite(schedule, fieldId, heading, owner));
                    }
                    catch (Exception)
                    {
                    }
                }

                for (var index = 0; index < definition.GetFilterCount(); index++)
                {
                    try
                    {
                        var filter = definition.GetFilter(index);
                        if (!filter.IsStringValue)
                            continue;

                        var value = filter.GetStringValue();
                        if (TextSite.HasCyrillic(value))
                            _sites.Add(new ScheduleFilterSite(schedule, index, value, owner));
                    }
                    catch (Exception)
                    {
                    }
                }

                CollectHeader(schedule, owner);
            }
        }

        /// <summary>
        /// The plain-text cells of a schedule's header. The title cell is skipped when it reads the
        /// schedule's own name: it follows the name, which is translated as a name.
        /// </summary>
        private void CollectHeader(ViewSchedule schedule, string owner)
        {
            TableSectionData header;
            try
            {
                header = schedule.GetTableData()?.GetSectionData(SectionType.Header);
            }
            catch (Exception)
            {
                return;
            }

            if (header == null)
                return;

            var title = TextSite.Normalize(schedule.Name);

            for (var row = header.FirstRowNumber; row <= header.LastRowNumber; row++)
            {
                for (var column = header.FirstColumnNumber; column <= header.LastColumnNumber; column++)
                {
                    try
                    {
                        // A merged cell answers for every cell it covers; only its top-left one is real.
                        var merged = header.GetMergedCell(row, column);
                        if (merged != null && (merged.Top != row || merged.Left != column))
                            continue;

                        if (header.GetCellType(row, column) != CellType.Text)
                            continue;

                        var text = header.GetCellText(row, column);
                        if (TextSite.HasCyrillic(text) && TextSite.Normalize(text) != title)
                            _sites.Add(new ScheduleCellSite(schedule, row, column, text, owner));
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        // ───────────────────────────── filters, notes, subcategories ─────────────────────────────

        private void CollectViewFilters()
        {
            foreach (var filter in new FilteredElementCollector(_doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
            {
                List<string> strings;
                try
                {
                    strings = ViewFilterRuleSite.RuleStrings(filter).Where(TextSite.HasCyrillic).ToList();
                }
                catch (Exception)
                {
                    continue;
                }

                var owner = "View filter \"" + filter.Name + "\"";
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var text in strings.Where(text => seen.Add(TextSite.Normalize(text))))
                    _sites.Add(new ViewFilterRuleSite(filter, text, owner));
            }
        }

        private void CollectTextNotes()
        {
            foreach (var note in new FilteredElementCollector(_doc).OfClass(typeof(TextNote)).Cast<TextNote>())
            {
                string text;
                try
                {
                    text = note.Text;
                }
                catch (Exception)
                {
                    continue;
                }

                if (!TextSite.HasCyrillic(text))
                    continue;

                var view = _doc.GetElement(note.OwnerViewId);
                var owner = "Text note on " + (view != null ? What(view) + " \"" + SafeName(view) + "\"" : "a view");

                _sites.Add(new TextNoteSite(note, text, owner));
            }
        }

        /// <summary>
        /// Line styles and object-style subcategories. Collected though nothing can be done to them,
        /// so the window shows what will stay Russian. Only Revit's own categories are walked: an
        /// imported CAD file is a category of its own, and its "subcategories" are the file's layers.
        /// </summary>
        private void CollectSubcategories()
        {
            var lines = new ElementId(BuiltInCategory.OST_Lines);

            foreach (Category category in _doc.Settings.Categories)
            {
                if (!(category.Id < ElementId.InvalidElementId))
                    continue;

                CategoryNameMap subcategories;
                try
                {
                    subcategories = category.SubCategories;
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Category subcategory in subcategories)
                {
                    if (!TextSite.HasCyrillic(subcategory.Name))
                        continue;

                    var isLineStyle = category.Id == lines;

                    _sites.Add(isLineStyle
                        ? new FixedTextSite(subcategory.Name, "Line style \"" + subcategory.Name + "\"", "line style name", LineStyleReason)
                        : new FixedTextSite(subcategory.Name,
                            "Subcategory \"" + subcategory.Name + "\" of \"" + category.Name + "\"",
                            "subcategory name",
                            SubcategoryReason));
                }
            }
        }

        // ───────────────────────────── words ─────────────────────────────

        private static string OwnerOf(Element element)
        {
            return element is ProjectInfo ? "Project information" : What(element) + " \"" + SafeName(element) + "\"";
        }

        private static string SafeName(Element element)
        {
            try
            {
                return element.Name ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>What an element is, in a word or two, for the "Where" column and the report.</summary>
        private static string What(Element element)
        {
            try
            {
                switch (element)
                {
                    case BrowserOrganization _:
                        return "Browser organization";
                    case ViewSheet _:
                        return "Sheet";
                    case View view when view.IsTemplate:
                        return "View template";
                    case View view:
                        return ViewKind(view);
                    case FamilySymbol symbol:
                        return "Type of family \"" + symbol.Family.Name + "\"";
                    case ElementType type:
                        return string.IsNullOrEmpty(type.FamilyName) ? "Type" : "Type of \"" + type.FamilyName + "\"";
                    case Family _:
                        return "Family";
                    case SharedParameterElement _:
                        return "Shared parameter";
                    case GlobalParameter _:
                        return "Global parameter";
                    case ParameterElement _:
                        return "Project parameter";
                }

                return element.Category?.Name ?? element.GetType().Name;
            }
            catch (Exception)
            {
                return element.GetType().Name;
            }
        }

        private static string ViewKind(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan: return "Floor plan";
                case ViewType.CeilingPlan: return "Ceiling plan";
                case ViewType.AreaPlan: return "Area plan";
                case ViewType.EngineeringPlan: return "Structural plan";
                case ViewType.Elevation: return "Elevation";
                case ViewType.Section: return "Section";
                case ViewType.Detail: return "Detail view";
                case ViewType.ThreeD: return "3D view";
                case ViewType.DraftingView: return "Drafting view";
                case ViewType.Legend: return "Legend";
                case ViewType.Schedule: return "Schedule";
                case ViewType.DrawingSheet: return "Sheet";
                case ViewType.Rendering: return "Rendering";
                case ViewType.Walkthrough: return "Walkthrough";
                case ViewType.ColumnSchedule: return "Graphical column schedule";
                case ViewType.PanelSchedule: return "Panel schedule";
                default: return view.ViewType.ToString();
            }
        }

        /// <summary>The "Where" word for a name — what the window counts and the export hands the translator.</summary>
        private static string NameKind(Element element)
        {
            switch (element)
            {
                // Ahead of the general cases: a browser organization is one of Revit's element types.
                case BrowserOrganization _: return "browser organization name";
                case ViewSheet _: return "sheet name";
                case View view when view.IsTemplate: return "view template name";
                case ViewSchedule _: return "schedule name";
                case View _: return "view name";
                case ElementType _: return "type name";
                case Family _: return "family name";
                case GlobalParameter _: return "global parameter name";
                case ParameterElement _: return "project parameter name";
                case Material _: return "material name";
                case AppearanceAssetElement _:
                case PropertySetElement _: return "material asset name";
                case ParameterFilterElement _:
                case SelectionFilterElement _: return "filter name";
                case Level _: return "level name";
                case Grid _: return "grid name";
                case Phase _: return "phase name";
                case PhaseFilter _: return "phase filter name";
                case FillPatternElement _: return "fill pattern name";
                case LinePatternElement _: return "line pattern name";
                case ViewSheetSet _: return "print set name";
                case AreaScheme _: return "area scheme name";
                case ProjectLocation _: return "site name";
                case RevisionNumberingSequence _: return "revision numbering name";
                default: return "name";
            }
        }

        /// <summary>Where to rename by hand when the API refuses — only for the names where that is known to happen.</summary>
        private static string HintFor(Element element)
        {
            switch (element)
            {
                case GlobalParameter _:
                    return "rename it by hand: Manage → Global Parameters";
                case ParameterElement _:
                    return "rename it by hand: Manage → Project Parameters → Modify";
                case ElementType _:
                    return "some of Revit's own system types keep the name Revit gave them";
                default:
                    return null;
            }
        }
    }
}
