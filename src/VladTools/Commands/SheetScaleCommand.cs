using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// The "Sheet Scale" button: a new scale for the views standing on the checked sheets, each view
    /// left where it stands on its sheet.
    ///
    /// Revit changes a scale one view at a time, through the view's properties — and not at all where a
    /// view template sets the scale, which is most views on most sheets: the template decides, not the
    /// view. That is also where "Views to Sheets" stops. So the window asks what to do with such a
    /// template — stop it setting the scale, or give it the new one — and the command does that first.
    /// A primary view and its dependents share one scale; the primary, which holds it, is the one changed.
    ///
    /// After the change the middle of every viewport of every changed view is put back where it stood,
    /// and everything is read back after the commit: every view's scale, where every viewport is, and
    /// whether what it draws still fits the sheet's drawing area.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SheetScaleCommand : IExternalCommand
    {
        private const string DialogTitle = "Sheet Scale";

        private const double MmPerFoot = 304.8;

        /// <summary>Half a millimetre of paper, in feet: how far a viewport may stand from where it stood.</summary>
        private const double Tolerance = 0.5 / MmPerFoot;

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
                    "The command works only in a project or a project template (.rvt, .rte).\n" +
                    "Sheets live in a project, not in a family.");
                return Result.Cancelled;
            }

            try
            {
                var sheets = Collect(doc, out var organization);
                if (sheets.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "No sheet of the project holds a view yet — there is no scale on a sheet to change.");
                    return Result.Cancelled;
                }

                Selected(uidoc, out var checkedSheets, out var checkedViews);

                var window = new SheetScaleWindow(sheets, organization, checkedSheets, checkedViews, SheetScalePreferences.Load());
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true || window.Request == null)
                    return Result.Cancelled;

                var outcome = Change(doc, window.Request);
                Report(window.Request, outcome);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── reading ─────────────────────────────

        /// <summary>A sheet on its way to the window, with what it is sorted by.</summary>
        private sealed class Entry
        {
            public Entry(ScaleSheetInfo info, int group, string sortValue)
            {
                Info = info;
                Group = group;
                SortValue = sortValue ?? string.Empty;
            }

            public ScaleSheetInfo Info { get; }

            /// <summary>0 — in the browser; 1 — hidden by its filter, listed after the rest.</summary>
            public int Group { get; }

            public string SortValue { get; }
        }

        /// <summary>
        /// Every sheet with a view on it, in the folders and the order of the Project Browser — the same
        /// reading as "Sheets" — with every view standing on it. A sheet the browser's filter hides is
        /// still a sheet of the project and gets a folder of its own at the end; a placeholder has no
        /// views to scale and is left out.
        /// </summary>
        private static List<ScaleSheetInfo> Collect(Document doc, out string organizationName)
        {
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(sheet => !sheet.IsTemplate && !sheet.IsPlaceholder)
                .ToDictionary(sheet => sheet.Id);

            var bySheet = new Dictionary<ElementId, List<Viewport>>();
            var sheetCounts = new Dictionary<ElementId, int>();

            foreach (var viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                if (!sheets.ContainsKey(viewport.SheetId))
                    continue;

                if (!bySheet.TryGetValue(viewport.SheetId, out var list))
                    bySheet[viewport.SheetId] = list = new List<Viewport>();

                list.Add(viewport);

                sheetCounts.TryGetValue(viewport.ViewId, out var count);
                sheetCounts[viewport.ViewId] = count + 1;
            }

            BrowserOrganization organization = null;
            var sortingId = ElementId.InvalidElementId;
            var descending = false;

            try
            {
                organization = BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc);
                if (organization != null)
                {
                    sortingId = organization.SortingParameterId;
                    descending = organization.SortingOrder == SortingOrder.Descending;
                }
            }
            catch (Exception)
            {
                // No organization means one flat list in sheet-number order — still the whole answer.
            }

            organizationName = ProjectBrowser.Name(organization);

            var users = TemplateUsers(doc);
            var templates = new Dictionary<ElementId, bool>();
            var views = new Dictionary<ElementId, ScaleViewInfo>();
            var titleblocks = Titleblocks(doc);
            var margins = ViewsToSheetsPreferences.Load();

            var entries = new List<Entry>();

            foreach (var sheet in sheets.Values)
            {
                if (!bySheet.TryGetValue(sheet.Id, out var list))
                    continue;

                var placed = new List<ScaleViewportInfo>();

                foreach (var viewport in list)
                {
                    if (!(doc.GetElement(viewport.ViewId) is View view))
                        continue;

                    if (!views.TryGetValue(view.Id, out var info))
                        views[view.Id] = info = Snapshot(doc, view, sheetCounts, users, templates);

                    var box = Safe(() => viewport.GetBoxOutline(), null);
                    placed.Add(box == null
                        ? new ScaleViewportInfo(info, 0, 0, 0, 0)
                        : new ScaleViewportInfo(info, Mm(box.MinimumPoint.X), Mm(box.MinimumPoint.Y), Mm(box.MaximumPoint.X), Mm(box.MaximumPoint.Y)));
                }

                if (placed.Count == 0)
                    continue;

                placed = placed
                    .OrderBy(item => item.View.IsLegend ? 1 : 0)
                    .ThenBy(item => item.View.Kind + ": " + item.View.Name, ProjectBrowser.NaturalOrder)
                    .ToList();

                var hidden = !ProjectBrowser.Passes(organization, sheet.Id);
                var folders = hidden ? new[] { ProjectBrowser.FilteredFolder } : ProjectBrowser.Folders(organization, sheet.Id);

                titleblocks.TryGetValue(sheet.Id, out var titleblock);

                var sheetInfo = new ScaleSheetInfo(sheet.UniqueId, sheet.SheetNumber, sheet.Name, folders, Area(doc, sheet, titleblock, margins), placed);
                entries.Add(new Entry(sheetInfo, hidden ? 1 : 0, hidden ? sheet.SheetNumber : ProjectBrowser.SortValue(sheet, sortingId, sheet.SheetNumber)));
            }

            entries.Sort((a, b) => Compare(a, b, descending));
            return entries.Select(entry => entry.Info).ToList();
        }

        private static int Compare(Entry a, Entry b, bool descending)
        {
            var result = a.Group.CompareTo(b.Group);
            if (result != 0)
                return result;

            for (var level = 0; level < Math.Min(a.Info.Folders.Count, b.Info.Folders.Count); level++)
            {
                result = ProjectBrowser.Natural(a.Info.Folders[level], b.Info.Folders[level]);
                if (result != 0)
                    return result;
            }

            result = a.Info.Folders.Count.CompareTo(b.Info.Folders.Count);
            if (result != 0)
                return result;

            result = ProjectBrowser.Natural(a.SortValue, b.SortValue);
            if (result != 0)
                return descending ? -result : result;

            return ProjectBrowser.Natural(a.Info.Number, b.Info.Number);
        }

        /// <summary>
        /// One view for the window. What decides its scale is read from the view that holds it — the
        /// primary, for a dependent — since that is the one whose template and scale the run changes.
        /// </summary>
        private static ScaleViewInfo Snapshot(
            Document doc,
            View view,
            Dictionary<ElementId, int> sheetCounts,
            Dictionary<ElementId, List<View>> users,
            Dictionary<ElementId, bool> templates)
        {
            var perspective = view is View3D view3D && Safe(() => view3D.IsPerspective, false);
            var scale = perspective ? 0 : Safe(() => view.Scale, 0);
            var problem = perspective ? "A perspective view has no scale" : scale <= 0 ? "Revit gives this view no scale" : null;

            var owner = ViewScale.Owner(doc, view);
            var dependents = Safe(() => owner.GetDependentViewIds().Count, 0);

            var template = TemplateOf(doc, owner);
            var sets = false;
            if (template != null && !templates.TryGetValue(template.Id, out sets))
                templates[template.Id] = sets = ViewScale.Controls(template) == true;

            var templateUsers = template != null && users.TryGetValue(template.Id, out var list) ? list.Count : 0;
            sheetCounts.TryGetValue(view.Id, out var sheetCount);

            return new ScaleViewInfo(
                view.UniqueId,
                view.Name,
                TextSiteCollector.ViewKind(view),
                scale,
                problem,
                view.ViewType == ViewType.Legend,
                sheetCount,
                dependents > 0 ? owner.UniqueId : null,
                dependents + 1,
                owner.Id != view.Id,
                template?.UniqueId,
                template?.Name,
                sets,
                templateUsers);
        }

        private static View TemplateOf(Document doc, View view)
        {
            var id = Safe(() => view.ViewTemplateId, ElementId.InvalidElementId);
            return id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id) as View;
        }

        /// <summary>Every view of the project by the template it uses — one pass, not one collector per template.</summary>
        private static Dictionary<ElementId, List<View>> TemplateUsers(Document doc)
        {
            var users = new Dictionary<ElementId, List<View>>();

            foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (view.IsTemplate)
                    continue;

                var id = Safe(() => view.ViewTemplateId, ElementId.InvalidElementId);
                if (id == null || id == ElementId.InvalidElementId)
                    continue;

                if (!users.TryGetValue(id, out var list))
                    users[id] = list = new List<View>();

                list.Add(view);
            }

            return users;
        }

        /// <summary>The first titleblock on every sheet — one pass over the project.</summary>
        private static Dictionary<ElementId, Element> Titleblocks(Document doc)
        {
            var titleblocks = new Dictionary<ElementId, Element>();

            foreach (var titleblock in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
            {
                if (!titleblocks.ContainsKey(titleblock.OwnerViewId))
                    titleblocks[titleblock.OwnerViewId] = titleblock;
            }

            return titleblocks;
        }

        /// <summary>
        /// The sheet's drawing area, millimetres — left, bottom, right, top: what its titleblock draws,
        /// less the margins "Views to Sheets" keeps for that titleblock type (where the title panel sits
        /// is a property of the titleblock, set once there). Null when the sheet has no titleblock.
        /// </summary>
        private static double[] Area(Document doc, ViewSheet sheet, Element titleblock, ViewsToSheetsPreferences preferences)
        {
            if (titleblock == null)
                return null;

            try
            {
                var box = titleblock.get_BoundingBox(sheet);
                if (box == null)
                    return null;

                var name = doc.GetElement(titleblock.GetTypeId()) is FamilySymbol symbol ? symbol.FamilyName + " : " + ProjectBrowser.Name(symbol) : null;
                var margins = preferences.Margins(name);

                var area = new[]
                {
                    Mm(box.Min.X) + margins[0],
                    Mm(box.Min.Y) + margins[3],
                    Mm(box.Max.X) - margins[2],
                    Mm(box.Max.Y) - margins[1]
                };

                return area[2] > area[0] && area[3] > area[1] ? area : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// What to start checked: the sheets selected in the Project Browser, the views selected there
        /// or as viewports on a sheet — or, with nothing selected, the sheet that is open.
        /// </summary>
        private static void Selected(UIDocument uidoc, out HashSet<string> sheets, out HashSet<string> views)
        {
            sheets = new HashSet<string>(StringComparer.Ordinal);
            views = new HashSet<string>(StringComparer.Ordinal);

            var doc = uidoc.Document;

            try
            {
                foreach (var id in uidoc.Selection.GetElementIds())
                {
                    switch (doc.GetElement(id))
                    {
                        case ViewSheet sheet:
                            sheets.Add(sheet.UniqueId);
                            break;
                        case Viewport viewport:
                            if (doc.GetElement(viewport.ViewId) is View shown)
                                views.Add(shown.UniqueId);
                            break;
                        case View view when !view.IsTemplate:
                            views.Add(view.UniqueId);
                            break;
                    }
                }

                if (sheets.Count == 0 && views.Count == 0 && uidoc.ActiveView is ViewSheet active)
                    sheets.Add(active.UniqueId);
            }
            catch (Exception)
            {
                // No guess is no harm: the sheets can be checked in the window.
            }
        }

        // ───────────────────────────── changing ─────────────────────────────

        /// <summary>One view that holds a scale — a primary view for its dependents — and what the run does with it.</summary>
        private sealed class Owner
        {
            public Owner(View view)
            {
                View = view;
            }

            public View View { get; }

            /// <summary>The view template the run edits for this view: it sets the scale. Null when there is none to edit.</summary>
            public View Template { get; set; }

            /// <summary>The names of the checked views this one stands for — itself, or dependents of it.</summary>
            public List<string> Requested { get; } = new List<string>();

            /// <summary>
            /// Every view whose scale changes with this one: itself and its dependents, and, with a
            /// template given the new scale, every view using that template.
            /// </summary>
            public HashSet<ElementId> Views { get; } = new HashSet<ElementId>();

            /// <summary>Why nothing of it was changed; null while all is well.</summary>
            public string Failure { get; set; }
        }

        /// <summary>Everything one run found out.</summary>
        private sealed class Outcome
        {
            public WarningSuppressor Warnings { get; } = new WarningSuppressor();

            /// <summary>The views checked in the window.</summary>
            public HashSet<ElementId> Requested { get; } = new HashSet<ElementId>();

            /// <summary>Every view the run touches, with its scale before.</summary>
            public Dictionary<ElementId, int> Before { get; } = new Dictionary<ElementId, int>();

            /// <summary>Where the middle of every viewport of those views stood before.</summary>
            public Dictionary<ElementId, XYZ> Centres { get; } = new Dictionary<ElementId, XYZ>();

            public List<string> Changed { get; } = new List<string>();

            /// <summary>Changed without being checked: a primary or a dependent of a checked view, or a user of a template given the new scale.</summary>
            public List<string> Along { get; } = new List<string>();

            public List<string> Released { get; } = new List<string>();

            public List<string> Retemplated { get; } = new List<string>();

            /// <summary>
            /// The other views of a template that stopped setting the scale, with their scale before: they
            /// are to keep it, and are read back to show they did.
            /// </summary>
            public Dictionary<ElementId, int> Watched { get; } = new Dictionary<ElementId, int>();

            /// <summary>Read back at another scale than before although the run was not to change them.</summary>
            public List<string> Unasked { get; } = new List<string>();

            /// <summary>Read back at another scale than asked.</summary>
            public List<string> NotChanged { get; } = new List<string>();

            /// <summary>Read back somewhere else than they stood.</summary>
            public List<string> Off { get; } = new List<string>();

            public List<string> Overhanging { get; } = new List<string>();

            /// <summary>Could not be read back at all — kept apart from "changed".</summary>
            public List<string> Unchecked { get; } = new List<string>();

            public List<string> Failures { get; } = new List<string>();

            public List<string> Notes { get; } = new List<string>();

            public TransactionStatus? RolledBack { get; set; }
        }

        private static Outcome Change(Document doc, SheetScaleRequest request)
        {
            var outcome = new Outcome();
            var owners = Owners(doc, request, outcome);

            if (owners.Count > 0)
            {
                var viewports = Viewports(doc);

                Borrow(doc, owners, viewports, outcome);
                Apply(doc, request, owners.Where(owner => owner.Failure == null).ToList(), viewports, outcome);
            }

            foreach (var owner in owners.Where(owner => owner.Failure != null))
            {
                foreach (var name in owner.Requested)
                    outcome.Failures.Add(name + " — " + owner.Failure);
            }

            return outcome;
        }

        /// <summary>
        /// The checked views grouped by the view that holds their scale, with what decides it read again
        /// from the model — not from the window's snapshot, which is as old as the window.
        /// </summary>
        private static List<Owner> Owners(Document doc, SheetScaleRequest request, Outcome outcome)
        {
            var owners = new Dictionary<ElementId, Owner>();
            Dictionary<ElementId, List<View>> users = null;

            foreach (var info in request.Views)
            {
                if (!(doc.GetElement(info.UniqueId) is View view))
                {
                    outcome.Failures.Add(info.Name + " — the view is no longer in the project");
                    continue;
                }

                outcome.Requested.Add(view.Id);
                var holder = ViewScale.Owner(doc, view);

                if (!owners.TryGetValue(holder.Id, out var owner))
                {
                    owners[holder.Id] = owner = new Owner(holder);
                    AddWithDependents(owner.Views, holder);

                    var template = TemplateOf(doc, holder);
                    if (template != null && ViewScale.Controls(template) == true)
                    {
                        if (request.Templates == ScaleTemplateMode.Leave)
                        {
                            owner.Failure = "its view template \"" + template.Name + "\" sets the scale";
                        }
                        else
                        {
                            owner.Template = template;

                            if (request.Templates == ScaleTemplateMode.ChangeTemplate)
                            {
                                users = users ?? TemplateUsers(doc);
                                if (users.TryGetValue(template.Id, out var list))
                                {
                                    foreach (var user in list)
                                        AddWithDependents(owner.Views, user);
                                }
                            }
                        }
                    }
                }

                owner.Requested.Add(view.Name);
            }

            return owners.Values.ToList();
        }

        private static void AddWithDependents(HashSet<ElementId> views, View view)
        {
            views.Add(view.Id);

            foreach (var id in Safe(() => view.GetDependentViewIds(), (ICollection<ElementId>)new List<ElementId>()))
                views.Add(id);
        }

        /// <summary>Every viewport on a sheet, by the view it shows.</summary>
        private static Dictionary<ElementId, List<Viewport>> Viewports(Document doc)
        {
            var viewports = new Dictionary<ElementId, List<Viewport>>();

            foreach (var viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                if (!viewports.TryGetValue(viewport.ViewId, out var list))
                    viewports[viewport.ViewId] = list = new List<Viewport>();

                list.Add(viewport);
            }

            return viewports;
        }

        /// <summary>
        /// Checks out what the run edits before any of it is edited: every view whose scale changes, its
        /// viewports (one may have to be put back), and the template edited. In a workshared model an
        /// element somebody else holds, or one changed in the central model since the last reload, fails
        /// the whole transaction at the commit rather than itself — so the views needing one are taken
        /// out here and named, the same rule "Move to Workset" keeps. A checkout that throws (no central
        /// yet, the network) is a note, not a stop.
        /// </summary>
        private static void Borrow(Document doc, List<Owner> owners, Dictionary<ElementId, List<Viewport>> viewports, Outcome outcome)
        {
            if (!doc.IsWorkshared)
                return;

            var needs = new Dictionary<Owner, List<ElementId>>();
            foreach (var owner in owners.Where(owner => owner.Failure == null))
            {
                var list = new List<ElementId>(owner.Views);

                foreach (var id in owner.Views)
                {
                    if (viewports.TryGetValue(id, out var placed))
                        list.AddRange(placed.Select(viewport => viewport.Id));
                }

                if (owner.Template != null)
                    list.Add(owner.Template.Id);

                needs[owner] = list;
            }

            HashSet<ElementId> owned = null;

            try
            {
                owned = new HashSet<ElementId>(WorksharingUtils.CheckoutElements(doc, needs.Values.SelectMany(list => list).Distinct().ToList()));
            }
            catch (Exception exception)
            {
                outcome.Notes.Add("The views could not be checked out in one go (" + LinkCatalog.Short(exception.Message) +
                                  "). Whatever Revit lets through is still changed.");
            }

            var reasons = new Dictionary<ElementId, string>();

            foreach (var pair in needs)
            {
                foreach (var id in pair.Value)
                {
                    if (!reasons.TryGetValue(id, out var reason))
                        reasons[id] = reason = Blocked(doc, id, owned);

                    if (reason != null)
                    {
                        pair.Key.Failure = Describe(doc, id) + " is " + reason;
                        break;
                    }
                }
            }
        }

        private static string Blocked(Document doc, ElementId id, HashSet<ElementId> owned)
        {
            var updates = Safe(() => WorksharingUtils.GetModelUpdatesStatus(doc, id), ModelUpdatesStatus.CurrentWithCentral);
            if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral)
                return "changed in the central model since your last reload — Collaborate → Reload Latest, then press the button again";

            if (owned != null && owned.Contains(id))
                return null;

            var user = string.Empty;
            var status = CheckoutStatus.NotOwned;

            try
            {
                status = WorksharingUtils.GetCheckoutStatus(doc, id, out user);
            }
            catch (Exception)
            {
                // Nothing could be found out — let the edit itself decide.
            }

            return status == CheckoutStatus.OwnedByOtherUser
                ? "held by " + (string.IsNullOrEmpty(user) ? "another user" : user) + " — they have to synchronise with central and relinquish it first"
                : null;
        }

        /// <summary>"the view template "A_Plan"", "its viewport on A101" — what a checkout failure is about.</summary>
        private static string Describe(Document doc, ElementId id)
        {
            switch (doc.GetElement(id))
            {
                case View view when view.IsTemplate:
                    return "its view template \"" + view.Name + "\"";
                case View view:
                    return "the view \"" + view.Name + "\"";
                case Viewport viewport:
                    return "its viewport on " + SheetTitle(doc, viewport);
                default:
                    return "an element it needs";
            }
        }

        /// <summary>
        /// The change itself, in one transaction. Templates first — until a template lets go of the
        /// scale, the view's own scale cannot be changed past it — then every view holding a scale, then
        /// one regeneration and every viewport put back where it stood. After the commit everything is
        /// read back.
        /// </summary>
        private static void Apply(Document doc, SheetScaleRequest request, List<Owner> live, Dictionary<ElementId, List<Viewport>> viewports, Outcome outcome)
        {
            if (live.Count == 0)
                return;

            var scale = request.Scale;

            foreach (var id in live.SelectMany(owner => owner.Views))
            {
                if (!outcome.Before.ContainsKey(id) && doc.GetElement(id) is View view)
                    outcome.Before[id] = Safe(() => view.Scale, 0);

                if (!viewports.TryGetValue(id, out var placed))
                    continue;

                foreach (var viewport in placed)
                {
                    var centre = Safe(() => viewport.GetBoxCenter(), null);
                    if (centre != null)
                        outcome.Centres[viewport.Id] = centre;
                }
            }

            var released = new List<string>();
            var retemplated = new List<string>();
            var users = request.Templates == ScaleTemplateMode.Leave ? null : TemplateUsers(doc);
            var done = new List<Owner>();

            if (request.Templates == ScaleTemplateMode.Release && users != null)
            {
                foreach (var template in live.Where(owner => owner.Template != null).Select(owner => owner.Template))
                {
                    if (!users.TryGetValue(template.Id, out var list))
                        continue;

                    foreach (var user in list.Where(user => !outcome.Before.ContainsKey(user.Id)))
                        outcome.Watched[user.Id] = Safe(() => user.Scale, 0);
                }
            }
            var status = TransactionStatus.Uninitialized;

            var count = live.Sum(owner => owner.Requested.Count);
            var undo = "Change the scale of " + Views(count) + " to " + ViewPlacementRow.ScaleCaption(scale);

            using (var transaction = new Transaction(doc, undo))
            {
                var options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(outcome.Warnings);
                transaction.SetFailureHandlingOptions(options);

                transaction.Start();

                foreach (var group in live.Where(owner => owner.Template != null).GroupBy(owner => owner.Template.Id))
                {
                    var template = group.First().Template;
                    var used = users != null && users.TryGetValue(template.Id, out var list) ? list.Count : 0;

                    try
                    {
                        if (request.Templates == ScaleTemplateMode.Release)
                        {
                            ViewScale.Release(template);
                            released.Add(template.Name + " — " + Views(used) + " use it");
                        }
                        else
                        {
                            SetTemplateScale(template, scale);
                            retemplated.Add(template.Name + " — " + Views(used) + " use it");
                        }
                    }
                    catch (Exception exception)
                    {
                        foreach (var owner in group)
                            owner.Failure = "its view template \"" + template.Name + "\" could not be changed: " + LinkCatalog.Short(exception.Message);
                    }
                }

                doc.Regenerate();

                foreach (var owner in live.Where(owner => owner.Failure == null))
                {
                    // A template given the new scale passes it on by itself; past it the view's own scale is not settable.
                    if (owner.Template != null && request.Templates == ScaleTemplateMode.ChangeTemplate)
                    {
                        done.Add(owner);
                        continue;
                    }

                    try
                    {
                        owner.View.Scale = scale;
                        done.Add(owner);
                    }
                    catch (Exception exception)
                    {
                        owner.Failure = LinkCatalog.Short(exception.Message);
                    }
                }

                if (done.Count == 0)
                {
                    transaction.RollBack();
                    return;
                }

                doc.Regenerate();
                Keep(doc, done, viewports, outcome);

                status = transaction.Commit();
            }

            if (status != TransactionStatus.Committed)
            {
                outcome.RolledBack = status;
                return;
            }

            outcome.Released.AddRange(released);
            outcome.Retemplated.AddRange(retemplated);
            Verify(doc, request, done, viewports, outcome);
        }

        /// <summary>
        /// A template's own scale. <c>View.Scale</c> is what Revit's own properties write; should a
        /// template refuse it, its "Scale Value 1:" is the other way in. The read-back after the commit
        /// says whether the views followed.
        /// </summary>
        private static void SetTemplateScale(View template, int scale)
        {
            try
            {
                template.Scale = scale;
            }
            catch (Exception)
            {
                var parameter = template.get_Parameter(BuiltInParameter.VIEW_SCALE);
                if (parameter == null || parameter.IsReadOnly || !parameter.Set(scale))
                    throw;
            }
        }

        /// <summary>
        /// Puts the middle of every viewport of every changed view back where it stood. Revit grows or
        /// shrinks a viewport round a point of its own choosing, and annotation that keeps its size on
        /// paper shifts the box besides; a sheet set laid out by hand must not come back rearranged. A
        /// pinned viewport has its pin taken off for the move and put back straight after, the same as
        /// "Views to Sheets" does.
        /// </summary>
        private static void Keep(Document doc, List<Owner> done, Dictionary<ElementId, List<Viewport>> viewports, Outcome outcome)
        {
            foreach (var id in done.SelectMany(owner => owner.Views).Distinct())
            {
                if (!viewports.TryGetValue(id, out var placed))
                    continue;

                foreach (var viewport in placed)
                {
                    if (!outcome.Centres.TryGetValue(viewport.Id, out var before))
                        continue;

                    try
                    {
                        if (Distance(viewport.GetBoxCenter(), before) <= Tolerance)
                            continue;

                        var pinned = viewport.Pinned;
                        if (pinned)
                            viewport.Pinned = false;

                        try
                        {
                            viewport.SetBoxCenter(before);
                        }
                        finally
                        {
                            if (pinned)
                            {
                                try
                                {
                                    viewport.Pinned = true;
                                }
                                catch (Exception)
                                {
                                    outcome.Notes.Add(ViewportTitle(doc, viewport) + ": the viewport was put back but could not be pinned again.");
                                }
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        outcome.Off.Add(ViewportTitle(doc, viewport) + " — could not be put back where it stood: " + LinkCatalog.Short(exception.Message));
                    }
                }
            }
        }

        /// <summary>
        /// Reads every changed view and every viewport back after the commit: does the view have the new
        /// scale, is the viewport where it stood, and does all it draws stay inside the sheet's drawing
        /// area. <c>View.Scale</c> returning without an error is not a view at that scale — the same rule
        /// "Views to Sheets" and "Accept Changes" keep.
        /// </summary>
        private static void Verify(Document doc, SheetScaleRequest request, List<Owner> done, Dictionary<ElementId, List<Viewport>> viewports, Outcome outcome)
        {
            var scale = request.Scale;
            var titleblocks = Titleblocks(doc);
            var margins = ViewsToSheetsPreferences.Load();
            var areas = new Dictionary<ElementId, double[]>();

            foreach (var id in done.SelectMany(owner => owner.Views).Distinct())
            {
                if (!outcome.Before.TryGetValue(id, out var before) || before == scale)
                    continue;

                if (!(doc.GetElement(id) is View view))
                {
                    outcome.Unchecked.Add("A view of the run — it is gone from the model");
                    continue;
                }

                viewports.TryGetValue(id, out var placed);
                placed = placed ?? new List<Viewport>();

                var label = Label(doc, view, placed);

                int now;
                try
                {
                    now = view.Scale;
                }
                catch (Exception exception)
                {
                    outcome.Unchecked.Add(label + " — " + LinkCatalog.Short(exception.Message));
                    continue;
                }

                if (now != scale)
                {
                    outcome.NotChanged.Add(label + " — Revit kept " + ViewPlacementRow.ScaleCaption(now));
                    continue;
                }

                (outcome.Requested.Contains(id) ? outcome.Changed : outcome.Along)
                    .Add(label + " — " + ViewPlacementRow.ScaleCaption(before) + " → " + ViewPlacementRow.ScaleCaption(scale));

                foreach (var viewport in placed)
                {
                    var title = ViewportTitle(doc, viewport);

                    try
                    {
                        if (outcome.Centres.TryGetValue(viewport.Id, out var centre))
                        {
                            var distance = Distance(viewport.GetBoxCenter(), centre);
                            if (distance > Tolerance)
                                outcome.Off.Add(title + " — " + Mm(distance).ToString("0", CultureInfo.CurrentCulture) + " mm from where it stood");
                        }

                        if (!areas.TryGetValue(viewport.SheetId, out var area))
                        {
                            titleblocks.TryGetValue(viewport.SheetId, out var titleblock);
                            areas[viewport.SheetId] = area = doc.GetElement(viewport.SheetId) is ViewSheet sheet ? Area(doc, sheet, titleblock, margins) : null;
                        }

                        if (area != null)
                        {
                            var overhang = Overhang(Outline(viewport), area);
                            if (overhang > 0.5)
                                outcome.Overhanging.Add(title + " — by " + overhang.ToString("0", CultureInfo.CurrentCulture) + " mm");
                        }
                    }
                    catch (Exception exception)
                    {
                        outcome.Unchecked.Add(title + " — " + LinkCatalog.Short(exception.Message));
                    }
                }
            }

            foreach (var pair in outcome.Watched)
            {
                if (!(doc.GetElement(pair.Key) is View view))
                    continue;

                var now = Safe(() => view.Scale, pair.Value);
                if (now != pair.Value)
                {
                    viewports.TryGetValue(pair.Key, out var placed);
                    outcome.Unasked.Add(Label(doc, view, placed ?? new List<Viewport>()) + " — " +
                                        ViewPlacementRow.ScaleCaption(pair.Value) + " → " + ViewPlacementRow.ScaleCaption(now));
                }
            }
        }

        /// <summary>The viewport's box and its title together, on the sheet, millimetres — left, bottom, right, top.</summary>
        private static double[] Outline(Viewport viewport)
        {
            var box = viewport.GetBoxOutline();
            var outline = new[] { box.MinimumPoint.X, box.MinimumPoint.Y, box.MaximumPoint.X, box.MaximumPoint.Y };

            try
            {
                var label = viewport.GetLabelOutline();
                if (label != null && !label.IsEmpty)
                {
                    outline[0] = Math.Min(outline[0], label.MinimumPoint.X);
                    outline[1] = Math.Min(outline[1], label.MinimumPoint.Y);
                    outline[2] = Math.Max(outline[2], label.MaximumPoint.X);
                    outline[3] = Math.Max(outline[3], label.MaximumPoint.Y);
                }
            }
            catch (Exception)
            {
                // No title to read — the box alone is everything the viewport draws.
            }

            return outline.Select(Mm).ToArray();
        }

        /// <summary>How far a rectangle sticks out of an area on its worst side; 0 or less when it is inside.</summary>
        private static double Overhang(double[] outline, double[] area)
        {
            return Math.Max(Math.Max(area[0] - outline[0], outline[2] - area[2]), Math.Max(area[1] - outline[1], outline[3] - area[3]));
        }

        /// <summary>"Floor plan: Level 1 (A101)" — the view and the sheets it stands on.</summary>
        private static string Label(Document doc, View view, List<Viewport> placed)
        {
            var numbers = placed
                .Select(viewport => doc.GetElement(viewport.SheetId) as ViewSheet)
                .Where(sheet => sheet != null)
                .Select(sheet => sheet.SheetNumber)
                .Distinct()
                .ToList();

            var where = numbers.Count == 0 ? "on no sheet" : string.Join(", ", numbers.Take(3)) + (numbers.Count > 3 ? " …" : string.Empty);
            return TextSiteCollector.ViewKind(view) + ": " + view.Name + " (" + where + ")";
        }

        private static string SheetTitle(Document doc, Viewport viewport)
        {
            return doc.GetElement(viewport.SheetId) is ViewSheet sheet ? sheet.SheetNumber + " - " + sheet.Name : "a sheet";
        }

        /// <summary>"A101 - Level 1: Level 1" — the sheet and the view.</summary>
        private static string ViewportTitle(Document doc, Viewport viewport)
        {
            var view = doc.GetElement(viewport.ViewId) as View;
            return SheetTitle(doc, viewport) + ": " + (view?.Name ?? "a view");
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(SheetScaleRequest request, Outcome outcome)
        {
            var scale = ViewPlacementRow.ScaleCaption(request.Scale);
            var changed = outcome.RolledBack.HasValue ? 0 : outcome.Changed.Count + outcome.Along.Count;
            string text;

            if (outcome.RolledBack.HasValue)
                text = "Revit rolled the whole change back (" + outcome.RolledBack.Value + ") — no scale was changed. The reason is usually " +
                       "in the Revit messages below; changing fewer views at a time shows which of them Revit objects to.";
            else if (changed == 0)
                text = "No scale was changed.";
            else
                text = "Scale changed to " + scale + " — " + Views(changed) + "." +
                       (outcome.Off.Count == 0 && outcome.Unchecked.Count == 0 ? " Every viewport stays where it stood." : string.Empty) +
                       (outcome.Changed.Count > 0 ? "\n• " + string.Join("\n• ", outcome.Changed.Take(20)) +
                                                    (outcome.Changed.Count > 20 ? "\n… and " + (outcome.Changed.Count - 20) + " more" : string.Empty)
                                                  : string.Empty);

            if (!outcome.RolledBack.HasValue)
            {
                Section(ref text, "Changed along with them — they share one scale with a checked view, or its view template's", outcome.Along);
                Section(ref text, "View templates that no longer set the scale — the views using them kept theirs", outcome.Released);
                Section(ref text, "View templates given " + scale + " — every view using them follows", outcome.Retemplated);
                Section(ref text, "Scale not changed", outcome.NotChanged);
                Section(ref text, "Changed although nobody checked it — they use a template that stopped setting the scale; check them", outcome.Unasked);
                Section(ref text, "Sticking out of the drawing area (the titleblock less the margins kept in \"Views to Sheets\") — check these sheets", outcome.Overhanging);
                Section(ref text, "Not where they stood — check these sheets", outcome.Off);
                Section(ref text, "Changed, but could not be checked", outcome.Unchecked);
            }

            if (outcome.Notes.Count > 0)
                text += "\n\n" + string.Join("\n", outcome.Notes);

            Section(ref text, "Could not be changed", outcome.Failures);

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

        private static void Section(ref string text, string heading, IReadOnlyList<string> lines)
        {
            if (lines.Count == 0)
                return;

            const int limit = 15;
            text += "\n\n" + heading + ":\n• " + string.Join("\n• ", lines.Take(limit));

            if (lines.Count > limit)
                text += "\n… and " + (lines.Count - limit) + " more";
        }

        private static string Views(int count)
        {
            return count == 1 ? "1 view" : count + " views";
        }

        // ───────────────────────────── geometry ─────────────────────────────

        private static double Distance(XYZ a, XYZ b)
        {
            return Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        }

        private static double Mm(double feet)
        {
            return feet * MmPerFoot;
        }

        private static T Safe<T>(Func<T> read, T fallback)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
