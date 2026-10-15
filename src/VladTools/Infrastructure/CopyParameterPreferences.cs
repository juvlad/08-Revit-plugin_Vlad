using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Copy Parameters" window's settings: the category and the two parameters used last, and the
    /// four check boxes. The same copy is usually repeated — after every new batch of doors, in every
    /// model of a building — so the window opens on it. The scope is not kept: it follows what is
    /// selected when the button is pressed.
    ///
    /// File: `%AppData%\VladTools\copyparams\_settings.txt`, lines of the form "KEY = value".
    /// </summary>
    internal sealed class CopyParameterPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Copy Parameters\" button settings — the Project panel.",
            "# CATEGORY — the category used last, by its name",
            "# FROM / TO — the two parameters: I|… an instance one, T|… a type one; BIP|<built-in id>, GUID|<shared GUID>, NAME|<name>",
            "# ONLY_EMPTY, EMPTY_CLEARS, NO_SYMBOL, NUMBER_FROM_TEXT, SHOW_READONLY — the window's check boxes, 1/0",
            "# The file is rewritten every time the window closes."
        };

        public string Category { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public string Target { get; set; } = string.Empty;

        public bool OnlyEmptyTargets { get; set; }

        public bool EmptyClears { get; set; }

        public bool NoUnitSymbol { get; set; }

        public bool NumberFromText { get; set; }

        public bool ShowReadOnly { get; set; }

        /// <summary>%AppData%\VladTools\copyparams</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "copyparams");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static CopyParameterPreferences Load()
        {
            var preferences = new CopyParameterPreferences();

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
                    Line("CATEGORY", Category ?? string.Empty),
                    Line("FROM", Source ?? string.Empty),
                    Line("TO", Target ?? string.Empty),
                    Line("ONLY_EMPTY", Flag(OnlyEmptyTargets)),
                    Line("EMPTY_CLEARS", Flag(EmptyClears)),
                    Line("NO_SYMBOL", Flag(NoUnitSymbol)),
                    Line("NUMBER_FROM_TEXT", Flag(NumberFromText)),
                    Line("SHOW_READONLY", Flag(ShowReadOnly))
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

        private static string Flag(bool value)
        {
            return value ? "1" : "0";
        }

        private void Apply(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return;

            // Split at the first sign only: a parameter's name may hold one of its own.
            var separator = text.IndexOf(Separator);
            if (separator <= 0)
                return;

            var key = text.Substring(0, separator).Trim().ToUpperInvariant();
            var value = text.Substring(separator + 1).Trim();

            switch (key)
            {
                case "CATEGORY":
                    Category = value;
                    break;
                case "FROM":
                    Source = value;
                    break;
                case "TO":
                    Target = value;
                    break;
                case "ONLY_EMPTY":
                    OnlyEmptyTargets = value == "1";
                    break;
                case "EMPTY_CLEARS":
                    EmptyClears = value == "1";
                    break;
                case "NO_SYMBOL":
                    NoUnitSymbol = value == "1";
                    break;
                case "NUMBER_FROM_TEXT":
                    NumberFromText = value == "1";
                    break;
                case "SHOW_READONLY":
                    ShowReadOnly = value == "1";
                    break;
            }
        }
    }
}
