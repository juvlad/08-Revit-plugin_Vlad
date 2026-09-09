using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>What the scan found in a single family.</summary>
    internal sealed class FamilyLabelRecord
    {
        public FamilyLabelRecord(string uniqueId, string version, IReadOnlyList<string> parameterGuids)
        {
            UniqueId = uniqueId;
            Version = version;
            ParameterGuids = parameterGuids ?? new List<string>();
        }

        /// <summary>The family UniqueId — it survives saving and working with shared files.</summary>
        public string UniqueId { get; }

        /// <summary>The family `Element.VersionGuid` at the moment of the scan.</summary>
        public string Version { get; }

        /// <summary>GUIDs of the shared parameters that label dimensions in this family.</summary>
        public IReadOnlyList<string> ParameterGuids { get; }
    }

    /// <summary>
    /// The saved result of scanning families for dimension labels — so that hundreds of families do
    /// not have to be reopened every time the "Delete Shared Parameters" window is opened.
    ///
    /// One file per project: `%AppData%\VladTools\dimensions\&lt;name&gt;_&lt;path hash&gt;.txt`.
    /// One line per family, so the scan is incremental: only the families that are missing from the
    /// file or whose version has changed are reopened.
    ///
    /// **The version does not catch everything.** Per the Revit documentation `Element.VersionGuid`
    /// changes on save and synchronisation, not on every edit: a family reloaded during the current
    /// session without saving the project looks unchanged to the cache. That is why the window has a
    /// "Scan again" mode — it ignores the cache.
    ///
    /// A project without a path (never saved) is not cached: there is no key.
    /// </summary>
    internal static class DimensionLabelCache
    {
        private const char Separator = '|';

        private static readonly string[] FileHeader =
        {
            "# Family scan for dimension labels — the \"Delete Shared Parameters\" button (the Project panel).",
            "# Line: family UniqueId | element version | shared parameter GUIDs, comma separated.",
            "# An empty GUID list means: the family was scanned and has no parameters on dimensions.",
            "# This is a cache. The file can be deleted — the scan will simply run again."
        };

        /// <summary>The folder holding all the scan files.</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "dimensions");
            }
        }

        /// <summary>The scan file for a particular project; null for a project that was never saved.</summary>
        public static string FilePathFor(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return null;

            var name = Path.GetFileNameWithoutExtension(projectPath) ?? string.Empty;

            foreach (var forbidden in Path.GetInvalidFileNameChars())
                name = name.Replace(forbidden, '_');

            if (name.Length > 40)
                name = name.Substring(0, 40);

            // The path hash keeps two different files with the same name from sharing one scan.
            return Path.Combine(FolderPath, name + "_" + Hash(projectPath) + ".txt");
        }

        /// <summary>When the scan was saved; null if there is no file.</summary>
        public static DateTime? SavedAt(string projectPath)
        {
            try
            {
                var path = FilePathFor(projectPath);
                return path != null && File.Exists(path) ? File.GetLastWriteTime(path) : (DateTime?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads the saved scan, keyed by family UniqueId.
        /// No file or a corrupt one yields an empty dictionary: the scan simply runs again.
        /// </summary>
        public static Dictionary<string, FamilyLabelRecord> Load(string projectPath)
        {
            var result = new Dictionary<string, FamilyLabelRecord>(StringComparer.Ordinal);

            try
            {
                var path = FilePathFor(projectPath);
                if (path == null || !File.Exists(path))
                    return result;

                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var record = Parse(line);
                    if (record != null)
                        result[record.UniqueId] = record;
                }
            }
            catch (Exception)
            {
                result.Clear();
            }

            return result;
        }

        /// <summary>
        /// Rewrites the whole file — with only what is loaded into the project right now, otherwise
        /// the file would grow for ever on families that were unloaded long ago.
        /// A failed write is no great loss: the scan will just have to run once more.
        /// </summary>
        public static void Save(string projectPath, IEnumerable<FamilyLabelRecord> records)
        {
            try
            {
                var path = FilePathFor(projectPath);
                if (path == null)
                    return;

                var lines = new List<string>(FileHeader) { "# Project: " + projectPath, string.Empty };

                lines.AddRange(records
                    .Where(record => !string.IsNullOrEmpty(record?.UniqueId) && !string.IsNullOrEmpty(record.Version))
                    .Select(record => record.UniqueId + Separator + record.Version + Separator +
                                      string.Join(",", record.ParameterGuids)));

                Directory.CreateDirectory(FolderPath);

                // The BOM keeps non-Latin text in the header readable when opened in Notepad.
                File.WriteAllLines(path, lines, new UTF8Encoding(true));
            }
            catch (Exception)
            {
                // A cache is not the command's output: if it does not get written, so be it.
            }
        }

        private static FamilyLabelRecord Parse(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return null;

            var parts = text.Split(Separator);
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0)
                return null;

            var guids = parts[2]
                .Split(',')
                .Select(guid => guid.Trim())
                .Where(guid => guid.Length > 0)
                .ToList();

            return new FamilyLabelRecord(parts[0], parts[1], guids);
        }

        private static string Hash(string projectPath)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(projectPath.ToLowerInvariant()));
                return BitConverter.ToString(bytes, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
