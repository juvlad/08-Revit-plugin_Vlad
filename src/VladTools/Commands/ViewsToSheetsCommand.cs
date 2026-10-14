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
    /// The "Views to Sheets" button: one new sheet per checked view, of the titleblock type chosen in
    /// the window, named after the view, with the view at the scale chosen and positioned in the
    /// titleblock's drawing area — or, "like a sample sheet", where a view the user placed by hand
    /// stands, for new sheets and for views already on sheets alike.
    ///
    /// Revit's own way is one view at a time: make a sheet, drag the view on, nudge it by eye. Here the
    /// geometry is computed rather than eyeballed — a viewport's box and title on the sheet
    /// (<see cref="Viewport.GetBoxOutline"/>, <see cref="Viewport.GetLabelOutline"/>), and for lining
    /// sheets up with each other, where the crop region lands on the sheet — measured, because Revit
    /// 2022 has no API from the model onto a sheet (see <see cref="MeasureCrops"/>).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ViewsToSheetsCommand : IExternalCommand
    {
        private const string DialogTitle = "Views to Sheets";

        private const double MmPerFoot = 304.8;

        /// <summary>Half a millimetre of paper, in feet: the tolerance of every check of where a viewport ended up.</summary>
        private const double Tolerance = 0.5 / MmPerFoot;

        /// <summary>Two millimetres of paper: how far a measured crop region may differ from its size by the crop box and the scale.</summary>
        private const double CropTolerance = 2 / MmPerFoot;

        /// <summary>
        /// The kinds of view a viewport can show, in the order the Project Browser lists their groups —
        /// as far as it can be seen: the API does not expose that order. Schedules are not here: they go
        /// on a sheet as a <c>ScheduleSheetInstance</c>, not a viewport, and a sheet of its own per
        /// schedule is not what this button is for.
        /// </summary>
        private static readonly ViewType[] Placeable =
        {
            ViewType.FloorPlan,
            ViewType.EngineeringPlan,
            ViewType.CeilingPlan,
            ViewType.ThreeD,
            ViewType.Elevation,
            ViewType.Section,
            ViewType.Detail,
            ViewType.Rendering,
            ViewType.DraftingView,
            ViewType.Walkthrough,
            ViewType.AreaPlan,
            ViewType.Legend
        };

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
                var titleblocks = Titleblocks(doc);
                var views = Collect(doc, out var organization, out var samples);

                if (titleblocks.Count == 0 && samples.Count == 0)
                {
                    TaskDialog.Show(DialogTitle,
                        "The project has no titleblock types.\n" +
                        "Load a titleblock family first (Insert → Load Family → Titleblocks) and press the button again.");
                    return Result.Cancelled;
                }

                if (views.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "The project has no views that could go on a sheet.");
                    return Result.Cancelled;
                }

                var taken = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Select(sheet => sheet.SheetNumber)
                    .ToList();

                var window = new ViewsToSheetsWindow(views, titleblocks, taken, samples, DefaultSample(uidoc), organization, ViewsToSheetsPreferences.Load());
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true || window.Request == null)
                    return Result.Cancelled;

                var outcome = Place(doc, window.Request);
                Report(window.Request, outcome);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── titleblocks ─────────────────────────────

        /// <summary>
        /// Every titleblock type with the frame it draws. The frame is measured, not read from the
        /// family's "Sheet Width"/"Sheet Height": those say nothing about where the titleblock sits
        /// relative to the sheet's origin, and they are whatever the family's author typed. So a sheet
        /// of every type is made in a transaction that is always rolled back, and each titleblock is
        /// asked for its extents on its sheet — what Revit actually draws. Nothing of it reaches the
        /// model or the undo list.
        /// </summary>
        private static List<TitleblockInfo> Titleblocks(Document doc)
        {
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>()
                .OrderBy(TitleblockName, ProjectBrowser.NaturalOrder)
                .ToList();

            var frames = new Dictionary<ElementId, BoundingBoxXYZ>();
            var problems = new Dictionary<ElementId, string>();

            if (symbols.Count == 0)
                return new List<TitleblockInfo>();

            using (var transaction = new Transaction(doc, "Measure titleblocks"))
            {
                var options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(new WarningSuppressor());
                options.SetClearAfterRollback(true);
                transaction.SetFailureHandlingOptions(options);

                try
                {
                    transaction.Start();
                }
                catch (Exception exception)
                {
                    // A document that cannot be edited cannot take new sheets either; the window says why.
                    var reason = "could not be measured (" + LinkCatalog.Short(exception.Message) + ")";
                    return symbols.Select(symbol => new TitleblockInfo(symbol.UniqueId, TitleblockName(symbol), 0, 0, 0, 0, reason)).ToList();
                }

                try
                {
                    var sheets = new Dictionary<ElementId, ViewSheet>();

                    foreach (var symbol in symbols)
                    {
                        try
                        {
                            if (!symbol.IsActive)
                                symbol.Activate();

                            sheets[symbol.Id] = ViewSheet.Create(doc, symbol.Id);
                        }
                        catch (Exception exception)
                        {
                            problems[symbol.Id] = "Revit would not make a sheet of it: " + LinkCatalog.Short(exception.Message);
                        }
                    }

                    doc.Regenerate();

                    foreach (var pair in sheets)
                    {
                        try
                        {
                            var box = TitleblockBox(doc, pair.Value, out _);
                            if (box == null)
                                problems[pair.Key] = "it draws nothing on the sheet";
                            else
                                frames[pair.Key] = box;
                        }
                        catch (Exception exception)
                        {
                            problems[pair.Key] = LinkCatalog.Short(exception.Message);
                        }
                    }
                }
                catch (Exception exception)
                {
                    foreach (var symbol in symbols.Where(symbol => !frames.ContainsKey(symbol.Id) && !problems.ContainsKey(symbol.Id)))
                        problems[symbol.Id] = LinkCatalog.Short(exception.Message);
                }
                finally
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                        transaction.RollBack();
                }
            }

            return symbols
                .Select(symbol => frames.TryGetValue(symbol.Id, out var box)
                    ? new TitleblockInfo(symbol.UniqueId, TitleblockName(symbol), Mm(box.Min.X), Mm(box.Min.Y), Mm(box.Max.X), Mm(box.Max.Y), null)
                    : new TitleblockInfo(symbol.UniqueId, TitleblockName(symbol), 0, 0, 0, 0, problems.TryGetValue(symbol.Id, out var problem) ? problem : "could not be measured"))
                .ToList();
        }

        /// <summary>The titleblock's extents on its sheet, in sheet feet, and its type's name; null when there is none or it draws nothing.</summary>
        private static BoundingBoxXYZ TitleblockBox(Document doc, ViewSheet sheet, out string name)
        {
            var titleblock = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement();

            name = doc.GetElement(titleblock?.GetTypeId() ?? ElementId.InvalidElementId) is FamilySymbol symbol ? TitleblockName(symbol) : null;

            var box = titleblock?.get_BoundingBox(sheet);
            return box != null && box.Max.X - box.Min.X > Tolerance && box.Max.Y - box.Min.Y > Tolerance ? box : null;
        }

        /// <summary>"Family : Type" — how Revit's own "Select a Titleblock" list writes it.</summary>
        private static string TitleblockName(FamilySymbol symbol)
        {
            return symbol.FamilyName + " : " + ProjectBrowser.Name(symbol);
        }

        // ───────────────────────────── views ─────────────────────────────

        /// <summary>A view on its way to the window, with what it is sorted by.</summary>
        private sealed class Entry
        {
            public Entry(PlaceableViewInfo info, int group, IReadOnlyList<string> folders, int rank, string typeGroup, string sortValue)
            {
                Info = info;
                Group = group;
                Folders = folders;
                Rank = rank;
                TypeGroup = typeGroup;
                SortValue = sortValue ?? string.Empty;
            }

            public PlaceableViewInfo Info { get; }

            /// <summary>0 — in the browser; 1 — hidden by its filter, listed after the rest.</summary>
            public int Group { get; }

            /// <summary>The organization's folders, without the view-type group.</summary>
            public IReadOnlyList<string> Folders { get; }

            public int Rank { get; }

            public string TypeGroup { get; }

            public string SortValue { get; }
        }

        /// <summary>
        /// Every view a viewport can show, in the folders and the order of the Project Browser: the
        /// organization's own folders (<see cref="BrowserOrganization.GetFolderItems"/>), and under them
        /// the view-type group the browser always adds itself ("Floor Plans", "Sections (Building
        /// Section)"). A view the organization's filter hides is still a view of the project — it gets a
        /// folder of its own at the end rather than being dropped, the same as in "Sheets". Also every
        /// viewport already on a sheet, as a possible sample.
        /// </summary>
        private static List<PlaceableViewInfo> Collect(Document doc, out string organizationName, out List<SampleViewportInfo> samples)
        {
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .ToDictionary(sheet => sheet.Id, sheet => sheet.SheetNumber + " - " + sheet.Name);

            var viewports = new Dictionary<ElementId, List<Viewport>>();
            foreach (var viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                if (!sheets.ContainsKey(viewport.SheetId))
                    continue;

                if (!viewports.TryGetValue(viewport.ViewId, out var list))
                    viewports[viewport.ViewId] = list = new List<Viewport>();

                list.Add(viewport);
            }

            BrowserOrganization organization = null;
            var sortingId = ElementId.InvalidElementId;
            var descending = false;

            try
            {
                organization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc);
                if (organization != null)
                {
                    sortingId = organization.SortingParameterId;
                    descending = organization.SortingOrder == SortingOrder.Descending;
                }
            }
            catch (Exception)
            {
                // No organization means the plain view-type groups — still the whole answer.
            }

            organizationName = ProjectBrowser.Name(organization);

            var templates = new Dictionary<ElementId, string>();
            var entries = new List<Entry>();

            foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                var rank = Array.IndexOf(Placeable, view.ViewType);
                if (view.IsTemplate || rank < 0)
                    continue;

                var hidden = !ProjectBrowser.Passes(organization, view.Id);
                var folders = hidden ? new[] { ProjectBrowser.FilteredFolder } : ProjectBrowser.Folders(organization, view.Id);
                var typeGroup = TypeGroup(doc, view);

                viewports.TryGetValue(view.Id, out var placed);

                var info = Snapshot(doc, view, folders.Concat(new[] { typeGroup }).ToList(), placed, sheets, templates);
                entries.Add(new Entry(info, hidden ? 1 : 0, folders, rank, typeGroup, ProjectBrowser.SortValue(view, sortingId, view.Name)));
            }

            entries.Sort((a, b) => Compare(a, b, descending));

            samples = Samples(doc, viewports, sheets);
            return entries.Select(entry => entry.Info).ToList();
        }

        private static int Compare(Entry a, Entry b, bool descending)
        {
            var result = a.Group.CompareTo(b.Group);
            if (result != 0)
                return result;

            for (var level = 0; level < Math.Min(a.Folders.Count, b.Folders.Count); level++)
            {
                result = ProjectBrowser.Natural(a.Folders[level], b.Folders[level]);
                if (result != 0)
                    return result;
            }

            result = a.Folders.Count.CompareTo(b.Folders.Count);
            if (result != 0)
                return result;

            result = a.Rank.CompareTo(b.Rank);
            if (result != 0)
                return result;

            result = ProjectBrowser.Natural(a.TypeGroup, b.TypeGroup);
            if (result != 0)
                return result;

            result = ProjectBrowser.Natural(a.SortValue, b.SortValue);
            if (result != 0)
                return descending ? -result : result;

            return ProjectBrowser.Natural(a.Info.Name, b.Info.Name);
        }

        /// <summary>The browser's view-type group: "Floor Plans", "Sections (Building Section)", "Area Plans (Gross Building)"…</summary>
        private static string TypeGroup(Document doc, View view)
        {
            string TypeName()
            {
                var name = ProjectBrowser.Name(doc.GetElement(view.GetTypeId()));
                return name.Length == 0 ? string.Empty : " (" + name + ")";
            }

            switch (view.ViewType)
            {
                case ViewType.FloorPlan: return "Floor Plans";
                case ViewType.EngineeringPlan: return "Structural Plans";
                case ViewType.CeilingPlan: return "Ceiling Plans";
                case ViewType.ThreeD: return "3D Views";
                case ViewType.Elevation: return "Elevations" + TypeName();
                case ViewType.Section: return "Sections" + TypeName();
                case ViewType.Detail: return "Detail Views" + TypeName();
                case ViewType.Rendering: return "Renderings";
                case ViewType.DraftingView: return "Drafting Views" + TypeName();
                case ViewType.Walkthrough: return "Walkthroughs";
                case ViewType.Legend: return "Legends";
                case ViewType.AreaPlan:
                    var scheme = ProjectBrowser.Name((view as ViewPlan)?.AreaScheme);
                    return "Area Plans" + (scheme.Length == 0 ? string.Empty : " (" + scheme + ")");
                default:
                    return TextSiteCollector.ViewKind(view);
            }
        }

        /// <summary>
        /// One view for the window. The size on paper is the crop region over the scale — free to read,
        /// and close: what the annotation adds beyond the crop is only known once the view is on a sheet,
        /// which the report checks. A drafting view or a legend has no crop region, but it is small and
        /// cheap to measure whole (<see cref="View.Outline"/> — paper feet, all zeros for an empty view).
        /// <see cref="View.Outline"/> is not asked of a model view: on a large model that would draw
        /// every view of the list just to open the window.
        /// </summary>
        private static PlaceableViewInfo Snapshot(
            Document doc,
            View view,
            IReadOnlyList<string> folders,
            List<Viewport> viewports,
            Dictionary<ElementId, string> sheets,
            Dictionary<ElementId, string> templates)
        {
            var kind = TextSiteCollector.ViewKind(view);
            var isLegend = view.ViewType == ViewType.Legend;
            var noModel = isLegend || view.ViewType == ViewType.DraftingView;
            var perspective = IsPerspective(view);
            var cropped = !noModel && Safe(() => view.CropBoxActive, false);
            var scale = perspective ? 0 : Safe(() => view.Scale, 0);

            string placed = null;
            string viewportId = null;
            if (viewports != null && viewports.Count > 0)
            {
                placed = sheets[viewports[0].SheetId] + (viewports.Count > 1 ? " and " + (viewports.Count - 1) + " more" : string.Empty);

                // A legend may stand on many sheets, and which of its viewports to move would be a guess.
                if (!isLegend && viewports.Count == 1)
                    viewportId = viewports[0].UniqueId;
            }

            double width = 0, height = 0;
            string problem = null;

            if (cropped && scale > 0)
            {
                var box = Safe(() => view.CropBox, null);
                if (box != null)
                {
                    width = Mm((box.Max.X - box.Min.X) / scale);
                    height = Mm((box.Max.Y - box.Min.Y) / scale);
                }
            }
            else if (noModel)
            {
                // An outline that could not be read is "size unknown", not "empty": only Revit's own zeros
                // may take the check box away.
                var outline = Safe(() => view.Outline, null);
                if (outline != null)
                {
                    width = Mm(outline.Max.U - outline.Min.U);
                    height = Mm(outline.Max.V - outline.Min.V);

                    if (width <= 0 || height <= 0)
                        problem = "Empty — Revit places only a view that shows something";
                }
            }

            var primary = Safe(() => view.GetPrimaryViewId(), ElementId.InvalidElementId);
            var isDependent = primary != null && primary != ElementId.InvalidElementId;
            string scaleGroup = null;

            if (isDependent)
                scaleGroup = doc.GetElement(primary)?.UniqueId;
            else if (Safe(() => view.GetDependentViewIds().Count, 0) > 0)
                scaleGroup = view.UniqueId;

            var alignGroup = AlignGroup(view, out var notAlignable);

            return new PlaceableViewInfo(
                view.UniqueId,
                view.Name,
                kind,
                folders,
                placed,
                viewportId,
                isLegend,
                problem,
                scale,
                scale > 0 ? ScaleLock(doc, view, templates) : null,
                scaleGroup,
                isDependent,
                width,
                height,
                alignGroup,
                notAlignable);
        }

        private static bool IsPerspective(View view)
        {
            return view is View3D view3D && Safe(() => view3D.IsPerspective, false);
        }

        /// <summary>
        /// The key of the views this one can be lined up with through the model — the view and up
        /// directions, rounded — or null, with the reason, when it cannot be: nothing to line up without a
        /// crop region, and a split crop region moves on the sheet one region at a time.
        /// </summary>
        private static string AlignGroup(View view, out string notAlignable)
        {
            notAlignable = null;

            if (view.ViewType == ViewType.Legend || view.ViewType == ViewType.DraftingView)
                notAlignable = "a " + TextSiteCollector.ViewKind(view).ToLowerInvariant() + " has no crop region";
            else if (IsPerspective(view))
                notAlignable = "a perspective view";
            else if (!Safe(() => view.CropBoxActive, false))
                notAlignable = "its crop region is off";
            else if (Safe(() => view.GetCropRegionShapeManager().Split, false))
                notAlignable = "its crop region is split";
            else
                return Key(view.ViewDirection) + "|" + Key(view.UpDirection);

            return null;
        }

        /// <summary>A direction as text, rounded so two plans of one building read the same; "-0" is folded into "0".</summary>
        private static string Key(XYZ direction)
        {
            string Part(double value)
            {
                return (Math.Round(value, 4) + 0.0).ToString("0.0000", CultureInfo.InvariantCulture);
            }

            return Part(direction.X) + "," + Part(direction.Y) + "," + Part(direction.Z);
        }

        /// <summary>
        /// Why the view's scale cannot be changed here, or null. A view template that controls the scale
        /// decides it: <c>View.Scale</c> would throw, and the right place to change it is the template.
        /// A template controls a parameter when it is in its parameter list and not in its
        /// "not controlled" list. Templates are shared by hundreds of views, so each is asked once.
        /// </summary>
        private static string ScaleLock(Document doc, View view, Dictionary<ElementId, string> templates)
        {
            var templateId = Safe(() => view.ViewTemplateId, ElementId.InvalidElementId);
            if (templateId == null || templateId == ElementId.InvalidElementId)
                return null;

            if (templates.TryGetValue(templateId, out var reason))
                return reason;

            // Not known (null) counts as free: the attempt to change it will tell.
            reason = doc.GetElement(templateId) is View template && ViewScale.Controls(template) == true
                ? "its view template \"" + template.Name + "\" sets the scale (\"Sheet Scale\" can change it)"
                : null;

            templates[templateId] = reason;
            return reason;
        }

        /// <summary>Every viewport on a sheet, in sheet-number order — what "Like a sample sheet" can repeat.</summary>
        private static List<SampleViewportInfo> Samples(Document doc, Dictionary<ElementId, List<Viewport>> viewports, Dictionary<ElementId, string> sheets)
        {
            var titleblocks = new Dictionary<ElementId, string>();
            foreach (var titleblock in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
            {
                if (!titleblocks.ContainsKey(titleblock.OwnerViewId) && doc.GetElement(titleblock.GetTypeId()) is FamilySymbol symbol)
                    titleblocks[titleblock.OwnerViewId] = TitleblockName(symbol);
            }

            var samples = new List<SampleViewportInfo>();

            foreach (var viewport in viewports.Values.SelectMany(list => list))
            {
                if (!(doc.GetElement(viewport.ViewId) is View view))
                    continue;

                var box = Safe(() => viewport.GetBoxOutline(), null);
                titleblocks.TryGetValue(viewport.SheetId, out var titleblock);

                samples.Add(new SampleViewportInfo(
                    viewport.UniqueId,
                    view.UniqueId,
                    sheets[viewport.SheetId],
                    TextSiteCollector.ViewKind(view) + ": " + view.Name,
                    titleblock,
                    AlignGroup(view, out _),
                    box == null ? 0 : Mm(box.MinimumPoint.X),
                    box == null ? 0 : Mm(box.MinimumPoint.Y),
                    box == null ? 0 : Mm(box.MaximumPoint.X),
                    box == null ? 0 : Mm(box.MaximumPoint.Y)));
            }

            return samples
                .OrderBy(sample => sample.SheetTitle, ProjectBrowser.NaturalOrder)
                .ThenBy(sample => sample.ViewCaption, ProjectBrowser.NaturalOrder)
                .ToList();
        }

        /// <summary>
        /// The viewport to offer as the sample: the one selected in Revit, or else the one on the sheet
        /// that is open — a model view before a legend, since a legend is rarely what is being repeated.
        /// That is where the user is standing right after moving a view by hand.
        /// </summary>
        private static string DefaultSample(UIDocument uidoc)
        {
            var doc = uidoc.Document;

            try
            {
                var selected = uidoc.Selection.GetElementIds().Select(doc.GetElement).OfType<Viewport>().ToList();
                if (selected.Count == 1)
                    return selected[0].UniqueId;

                if (uidoc.ActiveView is ViewSheet sheet)
                {
                    return sheet.GetAllViewports()
                        .Select(doc.GetElement)
                        .OfType<Viewport>()
                        .OrderBy(viewport => (doc.GetElement(viewport.ViewId) as View)?.ViewType == ViewType.Legend ? 1 : 0)
                        .Select(viewport => viewport.UniqueId)
                        .FirstOrDefault();
                }
            }
            catch (Exception)
            {
                // No guess is no harm: the window lets the sample be chosen from the list.
            }

            return null;
        }

        // ───────────────────────────── placing ─────────────────────────────

        /// <summary>What point of a view is put on its goal: the whole outline's middle, the box's middle, the crop region's middle, or a model point.</summary>
        private enum Aim
        {
            Outline,
            Box,
            Crop,
            ModelPoint
        }

        /// <summary>One view on its sheet — new or its own — and what became of it.</summary>
        private sealed class Placement
        {
            public Placement(ViewPlacementItem item, View view, ViewSheet sheet, Viewport viewport, bool isMove)
            {
                Item = item;
                View = view;
                Sheet = sheet;
                Viewport = viewport;
                IsMove = isMove;
            }

            /// <summary>Null for the sample, which is measured but never moved.</summary>
            public ViewPlacementItem Item { get; }

            public View View { get; }
            public ViewSheet Sheet { get; }
            public Viewport Viewport { get; }

            /// <summary>The view stood on this sheet already and is moved there, not placed.</summary>
            public bool IsMove { get; }

            /// <summary>The drawing area of the sheet, in sheet feet; null when the sheet has no titleblock to cut it from.</summary>
            public Extents Area { get; set; }

            public Aim Aim { get; set; }

            /// <summary>The model point put on the goal, when <see cref="Aim"/> is <see cref="Aim.ModelPoint"/>.</summary>
            public XYZ AimPoint { get; set; }

            /// <summary>Where on the sheet the aim is to land.</summary>
            public XYZ Goal { get; set; }

            /// <summary>Where the viewport's box centre was asked to go; null when positioning failed.</summary>
            public XYZ Expected { get; set; }

            /// <summary>Why the view was positioned another way than the one chosen; null when it was not.</summary>
            public string Fallback { get; set; }

            /// <summary>Why the crop region could not be measured; null when it was or was not asked for.</summary>
            public string CropProblem { get; set; }

            /// <summary>Why the new scale did not take; null when it did or none was asked for.</summary>
            public string ScaleProblem { get; set; }

            public string Title => Item?.SheetTitle ?? Sheet.SheetNumber + " - " + Sheet.Name;
        }

        /// <summary>The sample, as far as positioning needs it: its box's middle, and the model point it shows where.</summary>
        private sealed class SampleAim
        {
            public string AlignGroup { get; set; }

            public XYZ BoxCenter { get; set; }

            /// <summary>The middle of the sample's crop region, in the model; null when the sample cannot be lined up with through the model.</summary>
            public XYZ Point { get; set; }

            /// <summary>Where that point stands on the sample's sheet.</summary>
            public XYZ PointOnSheet { get; set; }
        }

        /// <summary>Everything one run found out.</summary>
        private sealed class Outcome
        {
            public WarningSuppressor Warnings { get; } = new WarningSuppressor();

            public List<Placement> Placed { get; } = new List<Placement>();

            public List<string> Failures { get; } = new List<string>();

            /// <summary>On a sheet, but not positioned — the reason.</summary>
            public List<string> NotCentred { get; } = new List<string>();

            /// <summary>Read back somewhere else than asked.</summary>
            public List<string> Off { get; } = new List<string>();

            /// <summary>Read back and sticking out of the drawing area.</summary>
            public List<string> Overhanging { get; } = new List<string>();

            /// <summary>Could not be read back at all — kept apart from "where it was asked".</summary>
            public List<string> Unchecked { get; } = new List<string>();

            public List<string> Notes { get; } = new List<string>();

            public TransactionStatus? RolledBack { get; set; }
        }

        /// <summary>
        /// Makes the sheets, sets the scales and positions the views, in one transaction, in passes:
        /// every sheet and viewport first, at the middle of the drawing area; one regeneration — before
        /// it a new viewport has no outline to measure; the crop regions measured, all at once, when the
        /// positioning needs them; then every viewport moved by what was measured. Measuring right after
        /// each <c>Viewport.Create</c> would cost a regeneration per view, which on two hundred views is
        /// minutes, not seconds. After the commit every viewport and every scale is read back.
        /// </summary>
        private static Outcome Place(Document doc, ViewPlacementRequest request)
        {
            var outcome = new Outcome();
            var creates = request.Items.Any(item => !item.IsMove);

            FamilySymbol titleblock = null;
            if (creates && (request.Titleblock == null || (titleblock = doc.GetElement(request.Titleblock.UniqueId) as FamilySymbol) == null))
            {
                outcome.Failures.Add("The titleblock type \"" + request.Titleblock?.Name + "\" is no longer in the project — nothing was placed.");
                return outcome;
            }

            Placement sample = null;
            if (request.Centering == ViewCentering.Sample)
            {
                var viewport = request.Sample == null ? null : doc.GetElement(request.Sample.ViewportUniqueId) as Viewport;
                var view = viewport == null ? null : doc.GetElement(viewport.ViewId) as View;
                var sheet = viewport == null ? null : doc.GetElement(viewport.SheetId) as ViewSheet;

                if (view == null || sheet == null)
                {
                    outcome.Failures.Add("The sample viewport is no longer on its sheet — nothing was placed.");
                    return outcome;
                }

                sample = new Placement(null, view, sheet, viewport, false);
            }

            var items = Borrow(doc, request.Items, outcome);
            if (items.Count == 0)
                return outcome;

            var area = creates ? new Extents(Feet(request.AreaLeft), Feet(request.AreaBottom), Feet(request.AreaRight), Feet(request.AreaTop)) : null;
            var status = TransactionStatus.Uninitialized;

            using (var transaction = new Transaction(doc, UndoName(request, items)))
            {
                var options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(outcome.Warnings);
                transaction.SetFailureHandlingOptions(options);

                transaction.Start();

                if (titleblock != null && !titleblock.IsActive)
                    titleblock.Activate();

                foreach (var item in items)
                    Prepare(doc, titleblock, item, area?.Center, outcome);

                if (outcome.Placed.Count == 0)
                {
                    transaction.RollBack();
                    return outcome;
                }

                doc.Regenerate();

                var firstNew = outcome.Placed.FirstOrDefault(placed => !placed.IsMove);
                if (firstNew != null)
                    area = Remeasure(doc, request, firstNew.Sheet, area, outcome);

                foreach (var placed in outcome.Placed)
                    placed.Area = placed.IsMove ? SheetArea(doc, placed.Sheet, request) : area;

                var crops = MeasureCrops(doc, ToMeasure(request, outcome.Placed, sample));

                var references = request.Centering == ViewCentering.ModelPoint
                    ? References(outcome.Placed.Where(crops.ContainsKey))
                    : new Dictionary<string, XYZ>();

                var sampleAim = sample == null ? null : SampleAimOf(sample, request.Sample.AlignGroup, crops, outcome);

                foreach (var placed in outcome.Placed)
                    Centre(placed, request.Centering, crops, references, sampleAim, outcome);

                if (request.Centering != ViewCentering.Viewport)
                    CheckCrops(doc, outcome);

                status = transaction.Commit();
            }

            if (status == TransactionStatus.Committed)
                Verify(outcome);
            else
                outcome.RolledBack = status;

            return outcome;
        }

        private static string UndoName(ViewPlacementRequest request, IReadOnlyCollection<ViewPlacementItem> items)
        {
            if (items.All(item => item.IsMove))
                return "Position " + (items.Count == 1 ? "a view" : items.Count + " views") + " like " + request.Sample?.SheetTitle;

            return items.Count == 1 ? "Place a view on a sheet" : "Place " + items.Count + " views on sheets";
        }

        /// <summary>
        /// Checks out what the run edits before any of it is edited: every view (its scale, its place on
        /// a sheet) and every viewport to be moved. In a workshared model an element somebody else holds,
        /// or one changed in the central model since the last reload, fails the whole transaction at the
        /// commit rather than itself — so those views are taken out here and named, the same rule
        /// "Move to Workset" keeps. A checkout that throws (no central yet, the network) is a note, not a stop.
        /// </summary>
        private static List<ViewPlacementItem> Borrow(Document doc, IReadOnlyList<ViewPlacementItem> items, Outcome outcome)
        {
            if (!doc.IsWorkshared)
                return items.ToList();

            var ids = new Dictionary<ViewPlacementItem, List<ElementId>>();
            foreach (var item in items)
            {
                var list = new List<ElementId>();

                var view = doc.GetElement(item.View.UniqueId);
                if (view != null)
                    list.Add(view.Id);

                var viewport = item.IsMove ? doc.GetElement(item.View.ViewportUniqueId) : null;
                if (viewport != null)
                    list.Add(viewport.Id);

                ids[item] = list;
            }

            HashSet<ElementId> owned = null;

            try
            {
                owned = new HashSet<ElementId>(WorksharingUtils.CheckoutElements(doc, ids.Values.SelectMany(list => list).Distinct().ToList()));
            }
            catch (Exception exception)
            {
                outcome.Notes.Add("The views could not be checked out in one go (" + LinkCatalog.Short(exception.Message) +
                                  "). Whatever Revit lets through is still placed.");
            }

            var free = new List<ViewPlacementItem>();

            foreach (var item in items)
            {
                string reason = null;

                foreach (var id in ids[item])
                {
                    var updates = Safe(() => WorksharingUtils.GetModelUpdatesStatus(doc, id), ModelUpdatesStatus.CurrentWithCentral);
                    if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral)
                    {
                        reason = "changed in the central model since your last reload — Collaborate → Reload Latest, then press the button again";
                        break;
                    }

                    if (owned != null && owned.Contains(id))
                        continue;

                    var owner = string.Empty;
                    var status = CheckoutStatus.NotOwned;

                    try
                    {
                        status = WorksharingUtils.GetCheckoutStatus(doc, id, out owner);
                    }
                    catch (Exception)
                    {
                        // Nothing could be found out — let the edit itself decide.
                    }

                    if (status == CheckoutStatus.OwnedByOtherUser)
                    {
                        reason = "held by " + (string.IsNullOrEmpty(owner) ? "another user" : owner) + " — they have to synchronise with central and relinquish it first";
                        break;
                    }
                }

                if (reason == null)
                    free.Add(item);
                else
                    outcome.Failures.Add(item.View.Name + " — " + reason);
            }

            return free;
        }

        /// <summary>
        /// One view made ready: a new sheet, its number and name, and the view on it — or, for a move, the
        /// viewport it already stands in — and the new scale. The scale is set only once the view is on its
        /// sheet: a view Revit refuses to place must not be left with a changed scale. A view Revit will not
        /// place takes its sheet back with it: an empty sheet left behind would be one more thing to clean
        /// up by hand.
        /// </summary>
        private static void Prepare(Document doc, FamilySymbol titleblock, ViewPlacementItem item, XYZ centre, Outcome outcome)
        {
            if (!(doc.GetElement(item.View.UniqueId) is View view))
            {
                outcome.Failures.Add(item.View.Name + " — the view is no longer in the project");
                return;
            }

            Placement placement;

            if (item.IsMove)
            {
                var viewport = doc.GetElement(item.View.ViewportUniqueId) as Viewport;
                var sheet = viewport == null ? null : doc.GetElement(viewport.SheetId) as ViewSheet;

                if (sheet == null)
                {
                    outcome.Failures.Add(item.View.Name + " — it no longer stands on " + item.View.PlacedOn);
                    return;
                }

                placement = new Placement(item, view, sheet, viewport, true);
            }
            else
            {
                ViewSheet sheet = null;

                try
                {
                    sheet = ViewSheet.Create(doc, titleblock.Id);
                    sheet.SheetNumber = item.SheetNumber;
                    sheet.Name = item.SheetName;

                    if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                        throw new InvalidOperationException("Revit will not put it on a sheet — it already stands on one, or it shows nothing");

                    placement = new Placement(item, view, sheet, Viewport.Create(doc, sheet.Id, view.Id, centre), false);
                }
                catch (Exception exception)
                {
                    outcome.Failures.Add(item.View.Name + " — " + LinkCatalog.Short(exception.Message));

                    if (sheet != null)
                    {
                        try
                        {
                            doc.Delete(sheet.Id);
                        }
                        catch (Exception)
                        {
                            outcome.Notes.Add("The sheet " + item.SheetNumber + " made for \"" + item.View.Name + "\" stayed behind empty — delete it by hand.");
                        }
                    }

                    return;
                }
            }

            if (item.ChangesScale)
            {
                try
                {
                    view.Scale = item.Scale;
                }
                catch (Exception exception)
                {
                    placement.ScaleProblem = "kept " + ViewPlacementRow.ScaleCaption(view.Scale) + ": " + LinkCatalog.Short(exception.Message);
                }
            }

            outcome.Placed.Add(placement);
        }

        /// <summary>
        /// The titleblock measured again, on a sheet of this very run. It is the same type the window
        /// measured, so the frame should not have moved; if it has, the real one wins — the drawing area
        /// is cut out of what is on the sheet, not out of a memory of it.
        /// </summary>
        private static Extents Remeasure(Document doc, ViewPlacementRequest request, ViewSheet sheet, Extents area, Outcome outcome)
        {
            try
            {
                var box = TitleblockBox(doc, sheet, out _);
                if (box == null)
                    return area;

                var measured = new Extents(
                    box.Min.X + Feet(request.MarginLeft),
                    box.Min.Y + Feet(request.MarginBottom),
                    box.Max.X - Feet(request.MarginRight),
                    box.Max.Y - Feet(request.MarginTop));

                if (measured.Width <= 0 || measured.Height <= 0)
                    return area;

                if (Math.Abs(measured.MinX - area.MinX) > Tolerance || Math.Abs(measured.MinY - area.MinY) > Tolerance ||
                    Math.Abs(measured.MaxX - area.MaxX) > Tolerance || Math.Abs(measured.MaxY - area.MaxY) > Tolerance)
                    outcome.Notes.Add("The titleblock measured differently on the new sheets than when the window opened; the margins were applied to what is on the sheets.");

                return measured;
            }
            catch (Exception)
            {
                return area;
            }
        }

        /// <summary>
        /// The drawing area of a sheet that already exists: its own titleblock's frame and the margins
        /// remembered for that titleblock type. Only what a moved view is checked against afterwards —
        /// a move puts the view where the sample's is, not in the middle of this area.
        /// </summary>
        private static Extents SheetArea(Document doc, ViewSheet sheet, ViewPlacementRequest request)
        {
            try
            {
                var box = TitleblockBox(doc, sheet, out var name);
                if (box == null)
                    return null;

                var margins = request.MarginsFor(name);
                var area = new Extents(box.Min.X + Feet(margins[0]), box.Min.Y + Feet(margins[3]), box.Max.X - Feet(margins[2]), box.Max.Y - Feet(margins[1]));
                return area.Width > 0 && area.Height > 0 ? area : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Which crop regions the positioning needs measured: none for the whole viewport; every view that
        /// has one for the crop region and the model point; and "like a sample", the sample and the views
        /// that line up with it through the model — the others go by their box, which needs no measuring.
        /// </summary>
        private static List<Placement> ToMeasure(ViewPlacementRequest request, IReadOnlyList<Placement> placed, Placement sample)
        {
            switch (request.Centering)
            {
                case ViewCentering.CropRegion:
                case ViewCentering.ModelPoint:
                    return placed.Where(item => item.Item.View.AlignGroup != null).ToList();

                case ViewCentering.Sample:
                    var group = request.Sample.AlignGroup;
                    if (group == null)
                        return new List<Placement>();

                    return placed.Where(item => item.Item.View.AlignGroup == group).Concat(new[] { sample }).ToList();

                default:
                    return new List<Placement>();
            }
        }

        /// <summary>
        /// The sample as positioning needs it. Its box's middle, always; and when it can be lined up with
        /// through the model, the middle of its crop region in the model and where that point stands on
        /// the sample's sheet — the measured crop region's middle, by construction.
        /// </summary>
        private static SampleAim SampleAimOf(Placement sample, string alignGroup, Dictionary<Placement, Extents> crops, Outcome outcome)
        {
            var box = sample.Viewport.GetBoxCenter();
            var aim = new SampleAim { AlignGroup = alignGroup, BoxCenter = new XYZ(box.X, box.Y, 0) };

            if (alignGroup == null)
                return aim;

            if (!crops.TryGetValue(sample, out var crop))
            {
                outcome.Notes.Add("The sample's crop region could not be measured (" + (sample.CropProblem ?? "no reason given") +
                                  ") — every view was put by the middle of its viewport instead.");
                return aim;
            }

            var cropBox = sample.View.CropBox;
            aim.Point = cropBox.Transform.OfPoint(new XYZ((cropBox.Min.X + cropBox.Max.X) / 2, (cropBox.Min.Y + cropBox.Max.Y) / 2, 0));
            aim.PointOnSheet = crop.Center;
            return aim;
        }

        /// <summary>
        /// Moves one viewport so that its aim lands on its goal. A pinned viewport — one already on a
        /// sheet often is — has its pin taken off for the move and put back straight after, the same as
        /// "Accept Changes" does with a pinned grid.
        /// </summary>
        private static void Centre(
            Placement placed,
            ViewCentering centering,
            Dictionary<Placement, Extents> crops,
            Dictionary<string, XYZ> references,
            SampleAim sample,
            Outcome outcome)
        {
            try
            {
                if (!ChooseAim(placed, centering, crops, references, sample))
                {
                    outcome.NotCentred.Add(placed.Title + " — the sheet has no titleblock to find the middle of");
                    return;
                }

                var viewport = placed.Viewport;
                var target = viewport.GetBoxCenter() + (placed.Goal - AimOnSheet(placed, crops));

                var pinned = viewport.Pinned;
                if (pinned)
                    viewport.Pinned = false;

                try
                {
                    viewport.SetBoxCenter(target);
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
                            outcome.Notes.Add(placed.Title + ": the viewport was moved but could not be pinned again.");
                        }
                    }
                }

                placed.Expected = target;
            }
            catch (Exception exception)
            {
                outcome.NotCentred.Add(placed.Title + " — " + LinkCatalog.Short(exception.Message));
            }
        }

        /// <summary>
        /// Decides what point of the view goes where, by the positioning chosen. A view that cannot be
        /// positioned that way — no crop region, a perspective, a crop that could not be measured — falls
        /// back to its viewport, and says why in <see cref="Placement.Fallback"/>; it is never left where
        /// <c>Viewport.Create</c> dropped it. False when there is no drawing area to centre in.
        /// </summary>
        private static bool ChooseAim(
            Placement placed,
            ViewCentering centering,
            Dictionary<Placement, Extents> crops,
            Dictionary<string, XYZ> references,
            SampleAim sample)
        {
            var info = placed.Item.View;
            var hasCrop = crops.ContainsKey(placed);

            if (centering == ViewCentering.Sample)
            {
                if (sample.Point != null && info.AlignGroup == sample.AlignGroup && hasCrop)
                {
                    placed.Aim = Aim.ModelPoint;
                    placed.AimPoint = sample.Point;
                    placed.Goal = sample.PointOnSheet;
                    return true;
                }

                if (sample.Point != null)
                    placed.Fallback = info.AlignGroup == null ? info.NotAlignable
                        : info.AlignGroup != sample.AlignGroup ? "it looks another way than the sample"
                        : placed.CropProblem ?? "its crop region could not be measured";

                placed.Aim = Aim.Box;
                placed.Goal = sample.BoxCenter;
                return true;
            }

            if (placed.Area == null)
                return false;

            placed.Goal = placed.Area.Center;

            if (centering == ViewCentering.Viewport)
            {
                placed.Aim = Aim.Outline;
                return true;
            }

            if (!hasCrop)
            {
                placed.Fallback = info.AlignGroup == null ? info.NotAlignable : placed.CropProblem ?? "its crop region could not be measured";
                placed.Aim = Aim.Outline;
                return true;
            }

            if (centering == ViewCentering.ModelPoint)
            {
                if (references.TryGetValue(info.AlignGroup, out var point))
                {
                    placed.Aim = Aim.ModelPoint;
                    placed.AimPoint = point;
                    return true;
                }

                placed.Fallback = "the model point could not be worked out — centred by the crop region instead";
            }

            placed.Aim = Aim.Crop;
            return true;
        }

        /// <summary>Where the view's aim stands on its sheet right now.</summary>
        private static XYZ AimOnSheet(Placement placed, Dictionary<Placement, Extents> crops)
        {
            switch (placed.Aim)
            {
                case Aim.Box:
                    var box = placed.Viewport.GetBoxCenter();
                    return new XYZ(box.X, box.Y, 0);
                case Aim.Crop:
                    return crops[placed].Center;
                case Aim.ModelPoint:
                    return ModelToSheet(placed.View, crops[placed], placed.AimPoint);
                default:
                    return Outline(placed.Viewport).Center;
            }
        }

        /// <summary>The viewport's box and its title together, on the sheet — everything the viewport draws.</summary>
        private static Extents Outline(Viewport viewport)
        {
            var extents = Extents.Of(viewport.GetBoxOutline());

            try
            {
                var label = viewport.GetLabelOutline();
                if (label != null && !label.IsEmpty)
                    extents = extents.Union(Extents.Of(label));
            }
            catch (Exception)
            {
                // No title to read — the box alone is everything the viewport draws.
            }

            return extents;
        }

        /// <summary>
        /// Where every given view's crop region lands on its sheet.
        ///
        /// **Revit 2022 has no way from the model onto a sheet.** <c>View.GetModelToProjectionTransforms</c>
        /// and <c>Viewport.GetProjectionToSheetTransform</c> arrived in 2024 — RevitAPI.xml 2022 documents
        /// them, but RevitAPI.dll 2022 does not have them (confirmed by reflection). So the crop region is
        /// made to *be* the viewport's box for a moment: in a sub-transaction that is always rolled back,
        /// the view template comes off, the crop boundary is shown and the annotation categories are
        /// hidden. What is left on the sheet is the model clipped by the crop and the crop boundary drawn
        /// round it, so the box Revit reports is the crop region. One regeneration for every view, every
        /// box read, roll back. The viewport is not moved meanwhile, and the drawing does not move on the
        /// sheet when what the view shows changes — the rectangle read is where the crop region stays.
        ///
        /// The box is checked against the crop region's size on paper: something else still sticking out
        /// (an element the crop does not clip) leaves the view out with the reason, rather than centring
        /// it on a rectangle that is not its crop region. A split crop never gets this far — each of its
        /// regions moves on the sheet by itself, and the crop box says nothing about where.
        /// </summary>
        private static Dictionary<Placement, Extents> MeasureCrops(Document doc, IReadOnlyList<Placement> placed)
        {
            var crops = new Dictionary<Placement, Extents>();
            if (placed.Count == 0)
                return crops;

            using (var sub = new SubTransaction(doc))
            {
                sub.Start();

                try
                {
                    var exposed = new List<Placement>();

                    foreach (var item in placed)
                    {
                        try
                        {
                            var view = item.View;
                            if (view.ViewTemplateId != ElementId.InvalidElementId)
                                view.ViewTemplateId = ElementId.InvalidElementId;

                            view.CropBoxVisible = true;
                            view.AreAnnotationCategoriesHidden = true;
                            exposed.Add(item);
                        }
                        catch (Exception exception)
                        {
                            item.CropProblem = "its crop region could not be shown for measuring (" + LinkCatalog.Short(exception.Message) + ")";
                        }
                    }

                    doc.Regenerate();

                    foreach (var item in exposed)
                    {
                        try
                        {
                            var box = Extents.Of(item.Viewport.GetBoxOutline());
                            var paper = PaperCrop(item.View);

                            if (paper == null)
                                item.CropProblem = "its crop region could not be read";
                            else if (Math.Abs(box.Width - paper.Width) > CropTolerance || Math.Abs(box.Height - paper.Height) > CropTolerance)
                                item.CropProblem = "something besides the crop region stays on the sheet (" +
                                                   TitleblockInfo.Millimetres(Mm(box.Width), Mm(box.Height)) + " against a crop region of " +
                                                   TitleblockInfo.Millimetres(Mm(paper.Width), Mm(paper.Height)) + ")";
                            else
                                crops[item] = box;
                        }
                        catch (Exception exception)
                        {
                            item.CropProblem = "its crop region could not be measured (" + LinkCatalog.Short(exception.Message) + ")";
                        }
                    }
                }
                finally
                {
                    if (sub.GetStatus() == TransactionStatus.Started)
                        sub.RollBack();
                }
            }

            // The rolled-back settings are back on the views; their boxes are drawn again before anything else is measured.
            doc.Regenerate();
            return crops;
        }

        /// <summary>
        /// The crop regions measured again after the move, the same way. Moving a viewport by its box
        /// centre is exact, but what the user asked for is the crop region — or the model point — on its
        /// goal, and that rests on the measurement above; so it is measured, not assumed. Done before the
        /// commit because the measuring needs a sub-transaction, and after the commit there is no
        /// transaction left to hold one.
        /// </summary>
        private static void CheckCrops(Document doc, Outcome outcome)
        {
            var moved = outcome.Placed
                .Where(item => item.Expected != null && (item.Aim == Aim.Crop || item.Aim == Aim.ModelPoint))
                .ToList();

            if (moved.Count == 0)
                return;

            var crops = MeasureCrops(doc, moved);

            foreach (var item in moved)
            {
                if (!crops.TryGetValue(item, out var crop))
                {
                    outcome.Unchecked.Add(item.Title + " — " + (item.CropProblem ?? "its crop region could not be measured again"));
                    continue;
                }

                var actual = item.Aim == Aim.ModelPoint ? ModelToSheet(item.View, crop, item.AimPoint) : crop.Center;
                var distance = Distance(actual, item.Goal);

                if (distance > CropTolerance / 2)
                    outcome.Off.Add(item.Title + " — " + (item.Aim == Aim.ModelPoint ? "the model point" : "the crop region") + " is " +
                                    Mm(distance).ToString("0", CultureInfo.CurrentCulture) + " mm from where it was to go");
            }
        }

        private static double Distance(XYZ a, XYZ b)
        {
            return Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        }

        /// <summary>The crop box in the view's own frame, shrunk to paper feet by the scale; null when the view has none.</summary>
        private static Extents PaperCrop(View view)
        {
            var box = view.CropBox;
            var scale = view.Scale;
            if (box == null || scale <= 0)
                return null;

            return new Extents(box.Min.X / scale, box.Min.Y / scale, box.Max.X / scale, box.Max.Y / scale);
        }

        /// <summary>
        /// Where a model point lands on the sheet through this view: the crop box's frame says where the
        /// point is relative to the middle of the crop region, the scale shrinks that to paper, and the
        /// measured crop region says where that middle is on the sheet. A new viewport is never rotated,
        /// so the view's right and up are the sheet's.
        /// </summary>
        private static XYZ ModelToSheet(View view, Extents crop, XYZ point)
        {
            var box = view.CropBox;
            var scale = view.Scale;

            var local = box.Transform.Inverse.OfPoint(point);
            var middleX = (box.Min.X + box.Max.X) / 2;
            var middleY = (box.Min.Y + box.Max.Y) / 2;

            return crop.Center + new XYZ((local.X - middleX) / scale, (local.Y - middleY) / scale, 0);
        }

        /// <summary>
        /// The model point every group of views looking the same way is lined up on: the middle of all
        /// their crop regions together, measured along the views' right and up directions. The depth
        /// along the view direction does not matter — every point on that line lands on the same spot.
        /// The middle of them all rather than of the first view's: the set as a whole should sit in the
        /// middle, not whichever view happened to come first in the list.
        /// </summary>
        private static Dictionary<string, XYZ> References(IEnumerable<Placement> placed)
        {
            var references = new Dictionary<string, XYZ>();

            foreach (var group in placed.Where(item => item.Item.View.AlignGroup != null).GroupBy(item => item.Item.View.AlignGroup))
            {
                var frame = group.First().View.CropBox.Transform;
                var right = frame.BasisX;
                var up = frame.BasisY;

                Extents extents = null;

                foreach (var item in group)
                {
                    var box = item.View.CropBox;

                    var corners = new[]
                    {
                        new XYZ(box.Min.X, box.Min.Y, 0),
                        new XYZ(box.Max.X, box.Min.Y, 0),
                        new XYZ(box.Min.X, box.Max.Y, 0),
                        new XYZ(box.Max.X, box.Max.Y, 0)
                    };

                    foreach (var corner in corners)
                    {
                        var model = box.Transform.OfPoint(corner);
                        var along = new XYZ(model.DotProduct(right), model.DotProduct(up), 0);
                        extents = extents == null ? Extents.At(along) : extents.Add(along);
                    }
                }

                if (extents != null)
                    references[group.Key] = right * extents.Center.X + up * extents.Center.Y;
            }

            return references;
        }

        /// <summary>
        /// Reads every viewport and every scale back after the commit: is the viewport where it was asked
        /// to go, does all it draws stay inside the drawing area, and did the view take its new scale.
        /// <c>SetBoxCenter</c> and <c>View.Scale</c> returning without an error are not a viewport in place
        /// and a view at that scale — the same rule "Accept Changes" keeps for a moved grid.
        /// </summary>
        private static void Verify(Outcome outcome)
        {
            foreach (var placed in outcome.Placed)
            {
                try
                {
                    if (!placed.Viewport.IsValidObject || !placed.Sheet.IsValidObject)
                    {
                        outcome.Unchecked.Add(placed.Title + " — the viewport is gone from the model");
                        continue;
                    }

                    if (placed.Expected != null)
                    {
                        var distance = Distance(placed.Viewport.GetBoxCenter(), placed.Expected);
                        if (distance > Tolerance)
                            outcome.Off.Add(placed.Title + " — " + Mm(distance).ToString("0", CultureInfo.CurrentCulture) + " mm from where it was asked to go");
                    }

                    if (placed.Area != null)
                    {
                        var overhang = Outline(placed.Viewport).Overhang(placed.Area);
                        if (overhang > Tolerance)
                            outcome.Overhanging.Add(placed.Title + " — by " + Mm(overhang).ToString("0", CultureInfo.CurrentCulture) + " mm");
                    }

                    if (placed.Item.ChangesScale && placed.ScaleProblem == null && placed.View.Scale != placed.Item.Scale)
                        placed.ScaleProblem = "Revit kept " + ViewPlacementRow.ScaleCaption(placed.View.Scale);
                }
                catch (Exception exception)
                {
                    outcome.Unchecked.Add(placed.Title + " — " + LinkCatalog.Short(exception.Message));
                }
            }
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(ViewPlacementRequest request, Outcome outcome)
        {
            var placed = outcome.RolledBack.HasValue ? new List<Placement>() : outcome.Placed;
            var created = placed.Count(item => !item.IsMove);
            var moved = placed.Count - created;

            string text;

            if (placed.Count == 0)
            {
                text = "Nothing was placed.";
            }
            else
            {
                var parts = new List<string>();
                if (created > 0)
                    parts.Add((created == 1 ? "1 view placed on a new sheet" : created + " views placed on new sheets") + " of \"" + request.Titleblock?.Name + "\"");
                if (moved > 0)
                    parts.Add((moved == 1 ? "1 view" : moved + " views") + " moved on " + (moved == 1 ? "its sheet" : "their sheets"));

                var how = request.Centering == ViewCentering.Sample
                    ? "like " + request.Sample?.SheetTitle
                    : "centred by " + Lower(ViewCenteringText.Caption(request.Centering));

                text = string.Join(", ", parts) + ", " + how + ":\n• " +
                       string.Join("\n• ", placed.Take(20).Select(Line)) +
                       (placed.Count > 20 ? "\n… and " + (placed.Count - 20) + " more" : string.Empty);
            }

            if (outcome.RolledBack.HasValue)
            {
                text += "\n\nRevit rolled the whole change back (" + outcome.RolledBack.Value + ") — nothing was made or moved. The reason is " +
                        "usually in the Revit messages below; placing fewer views at a time shows which of them Revit objects to.";
            }
            else
            {
                Section(ref text, "Sticking out of the drawing area — the view is larger than it, check these sheets", outcome.Overhanging);

                var fallbacks = placed.Where(item => item.Fallback != null && item.Expected != null).Select(item => item.Title + " — " + item.Fallback).ToList();
                if (request.Centering != ViewCentering.Viewport)
                    Section(ref text, "Positioned by the viewport rather than \"" + ViewCenteringText.Caption(request.Centering) + "\"", fallbacks);

                Section(ref text, "Scale not changed", placed.Where(item => item.ScaleProblem != null).Select(item => item.Item.View.Name + " — " + item.ScaleProblem).ToList());
                Section(ref text, "On the sheet, but not positioned", outcome.NotCentred);
                Section(ref text, "Not where they were asked to go — check these sheets", outcome.Off);
                Section(ref text, "Placed, but could not be checked", outcome.Unchecked);
            }

            if (outcome.Notes.Count > 0)
                text += "\n\n" + string.Join("\n", outcome.Notes);

            Section(ref text, "Could not be placed", outcome.Failures);

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

        /// <summary>"A101 - Level 1, 1:50" — the sheet, and the new scale when there is one.</summary>
        private static string Line(Placement placed)
        {
            var line = placed.Title + (placed.IsMove ? " (moved)" : string.Empty);

            if (placed.Item.ChangesScale && placed.ScaleProblem == null)
                line += ", " + ViewPlacementRow.ScaleCaption(placed.Item.Scale);

            return line;
        }

        private static string Lower(string text)
        {
            return text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text.Substring(1);
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

        // ───────────────────────────── geometry ─────────────────────────────

        /// <summary>A rectangle on the sheet, in sheet feet.</summary>
        private sealed class Extents
        {
            public Extents(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX;
                MinY = minY;
                MaxX = maxX;
                MaxY = maxY;
            }

            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }

            public double Width => MaxX - MinX;
            public double Height => MaxY - MinY;

            public XYZ Center => new XYZ((MinX + MaxX) / 2, (MinY + MaxY) / 2, 0);

            public static Extents At(XYZ point)
            {
                return new Extents(point.X, point.Y, point.X, point.Y);
            }

            public static Extents Of(Outline outline)
            {
                return new Extents(outline.MinimumPoint.X, outline.MinimumPoint.Y, outline.MaximumPoint.X, outline.MaximumPoint.Y);
            }

            public Extents Add(XYZ point)
            {
                return new Extents(Math.Min(MinX, point.X), Math.Min(MinY, point.Y), Math.Max(MaxX, point.X), Math.Max(MaxY, point.Y));
            }

            public Extents Union(Extents other)
            {
                return new Extents(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));
            }

            /// <summary>How far this rectangle sticks out of <paramref name="area"/> on its worst side; 0 or less when it is inside.</summary>
            public double Overhang(Extents area)
            {
                return Math.Max(Math.Max(area.MinX - MinX, MaxX - area.MaxX), Math.Max(area.MinY - MinY, MaxY - area.MaxY));
            }
        }

        private static double Mm(double feet)
        {
            return feet * MmPerFoot;
        }

        private static double Feet(double millimetres)
        {
            return millimetres / MmPerFoot;
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
