using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Link Updater" windows' settings: the server folder searched last by hand, and the batch's
    /// model list.
    ///
    /// The folder is a fallback, not the main road — the search normally starts from where the open
    /// model itself lies, and needs the remembered folder only when that cannot be worked out: the model
    /// is still in BIM360, a local file, or open detached. The next model of the same migration then
    /// opens on the same folder.
    ///
    /// The batch's list is remembered for the reason "Upload to Server" remembers its own: a run over a
    /// whole server takes hours, and one that broke off is started again over the same list — a model
    /// whose links already point at the server only counts them.
    ///
    /// The Revit Server names typed in the browser are not kept here: they live in
    /// <c>links\_settings.txt</c>, shared with "Link Manager" and "Base File".
    ///
    /// File: `%AppData%\VladTools\linkupdater\_settings.txt`, lines of the form "KEY = value"; MODEL repeats.
    /// The batch's own reports go into `linkupdater\reports\` (<see cref="ReportsFolder"/>).
    /// </summary>
    internal sealed class LinkUpdaterPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Link Updater\" button settings — the Project panel.",
            "# ROOT — the Revit Server folder searched last by hand, as SERVER | RSN://server/folder;",
            "#        used only when the open model does not lie on a Revit Server itself",
            "# SET — the link set the \"Several models…\" window worked with last",
            "# MODEL — a model of that window's list from last time, in the link-set line format: SERVER | RSN://…",
            "# The file is rewritten every time one of the windows closes."
        };

        /// <summary>The folder as a <see cref="ModelFolder.Format"/> line; empty on a first run.</summary>
        public string Root { get; set; } = string.Empty;

        /// <summary>The link set the batch window worked with last.</summary>
        public string Set { get; set; } = string.Empty;

        /// <summary>The models the batch window was closed with last time.</summary>
        public List<LinkEntry> Models { get; } = new List<LinkEntry>();

        /// <summary>%AppData%\VladTools\linkupdater</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "linkupdater");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Where every batch run writes its full report — one text file per run.</summary>
        public static string ReportsFolder => Path.Combine(FolderPath, "reports");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static LinkUpdaterPreferences Load()
        {
            var preferences = new LinkUpdaterPreferences();

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
                var lines = new List<string>(FileHeader)
                {
                    string.Empty,
                    Line("ROOT", Root ?? string.Empty),
                    Line("SET", Set ?? string.Empty)
                };

                lines.AddRange(Models.Where(entry => entry != null)
                    .Select(entry => Line("MODEL", LinkSetLibrary.Format(entry))));

                Directory.CreateDirectory(FolderPath);

                // The BOM keeps non-Latin folder names readable when the file is opened in Notepad.
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
                case "ROOT":
                    Root = value;
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
