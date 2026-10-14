using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Who decides a view's scale besides the view itself — shared by "Views to Sheets" and "Sheet
    /// Scale". Two things can: a view template that controls the scale (<c>View.Scale</c> cannot be
    /// changed past it), and a primary view — a dependent view has no scale of its own, it shares its
    /// primary's, and so does every other dependent of it.
    /// </summary>
    internal static class ViewScale
    {
        /// <summary>The parameters a view template can hold the scale by: one of them in its controlled list locks the scale.</summary>
        public static readonly BuiltInParameter[] Parameters =
        {
            BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC,
            BuiltInParameter.VIEW_SCALE_PULLDOWN_IMPERIAL,
            BuiltInParameter.VIEW_SCALE
        };

        /// <summary>
        /// Whether the template sets the scale of the views using it: a scale parameter is in its
        /// parameter list and not in its "not controlled" list — the "Include" column of Revit's own
        /// "View Template Properties". Null when the template cannot say; the attempt to change the
        /// scale will tell then.
        /// </summary>
        public static bool? Controls(View template)
        {
            try
            {
                var controlled = new HashSet<ElementId>(template.GetTemplateParameterIds());
                foreach (var id in template.GetNonControlledTemplateParameterIds())
                    controlled.Remove(id);

                return Parameters.Any(parameter => controlled.Contains(new ElementId(parameter)));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Unticks "View Scale" in the template: the views using it keep the scale they have, and each
        /// can be given one of its own from then on. Everything else the template sets it goes on setting.
        /// </summary>
        public static void Release(View template)
        {
            var parameters = new HashSet<ElementId>(template.GetTemplateParameterIds());
            var free = new HashSet<ElementId>(template.GetNonControlledTemplateParameterIds());

            foreach (var parameter in Parameters)
            {
                var id = new ElementId(parameter);
                if (parameters.Contains(id))
                    free.Add(id);
            }

            template.SetNonControlledTemplateParameterIds(free);
        }

        /// <summary>The view that holds the scale: a dependent view's primary, otherwise the view itself.</summary>
        public static View Owner(Document doc, View view)
        {
            try
            {
                var primary = view.GetPrimaryViewId();
                if (primary != null && primary != ElementId.InvalidElementId && doc.GetElement(primary) is View owner)
                    return owner;
            }
            catch (Exception)
            {
                // A view the API cannot place in a dependency holds its own scale.
            }

            return view;
        }
    }
}
