using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>Whether a parameter set entry is bound per instance or per type — mirrors Revit's own choice.</summary>
    internal enum ParameterBindingKind
    {
        Instance,
        Type
    }

    /// <summary>
    /// One parameter in a saved set: which shared parameter (by GUID — the only thing that survives a
    /// rename in the shared parameter file), and how it is meant to be bound in every project that
    /// takes the set: instance or type, which categories, which parameter group, and — for an instance
    /// parameter — whether its value is allowed to vary between the instances of a model group.
    ///
    /// The categories are kept as <see cref="Autodesk.Revit.DB.BuiltInCategory"/> names, not
    /// <c>Category.Name</c>: a project can be in any language, but the enum name is fixed English text —
    /// the same reasoning as storing a dimension chain kind or a project workset rule by name elsewhere
    /// in this add-in. The parameter group is kept as a <c>ForgeTypeId.TypeId</c> string, the same handle
    /// <c>Definition.GetGroupTypeId()</c> already returns everywhere else in the project, across every
    /// supported Revit year.
    /// </summary>
    internal sealed class ParameterEntry
    {
        public ParameterEntry(
            Guid guid,
            string name,
            ParameterBindingKind binding,
            bool variesAcrossGroups,
            IEnumerable<string> categories,
            string groupTypeId)
        {
            Guid = guid;
            Name = name ?? string.Empty;
            Binding = binding;
            VariesAcrossGroups = variesAcrossGroups;
            Categories = (categories ?? Enumerable.Empty<string>())
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                .ToList();
            GroupTypeId = groupTypeId ?? string.Empty;
        }

        /// <summary>The shared parameter's own identity — a name in the shared file can change, this cannot.</summary>
        public Guid Guid { get; }

        /// <summary>The name as last seen in the shared parameter file — a label only, never looked up by.</summary>
        public string Name { get; set; }

        public ParameterBindingKind Binding { get; set; }

        /// <summary>
        /// Meaningless for a type-bound parameter — Revit only asks the question of an instance one
        /// ("Values can vary by group instance").
        /// </summary>
        public bool VariesAcrossGroups { get; set; }

        /// <summary><see cref="Autodesk.Revit.DB.BuiltInCategory"/> names, sorted, with no duplicates.</summary>
        public IReadOnlyList<string> Categories { get; set; }

        /// <summary>A <c>ForgeTypeId.TypeId</c> string, or empty for "no group" (Revit's own "Other" bucket).</summary>
        public string GroupTypeId { get; set; }
    }

    /// <summary>
    /// Saved parameter sets: a discipline's own bundle of shared parameters, built once and offered in
    /// every project after — "Parameter Sets" button (the Project panel).
    ///
    /// One file per set, the same shape as <see cref="LinkSetLibrary"/>: fields separated by a vertical
    /// bar, because it cannot occur in a parameter name or a GUID. Kept in a folder of its own,
    /// `%AppData%\VladTools\parameters\`, next to `_settings.txt` (see <see cref="ParameterSetPreferences"/>).
    /// </summary>
    internal static class ParameterSetLibrary
    {
        private const char Separator = '|';

        private static readonly string[] FileHeader =
        {
            "# VladTools parameter set — the \"Parameter Sets\" button (the Project panel).",
            "# One line per parameter, fields separated by a vertical bar:",
            "#   PARAM | GUID | name | Instance|Type | vary across groups: 1|0 | group ForgeTypeId | categories, comma separated",
            "# The GUID is the parameter's real identity — it is looked up in the project and in the",
            "# shared parameter file by GUID, never by name; the name is only a label kept for reading.",
            "# Categories are Autodesk.Revit.DB.BuiltInCategory names (OST_Walls, and the like) — the same",
            "# name in every language Revit runs in.",
            "# The file can be edited by hand — it is re-read every time the window opens."
        };

        /// <summary>%AppData%\VladTools\parameters</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "parameters");
            }
        }

        /// <summary>The names of the saved sets in alphabetical order. An empty list if the folder does not exist yet.</summary>
        public static IReadOnlyList<string> Names()
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                    return new List<string>();

                return Directory.GetFiles(FolderPath, "*.txt")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrEmpty(name) && name[0] != '_')
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>Reads a set. No file or a corrupt one yields an empty list: that cannot break the button.</summary>
        public static IReadOnlyList<ParameterEntry> Load(string setName)
        {
            try
            {
                var file = FilePathFor(setName);
                if (file == null || !File.Exists(file))
                    return new List<ParameterEntry>();

                return File.ReadAllLines(file, Encoding.UTF8)
                    .Select(Parse)
                    .Where(entry => entry != null)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<ParameterEntry>();
            }
        }

        /// <summary>Rewrites the whole set.</summary>
        public static void Save(string setName, IEnumerable<ParameterEntry> entries)
        {
            var file = FilePathFor(setName);
            if (file == null)
                throw new ArgumentException("The set name is empty or consists only of forbidden characters.");

            var lines = new List<string>(FileHeader) { string.Empty };
            lines.AddRange(entries.Where(entry => entry != null).Select(Format));

            Directory.CreateDirectory(FolderPath);

            // The BOM keeps non-Latin names readable when the file is opened in Notepad.
            File.WriteAllLines(file, lines, new UTF8Encoding(true));
        }

        public static void Delete(string setName)
        {
            var file = FilePathFor(setName);
            if (file != null && File.Exists(file))
                File.Delete(file);
        }

        /// <summary>The path to the set file; null if the name is empty or made only of forbidden characters.</summary>
        public static string FilePathFor(string setName)
        {
            var name = (setName ?? string.Empty).Trim();
            if (name.Length == 0)
                return null;

            foreach (var forbidden in Path.GetInvalidFileNameChars())
                name = name.Replace(forbidden, '_');

            name = name.Trim('_', ' ');

            return name.Length == 0 ? null : Path.Combine(FolderPath, name + ".txt");
        }

        // ───────────────────────────── a file line ─────────────────────────────

        private static string Format(ParameterEntry entry)
        {
            return string.Join(Separator.ToString(),
                "PARAM",
                entry.Guid.ToString(),
                entry.Name,
                entry.Binding.ToString(),
                entry.VariesAcrossGroups ? "1" : "0",
                entry.GroupTypeId,
                string.Join(",", entry.Categories));
        }

        private static ParameterEntry Parse(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return null;

            var fields = text.Split(Separator);
            if (fields.Length < 7 || !string.Equals(fields[0].Trim(), "PARAM", StringComparison.OrdinalIgnoreCase))
                return null;

            Guid guid;
            if (!Guid.TryParse(fields[1].Trim(), out guid))
                return null;

            var name = fields[2].Trim();

            ParameterBindingKind binding;
            if (!Enum.TryParse(fields[3].Trim(), true, out binding))
                binding = ParameterBindingKind.Instance;

            var vary = fields[4].Trim() == "1";
            var groupTypeId = fields[5].Trim();

            var categories = fields[6].Split(',')
                .Select(category => category.Trim())
                .Where(category => category.Length > 0);

            return new ParameterEntry(guid, name, binding, vary, categories, groupTypeId);
        }
    }
}
