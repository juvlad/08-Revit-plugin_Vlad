using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>What the history knows about a sheet or a view since change tracking began.</summary>
    internal enum SheetChange
    {
        /// <summary>The same as at the first check — or as when it first turned up.</summary>
        None,

        /// <summary>First seen after tracking began: a new sheet, or a view newly placed on a sheet.</summary>
        Added,

        Changed
    }

    /// <summary>One sheet or view in the history: its fingerprint and the last change found in it.</summary>
    internal sealed class SheetHistoryEntry
    {
        public SheetHistoryEntry(string uniqueId, string fingerprint, SheetChange change, DateTime? foundOn, DateTime? after)
        {
            UniqueId = uniqueId;
            Fingerprint = fingerprint;
            Change = change;
            FoundOn = foundOn;
            After = after;
        }

        public string UniqueId { get; }

        /// <summary>A <see cref="ViewFingerprint"/> digest.</summary>
        public string Fingerprint { get; }

        public SheetChange Change { get; }

        /// <summary>The day of the check that found the change; null for <see cref="SheetChange.None"/>.</summary>
        public DateTime? FoundOn { get; }

        /// <summary>The day of the check before it: the change was made between the two.</summary>
        public DateTime? After { get; }

        /// <summary>True when this run is the one that found the change.</summary>
        public bool FoundNow { get; set; }
    }

    /// <summary>
    /// The "Sheets" button's record of when each sheet and each view placed on one last changed.
    ///
    /// Revit keeps no such date — not for an element, not for a view (`Element.VersionGuid` and
    /// `DocumentVersion` carry no time, RevitAPI.xml 2022/2024/2025 says so outright), so the button
    /// keeps it itself: every opening of the window is a check, a fingerprint of every sheet and view
    /// is compared with the one the previous check left, and a difference is dated with today. The
    /// date is therefore the day a change was *found*, not the day it was made — it was made after
    /// the previous check (<see cref="SheetHistoryEntry.After"/>). Nothing before the first check can
    /// be known: that one only sets the baseline.
    ///
    /// One file per model, keyed by the central model's path for a workshared one, so every local
    /// copy on this computer shares it: `%AppData%\VladTools\sheets\history\&lt;name&gt;_&lt;path hash&gt;.txt`.
    /// Unlike the add-in's settings this is data, so a failure to read or write it is shown, and a
    /// file that could not be read is never overwritten.
    /// </summary>
    internal sealed class SheetHistory
    {
        private const char Separator = '|';
        private const string DateFormat = "yyyy-MM-dd";

        private static readonly string[] FileHeader =
        {
            "# Change history of sheets and the views on them — the \"Sheets\" button (the Project panel).",
            "# STARTED — the first check; LAST_CHECK — the latest one. Every opening of the window is a check.",
            "# Line: UniqueId | fingerprint | None/Added/Changed | the check that found it | the check before it",
            "# Deleting the file starts the history again from the next check."
        };

        private readonly Dictionary<string, SheetHistoryEntry> _saved = new Dictionary<string, SheetHistoryEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, SheetHistoryEntry> _current = new Dictionary<string, SheetHistoryEntry>(StringComparer.Ordinal);
        private readonly string _modelPath;
        private bool _readFailed;

        private SheetHistory(string modelPath, DateTime today)
        {
            _modelPath = modelPath;
            Today = today.Date;
        }

        public DateTime Today { get; }

        /// <summary>The day of the first check; null when this run is the first.</summary>
        public DateTime? Since { get; private set; }

        /// <summary>The day of the check before this one; null when this run is the first.</summary>
        public DateTime? PreviousCheck { get; private set; }

        /// <summary>Why the history could not be read or written; empty when all is well.</summary>
        public string Problem { get; private set; } = string.Empty;

        /// <summary>False when the file is there but could not be read: nothing is dated, and nothing is written over it.</summary>
        public bool IsReadable => !_readFailed;

        /// <summary>%AppData%\VladTools\sheets\history</summary>
        public static string FolderPath => Path.Combine(SheetPreferences.FolderPath, "history");

        /// <summary>
        /// The history of a model; null when the model has no path (never saved) — there is nothing
        /// to key it by, and a history left under an empty key would be shared by every new project.
        /// </summary>
        public static SheetHistory Load(string modelPath, DateTime today)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                return null;

            var history = new SheetHistory(modelPath, today);

            try
            {
                var path = FilePathFor(modelPath);
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                        history.Apply(line);
                }
            }
            catch (Exception exception)
            {
                history._readFailed = true;
                history._saved.Clear();
                history.Since = null;
                history.PreviousCheck = null;
                history.Problem = "The change history could not be read, so no dates are shown this time: " + exception.Message;
            }

            // A file with records but no start line was cut short; treating it as the first check
            // keeps every date it holds from being read against a wrong baseline.
            if (history.Since == null)
                history._saved.Clear();

            return history;
        }

        /// <summary>
        /// Compares a sheet's or a view's fingerprint with the previous check's and returns what is
        /// known about it now. A null fingerprint (Revit gave no version) leaves the old record as it
        /// was rather than calling it a change. The same id checked twice — a legend on several
        /// sheets — is compared once.
        /// </summary>
        public SheetHistoryEntry Check(string uniqueId, string fingerprint)
        {
            if (string.IsNullOrEmpty(uniqueId) || _readFailed)
                return null;

            if (_current.TryGetValue(uniqueId, out var known))
                return known;

            _saved.TryGetValue(uniqueId, out var old);

            SheetHistoryEntry entry;
            if (string.IsNullOrEmpty(fingerprint))
                entry = old;
            else if (old == null)
                entry = Since == null
                    ? new SheetHistoryEntry(uniqueId, fingerprint, SheetChange.None, null, null)
                    : new SheetHistoryEntry(uniqueId, fingerprint, SheetChange.Added, Today, PreviousCheck) { FoundNow = true };
            else if (string.Equals(old.Fingerprint, fingerprint, StringComparison.Ordinal))
                entry = old;
            else
                entry = new SheetHistoryEntry(uniqueId, fingerprint, SheetChange.Changed, Today, PreviousCheck) { FoundNow = true };

            if (entry != null)
                _current[uniqueId] = entry;

            return entry;
        }

        /// <summary>
        /// Rewrites the file with what this run checked — sheets and views no longer on a sheet drop
        /// out, otherwise the file would grow for ever. Not written when the read failed: that would
        /// replace somebody's history with a fresh baseline.
        /// </summary>
        public void Save()
        {
            if (_readFailed)
                return;

            try
            {
                var lines = new List<string>(FileHeader)
                {
                    "# Model: " + _modelPath,
                    string.Empty,
                    "STARTED = " + Format(Since ?? Today),
                    "LAST_CHECK = " + Format(Today),
                    string.Empty
                };

                lines.AddRange(_current.Values
                    .OrderBy(entry => entry.UniqueId, StringComparer.Ordinal)
                    .Select(entry => string.Join(Separator.ToString(),
                        entry.UniqueId, entry.Fingerprint, entry.Change.ToString(), Format(entry.FoundOn), Format(entry.After))));

                Directory.CreateDirectory(FolderPath);
                File.WriteAllLines(FilePathFor(_modelPath), lines, new UTF8Encoding(true));
            }
            catch (Exception exception)
            {
                Problem = "The change history could not be saved — what this check found will be found again next time, with a later date: " +
                          exception.Message;
            }
        }

        private static string Format(DateTime? date)
        {
            return date?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string FilePathFor(string modelPath)
        {
            string name;
            try
            {
                name = Path.GetFileNameWithoutExtension(modelPath) ?? string.Empty;
            }
            catch (ArgumentException)
            {
                name = string.Empty;
            }

            foreach (var forbidden in Path.GetInvalidFileNameChars())
                name = name.Replace(forbidden, '_');

            if (name.Length > 40)
                name = name.Substring(0, 40);

            // The path hash keeps two models with the same name in different folders apart.
            return Path.Combine(FolderPath, (name.Length == 0 ? "model" : name) + "_" + Hash(modelPath) + ".txt");
        }

        private static string Hash(string modelPath)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(modelPath.ToLowerInvariant()));
                return BitConverter.ToString(bytes, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private void Apply(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] == '#')
                return;

            if (text.IndexOf(Separator) >= 0)
            {
                var entry = Parse(text);
                if (entry != null)
                    _saved[entry.UniqueId] = entry;
                return;
            }

            var equals = text.IndexOf('=');
            if (equals <= 0)
                return;

            var key = text.Substring(0, equals).Trim().ToUpperInvariant();
            var value = ParseDate(text.Substring(equals + 1));

            switch (key)
            {
                case "STARTED":
                    Since = value;
                    break;
                case "LAST_CHECK":
                    PreviousCheck = value;
                    break;
            }
        }

        private static SheetHistoryEntry Parse(string text)
        {
            var parts = text.Split(Separator).Select(part => part.Trim()).ToArray();
            if (parts.Length != 5 || parts[0].Length == 0 || parts[1].Length == 0 ||
                !Enum.TryParse(parts[2], true, out SheetChange change))
                return null;

            var foundOn = ParseDate(parts[3]);
            if (change != SheetChange.None && foundOn == null)
                change = SheetChange.None;

            return new SheetHistoryEntry(parts[0], parts[1], change, change == SheetChange.None ? null : foundOn, change == SheetChange.None ? null : ParseDate(parts[4]));
        }

        private static DateTime? ParseDate(string text)
        {
            return DateTime.TryParseExact((text ?? string.Empty).Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : (DateTime?)null;
        }
    }
}
