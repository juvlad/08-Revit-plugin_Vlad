using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Разбор формулы Revit: какие имена в ней ссылаются на параметры, которых в семействе нет.
    ///
    /// Разобрать формулу средствами API нельзя, поэтому идём от обратного: из текста вычёркиваются
    /// имена существующих параметров, строковые константы, названия функций и единицы измерения.
    /// Всё, что после этого осталось похожим на имя, — ссылка на отсутствующий параметр.
    /// </summary>
    internal static class FormulaParser
    {
        /// <summary>Слова, которые в формуле значат не параметр: функции и константы Revit.</summary>
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
        /// Имена из формулы, которых нет среди параметров семейства. Порядок — как в формуле, повторы убраны.
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

                // «if (…)» — вызов функции, а не параметр.
                if (OpeningBracket.IsMatch(text.Substring(match.Index + match.Length)))
                    continue;

                // «1 кг», «25 мм» — единица измерения при числе.
                if (NumberBefore.IsMatch(text.Substring(0, match.Index)))
                    continue;

                unknown.Add(name);
            }

            return unknown;
        }

        /// <summary>Убирает из формулы всё, что заведомо не является ссылкой на отсутствующий параметр.</summary>
        private static string Erase(string formula, IEnumerable<string> knownNames)
        {
            var text = TextLiteral.Replace(formula ?? string.Empty, " ");

            // Длинные имена вычёркиваются первыми: иначе из «SP_Масса брутто» уйдёт только «SP_Масса»,
            // а оставшееся «брутто» будет засчитано как отсутствующий параметр.
            var names = (knownNames ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderByDescending(name => name.Length);

            foreach (var name in names)
                text = Regex.Replace(text, @"(?<!\w)" + Regex.Escape(name) + @"(?!\w)", " ");

            return text;
        }
    }
}
