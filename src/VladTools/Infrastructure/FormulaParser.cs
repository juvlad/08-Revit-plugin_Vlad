using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Parsing a Revit formula: which names in it refer to parameters the family does not have.
    ///
    /// A formula cannot be parsed through the API, so we work backwards: the text has the names of
    /// existing parameters, string literals, function names and units struck out of it. Whatever
    /// still looks like a name after that is a reference to a missing parameter.
    /// </summary>
    internal static class FormulaParser
    {
        /// <summary>
        /// Words that do not mean a parameter inside a formula: Revit functions and constants.
        /// "да"/"нет" are the yes/no literals of a Russian-localised Revit — they are valid input,
        /// so they stay in the list whatever language the add-in itself speaks.
        /// </summary>
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "if", "and", "or", "not", "xor", "abs", "exp", "log", "log10", "sqrt",
            "sin", "cos", "tan", "asin", "acos", "atan", "round", "roundup", "rounddown",
            "mod", "pi", "e", "true", "false", "yes", "no", "да", "нет"
        };

        private static readonly Regex Identifier = new Regex(@"[\p{L}_][\p{L}\p{Nd}_]*", RegexOptions.Compiled);
        private static readonly Regex TextLiteral = new Regex("\"[^\"]*\"", RegexOptions.Compiled);
        private static readonly Regex OpeningBracket = new Regex(@"^\s*\(", RegexOptions.Compiled);
        private static readonly Regex NumberBefore = new Regex(@"\d\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Names from the formula that are not among the family parameters. The order follows the
        /// formula, duplicates are removed.
        /// </summary>
        public static IReadOnlyList<string> FindUnknownParameters(string formula, IEnumerable<string> knownNames)
        {
            var text = Erase(formula, knownNames);
            var unknown = new List<string>();

            foreach (Match match in Identifier.Matches(text))
            {
                var name = match.Value;

                if (Reserved.Contains(name) || unknown.Contains(name, StringComparer.Ordinal))
                    continue;

                // "if (…)" is a function call, not a parameter.
                if (OpeningBracket.IsMatch(text.Substring(match.Index + match.Length)))
                    continue;

                // "1 kg", "25 mm" — a unit following a number.
                if (NumberBefore.IsMatch(text.Substring(0, match.Index)))
                    continue;

                unknown.Add(name);
            }

            return unknown;
        }

        /// <summary>Strikes out of the formula everything that certainly is not a missing-parameter reference.</summary>
        private static string Erase(string formula, IEnumerable<string> knownNames)
        {
            var text = TextLiteral.Replace(formula ?? string.Empty, " ");

            // Long names are struck out first: otherwise only "SP_Mass" would go from "SP_Mass gross",
            // and the remaining "gross" would be counted as a missing parameter.
            var names = (knownNames ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderByDescending(name => name.Length);

            foreach (var name in names)
                text = Regex.Replace(text, @"(?<!\w)" + Regex.Escape(name) + @"(?!\w)", " ");

            return text;
        }
    }
}
