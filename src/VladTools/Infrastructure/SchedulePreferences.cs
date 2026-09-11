using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Schedule Library" window settings: which set was worked with last, which model the
    /// schedules were taken from, and what to do by default when a name clashes.
    ///
    /// Worth storing for the same reason as the "Link Manager" settings: a discipline has one set of
    /// schedules, and it is the same one in the next model — there is no point choosing it again.
    ///
    /// File: `%AppData%\VladTools\schedules\_settings.txt`, lines of the form "KEY = value".
    /// It lies next to the sets themselves, which are .rvt files, so the names cannot clash.
    /// </summary>
    internal sealed class SchedulePreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Schedule Library\" button settings — the Project panel.",
            "# SET — the set worked with last (a file of the same name, with a .rvt extension, next to this one)",
            "# MODEL — the model the schedules were last taken from, in the same line format as in link sets:",
            "#   FILE | path · SERVER | RSN://… · CLOUD | region | project GUID | model GUID | name",
            "# ACTION — Skip | Replace | AddCopy: what to do by default when the project already holds",
            "#   a schedule of that name. Skip by default — a button that inserts must not quietly",
            "#   delete somebody's schedule.",
            "# The file is rewritten every time the window closes."
        };

        /// <summary>The set worked with last; empty when one was never chosen.</summary>
        public string Set { get; set; } = string.Empty;

        /// <summary>The model the schedules were last taken from; null for the open project or a first run.</summary>
        public LinkEntry Model { get; set; }

        /// <summary>
        /// What to do by default when a name clashes. Deliberately <see cref="ScheduleAction.Skip"/>:
        /// the button is asked to insert, and replacing means deleting a schedule that may be sitting
        /// on a sheet — never a default.
        /// </summary>
        public ScheduleAction Action { get; set; } = ScheduleAction.Skip;

        public static string FilePath => Path.Combine(ScheduleLibrary.FolderPath, "_settings.txt");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static SchedulePreferences Load()
        {
            var preferences = new SchedulePreferences();

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
                var lines = new List<string>(FileHeader) { string.Empty };

                lines.Add(Line("SET", Set ?? string.Empty));

                if (Model != null)
                    lines.Add(Line("MODEL", LinkSetLibrary.Format(Model)));

                lines.Add(Line("ACTION", Action.ToString()));

                Directory.CreateDirectory(ScheduleLibrary.FolderPath);

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

                case "MODEL":
                    Model = LinkSetLibrary.Parse(value);
                    break;

                case "ACTION":
                    ScheduleAction action;
                    if (Enum.TryParse(value, true, out action))
                        Action = action;
                    break;
            }
        }
    }
}
