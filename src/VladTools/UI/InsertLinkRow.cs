using System.Collections.Generic;
using System.ComponentModel;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// A row of the "Insert Links" workset table: one link checked on the first step, the project workset
    /// it goes into, and what is known about that workset in the models of the third step.
    ///
    /// The workset is kept in the link's own <see cref="LinkEntry.Workset"/> — the same entry the first
    /// step's list holds — so a saved set carries "AR.rvt into 01_Link_AR" exactly as "Link Manager"'s sets
    /// do, and a set saved in either window is read the same way in the other. Unlike
    /// <see cref="LinkRow"/> there is no "(active)": a model the run opens itself has no active workset
    /// anybody chose, so every link needs a name.
    /// </summary>
    internal sealed class InsertLinkRow : INotifyPropertyChanged
    {
        private IReadOnlyList<string> _insideNames;
        private string _inModels = string.Empty;
        private bool _isMissing;
        private string _note = string.Empty;

        public InsertLinkRow(LinkEntry entry)
        {
            Entry = entry;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public LinkEntry Entry { get; }

        public string Key => Entry.Key;

        public string Name => Entry.Name;

        /// <summary>The project workset the link goes into in every model; empty — not chosen yet.</summary>
        public string Workset
        {
            get { return Entry.Workset; }
            set
            {
                var chosen = value ?? string.Empty;
                if (Entry.Workset == chosen)
                    return;

                Entry.Workset = chosen;
                Raise(nameof(Workset));
            }
        }

        /// <summary>The worksets inside the link, read without opening it; null — not read, which is not the same as none.</summary>
        public IReadOnlyList<string> InsideNames
        {
            get { return _insideNames; }
            set
            {
                _insideNames = value;
                Raise(nameof(InsideNames));
                Raise(nameof(Inside));
            }
        }

        /// <summary>The caption of the "Worksets inside" column.</summary>
        public string Inside
        {
            get
            {
                if (InsideNames == null)
                    return string.Empty;

                return InsideNames.Count == 0 ? "none" : InsideNames.Count.ToString();
            }
        }

        /// <summary>The caption of the "In the models" column: in how many of the models read the workset is.</summary>
        public string InModels
        {
            get { return _inModels; }
            set
            {
                if (_inModels == value)
                    return;

                _inModels = value ?? string.Empty;
                Raise(nameof(InModels));
            }
        }

        /// <summary>Some model read has no workset of this name — the caption is shown red.</summary>
        public bool IsMissing
        {
            get { return _isMissing; }
            set
            {
                if (_isMissing == value)
                    return;

                _isMissing = value;
                Raise(nameof(IsMissing));
            }
        }

        /// <summary>A short note: how the workset was guessed, or why it was not.</summary>
        public string Note
        {
            get { return _note; }
            set
            {
                if (_note == value)
                    return;

                _note = value ?? string.Empty;
                Raise(nameof(Note));
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
