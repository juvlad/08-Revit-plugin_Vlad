using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Translate" window settings: the folder the last export or import went through, so the
    /// file handed to a translator is found again without walking the disk.
    ///
    /// File: `%AppData%\VladTools\translate\_settings.txt`, lines of the form "KEY = value", next to
    /// the dictionary itself.
    /// </summary>
    internal sealed class TranslatePreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Translate\" button settings — the Project panel.",
            "# FOLDER — the folder the last export or import went through",
            "# The file is rewritten every time the window closes."
        };

        /// <summary>The folder of the last export or import; empty on a first run.</summary>
        public string Folder { get; set; } = string.Empty;

        public static string FilePath => Path.Combine(TranslationDictionary.FolderPath, "_settings.txt");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static TranslatePreferences Load()
        {
            var preferences = new TranslatePreferences();

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
                    "FOLDER " + Separator + " " + (Folder ?? string.Empty)
                };

                Directory.CreateDirectory(TranslationDictionary.FolderPath);

                // The BOM keeps non-Latin folder names readable when the file is opened in Notepad.
                File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
            }
            catch (Exception)
            {
            }
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

            if (key == "FOLDER")
                Folder = value;
        }
    }
}
