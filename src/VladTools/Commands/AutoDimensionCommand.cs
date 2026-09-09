using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using VladTools.Infrastructure;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// Places dimension chains along the sides of the selected rooms — the counterpart of what
    /// third-party plugin demos call "Auto Dim Lines": the user places a few chains along one wall
    /// by hand once, and from then on the button repeats the same set along every side of every
    /// selected room.
    ///
    /// A sample is not copied by its references — a sample's references belong to specific walls
    /// and are meaningless in another room. Instead there is a fixed catalogue of chain kinds
    /// (<see cref="DimensionChainKind"/>), and a sample only suggests which kinds and which offset
    /// to use — this is a best guess (a heuristic), and the result can always be fixed in the window.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AutoDimensionCommand : IExternalCommand
    {
        private const string DialogTitle = "Auto Dimensions";

        /// <summary>
        /// How much longer the dimension line runs past the side itself, at each end. Not
        /// cosmetic: the end ticks of a chain now sit not at the end of the side but on the far
        /// face of the adjoining wall (see "capture the thickness of adjoining walls") — that is,
        /// outside the span. The margin has to clear any reasonable wall thickness, or the
        /// reference will fall outside the line.
        /// </summary>
        private const double LineMarginMm = 1000;

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
                    "The command works only in a project — it places dimensions along rooms.\n" +
                    "The family editor has no rooms.");
                return Result.Cancelled;
            }

            var view = uidoc.ActiveView as ViewPlan;
            if (view == null || view.ViewType != ViewType.FloorPlan)
            {
                TaskDialog.Show(DialogTitle,
                    "The command works on floor plans. Switch to a floor plan and try again.");
                return Result.Cancelled;
            }

            try
            {
                var rooms = SelectedRooms(uidoc, doc, view);
                if (rooms == null)
                    return Result.Cancelled;

                if (rooms.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "This view has no placed room at all.");
                    return Result.Cancelled;
                }

                return RunWindow(commandData, uidoc, doc, view, rooms);
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        // ───────────────────────────── picking the rooms ─────────────────────────────

        /// <summary>Rooms from the current selection; empty — offers to take every room on the active view.</summary>
        private static List<Room> SelectedRooms(UIDocument uidoc, Document doc, ViewPlan view)
        {
            var picked = uidoc.Selection.GetElementIds()
                .Select(doc.GetElement)
                .OfType<Room>()
                .Where(room => room.Area > 0)
                .ToList();

            if (picked.Count > 0)
                return picked;

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = "No room is selected.",
                MainContent = "Take every placed room on the active floor plan?",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Yes
            };

            if (dialog.Show() != TaskDialogResult.Yes)
                return null;

            return new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(room => room.Area > 0)
                .ToList();
        }

        // ───────────────────────────── the window and the "take sample" loop ─────────────────────────────

        private static Result RunWindow(ExternalCommandData commandData, UIDocument uidoc, Document doc, ViewPlan view, List<Room> rooms)
        {
            var dimensionTypes = DimensionTypes(doc);
            var defaultTypeName = DefaultDimensionTypeName(doc);
            var prefs = AutoDimensionPreferences.Load();

            var settings = new AutoDimensionSettings
            {
                Boundary = prefs.Boundary,
                Outward = prefs.Outward,
                RemovePrevious = prefs.RemovePrevious,
                IncludeAdjacentThickness = prefs.IncludeAdjacentThickness,
                MoveSmallText = prefs.MoveSmallText
            };

            var templateName = prefs.LastTemplate;
            IReadOnlyList<DimensionChainRow> rows = InitialRows(templateName, settings);

            while (true)
            {
                var templateNames = DimensionTemplateLibrary.Names();

                var window = new AutoDimensionWindow(
                    rooms.Count,
                    dimensionTypes,
                    defaultTypeName,
                    templateNames,
                    rows,
                    settings.Boundary,
                    settings.Outward,
                    settings.RemovePrevious,
                    settings.IncludeAdjacentThickness,
                    settings.MoveSmallText,
                    templateName,
                    name => DimensionTemplateLibrary.Load(name),
                    (name, template) => DimensionTemplateLibrary.Save(name, template));

                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                var dialogResult = window.ShowDialog();

                settings.Boundary = window.Boundary;
                settings.Outward = window.Outward;
                settings.RemovePrevious = window.RemovePrevious;
                settings.IncludeAdjacentThickness = window.IncludeAdjacentThickness;
                settings.MoveSmallText = window.MoveSmallText;
                templateName = window.TemplateName;
                rows = window.Rows;

                prefs.Boundary = settings.Boundary;
                prefs.Outward = settings.Outward;
                prefs.RemovePrevious = settings.RemovePrevious;
                prefs.IncludeAdjacentThickness = settings.IncludeAdjacentThickness;
                prefs.MoveSmallText = settings.MoveSmallText;
                prefs.LastTemplate = templateName;
                prefs.Save();

                if (dialogResult != true)
                    return Result.Cancelled;

                if (!window.WantsSample)
                {
                    var selected = window.Rows.Where(r => r.IsEnabled).ToList();
                    return Place(doc, view, rooms, selected, settings);
                }

                rows = TakeSample(uidoc, doc, settings.Boundary, rows);
            }
        }

        /// <summary>The window settings shared by every chain — so as not to carry half a dozen loose parameters.</summary>
        private sealed class AutoDimensionSettings
        {
            public SpatialElementBoundaryLocation Boundary;
            public bool Outward;
            public bool RemovePrevious;
            public bool IncludeAdjacentThickness;
            public bool MoveSmallText;
        }

        private static IReadOnlyList<DimensionChainRow> InitialRows(string templateName, AutoDimensionSettings settings)
        {
            if (string.IsNullOrEmpty(templateName))
                return new List<DimensionChainRow>();

            var template = DimensionTemplateLibrary.Load(templateName);
            if (template.Chains.Count == 0)
                return new List<DimensionChainRow>();

            settings.Boundary = template.Boundary;
            settings.Outward = template.Outward;
            settings.IncludeAdjacentThickness = template.IncludeAdjacentWallThickness;
            settings.MoveSmallText = template.MoveSmallText;

            return template.Chains.Select(chain => new DimensionChainRow
            {
                Kind = chain.Kind,
                OffsetMm = chain.OffsetMm,
                DimensionTypeName = chain.DimensionTypeName
            }).ToList();
        }

        /// <summary>
        /// Picking sample dimensions outside the modal window (Revit will not let PickObject run
        /// while it is open) and parsing them into a template. Cancelling the pick is not an
        /// error, we simply reopen the window with what was already in it.
        /// </summary>
        private static IReadOnlyList<DimensionChainRow> TakeSample(
            UIDocument uidoc,
            Document doc,
            SpatialElementBoundaryLocation boundary,
            IReadOnlyList<DimensionChainRow> current)
        {
            IList<Reference> picked;
            try
            {
                picked = uidoc.Selection.PickObjects(
                    ObjectType.Element,
                    new DimensionOnlyFilter(),
                    "Select the sample dimensions along one wall and press \"Finish\"");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return current;
            }

            var samples = picked
                .Select(reference => doc.GetElement(reference.ElementId) as Dimension)
                .Where(dimension => dimension != null)
                .ToList();

            if (samples.Count == 0)
                return current;

            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = boundary };
            var result = DimensionSampleReader.Read(doc, samples, options);

            if (result.Messages.Count > 0)
            {
                const int limit = 10;
                var text = "Chains parsed: " + result.Rows.Count + " out of " + samples.Count + " selected dimensions.\n\n" +
                           "Not parsed:\n• " + string.Join("\n• ", result.Messages.Take(limit));

                if (result.Messages.Count > limit)
                    text += "\n… and " + (result.Messages.Count - limit) + " more";

                TaskDialog.Show(DialogTitle, text);
            }

            return result.Rows.Count > 0 ? result.Rows : current;
        }

        private sealed class DimensionOnlyFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is Dimension;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }

        // ───────────────────────────── placement ─────────────────────────────

        private static Result Place(
            Document doc,
            ViewPlan view,
            List<Room> rooms,
            List<DimensionChainRow> chains,
            AutoDimensionSettings settings)
        {
            if (chains.Count == 0)
            {
                TaskDialog.Show(DialogTitle, "No chain is checked — there is nothing to place.");
                return Result.Cancelled;
            }

            var boundaryOptions = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = settings.Boundary };
            var collector = new DimensionReferenceCollector(doc);
            var defaultTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.LinearDimensionType);
            var typesByName = DimensionTypesByName(doc);

            var skippedRooms = new List<string>();
            var skippedChains = new List<string>();
            var approximate = new List<string>();
            var pending = new List<PendingDimension>();

            // READ PHASE — every bit of wall geometry is read here, before a single document edit.
            //
            // Creating a Dimension is a document edit too: it marks the geometry as changed, and
            // any Face/Reference read BEFORE this point (including from the
            // DimensionReferenceCollector cache — which caches regardless of transactions) is no
            // longer valid to Revit after such an edit. Collecting references and creating a
            // dimension used to be interleaved inside one loop over the chains — and the second
            // chain on the same side would read a face already stale from the first one's cache,
            // with an obscure geometry-kernel error ("The input curve is not bound", with no clue
            // pointing at our code). So first — read everything and stack it into pending, then, in
            // a separate pass inside the transaction — only write.
            foreach (var room in rooms)
            {
                var roomLabel = RoomLabel(room);

                List<RoomSide> sides;
                try
                {
                    sides = RoomSideBuilder.Build(room, boundaryOptions);
                }
                catch (Exception exception)
                {
                    skippedRooms.Add(roomLabel + " — could not build the boundary: " + exception.Message);
                    continue;
                }

                if (sides.Count == 0)
                {
                    skippedRooms.Add(roomLabel + " — the room has no boundary (not placed?)");
                    continue;
                }

                var foundHere = 0;

                foreach (var side in sides)
                {
                    if (side.IsCurved)
                    {
                        skippedChains.Add(roomLabel + ", side " + SideLabel(side) + " — the side is curved, skipped");
                        continue;
                    }

                    if (!side.HasWall)
                        continue; // a boundary with no wall (a room separator) — nothing to pick up

                    var chainIndex = 0;
                    foreach (var chain in chains)
                    {
                        chainIndex++;

                        var label = roomLabel + ", side " + SideLabel(side) + ", chain \"" +
                                    DimensionChainKindText.Caption(chain.Kind) + "\"";

                        var result = collector.Collect(side, sides, chain.Kind, settings.IncludeAdjacentThickness);
                        if (!result.Success)
                        {
                            skippedChains.Add(label + " — " + result.FailureReason);
                            continue;
                        }

                        if (!string.IsNullOrEmpty(result.Warning))
                            approximate.Add(label + " — " + result.Warning);

                        pending.Add(new PendingDimension
                        {
                            Line = ChainLine(side, chain.OffsetMm, settings.Outward),
                            AwayNormal = ChainNormal(side, settings.Outward),
                            References = result.References,
                            DimensionType = ResolveType(chain.DimensionTypeName, typesByName, doc, defaultTypeId),
                            RoomUniqueId = room.UniqueId,
                            ChainIndex = chainIndex,
                            Label = label
                        });
                        foundHere++;
                    }
                }

                if (foundHere == 0)
                    skippedRooms.Add(roomLabel + " — no chain was found to place");
            }

            if (pending.Count == 0)
            {
                Report(0, rooms.Count, skippedRooms, skippedChains, approximate, 0, new List<string>(), new List<string>());
                return Result.Cancelled;
            }

            // WRITE PHASE — only dimension creation, no new reads of wall geometry.
            var placed = 0;
            var movedTexts = 0;
            var failures = new List<string>();
            var warnings = new WarningSuppressor();

            using (var transaction = new Transaction(doc, "Auto dimensions"))
            {
                transaction.Start();

                var failureOptions = transaction.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(warnings);
                transaction.SetFailureHandlingOptions(failureOptions);

                if (settings.RemovePrevious)
                {
                    var roomUniqueIds = new HashSet<string>(rooms.Select(room => room.UniqueId));
                    var previous = AutoDimensionMarker.FindMarked(doc, view.Id, roomUniqueIds);

                    foreach (var id in previous)
                    {
                        try
                        {
                            doc.Delete(id);
                        }
                        catch (Exception)
                        {
                            // the old dimension did not come off — the new one will sit next to it anyway, not critical
                        }
                    }
                }

                foreach (var item in pending)
                {
                    try
                    {
                        var dimension = item.DimensionType != null
                            ? doc.Create.NewDimension(view, item.Line, item.References, item.DimensionType)
                            : doc.Create.NewDimension(view, item.Line, item.References);

                        AutoDimensionMarker.Mark(dimension, item.RoomUniqueId, item.ChainIndex);
                        item.Created = dimension;
                        placed++;
                    }
                    catch (Exception exception)
                    {
                        failures.Add(item.Label + " — " + exception.Message);
                    }
                }

                // LABEL LAYOUT PHASE — a separate pass, and only after Regenerate: before it a
                // freshly created dimension has no segments filled in yet, and there is nothing to
                // spread out. Regenerate is safe here precisely because the wall faces are no
                // longer needed — all the geometry was already read in the first phase.
                if (settings.MoveSmallText && placed > 0)
                {
                    doc.Regenerate();

                    foreach (var item in pending)
                    {
                        if (item.Created == null)
                            continue;

                        movedTexts += DimensionTextLayout.Arrange(item.Created, item.AwayNormal, view.Scale);
                    }
                }

                if (placed == 0)
                    transaction.RollBack();
                else
                    transaction.Commit();
            }

            Report(placed, rooms.Count, skippedRooms, skippedChains, approximate, movedTexts, failures, warnings.Messages);
            return placed > 0 ? Result.Succeeded : Result.Cancelled;
        }

        /// <summary>One chain not yet created: the geometry for it is already read and ready to be written.</summary>
        private sealed class PendingDimension
        {
            public Line Line;

            /// <summary>Where to pull short-link labels out to — away from the wall, along the chain's offset.</summary>
            public XYZ AwayNormal;

            public ReferenceArray References;
            public DimensionType DimensionType;
            public string RoomUniqueId;
            public int ChainIndex;
            public string Label;

            /// <summary>The created dimension — filled in during the write phase, needed by the label-layout phase.</summary>
            public Dimension Created;
        }

        private static XYZ ChainNormal(RoomSide side, bool outward)
        {
            return outward ? side.InwardNormal.Negate() : side.InwardNormal;
        }

        private static Line ChainLine(RoomSide side, double offsetMm, bool outward)
        {
            var normal = ChainNormal(side, outward);
            var offsetFeet = FeetOf(offsetMm);
            var marginFeet = FeetOf(LineMarginMm);

            var start = side.Start + normal.Multiply(offsetFeet) - side.Direction.Multiply(marginFeet);
            var end = side.End + normal.Multiply(offsetFeet) + side.Direction.Multiply(marginFeet);

            return Line.CreateBound(start, end);
        }

        private static DimensionType ResolveType(string name, Dictionary<string, DimensionType> byName, Document doc, ElementId defaultTypeId)
        {
            if (!string.IsNullOrEmpty(name))
            {
                DimensionType found;
                if (byName.TryGetValue(name, out found))
                    return found;
            }

            return defaultTypeId == null || defaultTypeId == ElementId.InvalidElementId
                ? null
                : doc.GetElement(defaultTypeId) as DimensionType;
        }

        private static IReadOnlyList<DimensionTypeInfo> DimensionTypes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DimensionType))
                .Cast<DimensionType>()
                .Where(type => SafeStyle(type) == DimensionStyleType.Linear)
                .Select(type => new DimensionTypeInfo(type.Id.IntegerValue, type.Name))
                .OrderBy(info => info.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The name of the dimension type Revit would pick on its own if none is given. The window
        /// needs it: a new row should be born with that type rather than the alphabetically first
        /// one — otherwise "default" in the table would mean a random type that simply happened to
        /// come first in the project's list.
        /// </summary>
        private static string DefaultDimensionTypeName(Document doc)
        {
            try
            {
                var id = doc.GetDefaultElementTypeId(ElementTypeGroup.LinearDimensionType);
                if (id == null || id == ElementId.InvalidElementId)
                    return string.Empty;

                var type = doc.GetElement(id) as DimensionType;
                return type == null ? string.Empty : SafeName(type);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static Dictionary<string, DimensionType> DimensionTypesByName(Document doc)
        {
            var result = new Dictionary<string, DimensionType>(StringComparer.OrdinalIgnoreCase);

            foreach (var type in new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>())
            {
                if (SafeStyle(type) != DimensionStyleType.Linear)
                    continue;

                var name = SafeName(type);
                if (name.Length > 0 && !result.ContainsKey(name))
                    result[name] = type;
            }

            return result;
        }

        private static DimensionStyleType? SafeStyle(DimensionType type)
        {
            try
            {
                return type.StyleType;
            }
            catch (Exception)
            {
                return null;
            }
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

        private static string RoomLabel(Room room)
        {
            var name = SafeName(room);
            var numberText = string.IsNullOrEmpty(room.Number) ? string.Empty : room.Number + " ";
            return "Room " + numberText + name;
        }

        private static string SideLabel(RoomSide side)
        {
            return (side.LoopIndex + 1) + "." + side.Index;
        }

        private static double FeetOf(double millimeters)
        {
            return UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);
        }

        // ───────────────────────────── the report ─────────────────────────────

        private static void Report(
            int placed,
            int roomCount,
            List<string> skippedRooms,
            List<string> skippedChains,
            List<string> approximate,
            int movedTexts,
            List<string> failures,
            IReadOnlyList<string> warnings)
        {
            var text = placed > 0
                ? "Dimensions placed: " + placed + " (rooms: " + roomCount + ")."
                : "No dimension was placed.";

            if (movedTexts > 0)
                text += "\nLabels pulled out onto a leader: " + movedTexts + ".";

            if (skippedRooms.Count > 0)
                text += "\n\n" + Bulleted("Rooms skipped", skippedRooms, 10);

            if (skippedChains.Count > 0)
                text += "\n\n" + Bulleted("Chains not placed", skippedChains, 15);

            // A section of its own, not merged with "not placed": these chains are in place, just
            // shorter than they should be. Merging them with the successes would mean silently
            // handing over a wrong dimension.
            if (approximate.Count > 0)
                text += "\n\n" + Bulleted("Chains that need checking", approximate, 10);

            if (failures.Count > 0)
                text += "\n\n" + Bulleted("Revit errors", failures, 15);

            if (warnings.Count > 0)
                text += "\n\n" + Bulleted("Revit warnings", warnings.ToList(), 5);

            TaskDialog.Show(DialogTitle, text);
        }

        private static string Bulleted(string title, List<string> items, int limit)
        {
            var text = title + " (" + items.Count + "):\n• " + string.Join("\n• ", items.Take(limit));
            if (items.Count > limit)
                text += "\n… and " + (items.Count - limit) + " more";
            return text;
        }
    }
}
