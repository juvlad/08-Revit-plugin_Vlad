using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VladTools.Infrastructure;

namespace VladTools.UI
{
    /// <summary>
    /// A row of the "Parameter Sets" table: one parameter of the set, its check box, and the settings
    /// it will be applied to the project with.
    ///
    /// The row edits <see cref="ParameterEntry"/> in place — that object is exactly what gets saved to
    /// disk and handed to the command, so there is nothing to translate back on "Save set" or "Apply".
    ///
    /// The row knows nothing of its own fate against the open project: <see cref="StatusText"/> and
    /// <see cref="IsWarning"/> are filled in by the window after asking the command to look at the
    /// document — the same rule as <c>ScheduleRow</c> and <c>LinkRow</c>, both of which need a live
    /// Revit answer they cannot get themselves.
    /// </summary>
    internal sealed class ParameterSetRow : INotifyPropertyChanged
    {
        private readonly IReadOnlyDictionary<string, string> _categoryLabels;

        private bool _isSelected = true;
        private string _statusText = string.Empty;
        private bool _isWarning;

        public ParameterSetRow(ParameterEntry entry, IReadOnlyDictionary<string, string> categoryLabels)
        {
            Entry = entry;
            _categoryLabels = categoryLabels ?? new Dictionary<string, string>();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The parameter's settings — read and written directly, this is what gets saved and applied.</summary>
        public ParameterEntry Entry { get; }

        public Guid Guid => Entry.Guid;

        /// <summary>The "apply this parameter" check box.</summary>
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
        /// The name as the shared parameter file last spelled it. Read-only on purpose: the set is
        /// looked up by GUID, and a name edited here would only ever drift away from the file it
        /// came from without changing a thing.
        /// </summary>
        public string Name => Entry.Name;

        public string GuidText => Entry.Guid.ToString();

        public ParameterBindingKind Binding
        {
            get { return Entry.Binding; }
            set
            {
                if (Entry.Binding == value)
                    return;

                Entry.Binding = value;
                Raise(nameof(Binding));
                Raise(nameof(CanVary));

                // A type-bound parameter has no "varies across groups" of its own — Revit only asks
                // that question of an instance parameter, so the flag is meaningless with Type chosen.
                if (value == ParameterBindingKind.Type && Entry.VariesAcrossGroups)
                {
                    Entry.VariesAcrossGroups = false;
                    Raise(nameof(VariesAcrossGroups));
                }
            }
        }

        /// <summary>Whether the "varies across groups" box makes sense to show enabled for this row.</summary>
        public bool CanVary => Entry.Binding == ParameterBindingKind.Instance;

        public bool VariesAcrossGroups
        {
            get { return Entry.VariesAcrossGroups; }
            set
            {
                if (Entry.VariesAcrossGroups == value)
                    return;

                Entry.VariesAcrossGroups = value;
                Raise(nameof(VariesAcrossGroups));
            }
        }

        /// <summary><see cref="Autodesk.Revit.DB.BuiltInCategory"/> names — set whole by the category picker.</summary>
        public IReadOnlyList<string> Categories
        {
            get { return Entry.Categories; }
            set
            {
                Entry.Categories = value ?? new List<string>();
                Raise(nameof(Categories));
                Raise(nameof(CategoriesSummary));
            }
        }

        /// <summary>The "Categories" column: names where there are few, a count otherwise.</summary>
        public string CategoriesSummary
        {
            get
            {
                if (Entry.Categories.Count == 0)
                    return "(none chosen)";

                var names = Entry.Categories.Select(Label).ToList();

                return names.Count <= 3
                    ? string.Join(", ", names)
                    : names.Count + " categories";
            }
        }

        private string Label(string builtInName)
        {
            string label;
            return _categoryLabels.TryGetValue(builtInName, out label) ? label : builtInName;
        }

        /// <summary>A <c>ForgeTypeId.TypeId</c> string — bound straight to the group drop-down's <c>SelectedValue</c>.</summary>
        public string GroupTypeId
        {
            get { return Entry.GroupTypeId; }
            set
            {
                var text = value ?? string.Empty;
                if (Entry.GroupTypeId == text)
                    return;

                Entry.GroupTypeId = text;
                Raise(nameof(GroupTypeId));
            }
        }

        /// <summary>What will happen to this row against the open project — filled in by the window.</summary>
        public string StatusText
        {
            get { return _statusText; }
            set { _statusText = value ?? string.Empty; Raise(nameof(StatusText)); }
        }

        /// <summary>Something needs attention (the GUID is missing from the shared file, say) — colours the state.</summary>
        public bool IsWarning
        {
            get { return _isWarning; }
            set
            {
                if (_isWarning == value)
                    return;

                _isWarning = value;
                Raise(nameof(IsWarning));
                Raise(nameof(StatusBrush));
            }
        }

        public Brush StatusBrush => IsWarning ? Brushes.Firebrick : SystemColors.GrayTextBrush;

        private void Raise(string property)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }
}
