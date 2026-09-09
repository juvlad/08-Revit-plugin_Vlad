using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// Creates a "3D Thumbnail" 3D view in the open family and sets it up: annotations off,
    /// connectors hidden, realistic graphics, fine detail level.
    /// Running it again does not breed views — the settings are applied to the existing one.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Create3DThumbnailCommand : IExternalCommand
    {
        public const string ViewName = "3D Thumbnail";

        private const string DialogTitle = "3D Thumbnail";

        /// <summary>The connector categories that have to be hidden on the view.</summary>
        private static readonly BuiltInCategory[] ConnectorCategories =
        {
            BuiltInCategory.OST_ConnectorElem,
            BuiltInCategory.OST_ConnectorElemXAxis,
            BuiltInCategory.OST_ConnectorElemYAxis,
            BuiltInCategory.OST_ConnectorElemZAxis
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
            if (!doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "The command works only in the family editor.\n" +
                    "Open a family (.rfa) and try again.");
                return Result.Cancelled;
            }

            var warnings = new List<string>();

            try
            {
                View3D view;

                using (var transaction = new Transaction(doc, "Create \"" + ViewName + "\""))
                {
                    transaction.Start();

                    view = FindView(doc, ViewName);
                    if (view == null)
                    {
                        var viewTypeId = Get3DViewFamilyTypeId(doc);
                        if (viewTypeId == ElementId.InvalidElementId)
                        {
                            transaction.RollBack();
                            message = "This family has no 3D view type (ViewFamilyType), the view cannot be created.";
                            return Result.Failed;
                        }

                        view = View3D.CreateIsometric(doc, viewTypeId);
                        Rename(view, ViewName, warnings);
                    }

                    ApplySettings(view, warnings);

                    transaction.Commit();
                }

                ActivateView(uidoc, view, warnings);

                if (warnings.Count > 0)
                {
                    TaskDialog.Show(DialogTitle,
                        "The \"" + view.Name + "\" view is ready, but some settings could not be applied:\n\n• " +
                        string.Join("\n• ", warnings));
                }

                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        /// <summary>Applies every required view setting. Each one independently of the rest.</summary>
        private static void ApplySettings(View3D view, List<string> warnings)
        {
            Try(() => view.AreAnnotationCategoriesHidden = true, "turn annotations off", warnings);
            Try(() => HideConnectors(view, warnings), "hide the connectors", warnings);
            Try(() => view.DisplayStyle = DisplayStyle.Realistic, "turn on realistic graphics", warnings);
            Try(() => view.DetailLevel = ViewDetailLevel.Fine, "set the fine detail level", warnings);
        }

        /// <summary>
        /// Hides the connectors in two ways at once: by V/G categories and element by element.
        /// A category may be missing from Settings.Categories inside a family, so we address it
        /// directly by ElementId rather than through Category.GetCategory (which returns null).
        /// </summary>
        private static void HideConnectors(View3D view, List<string> warnings)
        {
            var problems = new List<string>();
            var hidSomething = HideConnectorCategories(view, problems);

            // Filtering in memory: OfClass(typeof(ConnectorElement)) is not always supported, and a
            // family document is small, so walking the whole thing is safer.
            var connectors = new FilteredElementCollector(view.Document)
                .WhereElementIsNotElementType()
                .OfType<ConnectorElement>()
                .Cast<Element>()
                .ToList();

            if (HideConnectorElements(view, connectors, problems))
                hidSomething = true;

            // We stay silent only if there was nothing to hide or everything worked.
            if (hidSomething || connectors.Count == 0)
                return;

            warnings.Add("could not hide the connectors (" + connectors.Count + "): " +
                         string.Join("; ", problems));
        }

        private static bool HideConnectorCategories(View3D view, List<string> problems)
        {
            var result = false;

            foreach (var builtInCategory in ConnectorCategories)
            {
                var categoryId = new ElementId(builtInCategory);
                try
                {
                    if (!view.GetCategoryHidden(categoryId))
                        view.SetCategoryHidden(categoryId, true);

                    result = true;
                }
                catch (Exception exception)
                {
                    problems.Add("category " + builtInCategory + " — " + exception.Message);
                }
            }

            return result;
        }

        private static bool HideConnectorElements(View3D view, ICollection<Element> connectors, List<string> problems)
        {
            var hideable = connectors.Where(element => element.CanBeHidden(view) && !element.IsHidden(view))
                                     .Select(element => element.Id)
                                     .ToList();
            if (hideable.Count == 0)
                return false;

            try
            {
                view.HideElements(hideable);
                return true;
            }
            catch (Exception exception)
            {
                problems.Add("hiding the elements — " + exception.Message);
                return false;
            }
        }

        private static View3D FindView(Document document, string name)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(view => !view.IsTemplate &&
                                        string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static ElementId Get3DViewFamilyTypeId(Document document)
        {
            var viewFamilyType = new FilteredElementCollector(document)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(type => type.ViewFamily == ViewFamily.ThreeDimensional);

            return viewFamilyType?.Id ?? ElementId.InvalidElementId;
        }

        private static void Rename(View3D view, string name, List<string> warnings)
        {
            // The name may be taken by a view of another kind (a plan, a section) — then we keep the default name.
            Try(() => view.Name = name, "rename the view to \"" + name + "\"", warnings);
        }

        private static void ActivateView(UIDocument uidoc, View3D view, List<string> warnings)
        {
            if (uidoc.ActiveView != null && uidoc.ActiveView.Id == view.Id)
                return;

            Try(() => uidoc.ActiveView = view, "open the view", warnings);
        }

        private static void Try(Action action, string what, List<string> warnings)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                warnings.Add("could not " + what + " (" + exception.Message + ")");
            }
        }
    }
}
