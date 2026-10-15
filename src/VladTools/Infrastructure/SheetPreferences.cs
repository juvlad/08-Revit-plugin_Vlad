using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Sheets" windows' settings: which parameter is a sheet's number, where the last export went
    /// and whether to open it in Excel, and for a batch — where the last summary went and the model list
    /// from last time.
    ///
    /// The list is remembered for the same reason as in "Accept Changes → Other models…": a sheet report
    /// over a project's models is run again and again over the same models. A saved set
    /// (<see cref="LinkSetLibrary"/>, shared with "Link Manager") is the named, kept form of a list;
    /// this is only "what was there last time".
    ///
    /// File: `%AppData%\VladTools\sheets\_settings.txt`, lines of the form "KEY = value"; MODEL repeats.
    /// </summary>
    internal sealed class SheetPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Sheets\" button settings — the Project panel.",
            "# NUMBER — the shared parameter shown as a sheet's number: GUID | name; empty — Revit's own Sheet Number",
            "# NUMBER_FILE — the shared parameter file it was last picked from",
            "# FOLDER — the folder of the last export to Excel",
            "# OPEN_AFTER — 1/0: open the exported file in Excel",
            "# BATCH_FOLDER — the folder of the last batch summary (\"Several models…\")",
            "# SET — the link set worked with last in the batch window",
            "# MODEL — a model of the batch list from last time, in the same line format as in link sets:",
            "#   FILE | path · SERVER | RSN://… · CLOUD | region | project GUID | model GUID | name",
            "# The file is rewritten every time the window closes."
        };

        /// <summary>The parameter shown as a sheet's number, in the window, the export and a batch's reports alike.</summary>
        public SheetNumberParameter Number { get; set; } = SheetNumberParameter.Revit;

        /// <summary>The shared parameter file the number parameter was last picked from — opened again by the picker.</summary>
        public string NumberFile { get; set; } = string.Empty;

        public string Folder { get; set; } = string.Empty;

        public bool OpenAfter { get; set; } = true;

        /// <summary>The folder the last batch summary was saved in.</summary>
        public string BatchFolder { get; set; } = string.Empty;

        /// <summary>The link set worked with last in the batch window.</summary>
        public string Set { get; set; } = string.Empty;

        /// <summary>The models the batch window was closed with last time.</summary>
        public List<LinkEntry> Models { get; } = new List<LinkEntry>();

        /// <summary>%AppData%\VladTools\sheets</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "sheets");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static SheetPreferences Load()
        {
            var preferences = new SheetPreferences();

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
                    Line("NUMBER", (Number ?? SheetNumberParameter.Revit).Format()),
                    Line("NUMBER_FILE", NumberFile ?? string.Empty),
                    Line("FOLDER", Folder ?? string.Empty),
                    Line("OPEN_AFTER", OpenAfter ? "1" : "0"),
                    Line("BATCH_FOLDER", BatchFolder ?? string.Empty),
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

        /// <summary>The remembered folder, if it still exists.</summary>
        public string ExistingFolder()
        {
            return !string.IsNullOrEmpty(Folder) && Directory.Exists(Folder) ? Folder : null;
        }

        /// <summary>The folder of the last batch summary, else of the last export — if it still exists.</summary>
        public string ExistingBatchFolder()
        {
            return !string.IsNullOrEmpty(BatchFolder) && Directory.Exists(BatchFolder) ? BatchFolder : ExistingFolder();
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
                case "NUMBER":
                    Number = SheetNumberParameter.Parse(value);
                    break;
                case "NUMBER_FILE":
                    NumberFile = value;
                    break;
                case "FOLDER":
                    Folder = value;
                    break;
                case "OPEN_AFTER":
                    OpenAfter = value != "0";
                    break;
                case "BATCH_FOLDER":
                    BatchFolder = value;
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
