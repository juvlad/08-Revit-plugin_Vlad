using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Excel" button's settings: where the last file went, how an export is laid out, and the
    /// import's two switches — so the next export lands in the same folder and looks the same.
    ///
    /// File: `%AppData%\VladTools\excel\_settings.txt`, lines of the form "KEY = value".
    /// </summary>
    internal sealed class ExcelPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Excel\" button settings — the Project panel.",
            "# FOLDER — the folder of the last export or import",
            "# LAYOUT — AsInRevit (headings, group lines and totals, as the schedule draws them) or PlainTable",
            "#   (one heading line and one line per row, with filter buttons)",
            "# OPEN_AFTER — 1/0: open the exported file in Excel",
            "# EMPTY_CLEARS — 1/0: an empty cell in the file clears the value in Revit",
            "# SELECT_AFTER — 1/0: after an import, open the schedule and select the changed elements",
            "# The file is rewritten every time one of the button's windows closes."
        };

        public string Folder { get; set; } = string.Empty;

        public ScheduleExportLayout Layout { get; set; } = ScheduleExportLayout.AsInRevit;

        public bool OpenAfter { get; set; } = true;

        /// <summary>
        /// Off by default: an empty cell is far more often a column nobody filled in, or one Excel lost,
        /// than a deliberate "delete this value" — and an import that wipes values out on a guess is the
        /// one kind of mistake the preview may not be looked at closely enough to catch.
        /// </summary>
        public bool EmptyClears { get; set; }

        public bool SelectAfter { get; set; } = true;

        /// <summary>%AppData%\VladTools\excel</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "excel");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static ExcelPreferences Load()
        {
            var preferences = new ExcelPreferences();

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
                    Line("LAYOUT", Layout.ToString()),
                    Line("OPEN_AFTER", OpenAfter ? "1" : "0"),
                    Line("EMPTY_CLEARS", EmptyClears ? "1" : "0"),
                    Line("SELECT_AFTER", SelectAfter ? "1" : "0")
                };

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
                case "LAYOUT":
                    if (Enum.TryParse(value, true, out ScheduleExportLayout layout))
                        Layout = layout;
                    break;
                case "OPEN_AFTER":
                    OpenAfter = value != "0";
                    break;
                case "EMPTY_CLEARS":
                    EmptyClears = value == "1";
                    break;
                case "SELECT_AFTER":
                    SelectAfter = value != "0";
                    break;
            }
        }
    }
}
