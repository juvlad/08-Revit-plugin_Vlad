using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Export IFC" window's settings: where the files go, the IFC version, whether only what a 3D
    /// view shows is exported and which view, the three export options, and the model list from last time.
    ///
    /// The list is remembered for the same reason as in "Upload to Server": an export is repeated — every
    /// week, at every issue — over the same models, and a run that broke off is run again from the same list.
    ///
    /// File: `%AppData%\VladTools\ifcexport\_settings.txt`, lines of the form "KEY = value"; MODEL repeats.
    /// The run's own reports go into `ifcexport\reports\` (<see cref="ReportsFolder"/>).
    /// </summary>
    internal sealed class IfcExportPreferences
    {
        private const char Separator = '=';

        /// <summary>The view name offered on a first run — the one a coordination export is usually set up in.</summary>
        public const string DefaultViewName = "IFC";

        private static readonly string[] FileHeader =
        {
            "# \"Export IFC\" button settings — the Project panel.",
            "# FOLDER — the folder the .ifc files went into last",
            "# VERSION — Ifc2x3CoordinationView2 / Ifc4ReferenceView / Ifc4DesignTransferView / Ifc2x3CoordinationView / Ifc2x3BasicFmHandover",
            "# BY_VIEW — 1/0: export only what the 3D view named in VIEW shows (0 — the whole model)",
            "# VIEW — the name of that 3D view",
            "# REVIT_PSETS, MATERIAL_PSETS, BASE_QUANTITIES, SPLIT_WALLS — 1/0: Revit property sets,",
            "#   material property sets, base quantities, walls and columns split by level",
            "# SET — the link set worked with last",
            "# MODEL — a model of the list from last time, in the same line format as in link sets:",
            "#   FILE | path · SERVER | RSN://… · CLOUD | region | project GUID | model GUID | name",
            "# The file is rewritten every time the window closes."
        };

        /// <summary>The folder the .ifc files go into; empty on a first run.</summary>
        public string Folder { get; set; } = string.Empty;

        public IfcSchema Schema { get; set; } = IfcSchema.Ifc2x3CoordinationView2;

        /// <summary>Export only the elements the 3D view named <see cref="ViewName"/> shows, rather than the whole model.</summary>
        public bool ByView { get; set; }

        /// <summary>The 3D view; kept when <see cref="ByView"/> is off, so turning it on again needs no typing.</summary>
        public string ViewName { get; set; } = DefaultViewName;

        /// <summary>Revit's own parameters as property sets of their own — off, as in Revit's own default setup.</summary>
        public bool RevitPropertySets { get; set; }

        /// <summary>Property sets on the IFC materials themselves — off, as in Revit's own default setup.</summary>
        public bool MaterialPropertySets { get; set; }

        public bool BaseQuantities { get; set; }

        public bool SplitWallsAndColumns { get; set; }

        /// <summary>The link set worked with last.</summary>
        public string Set { get; set; } = string.Empty;

        /// <summary>The models the window was closed with last time.</summary>
        public List<LinkEntry> Models { get; } = new List<LinkEntry>();

        /// <summary>%AppData%\VladTools\ifcexport</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "ifcexport");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Where every run writes its full report — one text file per run.</summary>
        public static string ReportsFolder => Path.Combine(FolderPath, "reports");

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static IfcExportPreferences Load()
        {
            var preferences = new IfcExportPreferences();

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
                    Line("FOLDER", Folder ?? string.Empty),
                    Line("VERSION", Schema.ToString()),
                    Line("BY_VIEW", ByView ? "1" : "0"),
                    Line("VIEW", ViewName ?? string.Empty),
                    Line("REVIT_PSETS", RevitPropertySets ? "1" : "0"),
                    Line("MATERIAL_PSETS", MaterialPropertySets ? "1" : "0"),
                    Line("BASE_QUANTITIES", BaseQuantities ? "1" : "0"),
                    Line("SPLIT_WALLS", SplitWallsAndColumns ? "1" : "0"),
                    Line("SET", Set ?? string.Empty)
                };

                lines.AddRange(Models.Where(entry => entry != null)
                    .Select(entry => Line("MODEL", LinkSetLibrary.Format(entry))));

                Directory.CreateDirectory(FolderPath);

                // The BOM keeps non-Latin folder and model names readable when the file is opened in Notepad.
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
                case "FOLDER":
                    Folder = value;
                    break;
                case "VERSION":
                    Schema = IfcSchemaText.Parse(value, Schema);
                    break;
                case "BY_VIEW":
                    ByView = value == "1";
                    break;
                case "VIEW":
                    ViewName = value;
                    break;
                case "REVIT_PSETS":
                    RevitPropertySets = value == "1";
                    break;
                case "MATERIAL_PSETS":
                    MaterialPropertySets = value == "1";
                    break;
                case "BASE_QUANTITIES":
                    BaseQuantities = value == "1";
                    break;
                case "SPLIT_WALLS":
                    SplitWallsAndColumns = value == "1";
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
