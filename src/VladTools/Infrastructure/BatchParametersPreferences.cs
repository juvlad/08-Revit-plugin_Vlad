using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Parameters" window's settings: the ParaManager file the parameters were taken from last, and the model
    /// list from last time.
    ///
    /// The list is remembered for the same reason as in the other batches: a run that broke off is run again
    /// from the same list, and models already holding the parameters are only counted the second time.
    /// Which parameters were checked is not remembered: a new file, or the same file edited, starts with every
    /// parameter that can be added checked.
    ///
    /// File: `%AppData%\VladTools\batchparameters\_settings.txt`, lines of the form "KEY = value"; MODEL repeats.
    /// The run's own reports go into `batchparameters\reports\` (<see cref="ReportsFolder"/>).
    /// </summary>
    internal sealed class BatchParametersPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Parameters\" button settings — the Project panel.",
            "# FILE — the DiRoots ParaManager Excel file the parameters were taken from last",
            "# SET — the link set worked with last",
            "# MODEL — a model of the list from last time, in the same line format as in link sets:",
            "#   FILE | path · SERVER | RSN://… · CLOUD | region | project GUID | model GUID | name",
            "# The file is rewritten every time the window closes."
        };

        /// <summary>The ParaManager file; empty on a first run.</summary>
        public string File { get; set; } = string.Empty;

        /// <summary>The link set worked with last.</summary>
        public string Set { get; set; } = string.Empty;

        /// <summary>The models the window was closed with last time.</summary>
        public List<LinkEntry> Models { get; } = new List<LinkEntry>();

        /// <summary>%AppData%\VladTools\batchparameters</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "batchparameters");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Where every run writes its full report — one text file per run.</summary>
        public static string ReportsFolder => Path.Combine(FolderPath, "reports");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static BatchParametersPreferences Load()
        {
            var preferences = new BatchParametersPreferences();

            try
            {
                if (!System.IO.File.Exists(FilePath))
                    return preferences;

                foreach (var line in System.IO.File.ReadAllLines(FilePath, Encoding.UTF8))
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
                    Line("FILE", File ?? string.Empty),
                    Line("SET", Set ?? string.Empty)
                };

                lines.AddRange(Models.Where(entry => entry != null)
                    .Select(entry => Line("MODEL", LinkSetLibrary.Format(entry))));

                Directory.CreateDirectory(FolderPath);

                // The BOM keeps non-Latin folder and model names readable when the file is opened in Notepad.
                System.IO.File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
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
                case "FILE":
                    File = value;
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
