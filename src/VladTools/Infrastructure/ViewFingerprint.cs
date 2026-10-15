using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// A short digest of what a view's drawing is made of, as far as the model records changes: the
    /// view itself (crop, scale, visibility/graphics…), its template, the primary of a dependent view,
    /// and every element the view owns — dimensions, tags, text, detail lines and components, filled
    /// regions, revision clouds; on a sheet that is also its viewports, titleblock and schedule
    /// instances. Each part enters by its <see cref="Element.VersionGuid"/>, so the digest moves when
    /// any of them changes, is added or is deleted — once the model is saved or synchronised: per
    /// RevitAPI.xml one version spans everything between two saves.
    ///
    /// Model elements seen in the view (walls, doors) are left out on purpose. Finding them means a
    /// visibility pass per view, minutes on a large model, and one wall would mark every plan it shows
    /// in as changed. What a model change does to the drawing still shows where it moves the view's
    /// own annotation — a dimension's value, a tag's text.
    /// </summary>
    internal static class ViewFingerprint
    {
        /// <summary>
        /// The digest of every view given, keyed by its id. A view Revit has no version for (a model
        /// never saved) is left out: there is nothing to compare it by.
        /// </summary>
        public static Dictionary<ElementId, string> Of(Document doc, IEnumerable<View> views)
        {
            var parts = new Dictionary<ElementId, List<string>>();

            // The views whose digest takes the elements a given view owns: the view itself, and every
            // dependent view of it — annotation drawn in a dependent view belongs to its primary.
            var takers = new Dictionary<ElementId, List<ElementId>>();

            void Take(ElementId owner, ElementId view)
            {
                if (!takers.TryGetValue(owner, out var list))
                {
                    list = new List<ElementId>();
                    takers[owner] = list;
                }

                if (!list.Contains(view))
                    list.Add(view);
            }

            foreach (var view in views)
            {
                if (view == null || parts.ContainsKey(view.Id))
                    continue;

                var own = Version(view);
                if (own == null)
                    continue;

                var list = new List<string> { "V " + own };
                parts[view.Id] = list;
                Take(view.Id, view.Id);

                if (doc.GetElement(view.ViewTemplateId) is View template)
                    list.Add("T " + (Version(template) ?? "?"));

                var primaryId = PrimaryOf(view);
                if (doc.GetElement(primaryId) is View primary)
                {
                    list.Add("P " + (Version(primary) ?? "?"));
                    Take(primary.Id, view.Id);
                }
            }

            if (parts.Count == 0)
                return new Dictionary<ElementId, string>();

            // One pass over every view-specific element in the model rather than a collector per view.
            var owned = new FilteredElementCollector(doc).WherePasses(new ElementOwnerViewFilter(ElementId.InvalidElementId, true));

            foreach (var element in owned)
            {
                if (!takers.TryGetValue(element.OwnerViewId, out var targets))
                    continue;

                // Elements with no category are Revit's own bookkeeping, nothing anybody drew.
                if (element.Category == null)
                    continue;

                var line = element.UniqueId + " " + (Version(element) ?? "?");
                foreach (var view in targets)
                    parts[view].Add(line);
            }

            return parts.ToDictionary(entry => entry.Key, entry => Digest(entry.Value));
        }

        private static ElementId PrimaryOf(View view)
        {
            try
            {
                return view.GetPrimaryViewId();
            }
            catch (Exception)
            {
                return ElementId.InvalidElementId;
            }
        }

        private static string Version(Element element)
        {
            try
            {
                return element.VersionGuid.ToString("N");
            }
            catch (Exception)
            {
                // Thrown for a transient element, or one the document keeps no version for.
                return null;
            }
        }

        /// <summary>The lines sorted first, so the order a collector hands elements out in does not matter.</summary>
        private static string Digest(List<string> lines)
        {
            lines.Sort(StringComparer.Ordinal);

            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
                return BitConverter.ToString(bytes, 0, 10).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
