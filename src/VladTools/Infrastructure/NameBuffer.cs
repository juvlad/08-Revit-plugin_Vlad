using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The name buffer: the fragments of names the user types over and over
    /// ("(FT)-FL_GOST 33259-2015", "DN" and the like).
    ///
    /// It lives in the Windows profile next to the formula list and survives closing Revit —
    /// save it once, then paste it into every family.
    /// File format: one line — one value. A line starting with a hash is a comment.
    /// </summary>
    internal static class NameBuffer
    {
        private static readonly string[] FileHeader =
        {
            "# VladTools name buffer — the \"Rename Nested\" button.",
            "# One line — one saved value, ready to be pasted into the window field.",
            "# A line starting with a hash is treated as a comment.",
            "# The file can be edited by hand — it is re-read every time the window opens."
        };

        /// <summary>%AppData%\VladTools\names.txt</summary>
        public static string FilePath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "names.txt");
            }
        }

        /// <summary>
        /// Reads the buffer. If the file does not exist yet or is corrupt, the buffer is simply
        /// empty: that cannot break the button.
        /// </summary>
        public static IReadOnlyList<string> Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new List<string>();

                return File.ReadAllLines(FilePath, Encoding.UTF8)
                    .Select(line => (line ?? string.Empty).Trim())
                    .Where(line => line.Length > 0 && line[0] != '#')
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>Rewrites the whole file.</summary>
        public static void Save(IEnumerable<string> values)
        {
            var lines = new List<string>(FileHeader) { string.Empty };

            lines.AddRange(values
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal));

            var folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            // The BOM keeps non-Latin names readable when the file is opened in Notepad.
            File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
        }
    }
}
