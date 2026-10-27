using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VladTools.Infrastructure
{
    /// <summary>A model found on Revit Server: the folder it lies in and the path a link takes.</summary>
    internal sealed class ServerModel
    {
        public ServerModel(string server, string folderPath, string name)
        {
            Server = RevitServerClient.NormalizeServer(server);
            FolderPath = string.IsNullOrEmpty(folderPath) ? RevitServerClient.RootFolder : folderPath;
            Name = name ?? string.Empty;
        }

        public string Server { get; }

        /// <summary>The folder in service form, "|VSC|3.0_AR".</summary>
        public string FolderPath { get; }

        /// <summary>The model name with its extension, as the server lists it.</summary>
        public string Name { get; }

        /// <summary>The path a link is loaded from: RSN://server/VSC/3.0_AR/model.rvt.</summary>
        public string RsnPath => RevitServerClient.RsnPath(Server, FolderPath, Name);

        /// <summary>The same key <see cref="LinkEntry.Key"/> gives a link pointing here.</summary>
        public string Key => LinkEntry.ForServer(RsnPath).Key;
    }

    /// <summary>
    /// The models of a part of Revit Server, walked once and indexed by name — what "Link Updater"
    /// looks a link up in.
    ///
    /// A link carries nothing but its old place: a BIM360 link knows a pair of GUIDs and a name, and
    /// nothing on the server answers to the GUIDs. What survives a move from the cloud to a server is
    /// the model's name, so the server is read folder by folder (<see cref="RevitServerClient.Contents"/>
    /// — the API itself lists no server folders) and a link is matched by its name. The walk is breadth
    /// first and bounded by <see cref="FolderLimit"/>, the same reasoning as the "Building Kit": every
    /// folder is an HTTP request, and a whole server can hold thousands of them.
    ///
    /// The index only grows: a wider search walks the new part and skips what was read already, so
    /// "search wider" costs the folders it adds, not the whole tree again.
    /// </summary>
    internal sealed class ServerModelIndex
    {
        /// <summary>How many folders one window may read in all, before it stops and says so.</summary>
        public const int FolderLimit = 400;

        /// <summary>A Revit version suffix at the end of a name: "_R22", "-R2024". Only a loose match ignores it.</summary>
        private static readonly Regex VersionSuffix =
            new Regex(@"[ _\-.]R(20)?\d{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly HashSet<string> _walked = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<ServerModel> _models = new List<ServerModel>();
        private readonly List<string> _failures = new List<string>();

        public int FolderCount => _walked.Count;

        public int ModelCount => _models.Count;

        /// <summary>The walk stopped at <see cref="FolderLimit"/>: what was not read may still hold the model.</summary>
        public bool LimitReached { get; private set; }

        /// <summary>Folders that could not be read — named, not fatal: the rest of the tree is still worth having.</summary>
        public IReadOnlyList<string> Failures => _failures;

        /// <summary>Whether the folder itself has been read already.</summary>
        public bool Covers(ModelFolder folder)
        {
            return folder != null && folder.Origin == LinkOrigin.Server && _walked.Contains(Key(folder.Server, folder.Path));
        }

        /// <summary>
        /// Reads a folder and everything under it, skipping the folders read before. A folder skipped
        /// that way was read with its whole subtree, so its children are not queued again.
        /// </summary>
        /// <returns>How many models the walk added.</returns>
        public int Walk(ModelFolder root)
        {
            if (root == null || root.Origin != LinkOrigin.Server)
                return 0;

            var added = 0;
            var queue = new Queue<string>();
            queue.Enqueue(root.Path);

            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                var key = Key(root.Server, path);

                if (_walked.Contains(key))
                    continue;

                if (_walked.Count >= FolderLimit)
                {
                    LimitReached = true;
                    break;
                }

                _walked.Add(key);

                IReadOnlyList<ServerEntry> entries;
                try
                {
                    entries = RevitServerClient.Contents(root.Server, path);
                }
                catch (Exception exception)
                {
                    _failures.Add(RevitServerClient.RsnPath(root.Server, path, string.Empty).TrimEnd('/') + " — " +
                                  LinkCatalog.Short(exception.Message));
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (entry.IsFolder)
                    {
                        queue.Enqueue(entry.Path);
                    }
                    else
                    {
                        _models.Add(new ServerModel(root.Server, path, entry.Name));
                        added++;
                    }
                }
            }

            return added;
        }

        /// <summary>The models of exactly this name — case and the extension aside, as Revit itself compares them.</summary>
        public IReadOnlyList<ServerModel> Named(string name)
        {
            var wanted = Bare(name);
            if (wanted.Length == 0)
                return new List<ServerModel>();

            return _models
                .Where(model => string.Equals(Bare(model.Name), wanted, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// The models whose name differs only in punctuation, case or a Revit version suffix:
        /// "Корпус1_OV_R22" and "Корпус1-OV-R24". Asked only when <see cref="Named"/> finds nothing, and
        /// never taken without the user's check — a loose name is a guess.
        /// </summary>
        public IReadOnlyList<ServerModel> Resembling(string name)
        {
            var wanted = Loose(name);
            if (wanted.Length == 0)
                return new List<ServerModel>();

            return _models.Where(model => Loose(model.Name) == wanted).ToList();
        }

        /// <summary>
        /// How far a model lies from a folder in the server's tree: the steps up to the shared folder
        /// plus the steps down to the model's. The nearest copy is the likeliest one — a model in the
        /// project's own discipline folder rather than its namesake in an archive. Another server is as
        /// far as it gets.
        /// </summary>
        public static int Distance(ModelFolder from, ServerModel model)
        {
            if (from == null || from.Origin != LinkOrigin.Server ||
                !string.Equals(from.Server, model.Server, StringComparison.OrdinalIgnoreCase))
                return int.MaxValue / 2;

            var a = Segments(from.Path);
            var b = Segments(model.FolderPath);

            var common = 0;
            while (common < a.Count && common < b.Count &&
                   string.Equals(a[common], b[common], StringComparison.CurrentCultureIgnoreCase))
                common++;

            return a.Count - common + (b.Count - common);
        }

        /// <summary>A name without its extension, trimmed.</summary>
        public static string Bare(string name)
        {
            var text = (name ?? string.Empty).Trim();

            return text.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
                ? text.Substring(0, text.Length - 4).TrimEnd()
                : text;
        }

        private static string Loose(string name)
        {
            var bare = VersionSuffix.Replace(Bare(name), string.Empty);

            var key = new StringBuilder(bare.Length);
            foreach (var character in bare)
            {
                if (char.IsLetterOrDigit(character))
                    key.Append(char.ToLowerInvariant(character));
            }

            return key.ToString();
        }

        private static List<string> Segments(string folderPath)
        {
            return (folderPath ?? string.Empty)
                .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        private static string Key(string server, string path)
        {
            return RevitServerClient.NormalizeServer(server).ToLowerInvariant() + "|" +
                   (string.IsNullOrEmpty(path) ? RevitServerClient.RootFolder : path).ToLowerInvariant();
        }
    }
}
