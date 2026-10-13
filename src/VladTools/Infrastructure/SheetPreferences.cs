using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Sheets" window's settings: where the last export went and whether to open it in Excel.
    ///
    /// File: `%AppData%\VladTools\sheets\_settings.txt`, lines of the form "KEY = value".
    /// </summary>
    internal sealed class SheetPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Sheets\" button settings — the Project panel.",
            "# FOLDER — the folder of the last export to Excel",
            "# OPEN_AFTER — 1/0: open the exported file in Excel",
            "# The file is rewritten every time the window closes."
        };

        public string Folder { get; set; } = string.Empty;

        public bool OpenAfter { get; set; } = true;

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
                    Line("FOLDER", Folder ?? string.Empty),
                    Line("OPEN_AFTER", OpenAfter ? "1" : "0")
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
                case "OPEN_AFTER":
                    OpenAfter = value != "0";
                    break;
            }
        }
    }
}
