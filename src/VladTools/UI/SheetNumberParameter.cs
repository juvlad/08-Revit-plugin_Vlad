using System;
using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// The parameter "Sheets" shows as a sheet's number: Revit's own Sheet Number, or a shared parameter
    /// known by its GUID — the one identity a shared parameter keeps across projects and files, the same
    /// rule as "Parameter Sets". The name is a label only: for the button, the column heading, and for
    /// saying which parameter a model is missing.
    /// </summary>
    internal sealed class SheetNumberParameter
    {
        public const string RevitName = "Sheet Number";

        public static readonly SheetNumberParameter Revit = new SheetNumberParameter(Guid.Empty, RevitName);

        public SheetNumberParameter(Guid guid, string name)
        {
            Guid = guid;
            Name = string.IsNullOrWhiteSpace(name) ? (guid == Guid.Empty ? RevitName : guid.ToString("D")) : name.Trim();
        }

        /// <summary>Empty for Revit's own Sheet Number.</summary>
        public Guid Guid { get; }

        public string Name { get; }

        public bool IsRevit => Guid == Guid.Empty;

        /// <summary>What the windows' "Number" button reads.</summary>
        public string Label => IsRevit ? RevitName + " (Revit)" : Name;

        public bool Same(SheetNumberParameter other)
        {
            return other != null && other.Guid == Guid;
        }

        /// <summary>"GUID | name", or empty for Revit's own — the settings file's form.</summary>
        public string Format()
        {
            return IsRevit ? string.Empty : Guid.ToString("D") + " | " + Name;
        }

        /// <summary>Reads <see cref="Format"/> back; anything unreadable is Revit's own Sheet Number.</summary>
        public static SheetNumberParameter Parse(string text)
        {
            var value = (text ?? string.Empty).Trim();
            if (value.Length == 0)
                return Revit;

            var bar = value.IndexOf('|');
            var guidText = bar < 0 ? value : value.Substring(0, bar);

            if (!Guid.TryParse(guidText.Trim(), out var guid) || guid == Guid.Empty)
                return Revit;

            return new SheetNumberParameter(guid, bar < 0 ? string.Empty : value.Substring(bar + 1));
        }
    }

    /// <summary>How the numbers of one model came out: the parameter asked for, whether the model has it, what is missing.</summary>
    internal sealed class SheetNumbering
    {
        public static readonly SheetNumbering Revit = new SheetNumbering(SheetNumberParameter.Revit, true, 0, string.Empty);

        public SheetNumbering(SheetNumberParameter parameter, bool isInModel, int withoutValue, string problem)
        {
            Parameter = parameter ?? SheetNumberParameter.Revit;
            IsInModel = isInModel;
            WithoutValue = withoutValue;
            Problem = problem ?? string.Empty;
        }

        /// <summary>The parameter asked for.</summary>
        public SheetNumberParameter Parameter { get; }

        /// <summary>False when no sheet and no titleblock of the model has the parameter asked for: Revit's own numbers stand in.</summary>
        public bool IsInModel { get; }

        /// <summary>
        /// The numbers are the shared parameter's — so Revit's own goes in a column of its own beside
        /// them: it is the one Revit keeps unique, and the one a sheet is found by in Revit.
        /// </summary>
        public bool UsesParameter => !Parameter.IsRevit && IsInModel;

        /// <summary>Sheets the parameter gives no value on — on the sheet or on its titleblock.</summary>
        public int WithoutValue { get; }

        /// <summary>Why the numbers are not the ones asked for; empty when they are.</summary>
        public string Problem { get; }

        /// <summary>The heading of the number column: the parameter's name, or plain "Number" for Revit's own.</summary>
        public string Heading => UsesParameter ? Parameter.Name : "Number";
    }

    /// <summary>The sheets of a model, numbered and in the browser's order — what asking for another number parameter hands back.</summary>
    internal sealed class NumberedSheets
    {
        public NumberedSheets(IReadOnlyList<SheetInfo> sheets, SheetNumbering numbering)
        {
            Sheets = sheets ?? new List<SheetInfo>();
            Numbering = numbering ?? SheetNumbering.Revit;
        }

        public IReadOnlyList<SheetInfo> Sheets { get; }

        public SheetNumbering Numbering { get; }
    }

    /// <summary>One shared parameter found on the open project's sheets or their titleblocks — so the picker can say which of a file's parameters are in use, and how filled.</summary>
    internal sealed class SheetParameterUse
    {
        public SheetParameterUse(Guid guid, string name, string dataType, string text)
        {
            Guid = guid;
            Name = name ?? string.Empty;
            DataType = dataType ?? string.Empty;
            Text = text ?? string.Empty;
        }

        public Guid Guid { get; }

        public string Name { get; }

        public string DataType { get; }

        /// <summary>"on 48 of 50 sheets, 45 with a value".</summary>
        public string Text { get; }
    }

    /// <summary>
    /// The Revit side of choosing a number parameter, handed to the windows as call-backs — reading a
    /// shared parameter file needs Revit's <c>Application</c>, and the windows know nothing about Revit.
    /// </summary>
    internal sealed class SheetNumberSource
    {
        private readonly Func<string, IReadOnlyList<SharedParameterInfo>> _read;
        private readonly Func<string> _currentFile;
        private readonly Func<IReadOnlyList<SheetParameterUse>> _uses;
        private IReadOnlyList<SheetParameterUse> _usesRead;

        /// <param name="read">Reads a shared parameter file's definitions; throws on failure.</param>
        /// <param name="currentFile">The shared parameter file Revit is pointed at.</param>
        /// <param name="uses">The shared parameters on the open project's sheets; null when there is no project — a batch.</param>
        public SheetNumberSource(Func<string, IReadOnlyList<SharedParameterInfo>> read, Func<string> currentFile, Func<IReadOnlyList<SheetParameterUse>> uses)
        {
            _read = read;
            _currentFile = currentFile;
            _uses = uses;
        }

        public bool HasProject => _uses != null;

        public IReadOnlyList<SharedParameterInfo> Read(string path)
        {
            return _read(path);
        }

        public string CurrentFile
        {
            get
            {
                try { return _currentFile?.Invoke() ?? string.Empty; }
                catch (Exception) { return string.Empty; }
            }
        }

        /// <summary>Read once, on the first opening of the picker: a pass over every sheet's parameters.</summary>
        public IReadOnlyList<SheetParameterUse> Uses
        {
            get
            {
                if (_usesRead == null)
                {
                    try { _usesRead = _uses?.Invoke() ?? new List<SheetParameterUse>(); }
                    catch (Exception) { _usesRead = new List<SheetParameterUse>(); }
                }

                return _usesRead;
            }
        }
    }
}
