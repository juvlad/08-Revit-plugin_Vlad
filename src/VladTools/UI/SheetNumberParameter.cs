using System;
using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// The parameter "Sheets" shows as a sheet's number: Revit's own Sheet Number; a shared parameter,
    /// known by its GUID — the one identity a shared parameter keeps across projects and files, the same
    /// rule as "Parameter Sets", the name a label only; or a parameter that is not shared — a project
    /// parameter, a titleblock family's own — which has nothing but its name to be known by.
    /// </summary>
    internal sealed class SheetNumberParameter
    {
        public const string RevitName = "Sheet Number";

        private const string NameKey = "NAME";

        public static readonly SheetNumberParameter Revit = new SheetNumberParameter(Guid.Empty, RevitName, false);

        public SheetNumberParameter(Guid guid, string name)
            : this(guid, name, false)
        {
        }

        private SheetNumberParameter(Guid guid, string name, bool isByName)
        {
            Guid = isByName ? Guid.Empty : guid;
            IsByName = isByName;
            Name = string.IsNullOrWhiteSpace(name) ? (guid == Guid.Empty ? RevitName : guid.ToString("D")) : name.Trim();
        }

        /// <summary>A parameter that is not shared, found on a sheet or a titleblock by its name.</summary>
        public static SheetNumberParameter ByName(string name)
        {
            return string.IsNullOrWhiteSpace(name) ? Revit : new SheetNumberParameter(Guid.Empty, name, true);
        }

        /// <summary>Empty for Revit's own Sheet Number and for a parameter known by its name.</summary>
        public Guid Guid { get; }

        public string Name { get; }

        /// <summary>Not shared: a project parameter, or a titleblock family's own — the name is all there is to find it by.</summary>
        public bool IsByName { get; }

        public bool IsShared => Guid != Guid.Empty;

        public bool IsRevit => !IsShared && !IsByName;

        /// <summary>What the windows' "Number" button reads.</summary>
        public string Label => IsRevit ? RevitName + " (Revit)" : Name;

        /// <summary>One string per parameter — a GUID, or the name for one known by it; what lists are keyed by.</summary>
        public string Key => IsByName ? NameKey + "|" + Name : Guid.ToString("D");

        public bool Same(SheetNumberParameter other)
        {
            return other != null && string.Equals(other.Key, Key, StringComparison.Ordinal);
        }

        /// <summary>"GUID | name", "NAME | name", or empty for Revit's own — the settings file's form.</summary>
        public string Format()
        {
            if (IsRevit)
                return string.Empty;

            return (IsByName ? NameKey : Guid.ToString("D")) + " | " + Name;
        }

        /// <summary>Reads <see cref="Format"/> back; anything unreadable is Revit's own Sheet Number.</summary>
        public static SheetNumberParameter Parse(string text)
        {
            var value = (text ?? string.Empty).Trim();
            if (value.Length == 0)
                return Revit;

            var bar = value.IndexOf('|');
            var head = (bar < 0 ? value : value.Substring(0, bar)).Trim();
            var name = bar < 0 ? string.Empty : value.Substring(bar + 1);

            if (string.Equals(head, NameKey, StringComparison.OrdinalIgnoreCase))
                return ByName(name);

            if (!Guid.TryParse(head, out var guid) || guid == Guid.Empty)
                return Revit;

            return new SheetNumberParameter(guid, name);
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

        /// <summary>The numbers are the parameter's, and the table and the reports are headed by its name.</summary>
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

    /// <summary>
    /// One parameter found on the open project's sheets or their titleblocks, shared or not — so the
    /// picker can say which parameters are in use and which of them actually hold the numbers.
    /// </summary>
    internal sealed class SheetParameterUse
    {
        public SheetParameterUse(SheetNumberParameter parameter, string dataType, string text, int filled)
        {
            Parameter = parameter;
            DataType = dataType ?? string.Empty;
            Text = text ?? string.Empty;
            Filled = filled;
        }

        public SheetNumberParameter Parameter { get; }

        public string DataType { get; }

        /// <summary>"on 48 of 50 sheets, 45 with a value".</summary>
        public string Text { get; }

        /// <summary>Sheets that get a value from it — off the sheet or off its titleblock.</summary>
        public int Filled { get; }
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
