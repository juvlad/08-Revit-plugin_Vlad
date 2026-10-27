using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>One place on Revit Server a link could be pointed at — an item of the "New path" drop-down.</summary>
    internal sealed class LinkTarget
    {
        public LinkTarget(string rsnPath, bool sameName, bool isManual, int distance)
        {
            RsnPath = rsnPath ?? string.Empty;
            SameName = sameName;
            IsManual = isManual;
            Distance = distance;
        }

        /// <summary>RSN://server/folder/model.rvt — exactly what the link will be loaded from.</summary>
        public string RsnPath { get; }

        /// <summary>The server model's name is the link's own, case aside — not a loose guess.</summary>
        public bool SameName { get; }

        /// <summary>Picked by hand in the server tree, so it is the user's word rather than the search's.</summary>
        public bool IsManual { get; }

        /// <summary>How far it lies from the open model in the server's tree (<see cref="ServerModelIndex.Distance"/>).</summary>
        public int Distance { get; }

        public string Name => (RsnPath.Split('/').LastOrDefault() ?? string.Empty);

        /// <summary>The key a link pointing here would have — compared with every link's current one.</summary>
        public string Key => LinkEntry.ForServer(RsnPath).Key;

        public override string ToString()
        {
            return RsnPath;
        }
    }

    /// <summary>
    /// A row of the "Link Updater" table: one link of the open project, where it points now and where
    /// it could point on Revit Server.
    ///
    /// The row never judges itself: <see cref="CanUpdate"/> and <see cref="StatusText"/> are filled in
    /// by the window, which alone sees the other rows (two links given one server model, a model linked
    /// twice) — the same rule as in <see cref="ProjectWorksetRow"/>.
    /// </summary>
    internal sealed class LinkUpdateRow : INotifyPropertyChanged
    {
        private static readonly LinkTarget[] None = new LinkTarget[0];

        private bool _isSelected;
        private bool _canUpdate;
        private bool _isWarning;
        private string _statusText = string.Empty;
        private LinkTarget _target;
        private IReadOnlyList<LinkTarget> _targets = None;

        public LinkUpdateRow(LinkPathInfo info)
        {
            Info = info;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public LinkPathInfo Info { get; }

        public string Name => Info.Name;

        public string Location => Info.Location;

        public string LoadState => Info.LoadState;

        /// <summary>The "repoint this link" check box.</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set { Set(ref _isSelected, value, nameof(IsSelected)); }
        }

        /// <summary>
        /// The user has set the check box themselves. Until then the window proposes — it checks a row
        /// once the search finds exactly one model for it — and from then on it never overrides the choice.
        /// </summary>
        public bool IsTouched { get; set; }

        /// <summary>The check box can be ticked at all: there is somewhere new to point, and nothing in the way.</summary>
        public bool CanUpdate
        {
            get { return _canUpdate; }
            set { Set(ref _canUpdate, value, nameof(CanUpdate)); }
        }

        /// <summary>The drop-down's items: the server models found for this link, the nearest first, one picked by hand on top.</summary>
        public IReadOnlyList<LinkTarget> Targets
        {
            get { return _targets; }
            set { Set(ref _targets, value ?? None, nameof(Targets)); }
        }

        /// <summary>Where the link will be pointed; null while nothing is chosen.</summary>
        public LinkTarget Target
        {
            get { return _target; }
            set
            {
                if (Set(ref _target, value, nameof(Target)))
                    Raise(nameof(Picked));
            }
        }

        /// <summary>
        /// <see cref="Target"/> as the drop-down sets it — never to null. A WPF combo box pushes a null
        /// into its <c>SelectedItem</c> binding when its items are replaced or its row is unloaded, and
        /// the user has no empty item to pick; a null from there is never a choice, and taking it would
        /// silently empty the row.
        /// </summary>
        public LinkTarget Picked
        {
            get { return _target; }
            set
            {
                if (value != null)
                    Target = value;
            }
        }

        /// <summary>What will happen to this link, in words — the "State" column.</summary>
        public string StatusText
        {
            get { return _statusText; }
            set { Set(ref _statusText, value ?? string.Empty, nameof(StatusText)); }
        }

        /// <summary>Something needs the user's eye — the state is coloured, not merely written.</summary>
        public bool IsWarning
        {
            get { return _isWarning; }
            set
            {
                if (Set(ref _isWarning, value, nameof(IsWarning)))
                    Raise(nameof(StatusBrush));
            }
        }

        public Brush StatusBrush => IsWarning ? Brushes.Firebrick : SystemColors.GrayTextBrush;

        private bool Set<T>(ref T field, T value, string property)
        {
            if (Equals(field, value))
                return false;

            field = value;
            Raise(property);
            return true;
        }

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
