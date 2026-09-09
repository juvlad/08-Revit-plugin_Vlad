using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>Where a link model comes from. Both the path and the way it is written to file depend on it.</summary>
    internal enum LinkOrigin
    {
        /// <summary>An ordinary file: a disk or a network folder.</summary>
        File,

        /// <summary>Revit Server: a path of the form RSN://server/folder/model.rvt.</summary>
        Server,

        /// <summary>BIM360/ACC: a region and two GUIDs — the project's and the model's.</summary>
        Cloud
    }

    /// <summary>
    /// One model in a saved set. For a file and for Revit Server everything rests on the path; a cloud
    /// model has no path at all: it is addressed by a region and a pair of GUIDs, and its name is stored
    /// separately — otherwise there would be no telling one list entry from another.
    /// </summary>
    internal sealed class LinkEntry
    {
        public LinkEntry(LinkOrigin origin, string name, string path, string region, string projectGuid, string modelGuid)
        {
            Origin = origin;
            Name = name ?? string.Empty;
            Path = path ?? string.Empty;
            Region = region ?? string.Empty;
            ProjectGuid = projectGuid ?? string.Empty;
            ModelGuid = modelGuid ?? string.Empty;
        }

        public static LinkEntry ForFile(string path)
        {
            return new LinkEntry(LinkOrigin.File, System.IO.Path.GetFileName(path), path, null, null, null);
        }

        public static LinkEntry ForServer(string rsnPath)
        {
            var name = (rsnPath ?? string.Empty).Split('/').LastOrDefault() ?? string.Empty;
            return new LinkEntry(LinkOrigin.Server, name, rsnPath, null, null, null);
        }

        public static LinkEntry ForCloud(string region, string projectGuid, string modelGuid, string name)
        {
            return new LinkEntry(LinkOrigin.Cloud, name, null, region, projectGuid, modelGuid);
        }

        public LinkOrigin Origin { get; }

        /// <summary>The model name including the extension — what is visible in the table.</summary>
        public string Name { get; }

        /// <summary>The file path or the RSN path; empty on a cloud model.</summary>
        public string Path { get; }

        public string Region { get; }
        public string ProjectGuid { get; }
        public string ModelGuid { get; }

        /// <summary>
        /// The workset of the **open project** this link will be placed into; empty means the active
        /// workset, as Revit itself does it. The only mutable field of the entry: the workset is chosen
        /// in the window and saved together with the link set, so that "AR into 01_Link_AR" does not
        /// have to be set up again in every project.
        /// </summary>
        public string Workset { get; set; } = string.Empty;

        /// <summary>
        /// The key by which two entries count as the same model: a case-insensitive path, or a pair of
        /// GUIDs. The same key catches duplicates on adding and links already present in the project.
        /// </summary>
        public string Key => Origin == LinkOrigin.Cloud
            ? "cloud|" + ProjectGuid.ToLowerInvariant() + "|" + ModelGuid.ToLowerInvariant()
            : "path|" + Path.Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// Saved link sets: "Stage D", "Consultants", "Underlay". A set is a list of models that is put
    /// together once and then offered in every project that follows.
    ///
    /// For BIM360 a set is not merely a convenience but the only way to do without browsing the cloud:
    /// a GUID list assembled once still works when Autodesk cannot be reached.
    ///
    /// One file per set: `%AppData%\VladTools\links\&lt;name&gt;.txt`, one line per model.
    /// The field separator is the vertical bar: it cannot occur in Windows paths, and even less so in
    /// the names of cloud models.
    /// </summary>
    internal static class LinkSetLibrary
    {
        private const char Separator = '|';

        private static readonly string[] FileHeader =
        {
            "# VladTools link set — the \"Link Manager\" button (the Project panel).",
            "# One line per model, fields separated by a vertical bar:",
            "#   FILE   | file path",
            "#   SERVER | RSN://server/folder/model.rvt",
            "#   CLOUD  | region | project GUID | model GUID | model name",
            "# A project workset to load the link into may be appended as the last field;",
            "# without it the link goes into the active workset, as Revit itself does.",
            "# The file can be edited by hand — it is re-read every time the window opens."
        };

        /// <summary>%AppData%\VladTools\links</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "links");
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
        public static IReadOnlyList<LinkEntry> Load(string setName)
        {
            try
            {
                var file = FilePathFor(setName);
                if (file == null || !File.Exists(file))
                    return new List<LinkEntry>();

                return File.ReadAllLines(file, Encoding.UTF8)
                    .Select(Parse)
                    .Where(entry => entry != null)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<LinkEntry>();
            }
        }

        /// <summary>Rewrites the whole set.</summary>
        public static void Save(string setName, IEnumerable<LinkEntry> entries)
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

        /// <summary>A set entry as a single file line. The "Base File" button remembers the last chosen
        /// model in the same format — so as not to invent a second one.</summary>
        public static string Format(LinkEntry entry)
        {
            var fields = entry.Origin == LinkOrigin.Cloud
                ? new List<string> { "CLOUD", entry.Region, entry.ProjectGuid, entry.ModelGuid, entry.Name }
                : new List<string> { entry.Origin == LinkOrigin.Server ? "SERVER" : "FILE", entry.Path };

            // The workset comes last and only if one was chosen: an empty field at the end of a line
            // means nothing and only litters the file.
            if (entry.Workset.Length > 0)
                fields.Add(entry.Workset);

            return string.Join(" " + Separator + " ", fields);
        }

        /// <summary>Parses a line written by <see cref="Format"/>; garbage and comments yield null.</summary>
        public static LinkEntry Parse(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return null;

            var parts = text.Split(Separator).Select(part => part.Trim()).ToArray();
            if (parts.Length < 2)
                return null;

            switch (parts[0].ToUpperInvariant())
            {
                case "FILE":
                    return parts[1].Length == 0 ? null : WithWorkset(LinkEntry.ForFile(parts[1]), parts, 2);

                case "SERVER":
                    return parts[1].Length == 0 ? null : WithWorkset(LinkEntry.ForServer(parts[1]), parts, 2);

                case "CLOUD":
                    // Region, project GUID, model GUID and name; without either GUID the line is meaningless.
                    if (parts.Length < 4 || parts[2].Length == 0 || parts[3].Length == 0)
                        return null;

                    var name = parts.Length > 4 ? parts[4] : parts[3];
                    return WithWorkset(LinkEntry.ForCloud(parts[1], parts[2], parts[3], name), parts, 5);

                default:
                    return null;
            }
        }

        /// <summary>Appends the workset if the line has one: older files do not have that field.</summary>
        private static LinkEntry WithWorkset(LinkEntry entry, string[] parts, int index)
        {
            if (parts.Length > index)
                entry.Workset = parts[index];

            return entry;
        }
    }
}
