using System;
using System.ComponentModel;

namespace VladTools.UI
{
    /// <summary>
    /// A list row in the "Model Cleanup" window: the check box, a heading with the number found, and an
    /// explanation of what exactly will disappear.
    ///
    /// The item text lives here rather than in the command: the command counts things in the document,
    /// the window displays them — and how to name an item to the user is the item's own business. An
    /// item with nothing to remove in this model is shown greyed out and cannot be checked: a check box
    /// on it would only be confusing.
    /// </summary>
    internal sealed class CleanupOption : INotifyPropertyChanged
    {
        private readonly string _hint;
        private readonly string _emptyHint;

        private bool _isSelected;

        private CleanupOption(CleanupTarget target, int count, string title, string hint, string emptyHint)
        {
            Target = target;
            Count = count;
            Title = title;
            _hint = hint;
            _emptyHint = emptyHint;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public CleanupTarget Target { get; }

        /// <summary>How many were found when the window opened.</summary>
        public int Count { get; }

        public string Title { get; }

        /// <summary>The heading with the number: without it the item would be a promise, not a report.</summary>
        public string Caption => Count > 0 ? Title + " (" + Count + ")" : Title;

        /// <summary>The explanation under the heading; on an empty item, why it is unavailable.</summary>
        public string Description => Count > 0 ? _hint : _emptyHint;

        /// <summary>There is nothing to remove — the item is disabled.</summary>
        public bool IsAvailable => Count > 0;

        /// <summary>The "remove this from the model" check box.</summary>
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

        /// <summary>Builds an item: the command supplies the number, the item takes all its text from its own table.</summary>
        public static CleanupOption For(CleanupTarget target, int count)
        {
            switch (target)
            {
                case CleanupTarget.UnusedFamilies:
                    return new CleanupOption(target, count,
                        "Purge unused families",
                        "Loaded families and their types that no model element uses; the number in brackets is " +
                        "the type count. Runs last: once the sheets and views are cleaned up, titleblocks, tags " +
                        "and detail components become unneeded as well.",
                        "Every loaded family is in use by something.");

                case CleanupTarget.Sheets:
                    return new CleanupOption(target, count,
                        "Delete every sheet in the model",
                        "The sheets go together with their viewports, titleblocks and stamps. " +
                        "The views themselves stay in the browser — removed from the sheets.",
                        "The model has no sheets.");

                case CleanupTarget.Filters:
                    return new CleanupOption(target, count,
                        "Delete every filter in the model",
                        "Everything from \"View → Filters\": both rule-based view filters and selection filters. " +
                        "The visibility settings that relied on them disappear along with them.",
                        "The model has no filters.");

                case CleanupTarget.Views:
                    return new CleanupOption(target, count,
                        "Delete every view in the model",
                        "Plans, ceiling plans, sections, elevations, callouts, 3D views, walkthroughs and " +
                        "drafting views. The active view stays — Revit will not delete the one you are on; " +
                        "view templates are left alone.",
                        "Apart from the active one, the model has no views.");

                case CleanupTarget.Legends:
                    return new CleanupOption(target, count,
                        "Delete every legend",
                        "Legend views in full, together with the components on them.",
                        "The model has no legends.");

                case CleanupTarget.Schedules:
                    return new CleanupOption(target, count,
                        "Delete every schedule",
                        "Schedules, material and note takeoffs, panel schedules. The internal ones are left " +
                        "alone: the revision schedule inside a titleblock and the internal keynote schedule stay.",
                        "The model has no schedules.");

                case CleanupTarget.ModelGroups:
                    return new CleanupOption(target, count,
                        "Ungroup model groups",
                        "The model groups are ungrouped: the elements stay where they are, only the groups " +
                        "themselves disappear. Pinned groups are unpinned first. The group types left empty " +
                        "are removed by the next item.",
                        "The model has no placed model groups.");

                case CleanupTarget.UnusedGroups:
                    return new CleanupOption(target, count,
                        "Purge unused groups in the model",
                        "Group types — model, detail and attached detail — that are placed nowhere in the " +
                        "model: the ones left hanging in the browser after groups were deleted or ungrouped.",
                        "Every group type is placed in the model.");

                default:
                    throw new ArgumentOutOfRangeException(nameof(target));
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
