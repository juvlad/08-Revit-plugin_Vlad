using System.Windows;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>Which elements "Copy Parameters" works on.</summary>
    internal enum CopyScope
    {
        /// <summary>What was selected in Revit when the button was pressed.</summary>
        Selection,

        /// <summary>What the active view shows — on a schedule, the schedule's own elements.</summary>
        ActiveView,

        Model
    }

    /// <summary>A category with elements in the chosen scope — an item of the window's category list.</summary>
    internal sealed class CopyCategoryInfo
    {
        public CopyCategoryInfo(string key, string name, int elements)
        {
            Key = key;
            Name = name;
            Elements = elements;
        }

        /// <summary>What the command finds the category by — its id as text; the window only compares it.</summary>
        public string Key { get; }

        public string Name { get; }

        public int Elements { get; }

        public override string ToString()
        {
            return Name + " (" + Elements + ")";
        }
    }

    /// <summary>
    /// One parameter of the chosen category, as both lists show it. Gathered from one element of each
    /// type, so <see cref="Elements"/> counts the elements of the types that have it — the copy itself
    /// asks every element again.
    /// </summary>
    internal sealed class CopyParameterInfo
    {
        public CopyParameterInfo(string key, string name, bool isType, string kind, string valueKind, int elements, int writable)
        {
            Key = key;
            Name = name;
            IsType = isType;
            Kind = kind;
            ValueKind = valueKind;
            Elements = elements;
            Writable = writable;
        }

        /// <summary>The parameter's identity across families (<c>ParameterKey.Text</c>); the window only compares it.</summary>
        public string Key { get; }

        public string Name { get; }

        public bool IsType { get; }

        /// <summary>The "Of" column: where the value sits.</summary>
        public string Level => IsType ? "Type" : "Instance";

        /// <summary>Built-in, shared, or a project / family one.</summary>
        public string Kind { get; }

        /// <summary>What it holds, in Revit's words: "Text", "Length", "Yes/No"; two kinds when families disagree.</summary>
        public string ValueKind { get; }

        public int Elements { get; }

        /// <summary>Of <see cref="Elements"/>, how many could take a value — the rest compute it or have it set by a formula.</summary>
        public int Writable { get; }

        public bool IsReadOnly => Writable == 0;

        public Brush Foreground => IsReadOnly ? SystemColors.GrayTextBrush : SystemColors.ControlTextBrush;

        public string Tooltip
        {
            get
            {
                var text = Name + " — " + Level.ToLowerInvariant() + " parameter, " + Kind.ToLowerInvariant() + ", " + ValueKind;

                if (IsReadOnly)
                    text += "\nRead-only: Revit computes it, or a formula or a reporting parameter in the family sets it. " +
                            "It can be copied from, not into.";
                else if (Writable < Elements)
                    text += "\nRead-only on " + (Elements - Writable) + " of " + Elements + " elements (a formula sets it in some families).";

                return text;
            }
        }
    }
}
