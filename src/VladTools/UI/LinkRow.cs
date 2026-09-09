using System.Collections.Generic;
using System.ComponentModel;
using Autodesk.Revit.DB;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// A table row in the "Link Manager" window: one model that is about to be linked.
    ///
    /// The model itself is described by <see cref="Entry"/> — a path or a pair of GUIDs; everything
    /// else here is only for the window: the check box, the column captions and whatever was learned
    /// about the model along the way (the worksets that were read, a read failure).
    ///
    /// The row knows the <see cref="ElementId"/> of an already existing link — the same exception to
    /// the "windows know nothing about Revit" rule as in <see cref="ProjectParameterRow"/>: the
    /// command needs something to reload, while the window needs only the "already in the project" flag.
    /// </summary>
    internal sealed class LinkRow : INotifyPropertyChanged
    {
        /// <summary>
        /// The caption of the first row in the workset list — "no workset chosen".
        /// An empty entry in the drop-down would look like an oversight, so it has a name; on the way
        /// out, into <see cref="LinkEntry.Workset"/>, it still leaves as an empty string.
        /// </summary>
        public const string ActiveWorkset = "(active)";

        private bool _isSelected;
        private IReadOnlyList<string> _worksetNames;
        private string _note = string.Empty;

        public LinkRow(LinkEntry entry, ElementId existingId = null)
        {
            Entry = entry;
            ExistingId = existingId ?? ElementId.InvalidElementId;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The model description: a path, or a region and a pair of GUIDs.</summary>
        public LinkEntry Entry { get; }

        /// <summary>A link to this model is already in the project; otherwise <c>InvalidElementId</c>.</summary>
        public ElementId ExistingId { get; }

        public bool IsExisting => ExistingId != null && ExistingId != ElementId.InvalidElementId;

        /// <summary>The key models are compared by: a case-insensitive path, or a pair of GUIDs.</summary>
        public string Key => Entry.Key;

        /// <summary>The "work with this link" check box.</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                Raise(nameof(IsSelected));
            }
        }

        /// <summary>
        /// The model worksets, read without opening it. Until they are read this is null, which is
        /// not the same as an empty list: a non-workshared model has no worksets at all.
        /// </summary>
        public IReadOnlyList<string> WorksetNames
        {
            get { return _worksetNames; }
            set
            {
                _worksetNames = value;
                Raise(nameof(WorksetNames));
                Raise(nameof(Worksets));
            }
        }

        /// <summary>
        /// The workset of the open project the link will be placed into. Not to be confused with
        /// <see cref="WorksetNames"/>: those are the worksets inside the link, this one is outside, in the project.
        /// </summary>
        public string Workset
        {
            get { return Entry.Workset.Length == 0 ? ActiveWorkset : Entry.Workset; }
            set
            {
                var chosen = value == ActiveWorkset ? string.Empty : value ?? string.Empty;
                if (Entry.Workset == chosen)
                    return;

                Entry.Workset = chosen;
                Raise(nameof(Workset));
            }
        }

        /// <summary>A short note: why the worksets could not be read, how the load turned out.</summary>
        public string Note
        {
            get { return _note; }
            set
            {
                if (_note == value)
                    return;

                _note = value ?? string.Empty;
                Raise(nameof(Note));
                Raise(nameof(Status));
            }
        }

        public string Name => Entry.Name;

        /// <summary>The caption of the "Source" column.</summary>
        public string Kind
        {
            get
            {
                switch (Entry.Origin)
                {
                    case LinkOrigin.Server:
                        return "Revit Server";
                    case LinkOrigin.Cloud:
                        return "BIM360";
                    default:
                        return "File";
                }
            }
        }

        /// <summary>Where the model lives: the file folder, the server path, or the region and project GUID.</summary>
        public string Location
        {
            get
            {
                switch (Entry.Origin)
                {
                    case LinkOrigin.Cloud:
                        return Entry.Region + " · project " + Entry.ProjectGuid;

                    case LinkOrigin.Server:
                        return Entry.Path;

                    default:
                        return System.IO.Path.GetDirectoryName(Entry.Path) ?? Entry.Path;
                }
            }
        }

        /// <summary>The caption of the "Worksets" column: how many the model has, once they were counted.</summary>
        public string Worksets
        {
            get
            {
                if (WorksetNames == null)
                    return string.Empty;

                return WorksetNames.Count == 0 ? "none" : WorksetNames.Count.ToString();
            }
        }

        /// <summary>The caption of the "State" column.</summary>
        public string Status
        {
            get
            {
                if (Note.Length > 0)
                    return Note;

                return IsExisting ? "Already in the project" : "New";
            }
        }

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
