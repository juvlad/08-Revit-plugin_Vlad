using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Base File" button settings: what exactly it does with the coordination model and what name
    /// to save the project site under.
    ///
    /// This is worth storing for exactly the same reason as the "Link Manager" settings: the site name
    /// and the "00_Shared levels and grids" workset are the same across an office's projects, yet they
    /// get typed in again in every one. The model itself is remembered too — in a new discipline of the
    /// same building the coordination file is the same, and there is no point picking it out of the server tree twice.
    ///
    /// File: `%AppData%\VladTools\basefile\_settings.txt`, lines of the form "KEY = value".
    /// </summary>
    internal sealed class BaseFilePreferences
    {
        private const char Separator = '=';

        /// <summary>The name of the workset switched to before copying the levels and grids.</summary>
        public const string SharedLevelsWorkset = "00_Shared levels and grids";

        /// <summary>
        /// The workset the base file link itself goes into. By the same convention as the consultants'
        /// "01_Link_OV": BM stands for base model.
        /// </summary>
        public const string BaseLinkWorkset = "01_Link_BM";

        private static readonly string[] FileHeader =
        {
            "# \"Base File\" button settings — the Project panel.",
            "# MODEL — the coordination model, in the same line format as in link sets:",
            "#   FILE | path · SERVER | RSN://… · CLOUD | region | project GUID | model GUID | name",
            "#   the last field is the project workset to put the link itself into",
            "# PLACEMENT — Shared | Origin | Centered | Site (Origin by default:",
            "#   the shared coordinates are yet to be acquired from the base file)",
            "# SITE — the name the project site will be given",
            "# LINK_WORKSET — the project workset the link itself goes into (empty — the active one)",
            "# WORKSET — the workset the command switches to before copying the levels and grids",
            "# ACQUIRE / RENAME / PIN / ACTIVATE / MONITOR — 1/0: whether to perform the matching step",
            "# AUTO_PICK — 1/0: whether to guess the base file from the open model's name when the window opens",
            "# BASE_FOLDER — the folder holding the base files of every building in the project",
            "# BASE_CODE — the base model code in the file name (MK3-VSC-B01-BM)",
            "#   which piece of the name counts as the building number lives in links\\_settings.txt, key KIT_TOKEN",
            "# The file is rewritten every time the window closes."
        };

        /// <summary>The coordination model chosen last time; null if one was never chosen.</summary>
        public LinkEntry Model { get; set; }

        /// <summary>
        /// The link placement. "Origin to origin" by default: the shared coordinates have not yet been
        /// acquired from the base file at this point, so there is nothing to place by.
        /// </summary>
        public LinkPlacement Placement { get; set; } = LinkPlacement.Origin;

        /// <summary>The name the project site will be given.</summary>
        public string Site { get; set; } = string.Empty;

        /// <summary>
        /// The project workset the link itself goes into. Stored separately from the model: the workset
        /// is the same across all disciplines, while the base file differs in a new building.
        /// Empty means the active workset, as Revit itself does it.
        /// </summary>
        public string LinkWorkset { get; set; } = BaseLinkWorkset;

        /// <summary>The workset the command switches to before copying the levels and grids.</summary>
        public string Workset { get; set; } = SharedLevelsWorkset;

        public bool Acquire { get; set; } = true;
        public bool Rename { get; set; } = true;
        public bool Pin { get; set; } = true;
        public bool Activate { get; set; } = true;

        /// <summary>Whether to open "Copy/Monitor" mode at the end.</summary>
        public bool Monitor { get; set; } = true;

        /// <summary>
        /// Whether to guess the base file from the open model's name when the window opens. The check box
        /// exists because the guess means reading store folders: a couple of network requests in the
        /// cloud, and someone who always picks the file by hand has no reason to pay for them.
        /// </summary>
        public bool AutoPick { get; set; } = true;

        /// <summary>The base file folder — the one the guess searches in.</summary>
        public string BaseFolder { get; set; } = BaseFileFinder.DefaultFolderName;

        /// <summary>The base model code in the file name: "MK3-VSC-B01-BM" → BM.</summary>
        public string BaseCode { get; set; } = BaseFileFinder.DefaultCode;

        /// <summary>%AppData%\VladTools\basefile</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "basefile");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static BaseFilePreferences Load()
        {
            var preferences = new BaseFilePreferences();

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

                if (Model != null)
                    lines.Add(Line("MODEL", LinkSetLibrary.Format(Model)));

                lines.Add(Line("PLACEMENT", Placement.ToString()));
                lines.Add(Line("SITE", Site ?? string.Empty));
                lines.Add(Line("LINK_WORKSET", LinkWorkset ?? string.Empty));
                lines.Add(Line("WORKSET", Workset ?? string.Empty));
                lines.Add(Line("ACQUIRE", Acquire ? "1" : "0"));
                lines.Add(Line("RENAME", Rename ? "1" : "0"));
                lines.Add(Line("PIN", Pin ? "1" : "0"));
                lines.Add(Line("ACTIVATE", Activate ? "1" : "0"));
                lines.Add(Line("MONITOR", Monitor ? "1" : "0"));
                lines.Add(Line("AUTO_PICK", AutoPick ? "1" : "0"));
                lines.Add(Line("BASE_FOLDER", BaseFolder ?? string.Empty));
                lines.Add(Line("BASE_CODE", BaseCode ?? string.Empty));

                Directory.CreateDirectory(FolderPath);

                // The BOM keeps non-Latin names readable when the file is opened in Notepad.
                File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
            }
            catch (Exception)
            {
            }
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
                case "MODEL":
                    Model = LinkSetLibrary.Parse(value);
                    break;

                case "PLACEMENT":
                    LinkPlacement placement;
                    if (Enum.TryParse(value, true, out placement))
                        Placement = placement;
                    break;

                case "SITE":
                    Site = value;
                    break;

                case "LINK_WORKSET":
                    LinkWorkset = value;
                    break;

                case "WORKSET":
                    Workset = value;
                    break;

                case "ACQUIRE":
                    Acquire = value != "0";
                    break;

                case "RENAME":
                    Rename = value != "0";
                    break;

                case "PIN":
                    Pin = value != "0";
                    break;

                case "ACTIVATE":
                    Activate = value != "0";
                    break;

                case "MONITOR":
                    Monitor = value != "0";
                    break;

                case "AUTO_PICK":
                    AutoPick = value != "0";
                    break;

                case "BASE_FOLDER":
                    BaseFolder = value;
                    break;

                case "BASE_CODE":
                    BaseCode = value;
                    break;
            }
        }
    }
}
