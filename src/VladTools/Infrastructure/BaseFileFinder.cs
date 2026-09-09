using System;
using System.Collections.Generic;
using System.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>The result of the guess: the building number, the base file folder, the candidates and what could not be read.</summary>
    internal sealed class BaseFileScan
    {
        /// <summary>The building number from the open model's name; empty if none was found.</summary>
        public string Building { get; set; } = string.Empty;

        /// <summary>The base file folder if it was found: that is what is shown to the user.</summary>
        public ModelFolder Folder { get; set; }

        /// <summary>
        /// The matching models. One — it can be filled in; several — the user chooses, the same rule as
        /// when guessing the workset and the building kit.
        /// </summary>
        public List<LinkEntry> Hits { get; } = new List<LinkEntry>();

        /// <summary>Folders the store would not hand over: one of these does not abort the guess.</summary>
        public List<string> Failures { get; } = new List<string>();

        /// <summary>
        /// It matched by building only: the base model code ("BM") is missing from the name. The model
        /// still lies in the base file folder, so it is a candidate — but we are obliged to say so.
        /// </summary>
        public bool IsLoose { get; set; }
    }

    /// <summary>
    /// Guessing the base (coordination) file from the open model's name.
    ///
    /// It rests on the same convention as the "Building Kit": every model name carries a building number
    /// (<c>MK3-VSC-B01-VOIDS</c> → <c>B01</c>), and the base files of the whole project lie in one
    /// folder — <c>01_Base Model</c> — next to the discipline folders. So a building's base file can be
    /// named outright: <c>MK3-VSC-B01-BM</c>, and there is no need to hunt for it in a tree.
    ///
    /// This is a heuristic of the same level as <see cref="DisciplineCatalog"/> and <see cref="ModelKit"/>:
    /// a set of rules over name tokens, not a parser of a naming convention. That is why what was guessed
    /// is shown in the window before anything is linked, and when several models match, none is
    /// taken: guessing is not allowed here.
    ///
    /// The walk is deliberately narrow — two folders rather than the project tree: the model's own folder
    /// and the one a level up. Every cloud folder costs a network request, and the guess runs when the
    /// window opens, that is, while the user is waiting.
    /// </summary>
    internal static class BaseFileFinder
    {
        /// <summary>The folder holding the base files of every building in the project.</summary>
        public const string DefaultFolderName = "01_Base Model";

        /// <summary>The base model code in the file name — by the same convention as the discipline codes.</summary>
        public const string DefaultCode = "BM";

        /// <param name="host">The open model: the building comes from its name, the search root from its folder.</param>
        /// <param name="folderName">The base file folder name; empty means <see cref="DefaultFolderName"/>.</param>
        /// <param name="code">The base model code in the name; empty means <see cref="DefaultCode"/>.</param>
        /// <param name="buildingToken">Which piece of the name counts as the building number.</param>
        public static BaseFileScan Find(HostModel host, string folderName, string code, int buildingToken)
        {
            var scan = new BaseFileScan();
            if (host == null)
                return scan;

            scan.Building = ModelKit.Building(host.Name, buildingToken);
            if (host.Folder == null || scan.Building.Length == 0)
                return scan;

            var folder = Locate(host.Folder, Words(Named(folderName, DefaultFolderName)), scan);
            if (folder == null)
                return scan;

            scan.Folder = folder;

            // The open model itself is struck off the candidates: Revit will not let it link to itself
            // anyway, and it may well live in that very folder.
            var models = Read(folder, scan)
                .Where(item => !item.IsFolder && item.Entry != null)
                .Select(item => item.Entry)
                .Where(entry => !string.Equals(entry.Key, host.Key, StringComparison.Ordinal))
                .ToList();

            var wanted = Named(code, DefaultCode);

            var strict = models
                .Where(entry => DisciplineCatalog.HasToken(entry.Name, scan.Building) &&
                                DisciplineCatalog.HasToken(entry.Name, wanted))
                .ToList();

            if (strict.Count > 0)
            {
                scan.Hits.AddRange(strict);
                return scan;
            }

            // The code is missing from the name — but a model of the right building lies in the base
            // file folder, and that is a better answer than "nothing was found". The window will say it is a stretch.
            var loose = models
                .Where(entry => DisciplineCatalog.HasToken(entry.Name, scan.Building))
                .ToList();

            scan.IsLoose = loose.Count > 0;
            scan.Hits.AddRange(loose);

            return scan;
        }

        // ───────────────────────────── where to search ─────────────────────────────

        /// <summary>
        /// The base file folder next to the open model. We look in exactly two places: where the model
        /// lies (that folder may itself be the one — base files are edited from there too), and one level
        /// up — a discipline model sits in "3.0_AR" while the base files are beside it, not inside.
        /// </summary>
        private static ModelFolder Locate(ModelFolder start, IReadOnlyList<string> wanted, BaseFileScan scan)
        {
            if (wanted.Count == 0)
                return null;

            var here = ModelStore.WithCloudName(start);
            if (Matches(here.Name, wanted))
                return here;

            var found = Pick(Read(here, scan), wanted);
            if (found != null)
                return found;

            var above = Above(here, scan);
            if (above == null)
                return null;

            return Matches(above.Name, wanted) ? above : Pick(Read(above, scan), wanted);
        }

        /// <summary>The folder one level up; on failure, null plus a line among the unread ones.</summary>
        private static ModelFolder Above(ModelFolder folder, BaseFileScan scan)
        {
            try
            {
                return ModelStore.WithCloudName(ModelStore.Parent(folder));
            }
            catch (Exception exception)
            {
                scan.Failures.Add("the folder above " + folder.Display + " — " + LinkCatalog.Short(exception.Message));
                return null;
            }
        }

        private static ModelFolder Pick(IEnumerable<StoreItem> items, IReadOnlyList<string> wanted)
        {
            return items
                .Where(item => item.IsFolder && Matches(item.Name, wanted))
                .Select(item => item.Folder)
                .FirstOrDefault();
        }

        private static IReadOnlyList<StoreItem> Read(ModelFolder folder, BaseFileScan scan)
        {
            try
            {
                return ModelStore.Children(folder);
            }
            catch (Exception exception)
            {
                // An unread folder does not cancel the guess: we try the second one anyway, and the
                // reason goes into the caption under the chosen model.
                scan.Failures.Add(folder.Display + " — " + LinkCatalog.Short(exception.Message));
                return new List<StoreItem>();
            }
        }

        // ───────────────────────────── the folder name ─────────────────────────────

        /// <summary>
        /// The folder name is compared word by word, without the leading numbers: "01_Base Model",
        /// "02 Base Model" and "Base Model" are the same folder. The leading number changes from project
        /// to project while the name itself stays; comparing whole strings would miss for no reason.
        /// All the words must match, and in order — "Base Models" is already a different folder, and one
        /// like that is supplied by the user through the setting, not by a guess.
        /// </summary>
        private static bool Matches(string name, IReadOnlyList<string> wanted)
        {
            var words = Words(name);

            return words.Count == wanted.Count &&
                   !words.Where((word, i) => !string.Equals(word, wanted[i], StringComparison.OrdinalIgnoreCase)).Any();
        }

        /// <summary>The name's words without purely numeric pieces; the split is shared with the discipline guess.</summary>
        private static IReadOnlyList<string> Words(string text)
        {
            return DisciplineCatalog.Tokens(text).Where(token => !token.All(char.IsDigit)).ToList();
        }

        private static string Named(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }
}
