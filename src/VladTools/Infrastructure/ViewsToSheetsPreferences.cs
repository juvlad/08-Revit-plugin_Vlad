using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Views to Sheets" window's settings: the titleblock, the margins of its drawing area, how
    /// to position, how to number the new sheets, and the last scale set for the checked views.
    ///
    /// The margins are kept **per titleblock type**, by its name: where the title panel sits is a
    /// property of the titleblock, an A1 with a panel on the right and an A3 with a stamp at the
    /// bottom need different margins, and retyping them every time the type changes is exactly the
    /// kind of slip that puts a plan under the stamp.
    ///
    /// File: `%AppData%\VladTools\viewstosheets\_settings.txt`, lines of the form "KEY = value".
    /// </summary>
    internal sealed class ViewsToSheetsPreferences
    {
        private const char Separator = '=';
        private const char FieldSeparator = '|';

        /// <summary>A filing margin on the left, a narrow one elsewhere — the frame of an ISO 5457 sheet.</summary>
        public static readonly double[] DefaultMargins = { 20, 10, 10, 10 };

        private static readonly string[] FileHeader =
        {
            "# \"Views to Sheets\" button settings — the Project panel.",
            "# TITLEBLOCK — the titleblock type used last (\"Family : Type\")",
            "# MARGINS — titleblock type | left | top | right | bottom, millimetres from its outer edge to the drawing area; may repeat",
            "# CENTER — Viewport / CropRegion / ModelPoint / Sample",
            "# PREFIX, START — the new sheets are numbered PREFIX + START, PREFIX + START+1…; leading zeros in START are kept",
            "# SHOW_PLACED — 1/0: list the views that cannot be checked as well",
            "# SCALE — the last scale in \"Scale for the checked views\" (\"1:100\", or the each-view's-own item)",
            "# The file is rewritten every time the window closes."
        };

        private readonly Dictionary<string, double[]> _margins = new Dictionary<string, double[]>(StringComparer.Ordinal);

        public string Titleblock { get; set; } = string.Empty;

        public ViewCentering Centering { get; set; } = ViewCentering.Viewport;

        public string Prefix { get; set; } = string.Empty;

        public string Start { get; set; } = "101";

        public bool ShowPlaced { get; set; }

        /// <summary>The text last in the window's "Scale for the checked views" box; empty means each view's own.</summary>
        public string Scale { get; set; } = string.Empty;

        /// <summary>%AppData%\VladTools\viewstosheets</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "viewstosheets");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Left, top, right, bottom for this titleblock type — the defaults when it was never set.</summary>
        public double[] Margins(string titleblock)
        {
            return _margins.TryGetValue(titleblock ?? string.Empty, out var margins) ? (double[])margins.Clone() : (double[])DefaultMargins.Clone();
        }

        public void SetMargins(string titleblock, double[] margins)
        {
            if (string.IsNullOrEmpty(titleblock) || margins == null || margins.Length != 4)
                return;

            _margins[titleblock] = (double[])margins.Clone();
        }

        /// <summary>Reads the settings. No file or a corrupt one yields the defaults.</summary>
        public static ViewsToSheetsPreferences Load()
        {
            var preferences = new ViewsToSheetsPreferences();

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
                    Line("TITLEBLOCK", Titleblock ?? string.Empty),
                    Line("CENTER", Centering.ToString()),
                    Line("PREFIX", Prefix ?? string.Empty),
                    Line("START", Start ?? string.Empty),
                    Line("SHOW_PLACED", ShowPlaced ? "1" : "0"),
                    Line("SCALE", Scale ?? string.Empty)
                };

                foreach (var pair in _margins.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase))
                {
                    lines.Add(Line("MARGINS", pair.Key + " " + FieldSeparator + " " +
                                              string.Join(" " + FieldSeparator + " ", pair.Value.Select(value => value.ToString("0.##", CultureInfo.InvariantCulture)))));
                }

                Directory.CreateDirectory(FolderPath);

                // The BOM keeps non-Latin titleblock names readable when the file is opened in Notepad.
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
                case "TITLEBLOCK":
                    Titleblock = value;
                    break;
                case "CENTER":
                    Centering = ViewCenteringText.Parse(value, ViewCentering.Viewport);
                    break;
                case "PREFIX":
                    Prefix = value;
                    break;
                case "START":
                    Start = value;
                    break;
                case "SHOW_PLACED":
                    ShowPlaced = value == "1";
                    break;
                case "SCALE":
                    Scale = value;
                    break;
                case "MARGINS":
                    ApplyMargins(value);
                    break;
            }
        }

        /// <summary>
        /// "A1 Titleblock : A1 | 20 | 10 | 180 | 10". The name is everything before the last four
        /// fields, so a vertical bar inside a type name does not break the line.
        /// </summary>
        private void ApplyMargins(string value)
        {
            var fields = value.Split(FieldSeparator);
            if (fields.Length < 5)
                return;

            var numbers = new double[4];
            for (var i = 0; i < 4; i++)
            {
                if (!double.TryParse(fields[fields.Length - 4 + i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] < 0)
                    return;
            }

            var name = string.Join(FieldSeparator.ToString(), fields.Take(fields.Length - 4)).Trim();
            if (name.Length > 0)
                _margins[name] = numbers;
        }
    }
}
