using System.ComponentModel;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// A table row in the "other models" window: one model whose grids are to be put in line with
    /// the coordination file.
    ///
    /// Close kin to <see cref="LinkRow"/>, and deliberately not the same class: there a row is a
    /// link about to be inserted into the open project, with a workset of its own and an "already in
    /// the project" state, and none of that means anything here. What is shared between them is the
    /// model itself — <see cref="LinkEntry"/> — and that is shared as it stands.
    /// </summary>
    internal sealed class BatchModelRow : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        public BatchModelRow(LinkEntry entry)
        {
            Entry = entry;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The model description: a path, or a region and a pair of GUIDs.</summary>
        public LinkEntry Entry { get; }

        /// <summary>The key models are compared by: a case-insensitive path, or a pair of GUIDs.</summary>
        public string Key => Entry.Key;

        /// <summary>The "work on this model" check box.</summary>
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

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
