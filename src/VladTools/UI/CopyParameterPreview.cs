using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace VladTools.UI
{
    /// <summary>What the window asks the command to preview — and, once confirmed, to copy.</summary>
    internal sealed class CopyParameterRequest
    {
        public CopyScope Scope { get; set; }

        /// <summary><see cref="CopyCategoryInfo.Key"/>.</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary><see cref="CopyParameterInfo.Key"/> of the parameter copied from.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary><see cref="CopyParameterInfo.Key"/> of the parameter copied into.</summary>
        public string Target { get; set; } = string.Empty;

        /// <summary>The two parameters' names, for the report and the Undo list.</summary>
        public string SourceName { get; set; } = string.Empty;

        public string TargetName { get; set; } = string.Empty;

        /// <summary>A target that already holds a value is left alone.</summary>
        public bool OnlyEmptyTargets { get; set; }

        /// <summary>An empty source empties a text target, rather than leaving it as it is.</summary>
        public bool EmptyClears { get; set; }

        /// <summary>A number goes into a text without the unit symbol: "1200", not "1200 mm".</summary>
        public bool NoUnitSymbol { get; set; }

        /// <summary>A text that is not a number gives the first number in it: "DN 50" → 50.</summary>
        public bool NumberFromText { get; set; }
    }

    /// <summary>What the copy will do to one line of the preview.</summary>
    internal enum CopyRowState
    {
        Write,

        /// <summary>The target already holds the value.</summary>
        Same,

        /// <summary>Left as it is on purpose: the source is empty, or the target is filled and only empty ones are asked for.</summary>
        Skipped,

        /// <summary>Cannot be written — the state says why.</summary>
        Blocked
    }

    /// <summary>
    /// One line of the preview: an element, or — when the target is a type parameter — a type with the
    /// elements of it in scope.
    /// </summary>
    internal sealed class CopyPreviewRow
    {
        public CopyPreviewRow(string id, string element, int elements, string source, string before, string after, CopyRowState state, string stateText)
        {
            Id = id;
            Element = element;
            Elements = elements;
            Source = source;
            Before = before;
            After = after;
            State = state;
            StateText = stateText;
        }

        public string Id { get; }

        public string Element { get; }

        /// <summary>1 for an element; for a type, its elements in scope.</summary>
        public int Elements { get; }

        public string Source { get; }

        public string Before { get; }

        public string After { get; }

        public CopyRowState State { get; }

        public string StateText { get; }

        public Brush StateBrush
        {
            get
            {
                switch (State)
                {
                    case CopyRowState.Write:
                        return Brushes.DarkGreen;
                    case CopyRowState.Blocked:
                        return Brushes.Firebrick;
                    default:
                        return SystemColors.GrayTextBrush;
                }
            }
        }
    }

    /// <summary>The preview of one request: the lines, what they add up to, and what the window should say above them.</summary>
    internal sealed class CopyParameterPreview
    {
        public CopyParameterPreview(
            CopyParameterRequest request,
            IReadOnlyList<CopyPreviewRow> rows,
            bool targetIsType,
            string conversion,
            IReadOnlyList<string> notes)
        {
            Request = request;
            Rows = rows;
            TargetIsType = targetIsType;
            Conversion = conversion ?? string.Empty;
            Notes = notes ?? new List<string>();

            Write = rows.Count(row => row.State == CopyRowState.Write);
            Same = rows.Count(row => row.State == CopyRowState.Same);
            Skipped = rows.Count(row => row.State == CopyRowState.Skipped);
            Blocked = rows.Count(row => row.State == CopyRowState.Blocked);
            Elements = rows.Sum(row => row.Elements);
        }

        public CopyParameterRequest Request { get; }

        public IReadOnlyList<CopyPreviewRow> Rows { get; }

        /// <summary>The lines are types: writing one changes every element of it, in scope or not.</summary>
        public bool TargetIsType { get; }

        /// <summary>The rule the two parameters are copied by: "Length → Text: …".</summary>
        public string Conversion { get; }

        public IReadOnlyList<string> Notes { get; }

        public int Write { get; }

        public int Same { get; }

        public int Skipped { get; }

        public int Blocked { get; }

        /// <summary>The elements behind the lines.</summary>
        public int Elements { get; }
    }
}
