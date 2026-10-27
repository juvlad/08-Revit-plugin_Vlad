using Autodesk.Revit.DB;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of one top-level Revit link of the open project for the "Link Updater" window: where
    /// it points now, whether it loads from there, and whether anything stops it being repointed.
    ///
    /// It carries the link type's <see cref="ElementId"/> — the same exception to the "windows know
    /// nothing about Revit" rule as <see cref="LinkRow"/>: the command needs something to reload, the
    /// window only a key.
    /// </summary>
    internal sealed class LinkPathInfo
    {
        public LinkPathInfo(
            ElementId typeId,
            LinkEntry current,
            string location,
            string loadState,
            bool isLoaded,
            bool isCloud,
            bool isResource,
            string obstacle)
        {
            TypeId = typeId;
            Current = current;
            Location = location ?? string.Empty;
            LoadState = loadState ?? string.Empty;
            IsLoaded = isLoaded;
            IsCloud = isCloud;
            IsResource = isResource;
            Obstacle = obstacle ?? string.Empty;
        }

        /// <summary>The <c>RevitLinkType</c> — what <c>LoadFrom</c> is called on.</summary>
        public ElementId TypeId { get; }

        /// <summary>Where the link points now: a file, a Revit Server path, or a pair of cloud GUIDs.</summary>
        public LinkEntry Current { get; }

        /// <summary>The model name the server is searched for — the file name, or a cloud model's own name.</summary>
        public string Name => Current.Name;

        /// <summary>The current place as Revit's "Manage Links" shows it ("BIM 360://Project/Model.rvt" for a cloud one).</summary>
        public string Location { get; }

        /// <summary>"Loaded", "Not found", "Unloaded"… — what Revit says about the link right now.</summary>
        public string LoadState { get; }

        /// <summary>The link is loaded, so its worksets can be read before the move and compared after it.</summary>
        public bool IsLoaded { get; }

        /// <summary>
        /// The link comes from BIM360 — linked from the cloud, or through Desktop Connector's ACCDocs
        /// folder, which Revit sees as an ordinary file path. Either way it is what the move is about,
        /// unlike a plain file link, which may be a local model on purpose.
        /// </summary>
        public bool IsCloud { get; }

        /// <summary>
        /// Revit keeps the link as an external resource of its cloud server rather than a file path:
        /// a model linked from BIM360 / Autodesk Docs. <c>ExternalFileUtils</c> refuses such a link, and
        /// it may need <c>LoadFrom(ExternalResourceReference)</c> rather than a path to be repointed.
        /// </summary>
        public bool IsResource { get; }

        /// <summary>
        /// Why this link cannot be repointed in this session at all — held by another user, or standing
        /// in a closed workset (<c>LoadFrom</c> refuses both); empty when nothing stands in the way.
        /// </summary>
        public string Obstacle { get; }
    }
}
