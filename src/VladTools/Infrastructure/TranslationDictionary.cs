using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Translate" button's dictionary — every Russian text ever translated, with its English —
    /// and the file format the button exports and imports for translating outside Revit.
    ///
    /// Both are the same format: one line per text, the original and the translation separated by a
    /// TAB (the export adds a third column, where the text is used, as a hint for whoever translates;
    /// it is ignored on the way back). A TAB rather than the vertical bar the rest of the add-in
    /// uses, for two reasons: a bar can stand in a name or a note, a TAB essentially never does; and
    /// Excel opens a TAB-separated file straight into two columns.
    ///
    /// Line breaks and TABs inside a text are written as "\n" and "\t", a backslash as "\\", and a
    /// leading "#" as "\#" — otherwise the line would read back as a comment.
    ///
    /// Files: `%AppData%\VladTools\translate\dictionary.txt` (the dictionary itself, rewritten when
    /// the window closes) and whatever the user names on export.
    /// </summary>
    internal static class TranslationDictionary
    {
        private const char Separator = '\t';

        private static readonly string[] DictionaryHeader =
        {
            "# VladTools translation dictionary — the \"Translate\" button, Project panel.",
            "# One line per text: the Russian original, a TAB, the English translation.",
            "# Inside a text, \\n is a line break, \\t a tab and \\\\ a backslash.",
            "# Every translation in the window lands here when it closes, and is offered in the next project.",
            "# The file can be edited by hand — it is re-read every time the window opens."
        };

        /// <summary>%AppData%\VladTools\translate</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "translate");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "dictionary.txt");

        /// <summary>
        /// The form a text is compared and stored in: line breaks as "\n", no surrounding whitespace.
        /// One rule for the project's texts, the dictionary and the imported files — otherwise a text
        /// note ending in Revit's own "\r" would never meet its translation.
        /// </summary>
        public static string Normalize(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        /// <summary>Russian, and every other Cyrillic script along with it — the U+0400–U+052F blocks.</summary>
        public static bool HasCyrillic(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (var character in text)
            {
                if (character >= 'Ѐ' && character <= 'ԯ')
                    return true;
            }

            return false;
        }

        /// <summary>The dictionary. No file, or one that cannot be read, is an empty dictionary — never a reason not to open the window.</summary>
        public static Dictionary<string, string> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return Read(FilePath, out _);
            }
            catch (Exception)
            {
            }

            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Rewrites the dictionary. Unlike the add-in's settings files, a failure here is thrown to
        /// the caller: these are translations somebody typed, and losing them must not be silent.
        /// </summary>
        public static void Save(IReadOnlyDictionary<string, string> entries)
        {
            Directory.CreateDirectory(FolderPath);

            var rows = entries
                .Where(entry => entry.Key.Length > 0 && entry.Value.Length > 0)
                .OrderBy(entry => entry.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(entry => new[] { entry.Key, entry.Value });

            Write(FilePath, DictionaryHeader, rows);
        }

        /// <summary>
        /// Reads a dictionary or an exported file: original → translation, both normalised. A line
        /// with no translation is skipped — an empty second column means "not translated yet".
        /// </summary>
        /// <param name="unreadable">Lines that are neither empty nor a comment and carry no TAB — the columns ran together somewhere.</param>
        public static Dictionary<string, string> Read(string path, out int unreadable)
        {
            unreadable = 0;
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);

            // ReadAllLines honours a byte order mark, so a file Excel saved as "Unicode Text"
            // (UTF-16) reads just as well as our own UTF-8 one.
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    continue;

                var columns = line.Split(Separator);
                if (columns.Length < 2)
                {
                    unreadable++;
                    continue;
                }

                var original = Normalize(Unescape(ExcelUnquote(columns[0])));
                var translation = Normalize(Unescape(ExcelUnquote(columns[1])));

                if (original.Length > 0 && translation.Length > 0)
                    entries[original] = translation;
            }

            return entries;
        }

        /// <summary>Writes a header and rows of columns, escaped. UTF-8 with a BOM, like every file of the add-in.</summary>
        public static void Write(string path, IEnumerable<string> header, IEnumerable<IReadOnlyList<string>> rows)
        {
            var lines = new List<string>(header) { string.Empty };
            lines.AddRange(rows.Select(columns => string.Join(Separator.ToString(), columns.Select(Escape))));

            // The BOM keeps Cyrillic readable when the file is opened in Notepad or Excel.
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
        }

        public static string Escape(string text)
        {
            var escaped = Normalize(text)
                .Replace("\\", "\\\\")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");

            return escaped.StartsWith("#", StringComparison.Ordinal) ? "\\" + escaped : escaped;
        }

        public static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0)
                return text ?? string.Empty;

            var result = new StringBuilder(text.Length);

            for (var i = 0; i < text.Length; i++)
            {
                var character = text[i];
                if (character != '\\' || i + 1 == text.Length)
                {
                    result.Append(character);
                    continue;
                }

                var next = text[++i];
                switch (next)
                {
                    case 'n':
                        result.Append('\n');
                        break;
                    case 't':
                        result.Append('\t');
                        break;
                    case '\\':
                    case '#':
                        result.Append(next);
                        break;
                    default:
                        // Not one of ours — a path, say. Kept exactly as it was.
                        result.Append('\\').Append(next);
                        break;
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Undoes the quoting Excel adds when it saves a cell that holds a quote:
        /// <c>value of "A"</c> comes back as <c>"value of ""A"""</c>. Only a field that is wrapped in
        /// quotes **and** has a doubled quote inside is touched — a text that genuinely starts and
        /// ends with a quote (<c>"Ось"</c>) has none, and is left alone.
        /// </summary>
        private static string ExcelUnquote(string field)
        {
            if (field == null || field.Length < 2 || field[0] != '"' || field[field.Length - 1] != '"')
                return field;

            var inner = field.Substring(1, field.Length - 2);
            return inner.Contains("\"\"") ? inner.Replace("\"\"", "\"") : field;
        }
    }
}
