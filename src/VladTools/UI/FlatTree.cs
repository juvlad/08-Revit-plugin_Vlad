using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VladTools.UI
{
    /// <summary>
    /// A tree laid flat into the rows of a table — how the "Sheets" and "Views to Sheets" windows show
    /// the Project Browser's folders: a folder line, then everything under it one step deeper.
    /// </summary>
    internal static class FlatTree
    {
        /// <summary>
        /// Brings the table in line by removing and inserting only the lines that changed, rather than
        /// clearing it: folding one folder must not throw the scroll position and the selection away.
        /// Both lists keep the order of the whole tree, so one merge pass is enough — a line that is
        /// not wanted any more goes, a wanted line not there yet goes in where the merge has got to.
        /// </summary>
        public static void Sync<T>(ObservableCollection<T> visible, IReadOnlyList<T> target) where T : class
        {
            var wanted = new HashSet<T>(target);

            var position = 0;
            foreach (var row in target)
            {
                while (position < visible.Count && visible[position] != row && !wanted.Contains(visible[position]))
                    visible.RemoveAt(position);

                if (position < visible.Count && visible[position] == row)
                    position++;
                else
                    visible.Insert(position++, row);
            }

            while (visible.Count > position)
                visible.RemoveAt(position);
        }
    }
}
