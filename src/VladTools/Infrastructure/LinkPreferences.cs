using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// How the link is positioned. It mirrors Revit's own list from the "Link Revit" dialog, but as an
    /// enum of our own: the window must know nothing about the Revit API, and the command translates
    /// this into an <c>ImportPlacement</c> in a single line.
    /// </summary>
    internal enum LinkPlacement
    {
        /// <summary>By shared coordinates — what is used in 99 cases out of 100.</summary>
        Shared,

        /// <summary>Origin to origin.</summary>
        Origin,

        /// <summary>Centre to centre.</summary>
        Centered,

        /// <summary>By the project site location.</summary>
        Site
    }

    /// <summary>What to do with the link's worksets at load time.</summary>
    internal enum LinkWorksetMode
    {
        /// <summary>Open all except the checked ones.</summary>
        OpenAll,

        /// <summary>Close all except the checked ones.</summary>
        CloseAll,

        /// <summary>As when the model was last opened; the checked ones are closed anyway.</summary>
        LastViewed
    }

    /// <summary>
    /// The "Link Manager" window settings that survive closing Revit: the placement, the link type and —
    /// most of all — which worksets to close.
    ///
    /// That last one is what all of this is stored for. The "00_Shared levels and grids" workset is
    /// closed in every project and in every link; typing it in again each time is exactly the work the
    /// button exists to spare. Worksets are remembered by name rather than by id: every model has ids of
    /// its own, while a shared workset has one name across all of them.
    ///
    /// File: `%AppData%\VladTools\links\_settings.txt`, lines of the form "KEY = value".
    /// The CLOSE and SERVER keys may repeat — they are lists.
    /// </summary>
    internal sealed class LinkPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Link Manager\" window settings — the Project panel.",
            "# PLACEMENT — Shared | Origin | Centered | Site",
            "# ATTACHMENT — Overlay | Attachment",
            "# WORKSETS — OpenAll | CloseAll | LastViewed",
            "# CLOSE — the name of a workset checked in the list (there may be many lines)",
            "# RULE / RULE_CONTAINS — the workset name rule: the string, and \"contains\" instead of \"starts with\"",
            "# SERVER — a Revit Server name offered in the browser (there may be many lines)",
            "# MATCH_WORKSET — 1/0: guess the project workset from the discipline code in the model name",
            "# DISCIPLINE — a discipline code for that guess (there may be many lines); with none, the default list is used",
            "# KIT — a discipline of the building kit (there may be many lines); with none, the default list is used",
            "# KIT_TOKEN — which piece of the model name counts as the building number (MK3-VSC-B01-AR → 3)",
            "# KIT_DEEP — 1/0: whether to descend into nested discipline folders",
            "# KIT_BUILDING — the building number from last time",
            "# KIT_ROOT — the folder to search in: FILE | path, SERVER | RSN://…, CLOUD | region | project | folder | name",
            "# The file is rewritten every time the window closes."
        };

        public LinkPlacement Placement { get; set; } = LinkPlacement.Shared;

        /// <summary>Attachment instead of overlay. Overlay is the default — as in Revit itself.</summary>
        public bool IsAttachment { get; set; }

        /// <summary>A relative path to the link file. Meaningless for Revit Server and the cloud.</summary>
        public bool IsRelativePath { get; set; } = true;

        public LinkWorksetMode WorksetMode { get; set; } = LinkWorksetMode.OpenAll;

        /// <summary>The names of the worksets checked in the window list.</summary>
        public List<string> Worksets { get; } = new List<string>();

        /// <summary>The workset name rule string; empty means there is no rule.</summary>
        public string WorksetPattern { get; set; } = string.Empty;

        /// <summary>The rule looks for a substring rather than the start of the name.</summary>
        public bool WorksetPatternContains { get; set; }

        /// <summary>The Revit Server names the user has already typed in.</summary>
        public List<string> Servers { get; } = new List<string>();

        /// <summary>
        /// Guess the project workset from the discipline code in the model name: for a new link with no
        /// workset set, the add-in looks for a workset like "01_Link_OV" from the "OV" code in the file name.
        /// A hand-made choice in the table is never overwritten — the guess only fills in what is empty.
        /// </summary>
        public bool MatchProjectWorkset { get; set; } = true;

        /// <summary>
        /// The discipline codes for that guess. An empty list means <see cref="DisciplineCatalog.Defaults"/>:
        /// the user adds their own code to _settings.txt, and the whole list is saved back there so there
        /// is something to edit.
        /// </summary>
        public List<string> Disciplines { get; } = new List<string>();

        /// <summary>
        /// The discipline codes with the defaults filled in when the user never touched the list.
        /// The kit codes always belong here: if the user named a discipline in the "Building Kit", it
        /// would be odd not to recognise the same code in a model name when guessing the workset.
        /// This also repairs an old settings file written before the code appeared in the default list:
        /// the list in the file overrides the defaults.
        /// </summary>
        public IReadOnlyList<string> EffectiveDisciplines
        {
            get
            {
                if (Disciplines.Count == 0)
                    return DisciplineCatalog.Defaults;

                return Disciplines
                    .Concat(EffectiveKit)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>
        /// The disciplines the "Building Kit" looks for in the project folders. The list is separate from
        /// <see cref="Disciplines"/>: there are all the codes that occur in file names at all, here are
        /// the ones whose models have to be linked into every model.
        /// </summary>
        public List<string> Kit { get; } = new List<string>();

        /// <summary>The kit disciplines with the defaults filled in.</summary>
        public IReadOnlyList<string> EffectiveKit =>
            Kit.Count > 0 ? (IReadOnlyList<string>)Kit : DisciplineCatalog.KitDefaults;

        /// <summary>Which piece of the model name counts as the building number: <c>MK3-VSC-B01-AR</c> → the third.</summary>
        public int BuildingToken { get; set; } = ModelKit.DefaultBuildingToken;

        /// <summary>Whether to descend into nested discipline folders when searching for the kit.</summary>
        public bool KitDeep { get; set; } = true;

        /// <summary>The building number from last time — used when it could not be read from the open model's name.</summary>
        public string KitBuilding { get; set; } = string.Empty;

        /// <summary>
        /// The folder to search the kit in, as a <see cref="ModelFolder.Format"/> line. Usually not
        /// needed: the button works the folder out from the open model itself. It comes in useful where
        /// there is nothing to work it out from — the project is open as a detached file and the models live on a server.
        /// </summary>
        public string KitRoot { get; set; } = string.Empty;

        /// <summary>%AppData%\VladTools\links\_settings.txt</summary>
        public static string FilePath => Path.Combine(LinkSetLibrary.FolderPath, "_settings.txt");

        /// <summary>
        /// A workset name in comparable form: without leading or trailing spaces.
        ///
        /// The trim is not cosmetic. A consultant's workset can easily be called "00_Reference planes "
        /// with a trailing space — nothing in Revit's list shows it. While the window trimmed the name on
        /// adding it to the list but the names were compared as they were, such a workset went missing twice:
        /// it never appeared as a row of its own (the trimmed name looked like a duplicate of one already
        /// checked), it showed "in none of them" in the "Found in" column — and it **was not closed at all**,
        /// because <c>Matches</c> never found it.
        /// </summary>
        public static string NormalizeWorkset(string name)
        {
            return (name ?? string.Empty).Trim();
        }

        /// <summary>
        /// Whether two workset names are the same. Case is ignored — Revit does not distinguish it in
        /// workset names either. One rule for everyone: both the window and the command compare link
        /// workset names through this method and nothing else.
        /// </summary>
        public static bool SameWorkset(string first, string second)
        {
            return string.Equals(
                NormalizeWorkset(first),
                NormalizeWorkset(second),
                StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static LinkPreferences Load()
        {
            var preferences = new LinkPreferences();

            try
            {
                if (!File.Exists(FilePath))
                    return preferences;

                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    preferences.Apply(line);
            }
            catch (Exception)
            {
                // A corrupt settings file is no reason not to open the window.
            }

            return preferences;
        }

        /// <summary>Rewrites the whole file. A write failure is swallowed: these are settings, not data.</summary>
        public void Save()
        {
            try
            {
                var lines = new List<string>(FileHeader) { string.Empty };

                lines.Add(Line("PLACEMENT", Placement.ToString()));
                lines.Add(Line("ATTACHMENT", IsAttachment ? "Attachment" : "Overlay"));
                lines.Add(Line("RELATIVE", IsRelativePath ? "1" : "0"));
                lines.Add(Line("WORKSETS", WorksetMode.ToString()));
                lines.Add(Line("RULE", WorksetPattern ?? string.Empty));
                lines.Add(Line("RULE_CONTAINS", WorksetPatternContains ? "1" : "0"));
                lines.Add(Line("MATCH_WORKSET", MatchProjectWorkset ? "1" : "0"));

                lines.Add(Line("KIT_TOKEN", BuildingToken.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                lines.Add(Line("KIT_DEEP", KitDeep ? "1" : "0"));
                lines.Add(Line("KIT_BUILDING", KitBuilding ?? string.Empty));
                lines.Add(Line("KIT_ROOT", KitRoot ?? string.Empty));

                lines.AddRange(Clean(Worksets).Select(name => Line("CLOSE", name)));
                lines.AddRange(Clean(Servers).Select(name => Line("SERVER", name)));

                // An empty list implies the defaults, but what goes into the file is what actually
                // applies — otherwise there would be nothing to edit.
                lines.AddRange(Clean(EffectiveDisciplines).Select(code => Line("DISCIPLINE", code)));
                lines.AddRange(Clean(EffectiveKit).Select(code => Line("KIT", code)));

                Directory.CreateDirectory(LinkSetLibrary.FolderPath);

                // The BOM keeps non-Latin names readable when the file is opened in Notepad.
                File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
            }
            catch (Exception)
            {
            }
        }

        private static IEnumerable<string> Clean(IEnumerable<string> values)
        {
            return values
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase);
        }

        private static string Line(string key, string value)
        {
            return key + " " + Separator + " " + value;
        }

        private void Apply(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return;

            var separator = text.IndexOf(Separator);
            if (separator <= 0)
                return;

            var key = text.Substring(0, separator).Trim().ToUpperInvariant();
            var value = text.Substring(separator + 1).Trim();

            switch (key)
            {
                case "PLACEMENT":
                    Placement = Parse(value, LinkPlacement.Shared);
                    break;

                case "ATTACHMENT":
                    IsAttachment = string.Equals(value, "Attachment", StringComparison.OrdinalIgnoreCase);
                    break;

                case "RELATIVE":
                    IsRelativePath = value != "0";
                    break;

                case "WORKSETS":
                    WorksetMode = Parse(value, LinkWorksetMode.OpenAll);
                    break;

                case "RULE":
                    WorksetPattern = value;
                    break;

                case "RULE_CONTAINS":
                    WorksetPatternContains = value == "1";
                    break;

                case "CLOSE":
                    if (value.Length > 0)
                        Worksets.Add(value);
                    break;

                case "SERVER":
                    if (value.Length > 0)
                        Servers.Add(value);
                    break;

                case "MATCH_WORKSET":
                    MatchProjectWorkset = value != "0";
                    break;

                case "DISCIPLINE":
                    if (value.Length > 0)
                        Disciplines.Add(value);
                    break;

                case "KIT":
                    if (value.Length > 0)
                        Kit.Add(value);
                    break;

                case "KIT_TOKEN":
                    int token;
                    if (int.TryParse(value, out token) && token >= 1)
                        BuildingToken = token;
                    break;

                case "KIT_DEEP":
                    KitDeep = value != "0";
                    break;

                case "KIT_BUILDING":
                    KitBuilding = value;
                    break;

                case "KIT_ROOT":
                    KitRoot = value;
                    break;
            }
        }

        private static T Parse<T>(string value, T fallback) where T : struct
        {
            T parsed;
            return Enum.TryParse(value, true, out parsed) ? parsed : fallback;
        }
    }
}
