using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Parameter Sets" window settings: which set was worked with last, and which shared
    /// parameter file was last browsed for definitions.
    ///
    /// Worth storing for the same reason as every other remembered model/path in this add-in: a
    /// discipline works from one shared parameter file and one set, and there is no point asking
    /// again in the next project.
    ///
    /// File: `%AppData%\VladTools\parameters\_settings.txt`, lines of the form "KEY = value".
    /// It lies next to the sets themselves, which are `.txt` files too, so the "_settings" name is
    /// reserved from being offered as a set — the same rule as in <see cref="LinkSetLibrary"/>.
    /// </summary>
    internal sealed class ParameterSetPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Parameter Sets\" button settings — the Project panel.",
            "# SET — the set worked with last (a file of the same name, with a .txt extension, next to this one)",
            "# FILE — the shared parameter file last browsed for definitions",
            "# The file is rewritten every time the window closes."
        };

        /// <summary>The set worked with last; empty when one was never chosen.</summary>
        public string Set { get; set; } = string.Empty;

        /// <summary>The shared parameter file last browsed; empty on a first run.</summary>
        public string SharedParameterFile { get; set; } = string.Empty;

        public static string FilePath => Path.Combine(ParameterSetLibrary.FolderPath, "_settings.txt");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static ParameterSetPreferences Load()
        {
            var preferences = new ParameterSetPreferences();

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
                    Line("SET", Set ?? string.Empty),
                    Line("FILE", SharedParameterFile ?? string.Empty)
                };

                Directory.CreateDirectory(ParameterSetLibrary.FolderPath);

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
                case "SET":
                    Set = value;
                    break;

                case "FILE":
                    SharedParameterFile = value;
                    break;
            }
        }
    }
}
