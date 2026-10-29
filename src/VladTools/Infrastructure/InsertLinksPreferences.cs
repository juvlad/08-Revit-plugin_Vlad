using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The "Insert Links" window's settings: how the links are placed, which worksets inside them to
    /// close, what to do where a model lacks a link's workset — and both lists, the links (each with its
    /// project workset) and the models they go into.
    ///
    /// Both lists are remembered for the reason the other batches remember theirs: a run over a project's
    /// models takes hours, and one that broke off is started again over the same lists — a link already in
    /// a model is only counted there.
    ///
    /// The worksets inside the links are kept here rather than shared with "Link Manager": the two windows
    /// save on every close, and one would keep overwriting the other's list. On the first run, with no
    /// file yet, they start from "Link Manager"'s — the office closes the same worksets in every link.
    ///
    /// The Revit Server names typed in the browser are not kept here: they live in
    /// <c>links\_settings.txt</c>, shared with "Link Manager" and every batch.
    ///
    /// File: `%AppData%\VladTools\insertlinks\_settings.txt`, lines of the form "KEY = value"; CLOSE, LINK
    /// and MODEL repeat. Each run's full report goes into `insertlinks\reports\` (<see cref="ReportsFolder"/>).
    /// </summary>
    internal sealed class InsertLinksPreferences
    {
        private const char Separator = '=';

        private static readonly string[] FileHeader =
        {
            "# \"Insert Links\" button settings — the Project panel.",
            "# PLACEMENT — Shared | Origin | Centered | Site",
            "# ATTACHMENT — Overlay | Attachment",
            "# WORKSETS — OpenAll | CloseAll | LastViewed: the worksets inside every link",
            "# CLOSE — a workset inside the links checked in the list (there may be many lines)",
            "# RULE / RULE_CONTAINS — the workset name rule: the string, and \"contains\" instead of \"starts with\"",
            "# MATCH_WORKSET — 1/0: guess a link's project workset from the discipline code in its name",
            "# CREATE_WORKSET — 1/0: create a link's project workset in a model that does not have it",
            "# LINK_SET / MODEL_SET — the link sets the two lists worked with last",
            "# LINK — a link of the list from last time, in the link-set line format; its project workset is the last field",
            "# MODEL — a model the links went into last time, in the link-set line format",
            "# The file is rewritten every time the window closes."
        };

        public LinkPlacement Placement { get; set; } = LinkPlacement.Shared;

        /// <summary>Attachment instead of overlay. Overlay is the default — as in Revit itself.</summary>
        public bool IsAttachment { get; set; }

        public LinkWorksetMode WorksetMode { get; set; } = LinkWorksetMode.OpenAll;

        /// <summary>The worksets inside the links checked in the window's list.</summary>
        public List<string> Worksets { get; } = new List<string>();

        /// <summary>The workset name rule string; empty means there is no rule.</summary>
        public string WorksetPattern { get; set; } = string.Empty;

        /// <summary>The rule looks for a substring rather than the start of the name.</summary>
        public bool WorksetPatternContains { get; set; }

        /// <summary>Guess a link's project workset ("01_Link_OV") from the discipline code in its name ("…_OV").</summary>
        public bool MatchProjectWorkset { get; set; } = true;

        /// <summary>
        /// Create a link's project workset in a model that does not have it. Off by default: a name typed
        /// wrong once would otherwise become a stray workset in every model of the run.
        /// </summary>
        public bool CreateWorksets { get; set; }

        public string LinkSet { get; set; } = string.Empty;

        public string ModelSet { get; set; } = string.Empty;

        /// <summary>The links the window was closed with — each with its project workset in <see cref="LinkEntry.Workset"/>.</summary>
        public List<LinkEntry> Links { get; } = new List<LinkEntry>();

        /// <summary>The models the window was closed with.</summary>
        public List<LinkEntry> Models { get; } = new List<LinkEntry>();

        /// <summary>The setup of the worksets inside the links, as the run applies it.</summary>
        public LinkWorksetRule Rule => new LinkWorksetRule(WorksetMode, Worksets, WorksetPattern, WorksetPatternContains);

        /// <summary>%AppData%\VladTools\insertlinks</summary>
        public static string FolderPath
        {
            get
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(folder, "VladTools", "insertlinks");
            }
        }

        public static string FilePath => Path.Combine(FolderPath, "_settings.txt");

        /// <summary>Where every run writes its full report — one text file per run.</summary>
        public static string ReportsFolder => Path.Combine(FolderPath, "reports");

        /// <summary>
        /// Reads the settings. No file yet — the placement and the worksets inside the links start from
        /// "Link Manager"'s; a corrupt file yields the defaults.
        /// </summary>
        public static InsertLinksPreferences Load(LinkPreferences linkManager)
        {
            var preferences = new InsertLinksPreferences();

            try
            {
                if (!File.Exists(FilePath))
                {
                    if (linkManager != null)
                    {
                        preferences.Placement = linkManager.Placement;
                        preferences.IsAttachment = linkManager.IsAttachment;
                        preferences.WorksetMode = linkManager.WorksetMode;
                        preferences.Worksets.AddRange(linkManager.Worksets);
                        preferences.WorksetPattern = linkManager.WorksetPattern;
                        preferences.WorksetPatternContains = linkManager.WorksetPatternContains;
                        preferences.MatchProjectWorkset = linkManager.MatchProjectWorkset;
                    }

                    return preferences;
                }

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
                    Line("PLACEMENT", Placement.ToString()),
                    Line("ATTACHMENT", IsAttachment ? "Attachment" : "Overlay"),
                    Line("WORKSETS", WorksetMode.ToString()),
                    Line("RULE", WorksetPattern ?? string.Empty),
                    Line("RULE_CONTAINS", WorksetPatternContains ? "1" : "0"),
                    Line("MATCH_WORKSET", MatchProjectWorkset ? "1" : "0"),
                    Line("CREATE_WORKSET", CreateWorksets ? "1" : "0"),
                    Line("LINK_SET", LinkSet ?? string.Empty),
                    Line("MODEL_SET", ModelSet ?? string.Empty)
                };

                lines.AddRange(Worksets
                    .Select(LinkPreferences.NormalizeWorkset)
                    .Where(name => name.Length > 0)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .Select(name => Line("CLOSE", name)));

                lines.AddRange(Links.Where(entry => entry != null).Select(entry => Line("LINK", LinkSetLibrary.Format(entry))));
                lines.AddRange(Models.Where(entry => entry != null).Select(entry => Line("MODEL", LinkSetLibrary.Format(entry))));

                Directory.CreateDirectory(FolderPath);

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
                case "PLACEMENT":
                    Placement = Parse(value, LinkPlacement.Shared);
                    break;

                case "ATTACHMENT":
                    IsAttachment = string.Equals(value, "Attachment", StringComparison.OrdinalIgnoreCase);
                    break;

                case "WORKSETS":
                    WorksetMode = Parse(value, LinkWorksetMode.OpenAll);
                    break;

                case "CLOSE":
                    if (value.Length > 0)
                        Worksets.Add(value);
                    break;

                case "RULE":
                    WorksetPattern = value;
                    break;

                case "RULE_CONTAINS":
                    WorksetPatternContains = value == "1";
                    break;

                case "MATCH_WORKSET":
                    MatchProjectWorkset = value != "0";
                    break;

                case "CREATE_WORKSET":
                    CreateWorksets = value == "1";
                    break;

                case "LINK_SET":
                    LinkSet = value;
                    break;

                case "MODEL_SET":
                    ModelSet = value;
                    break;

                case "LINK":
                case "MODEL":
                    // The line format is the link sets' own — there is no second parser for it.
                    var entry = LinkSetLibrary.Parse(value);
                    if (entry != null)
                        (key == "LINK" ? Links : Models).Add(entry);
                    break;
            }
        }

        private static T Parse<T>(string value, T fallback) where T : struct
        {
            T parsed;
            return Enum.TryParse(value, true, out parsed) ? parsed : fallback;
        }
    }
}
