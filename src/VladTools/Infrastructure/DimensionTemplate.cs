using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>One chain inside a saved template: the kind, the offset and the dimension type by name.</summary>
    internal sealed class DimensionTemplateChain
    {
        public DimensionChainKind Kind { get; set; }
        public double OffsetMm { get; set; }

        /// <summary>A name, not an id — a template has to travel between projects, where types have ids of their own.</summary>
        public string DimensionTypeName { get; set; } = string.Empty;
    }

    /// <summary>
    /// An auto-dimension template: which line the room boundary runs along, which way the chains face,
    /// and the list of chains itself. Exactly what the "Auto Dimensions" window shows and what can be
    /// saved with the "Save template" button — or not saved at all: a template is only needed to carry
    /// a set of chains between projects.
    /// </summary>
    internal sealed class DimensionTemplate
    {
        public SpatialElementBoundaryLocation Boundary { get; set; } = SpatialElementBoundaryLocation.CoreBoundary;

        /// <summary>The chains are placed outside the room rather than inside (inside by default).</summary>
        public bool Outward { get; set; }

        /// <summary>
        /// The end ticks of the chain are taken from the far face of the adjoining wall rather than the
        /// near one — the first and last link of the chain becomes that wall's thickness ("120 | 3775 | 120").
        /// On by default: that is how every chain on a masonry plan is built. Templates written before
        /// this field existed read with the same value — the line is simply absent from the file.
        /// </summary>
        public bool IncludeAdjacentWallThickness { get; set; } = true;

        /// <summary>The labels of links that do not fit between their ticks are pulled out onto a leader.</summary>
        public bool MoveSmallText { get; set; } = true;

        public List<DimensionTemplateChain> Chains { get; } = new List<DimensionTemplateChain>();
    }

    /// <summary>
    /// The store of auto-dimension templates: `%AppData%\VladTools\autodim\&lt;name&gt;.txt`.
    ///
    /// A separate folder, not `dimensions\` — that one holds the cache of the family scan for dimension
    /// labels (see <see cref="DimensionLabelCache"/>); files that mean different things must not be mixed.
    /// The format is one line per entity, fields separated by a vertical bar, as in link sets
    /// (<see cref="LinkSetLibrary"/>): a bar occurs neither in a dimension type name nor in a number.
    /// </summary>
    internal static class DimensionTemplateLibrary
    {
        private const char Separator = '|';

        private static readonly string[] FileHeader =
        {
            "# VladTools auto-dimension template — the \"Auto Dimensions\" button (the Project panel).",
            "# BOUNDARY  | Finish | Center | CoreBoundary | CoreCenter — which line the room boundary runs along.",
            "# SIDE      | Inward | Outward — which way the chains face.",
            "# THICKNESS | Yes | No — whether the end ticks pick up the thickness of the adjoining walls.",
            "# LABELS    | Leader | Inline — whether labels that do not fit are pulled out onto a leader.",
            "# CHAIN     | number | offset_mm | chain kind | dimension type name",
            "#   chain kind is one of: Overall, OpeningEdges, OpeningCenters, Partitions, WallFaces, Combined",
            "# The chain number is only there to make the file easier to read by eye; it is not used on load:",
            "# the chain order is the order of the CHAIN lines. The file can be edited by hand."
        };

        /// <summary>%AppData%\VladTools\autodim</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "autodim");
            }
        }

        /// <summary>The names of the saved templates in alphabetical order. Names starting with an underscore are internal and are left out.</summary>
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

        /// <summary>Reads a template. No file or a corrupt one yields an empty template: that cannot break the button.</summary>
        public static DimensionTemplate Load(string templateName)
        {
            var template = new DimensionTemplate();

            try
            {
                var file = FilePathFor(templateName);
                if (file == null || !File.Exists(file))
                    return template;

                foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                    Apply(template, line);
            }
            catch (Exception)
            {
                return new DimensionTemplate();
            }

            return template;
        }

        /// <summary>Rewrites the whole template.</summary>
        public static void Save(string templateName, DimensionTemplate template)
        {
            var file = FilePathFor(templateName);
            if (file == null)
                throw new ArgumentException("The template name is empty or consists only of forbidden characters.");

            var lines = new List<string>(FileHeader) { string.Empty };
            lines.Add(Field("BOUNDARY", template.Boundary.ToString()));
            lines.Add(Field("SIDE", template.Outward ? "Outward" : "Inward"));
            lines.Add(Field("THICKNESS", template.IncludeAdjacentWallThickness ? "Yes" : "No"));
            lines.Add(Field("LABELS", template.MoveSmallText ? "Leader" : "Inline"));
            lines.Add(string.Empty);

            var number = 1;
            foreach (var chain in template.Chains)
            {
                lines.Add(string.Join(" " + Separator + " ", new[]
                {
                    "CHAIN",
                    number.ToString(CultureInfo.InvariantCulture),
                    chain.OffsetMm.ToString(CultureInfo.InvariantCulture),
                    chain.Kind.ToString(),
                    chain.DimensionTypeName
                }));
                number++;
            }

            Directory.CreateDirectory(FolderPath);

            // The BOM keeps non-Latin names readable when the file is opened in Notepad.
            File.WriteAllLines(file, lines, new UTF8Encoding(true));
        }

        public static void Delete(string templateName)
        {
            var file = FilePathFor(templateName);
            if (file != null && File.Exists(file))
                File.Delete(file);
        }

        /// <summary>The path to the template file; null if the name is empty or made only of forbidden characters.</summary>
        public static string FilePathFor(string templateName)
        {
            var name = (templateName ?? string.Empty).Trim();
            if (name.Length == 0)
                return null;

            foreach (var forbidden in Path.GetInvalidFileNameChars())
                name = name.Replace(forbidden, '_');

            name = name.Trim('_', ' ');

            return name.Length == 0 ? null : Path.Combine(FolderPath, name + ".txt");
        }

        private static void Apply(DimensionTemplate template, string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return;

            var parts = text.Split(Separator).Select(part => part.Trim()).ToArray();
            if (parts.Length < 2)
                return;

            // The Russian keys and values are what this format used before the add-in was translated.
            // They are still accepted so that templates already saved in the autodim folder keep
            // working; only the English form is ever written back.
            switch (parts[0].ToUpperInvariant())
            {
                case "BOUNDARY":
                case "ГРАНИЦА":
                    SpatialElementBoundaryLocation boundary;
                    if (Enum.TryParse(parts[1], true, out boundary))
                        template.Boundary = boundary;
                    break;

                case "SIDE":
                case "СТОРОНА":
                    template.Outward = string.Equals(parts[1], "Outward", StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(parts[1], "Наружу", StringComparison.OrdinalIgnoreCase);
                    break;

                case "THICKNESS":
                case "ТОЛЩИНА":
                    template.IncludeAdjacentWallThickness =
                        !string.Equals(parts[1], "No", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(parts[1], "Нет", StringComparison.OrdinalIgnoreCase);
                    break;

                case "LABELS":
                case "ПОДПИСИ":
                    template.MoveSmallText =
                        !string.Equals(parts[1], "Inline", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(parts[1], "Наместе", StringComparison.OrdinalIgnoreCase);
                    break;

                case "CHAIN":
                case "НИТКА":
                    var chain = ParseChain(parts);
                    if (chain != null)
                        template.Chains.Add(chain);
                    break;
            }
        }

        private static DimensionTemplateChain ParseChain(string[] parts)
        {
            // CHAIN | number | offset | kind | type name — the number is unused but must be present.
            if (parts.Length < 4)
                return null;

            double offset;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out offset))
                return null;

            DimensionChainKind kind;
            if (!Enum.TryParse(parts[3], true, out kind))
                return null;

            return new DimensionTemplateChain
            {
                OffsetMm = offset,
                Kind = kind,
                DimensionTypeName = parts.Length > 4 ? parts[4] : string.Empty
            };
        }

        private static string Field(string key, string value)
        {
            return key + " " + Separator + " " + value;
        }
    }

    /// <summary>
    /// The "Auto Dimensions" window settings that survive closing Revit: the last template used, the
    /// boundary and the direction. Rewritten whenever the window closes — like the "Link Manager"
    /// settings (<see cref="LinkPreferences"/>), unlike the templates themselves, which are saved only
    /// by pressing the button.
    ///
    /// File: `%AppData%\VladTools\autodim\_settings.txt`. The name "_settings" is taken by this file —
    /// `DimensionTemplateLibrary.Names()` skips everything starting with an underscore.
    /// </summary>
    internal sealed class AutoDimensionPreferences
    {
        public string LastTemplate { get; set; } = string.Empty;
        public SpatialElementBoundaryLocation Boundary { get; set; } = SpatialElementBoundaryLocation.CoreBoundary;
        public bool Outward { get; set; }
        public bool RemovePrevious { get; set; } = true;

        /// <summary>See <see cref="DimensionTemplate.IncludeAdjacentWallThickness"/> — on by default.</summary>
        public bool IncludeAdjacentThickness { get; set; } = true;

        /// <summary>See <see cref="DimensionTemplate.MoveSmallText"/> — on by default.</summary>
        public bool MoveSmallText { get; set; } = true;

        public static string FilePath => Path.Combine(DimensionTemplateLibrary.FolderPath, "_settings.txt");

        public static AutoDimensionPreferences Load()
        {
            var preferences = new AutoDimensionPreferences();

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

        public void Save()
        {
            try
            {
                var lines = new List<string>
                {
                    "# \"Auto Dimensions\" window settings — the Project panel.",
                    "# The file is rewritten every time the window closes.",
                    string.Empty,
                    Line("TEMPLATE", LastTemplate),
                    Line("BOUNDARY", Boundary.ToString()),
                    Line("OUTWARD", Outward ? "1" : "0"),
                    Line("REMOVE_PREVIOUS", RemovePrevious ? "1" : "0"),
                    Line("ADJACENT_THICKNESS", IncludeAdjacentThickness ? "1" : "0"),
                    Line("MOVE_SMALL_TEXT", MoveSmallText ? "1" : "0")
                };

                Directory.CreateDirectory(DimensionTemplateLibrary.FolderPath);

                // The BOM keeps non-Latin names readable when the file is opened in Notepad.
                File.WriteAllLines(FilePath, lines, new UTF8Encoding(true));
            }
            catch (Exception)
            {
                // Settings, not data — a write failure must not get in the way of the button.
            }
        }

        private static string Line(string key, string value)
        {
            return key + " = " + value;
        }

        private void Apply(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return;

            var separator = text.IndexOf('=');
            if (separator <= 0)
                return;

            var key = text.Substring(0, separator).Trim().ToUpperInvariant();
            var value = text.Substring(separator + 1).Trim();

            switch (key)
            {
                case "TEMPLATE":
                    LastTemplate = value;
                    break;

                case "BOUNDARY":
                    SpatialElementBoundaryLocation boundary;
                    if (Enum.TryParse(value, true, out boundary))
                        Boundary = boundary;
                    break;

                case "OUTWARD":
                    Outward = value == "1";
                    break;

                case "REMOVE_PREVIOUS":
                    RemovePrevious = value != "0";
                    break;

                case "ADJACENT_THICKNESS":
                    IncludeAdjacentThickness = value != "0";
                    break;

                case "MOVE_SMALL_TEXT":
                    MoveSmallText = value != "0";
                    break;
            }
        }
    }
}
