using System;
using System.Collections.Generic;
using System.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>One model found for the kit: whose discipline it is, which model, and where it was found.</summary>
    internal sealed class ModelKitHit
    {
        public ModelKitHit(string discipline, LinkEntry entry, string folder, bool isKnown)
        {
            Discipline = discipline ?? string.Empty;
            Entry = entry;
            Folder = folder ?? string.Empty;
            IsKnown = isKnown;
        }

        public string Discipline { get; }

        public LinkEntry Entry { get; }

        /// <summary>The name of the folder the model lies in: "3.0_AR".</summary>
        public string Folder { get; }

        /// <summary>The model is already in the link table or in the project — no point offering it twice.</summary>
        public bool IsKnown { get; }
    }

    /// <summary>The result of the walk: what was found, how many folders were read, and what could not be.</summary>
    internal sealed class ModelKitScan
    {
        public List<ModelKitHit> Hits { get; } = new List<ModelKitHit>();

        /// <summary>Folders the store would not hand over: one of these does not abort the walk.</summary>
        public List<string> Failures { get; } = new List<string>();

        /// <summary>How many folders were read — it shows the walk went where it was expected to.</summary>
        public int Folders { get; set; }

        /// <summary>The walk hit the folder-count limit: what is shown may be incomplete.</summary>
        public bool IsTruncated { get; set; }

        /// <summary>
        /// The folder the walk actually ran in. It may differ from the one asked for: if no discipline
        /// folders were found in it, the search climbs a level up — the open model may well lie in
        /// "01_Base Model" beside them rather than above them.
        /// </summary>
        public ModelFolder Root { get; set; }

        /// <summary>The disciplines no model was found for, in the order of the requested list.</summary>
        public IReadOnlyList<string> Missing(IEnumerable<string> asked)
        {
            var found = new HashSet<string>(Hits.Select(hit => hit.Discipline), StringComparer.OrdinalIgnoreCase);

            return asked.Where(code => !found.Contains(code)).ToList();
        }
    }

    /// <summary>
    /// Assembling a link kit by building number: "the model of building B01 needs AR, KR, ES, PS,
    /// PT, OV and VK of the same building".
    ///
    /// It rests on how the project folders are laid out and on nothing else: the discipline folders lie
    /// side by side — <c>3.0_AR</c>, <c>4.2_KR</c>, <c>5.1_ES</c> — and every model name carries the
    /// building number (<c>MK3-VSC-B01-AR</c>). So the kit can assemble itself: take the folders whose
    /// name carries a discipline code as a separate word, and in each of them the models of the right building.
    ///
    /// This is a heuristic of the same level as <see cref="FormulaParser"/> and <see cref="DisciplineCatalog"/>:
    /// a set of simple rules over name tokens, not a parser of a naming convention. That is why what was
    /// found is shown in a table before anything is linked, and a discipline that turned up several
    /// models is listed without a check mark — guessing is not allowed here.
    ///
    /// The walk is deliberately narrow: the root and the discipline folders inside it, not the whole
    /// project tree. Every cloud folder is a network request, and a full walk of a project with a
    /// hundred folders would mean a minute of waiting instead of a second.
    /// </summary>
    internal static class ModelKit
    {
        /// <summary>The building number is usually the third piece of a model name: <c>MK3-VSC-B01-AR</c>.</summary>
        public const int DefaultBuildingToken = 3;

        /// <summary>How many nesting levels to walk inside a discipline folder when "search nested folders" is on.</summary>
        private const int InnerDepth = 2;

        /// <summary>
        /// The limit on how many folders are read. A guard against missing the root: if the root turns
        /// out to be the top of the project, the walk must not become a half-hour interrogation of the cloud.
        /// </summary>
        private const int FolderLimit = 60;

        /// <summary>How many folders to inspect on the second pass, when no disciplines were found in the root.</summary>
        private const int SecondPassLimit = 25;

        /// <summary>
        /// The building number from a model name: the <paramref name="position"/>-th piece of the name, counting from one.
        /// The name is split by the same parser as in <see cref="DisciplineCatalog"/>, so hyphens,
        /// underscores and the extension fall away by themselves: <c>MK3-VSC-B01-AR.rvt</c> → the third piece, <c>B01</c>.
        /// With fewer pieces, an empty string: substituting "some piece or other" is not allowed, the error would be silent.
        /// </summary>
        public static string Building(string modelName, int position)
        {
            var tokens = Tokens(modelName);

            return position >= 1 && position <= tokens.Count ? tokens[position - 1] : string.Empty;
        }

        /// <summary>
        /// The folder to start the search from, derived from the open model's own folder: if that one is
        /// named after a discipline ("3.0_AR"), the search must go a level up — where the other
        /// disciplines' folders stand; otherwise, right inside it.
        ///
        /// It costs one or two cloud requests, so it is called once, when the window opens, and under a
        /// wait cursor.
        /// </summary>
        public static ModelFolder Root(ModelFolder folder, IReadOnlyList<string> disciplines)
        {
            if (folder == null)
                return null;

            var named = ModelStore.WithCloudName(folder);

            // The discipline is looked up in both the kit list and the general code list: a folder may
            // be named after a discipline that is not in the kit ("2.0_PZU") — we still have to
            // climb up in that case as well.
            var codes = (disciplines ?? DisciplineCatalog.KitDefaults).Concat(DisciplineCatalog.Defaults).ToList();
            if (DisciplineCatalog.Detect(named.Name, codes).Length == 0)
                return named;

            try
            {
                var above = ModelStore.Parent(named);
                return above == null ? named : ModelStore.WithCloudName(above);
            }
            catch (Exception)
            {
                // We did not climb — we search where we stand, and the folder can be given by hand.
                return named;
            }
        }

        /// <summary>The pieces of the name without the extension — the user picks the building number among them.</summary>
        public static IReadOnlyList<string> Tokens(string modelName)
        {
            var name = (modelName ?? string.Empty).Trim();

            if (name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            return DisciplineCatalog.Tokens(name);
        }

        /// <summary>
        /// Assembles the kit: walks the discipline folders in <paramref name="root"/> and takes from them
        /// the models whose names carry <paramref name="building"/>.
        /// </summary>
        /// <param name="deep">Descend into nested discipline folders too ("3.0_AR\Models").</param>
        /// <param name="isKnown">The model is already in the table or in the project — by the <see cref="LinkEntry.Key"/>.</param>
        public static ModelKitScan Find(
            ModelFolder root,
            string building,
            IReadOnlyList<string> disciplines,
            bool deep,
            Func<string, bool> isKnown)
        {
            var scan = new ModelKitScan();
            if (root == null || string.IsNullOrWhiteSpace(building) || disciplines == null || disciplines.Count == 0)
                return scan;

            var codes = disciplines
                .Select(code => (code ?? string.Empty).Trim())
                .Where(code => code.Length > 0)
                .ToList();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = root;
            var items = Read(current, scan);
            var folders = DisciplineFolders(items, codes);

            // There are no discipline folders in the requested folder — we look a level up. The open
            // model need not lie in its own discipline folder: it may well sit in "01_Base Model" or
            // "0.0_Federated Model", that is, beside the discipline folders rather than inside them.
            // One request, and in exchange the search does not come back empty where everything is in place.
            if (folders.Count == 0)
            {
                var above = Above(current, scan);
                if (above != null)
                {
                    var aboveItems = Read(above, scan);
                    var aboveFolders = DisciplineFolders(aboveItems, codes);

                    if (aboveFolders.Count > 0)
                    {
                        current = above;
                        items = aboveItems;
                        folders = aboveFolders;
                    }
                }
            }

            // Discipline folders are sometimes a level down too ("03_Models\3.0_AR").
            // Descending costs a request per folder, so it is done only when there is nowhere else to look.
            if (folders.Count == 0)
            {
                foreach (var item in items.Where(item => item.IsFolder).Take(SecondPassLimit))
                {
                    if (scan.Folders >= FolderLimit)
                        break;

                    folders.AddRange(DisciplineFolders(Read(item.Folder, scan), codes));
                }
            }

            // Models lying directly in the search folder count too: their discipline is taken from the
            // name. That is how the kit is assembled where there are no discipline folders at all.
            foreach (var item in items.Where(item => !item.IsFolder))
                Take(scan, seen, item.Entry, DisciplineCatalog.Detect(item.Entry.Name, codes), current.Name, building, isKnown);

            foreach (var folder in folders)
                Walk(scan, seen, folder.Item1, folder.Item2, building, codes, deep ? InnerDepth : 0, isKnown);

            scan.Root = current;

            return scan;
        }

        // ───────────────────────────── the walk ─────────────────────────────

        /// <summary>The folder one level up; on failure, null plus a line among the unread ones.</summary>
        private static ModelFolder Above(ModelFolder folder, ModelKitScan scan)
        {
            try
            {
                var above = ModelStore.Parent(folder);

                return above == null ? null : ModelStore.WithCloudName(above);
            }
            catch (Exception exception)
            {
                scan.Failures.Add("the folder above " + folder.Display + " — " + LinkCatalog.Short(exception.Message));
                return null;
            }
        }

        /// <summary>Folders whose name carries a discipline code as a separate word: "3.0_AR" → AR.</summary>
        private static List<Tuple<ModelFolder, string>> DisciplineFolders(IEnumerable<StoreItem> items, IReadOnlyList<string> codes)
        {
            var folders = new List<Tuple<ModelFolder, string>>();

            foreach (var item in items.Where(item => item.IsFolder))
            {
                var code = DisciplineCatalog.Detect(item.Name, codes);
                if (code.Length > 0)
                    folders.Add(Tuple.Create(item.Folder, code));
            }

            return folders;
        }

        private static void Walk(
            ModelKitScan scan,
            HashSet<string> seen,
            ModelFolder folder,
            string code,
            string building,
            IReadOnlyList<string> codes,
            int depth,
            Func<string, bool> isKnown)
        {
            if (scan.Folders >= FolderLimit)
            {
                scan.IsTruncated = true;
                return;
            }

            var items = Read(folder, scan);

            foreach (var item in items)
            {
                if (!item.IsFolder)
                {
                    Take(scan, seen, item.Entry, code, folder.Name, building, isKnown);
                    continue;
                }

                if (depth <= 0)
                    continue;

                // A nested folder of another discipline inside this one is not our business: its own
                // pass over that discipline will handle it, if it was requested.
                var inner = DisciplineCatalog.Detect(item.Name, codes);
                if (inner.Length > 0 && !string.Equals(inner, code, StringComparison.OrdinalIgnoreCase))
                    continue;

                Walk(scan, seen, item.Folder, code, building, codes, depth - 1, isKnown);
            }
        }

        /// <summary>Puts a model into the result if it has a discipline and the right building number in its name.</summary>
        private static void Take(
            ModelKitScan scan,
            HashSet<string> seen,
            LinkEntry entry,
            string code,
            string folder,
            string building,
            Func<string, bool> isKnown)
        {
            if (entry == null || code.Length == 0)
                return;

            if (!DisciplineCatalog.HasToken(StripExtension(entry.Name), building))
                return;

            if (!seen.Add(entry.Key))
                return;

            scan.Hits.Add(new ModelKitHit(code, entry, folder, isKnown != null && isKnown(entry.Key)));
        }

        private static IReadOnlyList<StoreItem> Read(ModelFolder folder, ModelKitScan scan)
        {
            if (scan.Folders >= FolderLimit)
            {
                scan.IsTruncated = true;
                return new List<StoreItem>();
            }

            try
            {
                scan.Folders++;
                return ModelStore.Children(folder);
            }
            catch (Exception exception)
            {
                // A single unread folder must not cancel the whole kit: the other disciplines will
                // still assemble, and this one gets a line of its own.
                scan.Failures.Add(folder.Display + " — " + LinkCatalog.Short(exception.Message));
                return new List<StoreItem>();
            }
        }

        private static string StripExtension(string name)
        {
            var text = name ?? string.Empty;

            return text.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
                ? text.Substring(0, text.Length - 4)
                : text;
        }
    }
}
