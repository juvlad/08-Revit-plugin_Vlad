using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// One saved formula: the parameter name, the formula itself and whether it is checked.
    /// </summary>
    internal sealed class FormulaEntry
    {
        public FormulaEntry(string parameterName, string formula, bool isEnabled = true)
        {
            ParameterName = parameterName ?? string.Empty;
            Formula = formula ?? string.Empty;
            IsEnabled = isEnabled;
        }

        public string ParameterName { get; }

        public string Formula { get; }

        public bool IsEnabled { get; }
    }

    /// <summary>
    /// The user's formula list. It lives in the Windows profile and survives closing Revit — so the
    /// same set is offered in every family that follows.
    ///
    /// File format: one line — one formula, the parameter name on the left, then an equals sign, then the formula.
    /// A line starting with a hash is read as an unchecked entry (and, if it has no equals sign, as a comment).
    /// </summary>
    internal static class FormulaLibrary
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# VladTools formula list — the \"Add Formulas\" button.",
            "# One line — one formula: parameter name, equals sign, formula.",
            "# A hash at the start of a line means the entry is unchecked in the window.",
            "# The file can be edited by hand — it is re-read every time the window opens."
        };

        /// <summary>The formulas the window opens with the very first time.</summary>
        public static IReadOnlyList<FormulaEntry> Defaults =>
            new List<FormulaEntry>
            {
                new FormulaEntry("ADSK_Размер_Диаметр", "if (1=1,PI_DN,0)"),
                new FormulaEntry("ADSK_Масса", "SP_Масса/1 кг")
            };

        /// <summary>%AppData%\VladTools\formulas.txt</summary>
        public static string FilePath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "formulas.txt");
            }
        }

        /// <summary>
        /// Reads the list. If the file does not exist yet (the first run) the defaults are returned.
        /// A corrupt file must not break the button, so a read error also yields the default list.
        /// </summary>
        public static IReadOnlyList<FormulaEntry> Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return Defaults;

                var entries = File.ReadAllLines(FilePath, Encoding.UTF8)
                    .Select(Parse)
                    .Where(entry => entry != null)
                    .ToList();

                return entries.Count > 0 ? (IReadOnlyList<FormulaEntry>)entries : Defaults;
            }
            catch (Exception)
            {
                return Defaults;
            }
        }

        /// <summary>Rewrites the whole file. Empty table rows are not saved.</summary>
        public static void Save(IEnumerable<FormulaEntry> entries)
        {
            var lines = new List<string>(FileHeader) { string.Empty };

            lines.AddRange(entries
                .Where(entry => entry != null)
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ParameterName) || !string.IsNullOrWhiteSpace(entry.Formula))
                .Select(Format));

            var folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            // The BOM keeps non-Latin names readable when the file is opened in Notepad.
            File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
        }

        private static string Format(FormulaEntry entry)
        {
            var line = entry.ParameterName.Trim() + " " + Separator + " " + entry.Formula.Trim();
            return entry.IsEnabled ? line : "# " + line;
        }

        /// <summary>Parses a file line. Returns null if it is a comment or garbage.</summary>
        private static FormulaEntry Parse(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0)
                return null;

            var isEnabled = true;
            if (text[0] == '#')
            {
                isEnabled = false;
                text = text.TrimStart('#').Trim();
            }

            // Only the first equals sign splits the line: later ones may belong to the formula, as in "if (1=1,…)".
            var separator = text.IndexOf(Separator);
            if (separator <= 0)
                return null;

            var name = text.Substring(0, separator).Trim();
            var formula = text.Substring(separator + 1).Trim();

            return name.Length == 0 || formula.Length == 0
                ? null
                : new FormulaEntry(name, formula, isEnabled);
        }
    }
}
