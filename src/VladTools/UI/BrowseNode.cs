using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// A tree node in the model browser window: either a folder that can be expanded, or a model
    /// with a check box.
    ///
    /// One tree serves both Revit Server and BIM360: they are built the same way — nested lists that
    /// are expensive to read whole and are therefore read as the nodes are expanded.
    /// The node does not know what to fill its children with: that is up to whoever created it.
    ///
    /// The presentation properties (font, colour, check box visibility) live right here: the markup in
    /// this project is built in code, and binding to a ready-made property is shorter and clearer than
    /// a value converter for every case.
    /// </summary>
    internal sealed class BrowseNode : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _note;

        private BrowseNode(string name, bool isModel, LinkEntry entry, object context)
        {
            Name = name ?? string.Empty;
            IsModel = isModel;
            Entry = entry;
            Context = context;
        }

        /// <summary>A folder whose contents are loaded when it is expanded.</summary>
        public static BrowseNode Folder(string name, object context)
        {
            var node = new BrowseNode(name, false, null, context);

            // The placeholder is what gives the folder its expander triangle: without a single child
            // WPF treats the node as a leaf and will not let it be expanded.
            node.Children.Add(new BrowseNode("…", false, null, null) { IsPlaceholder = true });

            return node;
        }

        /// <summary>A model with a check box. <paramref name="entry"/> is what will go into the link table.</summary>
        public static BrowseNode Model(LinkEntry entry, string note, bool isCheckable)
        {
            return new BrowseNode(entry.Name, true, entry, null)
            {
                _note = note ?? string.Empty,
                IsCheckable = isCheckable
            };
        }

        /// <summary>A line instead of contents: "empty" or the reason for the failure.</summary>
        public static BrowseNode Message(string text, bool isError)
        {
            return new BrowseNode(text, false, null, null) { IsPlaceholder = true, IsError = isError };
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string Name { get; }

        public bool IsModel { get; }

        /// <summary>Which model this is — filled in only on the leaves of the tree.</summary>
        public LinkEntry Entry { get; }

        /// <summary>Everything whoever expands this folder needs to know: the server, the project, the id.</summary>
        public object Context { get; }

        public ObservableCollection<BrowseNode> Children { get; } = new ObservableCollection<BrowseNode>();

        /// <summary>A service row: the expander placeholder, "empty", or an error.</summary>
        public bool IsPlaceholder { get; private set; }

        public bool IsError { get; private set; }

        /// <summary>The contents have already been read — we do not go to the service a second time.</summary>
        public bool IsLoaded { get; set; }

        /// <summary>The check box can be ticked: on a model that is already linked it cannot.</summary>
        public bool IsCheckable { get; private set; } = true;

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

        /// <summary>The grey note to the right of the name: "already in the project", the region, the model count.</summary>
        public string Note
        {
            get { return _note ?? string.Empty; }
            set
            {
                _note = value;
                Raise(nameof(Note));
            }
        }

        // ───────────────────────────── presentation ─────────────────────────────

        public Visibility CheckBoxVisibility => IsModel ? Visibility.Visible : Visibility.Collapsed;

        public FontWeight Weight => IsModel || IsPlaceholder ? FontWeights.Normal : FontWeights.SemiBold;

        public Brush Foreground
        {
            get
            {
                if (IsError)
                    return Brushes.Firebrick;

                return IsPlaceholder || !IsCheckable ? SystemColors.GrayTextBrush : SystemColors.ControlTextBrush;
            }
        }

        /// <summary>Every checked model of this node and of all nested ones.</summary>
        public IEnumerable<BrowseNode> CheckedModels()
        {
            if (IsModel && IsSelected)
                yield return this;

            foreach (var child in Children)
            {
                foreach (var node in child.CheckedModels())
                    yield return node;
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
