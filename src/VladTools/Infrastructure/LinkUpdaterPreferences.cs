using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Link Updater" window's settings: the server folder searched last by hand. It is a fallback,
    /// not the main road — the search normally starts from where the open model itself lies, and needs
    /// the remembered folder only when that cannot be worked out: the model is still in BIM360, a local
    /// file, or open detached. The next model of the same migration then opens on the same folder.
    ///
    /// The Revit Server names typed in the browser are not kept here: they live in
    /// <c>links\_settings.txt</c>, shared with "Link Manager" and "Base File".
    ///
    /// File: `%AppData%\VladTools\linkupdater\_settings.txt`, lines of the form "KEY = value".
    /// </summary>
    internal sealed class LinkUpdaterPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Link Updater\" button settings — the Project panel.",
            "# ROOT — the Revit Server folder searched last by hand, as SERVER | RSN://server/folder;",
            "#        used only when the open model does not lie on a Revit Server itself",
            "# The file is rewritten every time the window closes."
        };

        /// <summary>The folder as a <see cref="ModelFolder.Format"/> line; empty on a first run.</summary>
        public string Root { get; set; } = string.Empty;

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
                    "ROOT " + Separator + " " + (Root ?? string.Empty)
                };

                Directory.CreateDirectory(FolderPath);

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

            if (key == "ROOT")
                Root = value;
        }
    }
}
