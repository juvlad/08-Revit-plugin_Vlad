using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Sheet Scale" window's settings: the scale set last and what to do with view templates that
    /// set the scale. The "change only the views at" choice is deliberately not kept: remembered, it
    /// would open the next run with most of the views greyed out for a reason nobody chose that day.
    ///
    /// File: `%AppData%\VladTools\sheetscale\_settings.txt`, lines of the form "KEY = value".
    /// </summary>
    internal sealed class SheetScalePreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Sheet Scale\" button settings — the Project panel.",
            "# SCALE — the new scale typed or picked last (\"1:200\")",
            "# TEMPLATES — Release / ChangeTemplate / Leave: what to do with a view whose view template sets the scale",
            "# The file is rewritten every time the window closes."
        };

        public string Scale { get; set; } = string.Empty;

        public ScaleTemplateMode Templates { get; set; } = ScaleTemplateMode.Release;

        /// <summary>%AppData%\VladTools\sheetscale</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "sheetscale");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static SheetScalePreferences Load()
        {
            var preferences = new SheetScalePreferences();

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
                    Line("SCALE", Scale ?? string.Empty),
                    Line("TEMPLATES", Templates.ToString())
                };

                Directory.CreateDirectory(FolderPath);
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
                case "SCALE":
                    Scale = value;
                    break;
                case "TEMPLATES":
                    Templates = ScaleTemplateModeText.Parse(value, ScaleTemplateMode.Release);
                    break;
            }
        }
    }
}
