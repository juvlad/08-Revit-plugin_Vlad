using System;
using System.Collections.Generic;
using System.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Guessing the project workset from the name of a link model.
    ///
    /// Consultants' models are almost always named on the same principle: somewhere in the name sits a
    /// discipline code — <c>OV</c>, <c>VK</c>, <c>AR</c>, <c>KR</c> — with a Revit version suffix
    /// (<c>_R22</c>) possibly next to it. The project has a link workset for each discipline:
    /// <c>01_Link_OV</c>, <c>01_Связи_OV</c>. So the workset can suggest itself: pull the code out of
    /// the model name and find the workset where that code stands as a separate word.
    ///
    /// The parsing is a heuristic of the same level as <see cref="FormulaParser"/>: not a name parser
    /// but a set of simple rules. The code is looked for **against a list of known ones** rather than
    /// as "the last token": anything at all may sit at the end of a name (a building number, a date,
    /// initials), and the list cuts the rest away. The list is edited in <c>links\_settings.txt</c> (the <c>DISCIPLINE</c> key).
    /// </summary>
    internal static class DisciplineCatalog
    {
        /// <summary>
        /// The default discipline codes, in Latin letters as they are usually written in file names
        /// (transliterated Russian discipline marks plus a few English ones). The list is not
        /// exhaustive: that is exactly why it lives in the settings, so a code can be added without a rebuild.
        /// </summary>
        public static readonly IReadOnlyList<string> Defaults = new[]
        {
            "AR", "AS", "GP", "PZ",
            "KR", "KJ", "KZH", "KM", "KMD", "KD",
            "OV", "OViK", "HVAC", "TM", "TS", "ITP",
            "VK", "NVK", "VOK", "PL",
            "EOM", "EO", "EM", "EG", "ES", "EN",
            "SS", "SKS", "SORS", "SOUE", "AK", "ATX", "ASU", "KIPiA",
            "APS", "AUPT", "APT", "OS", "PS", "PT", "SPS",
            "TX", "GS", "GSN", "GSV", "HC", "PB", "OOS", "POS", "PPO"
        };

        /// <summary>
        /// The disciplines usually linked into every project model: architecture, structure, electrics,
        /// fire systems, heating and ventilation, water supply. This is not a subset of
        /// <see cref="Defaults"/> "by meaning" but a separate list: there are all the codes that occur
        /// in file names, here are the ones whose models are actually needed at work.
        /// Edited in <c>links\_settings.txt</c> (the <c>KIT</c> lines).
        /// </summary>
        public static readonly IReadOnlyList<string> KitDefaults = new[]
        {
            "AR", "KR", "ES", "PS", "PT", "OV", "VK"
        };

        /// <summary>
        /// The discipline code from a model name, or an empty string. The name is split into tokens at
        /// every non-alphanumeric character (so <c>_R22</c>, dots, hyphens and spaces fall away by
        /// themselves), and the last token matching a code from the list is taken — it is closer to the
        /// end of the name, which is where the discipline is put.
        /// </summary>
        public static string Detect(string modelName, IEnumerable<string> codes)
        {
            var known = Known(codes);

            string found = string.Empty;
            foreach (var token in Split(modelName))
            {
                if (known.Contains(token))
                    found = token;
            }

            return found;
        }

        /// <summary>
        /// Project workset names in which the code stands as a separate word: <c>01_Link_OV</c>,
        /// <c>01_Связи_OV</c>, <c>Link OV</c> — yes; <c>Provod</c> — no. The comparison is by tokens,
        /// the same ones <see cref="Detect"/> uses, so case does not matter and an <c>OV</c> inside
        /// another word is not picked up.
        /// </summary>
        public static IReadOnlyList<string> MatchingWorksets(string code, IEnumerable<string> worksetNames)
        {
            if (string.IsNullOrWhiteSpace(code) || worksetNames == null)
                return new List<string>();

            return worksetNames
                .Where(name => Split(name).Any(token => string.Equals(token, code, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        /// <summary>
        /// A name split into alphanumeric pieces: <c>MK3-VSC-B01-AR_R22</c> →
        /// <c>MK3</c>, <c>VSC</c>, <c>B01</c>, <c>AR</c>, <c>R22</c>. One split serves everything: it
        /// finds the discipline code, the building number and the workset name match alike.
        /// </summary>
        public static IReadOnlyList<string> Tokens(string text)
        {
            return Split(text).ToList();
        }

        /// <summary>The name contains this piece as a separate word: <c>B01</c> in <c>MK3-VSC-B01-AR</c> — yes, in <c>B012</c> — no.</summary>
        public static bool HasToken(string text, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            var wanted = token.Trim();

            return Split(text).Any(part => string.Equals(part, wanted, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Whether the project has per-discipline worksets at all. Needed to tell "the workset for this
        /// discipline is missing" from "they do not split them that way here": in the second case a note
        /// on every workset that was not found would be noise across the whole table.
        /// </summary>
        public static bool HasDisciplineWorksets(IEnumerable<string> worksetNames, IEnumerable<string> codes)
        {
            if (worksetNames == null || codes == null)
                return false;

            var known = Known(codes);

            return worksetNames.Any(name => Split(name).Any(token => known.Contains(token)));
        }

        /// <summary>
        /// When several worksets match the code, this picks between them by two rules in turn.
        ///
        /// **The first — by the model name itself.** Worksets are sometimes split not only by discipline
        /// but by building too: "01_Link_AR_B01", "01_Link_AR_B03". Then the right one identifies itself —
        /// its name carries the same building as the model name ("MK3-VSC-B03-AR" → B03).
        /// The rule is general rather than about buildings: the workset with the most words in common
        /// with the model name wins.
        ///
        /// **The second — by how the other disciplines' worksets are named.** If the project has
        /// "01_Link_ES", "01_Link_OV" and "01_Link_VK", then out of "01_Link_AR" and "05_AR_Elevations"
        /// the first is wanted. It is computed without sample lists: a name's "shape" is taken — the name
        /// with the code cut out ("01_Link_ES" → "01_Link_·") — and the winner is the candidate whose
        /// shape is worn by the worksets of the greatest number of **other** disciplines.
        ///
        /// If neither rule separates the candidates — null: guessing is not allowed, the choice is the user's.
        /// </summary>
        /// <param name="modelName">The link model name — the first rule works from it.</param>
        public static string Preferred(
            string code,
            IReadOnlyList<string> candidates,
            IEnumerable<string> worksetNames,
            IEnumerable<string> codes,
            string modelName)
        {
            if (candidates == null || candidates.Count == 0 || worksetNames == null)
                return null;

            if (candidates.Count == 1)
                return candidates[0];

            var byName = Best(candidates, candidate => Shared(candidate, code, modelName));
            if (byName != null)
                return byName;

            var known = Known(codes ?? Defaults);
            var shapes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in worksetNames)
            {
                foreach (var token in Split(name).Where(known.Contains))
                {
                    var shape = Shape(name, token);
                    if (shape == null)
                        continue;

                    HashSet<string> owners;
                    if (!shapes.TryGetValue(shape, out owners))
                        shapes[shape] = owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    owners.Add(token);
                }
            }

            return Best(candidates, candidate =>
            {
                var shape = Shape(candidate, code);
                HashSet<string> owners;

                if (shape == null || !shapes.TryGetValue(shape, out owners))
                    return 0;

                // We do not count ourselves: what matters is how many **other** disciplines are named the same way.
                return owners.Count(owner => !string.Equals(owner, code, StringComparison.OrdinalIgnoreCase));
            });
        }

        /// <summary>
        /// The candidate with the highest score; zero or a tie yields null. A tie here is not an annoying
        /// detail but the only honest answer: two equally suitable worksets mean "the user chooses".
        /// </summary>
        private static string Best(IReadOnlyList<string> candidates, Func<string, int> score)
        {
            string best = null;
            var bestScore = 0;
            var tied = false;

            foreach (var candidate in candidates)
            {
                var value = score(candidate);

                if (value > bestScore)
                {
                    best = candidate;
                    bestScore = value;
                    tied = false;
                }
                else if (value == bestScore && value > 0)
                {
                    tied = true;
                }
            }

            return tied || bestScore == 0 ? null : best;
        }

        /// <summary>
        /// How many words of the workset name, apart from the discipline code itself, occur in the model
        /// name. That is how "01_Link_AR_B03" beats "01_Link_AR_B01" on the model "MK3-VSC-B03-AR".
        /// </summary>
        private static int Shared(string worksetName, string code, string modelName)
        {
            if (string.IsNullOrEmpty(modelName))
                return 0;

            var words = new HashSet<string>(Split(modelName), StringComparer.OrdinalIgnoreCase);

            return Split(worksetName)
                .Where(token => !string.Equals(token, code, StringComparison.OrdinalIgnoreCase))
                .Count(words.Contains);
        }

        /// <summary>The workset name with the code cut out: "01_Link_ES" + ES → "01_Link_·". No code — null.</summary>
        private static string Shape(string name, string code)
        {
            var text = name ?? string.Empty;
            var start = -1;

            for (var i = 0; i <= text.Length; i++)
            {
                var letter = i < text.Length && char.IsLetterOrDigit(text[i]);

                if (letter)
                {
                    if (start < 0)
                        start = i;

                    continue;
                }

                if (start >= 0)
                {
                    if (string.Equals(text.Substring(start, i - start), code, StringComparison.OrdinalIgnoreCase))
                        return text.Substring(0, start) + '\u00b7' + text.Substring(i);

                    start = -1;
                }
            }

            return null;
        }

        private static HashSet<string> Known(IEnumerable<string> codes)
        {
            return new HashSet<string>(
                (codes ?? Defaults).Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code.Trim()),
                StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> Split(string text)
        {
            if (string.IsNullOrEmpty(text))
                yield break;

            var start = -1;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsLetterOrDigit(text[i]))
                {
                    if (start < 0)
                        start = i;
                }
                else if (start >= 0)
                {
                    yield return text.Substring(start, i - start);
                    start = -1;
                }
            }

            if (start >= 0)
                yield return text.Substring(start);
        }
    }
}
