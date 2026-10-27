using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Upload to Server" window's settings: the Revit Server folder the models went into last,
    /// whether their links are updated afterwards, and the model list from last time.
    ///
    /// The list is remembered for a reason of its own here: a run over a hundred models takes hours,
    /// and when it breaks off — Revit crashes, the network drops — the next run starts from the same
    /// list, and every model already on the server is skipped by name.
    ///
    /// What to do with a model already on the server is deliberately **not** remembered: "Replace" left
    /// over from one migration would greet the next with overwriting a central model the team works in.
    ///
    /// File: `%AppData%\VladTools\serverupload\_settings.txt`, lines of the form "KEY = value"; MODEL repeats.
    /// The run's own reports go into `serverupload\reports\` (<see cref="ReportsFolder"/>).
    /// </summary>
    internal sealed class ServerUploadPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Upload to Server\" button settings — the Project panel.",
            "# FOLDER — the Revit Server folder the models went into last, as SERVER | RSN://server/folder",
            "# LINKS — 1/0: update the links in the uploaded models afterwards",
            "# SUBFOLDERS — 1/0: recreate the files' subfolders below the Revit Server folder",
            "# SET — the link set worked with last",
            "# MODEL — a model of the list from last time, in the same line format as in link sets:",
            "#   FILE | path · SERVER | RSN://…",
            "# The file is rewritten every time the window closes."
        };

        /// <summary>The destination as a <see cref="ModelFolder.Format"/> line; empty on a first run.</summary>
        public string Folder { get; set; } = string.Empty;

        public bool UpdateLinks { get; set; } = true;

        /// <summary>Recreate the subfolders the files lie in below the destination, rather than put every file straight into it.</summary>
        public bool KeepFolders { get; set; } = true;

        /// <summary>The link set worked with last.</summary>
        public string Set { get; set; } = string.Empty;

        /// <summary>The models the window was closed with last time.</summary>
        public List<LinkEntry> Models { get; } = new List<LinkEntry>();

        /// <summary>%AppData%\VladTools\serverupload</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "serverupload");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Where every run writes its full report — one text file per run.</summary>
        public static string ReportsFolder => Path.Combine(FolderPath, "reports");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static ServerUploadPreferences Load()
        {
            var preferences = new ServerUploadPreferences();

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
                    Line("FOLDER", Folder ?? string.Empty),
                    Line("LINKS", UpdateLinks ? "1" : "0"),
                    Line("SUBFOLDERS", KeepFolders ? "1" : "0"),
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
                case "FOLDER":
                    Folder = value;
                    break;
                case "LINKS":
                    UpdateLinks = value != "0";
                    break;
                case "SUBFOLDERS":
                    KeepFolders = value != "0";
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
