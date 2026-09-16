using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "other models" window settings: which worksets to open in somebody else's model, what to
    /// put in line with the coordination file there, and the model list from last time.
    ///
    /// The list is remembered for the same reason as the coordination model in "Base File": a batch
    /// of models is run again after every new base file is issued, and picking the same dozen models
    /// out of the server tree each time is exactly the work the button exists to spare. It is not a
    /// replacement for a saved set, though — a set (<see cref="LinkSetLibrary"/>) is shared with
    /// "Link Manager" and is meant to be named and kept; this is just "what was open last time".
    ///
    /// File: `%AppData%\VladTools\coordination\_settings.txt`, lines of the form "KEY = value".
    /// The WORKSET and MODEL keys may repeat — they are lists.
    /// </summary>
    internal sealed class CoordinationPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Accept Changes → Other models…\" settings — the Project panel.",
            "# WORKSET — the start of the name of a workset to open in the model (there may be many lines);",
            "#   a prefix, not a whole name: \"00_Link_BM\" also opens \"00_Link_BM_K3\".",
            "#   With none, the default list is used: everything that applies is written back here.",
            "# LEVELS — 1/0: put levels in line with the file as well, not only grids",
            "# RENAME — 1/0: rename grids and levels to follow the coordination file",
            "# SET — the link set worked with last",
            "# MODEL — a model from last time, in the same line format as in link sets:",
            "#   FILE | path · SERVER | RSN://… · CLOUD | region | project GUID | model GUID | name",
            "# The file is rewritten every time the window closes."
        };

        /// <summary>
        /// The names — prefixes, really — of the worksets to open. An empty list means
        /// <see cref="BatchCoordination.DefaultWorksets"/>; whatever actually applies is written back
        /// to the file, so there is something to edit.
        /// </summary>
        public List<string> Worksets { get; } = new List<string>();

        /// <summary>The worksets with the defaults filled in when the user never touched the list.</summary>
        public IReadOnlyList<string> EffectiveWorksets =>
            Worksets.Count > 0 ? (IReadOnlyList<string>)Worksets : BatchCoordination.DefaultWorksets;

        /// <summary>
        /// Put levels in line with the file as well. Off by default, and that is not timidity: a level
        /// moved in a discipline model takes every wall, room and view standing on it along, and in a
        /// batch run nobody is watching it happen. Grids are what a coordination file usually shifts.
        /// </summary>
        public bool Levels { get; set; }

        /// <summary>Rename grids and levels to follow the coordination file. Off by default — see <see cref="Levels"/>.</summary>
        public bool Rename { get; set; }

        /// <summary>The link set worked with last.</summary>
        public string Set { get; set; } = string.Empty;

        /// <summary>The models the window was closed with last time.</summary>
        public List<LinkEntry> Models { get; } = new List<LinkEntry>();

        /// <summary>%AppData%\VladTools\coordination</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "coordination");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static CoordinationPreferences Load()
        {
            var preferences = new CoordinationPreferences();

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

                lines.Add(Line("LEVELS", Levels ? "1" : "0"));
                lines.Add(Line("RENAME", Rename ? "1" : "0"));
                lines.Add(Line("SET", Set ?? string.Empty));

                // An empty list implies the defaults, but what goes into the file is what actually
                // applies — the same rule as with the discipline codes in "Link Manager".
                lines.AddRange(EffectiveWorksets
                    .Select(LinkPreferences.NormalizeWorkset)
                    .Where(name => name.Length > 0)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .Select(name => Line("WORKSET", name)));

                lines.AddRange(Models.Where(entry => entry != null)
                    .Select(entry => Line("MODEL", LinkSetLibrary.Format(entry))));

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
                case "WORKSET":
                    if (value.Length > 0)
                        Worksets.Add(value);
                    break;

                case "LEVELS":
                    Levels = value == "1";
                    break;

                case "RENAME":
                    Rename = value == "1";
                    break;

                case "SET":
                    Set = value;
                    break;

                case "MODEL":
                    // The line format is the link sets' own — there is no second parser for it.
                    var entry = LinkSetLibrary.Parse(value);
                    if (entry != null)
                        Models.Add(entry);
                    break;
            }
        }
    }
}
