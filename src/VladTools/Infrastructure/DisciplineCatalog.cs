using System;
using System.Collections.Generic;
using System.Linq;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Подбор рабочего набора проекта по имени модели связи.
    ///
    /// Модели смежников почти всегда названы по одному принципу: где-то в имени стоит код
    /// раздела проектирования — <c>OV</c>, <c>VK</c>, <c>AR</c>, <c>KR</c>, — а рядом может быть
    /// суффикс версии Revit (<c>_R22</c>). В проекте под каждый раздел заведён свой набор для
    /// связей: <c>01_Link_OV</c>, <c>01_Связи_OV</c>. Значит, набор можно предложить сам:
    /// вытащить код из имени модели и найти набор, где этот код стоит отдельным словом.
    ///
    /// Разбор — эвристика того же уровня, что <see cref="FormulaParser"/>: не парсер имени,
    /// а набор простых правил. Код ищется **по списку известных**, а не «последним токеном»:
    /// в конце имени может оказаться что угодно (номер корпуса, дата, инициалы), а список
    /// отсекает лишнее. Список правится в <c>links\_settings.txt</c> (ключ <c>DISCIPLINE</c>).
    /// </summary>
    internal static class DisciplineCatalog
    {
        /// <summary>
        /// Коды разделов по умолчанию — латиницей, как их обычно пишут в именах файлов
        /// (русские марки в транслите плюс несколько англоязычных). Список не исчерпывающий:
        /// он для того и вынесен в настройки, чтобы дописать свой код без пересборки.
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
        /// Разделы, которые обычно грузят связями в каждую модель проекта: АР, КР, электрика,
        /// пожарные системы, отопление-вентиляция, водоснабжение. Это не подмножество
        /// <see cref="Defaults"/> «по смыслу», а отдельный список: там — все коды, какие
        /// встречаются в именах файлов, здесь — те, чьи модели нужны в работе.
        /// Правится в <c>links\_settings.txt</c> (строки <c>KIT</c>).
        /// </summary>
        public static readonly IReadOnlyList<string> KitDefaults = new[]
        {
            "AR", "KR", "ES", "PS", "PT", "OV", "VK"
        };

        /// <summary>
        /// Код раздела из имени модели или пустая строка. Имя дробится на токены по всем
        /// не-буквенно-цифровым знакам (так <c>_R22</c>, точки, дефисы и пробелы уходят сами),
        /// и берётся последний токен, совпавший с кодом из списка, — он ближе к концу имени,
        /// где раздел и ставят.
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
        /// Имена наборов проекта, в которых код стоит отдельным словом: <c>01_Link_OV</c>,
        /// <c>01_Связи_OV</c>, <c>Связь OV</c> — да, а <c>Provod</c> — нет. Сравнение по токенам,
        /// тем же, что у <see cref="Detect"/>, поэтому регистр не важен, а <c>OV</c> внутри
        /// другого слова не цепляется.
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
        /// Имя, разобранное на буквенно-цифровые куски: <c>MK3-VSC-B01-AR_R22</c> →
        /// <c>MK3</c>, <c>VSC</c>, <c>B01</c>, <c>AR</c>, <c>R22</c>. Один разбор на всех:
        /// им ищется и код раздела, и номер корпуса, и совпадение имени набора.
        /// </summary>
        public static IReadOnlyList<string> Tokens(string text)
        {
            return Split(text).ToList();
        }

        /// <summary>Имя содержит этот кусок отдельным словом: <c>B01</c> в <c>MK3-VSC-B01-AR</c> — да, в <c>B012</c> — нет.</summary>
        public static bool HasToken(string text, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            var wanted = token.Trim();

            return Split(text).Any(part => string.Equals(part, wanted, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// В проекте вообще заведены наборы под разделы или нет. Нужно, чтобы отличить
        /// «набора для этого раздела не хватает» от «здесь так не делят»: во втором случае
        /// приписка про каждый ненайденный набор была бы шумом на всю таблицу.
        /// </summary>
        public static bool HasDisciplineWorksets(IEnumerable<string> worksetNames, IEnumerable<string> codes)
        {
            if (worksetNames == null || codes == null)
                return false;

            var known = Known(codes);

            return worksetNames.Any(name => Split(name).Any(token => known.Contains(token)));
        }

        /// <summary>
        /// Когда под код подходит несколько наборов, выбирает из них по двум правилам подряд.
        ///
        /// **Первое — по имени самой модели.** Наборы бывают разведены не только по разделам,
        /// но и по корпусам: «01_Link_AR_B01», «01_Link_AR_B03». Тогда нужный узнаётся сам —
        /// в его имени стоит тот же корпус, что и в имени модели («MK3-VSC-B03-AR» → B03).
        /// Правило общее, а не про корпуса: выигрывает набор, у которого больше общих слов
        /// с именем модели.
        ///
        /// **Второе — по тому, как названы наборы остальных разделов.** Раз в проекте есть
        /// «01_Link_ES», «01_Link_OV» и «01_Link_VK», то из «01_Link_AR» и «05_AR_Фасады»
        /// нужен первый. Считается без списков образцов: у имени берётся «облик» — само имя
        /// с вырезанным кодом («01_Link_ES» → «01_Link_·»), — и выигрывает кандидат, чей
        /// облик носят наборы наибольшего числа **других** разделов.
        ///
        /// Ни то ни другое не разделило кандидатов — null: гадать нельзя, выбор за человеком.
        /// </summary>
        /// <param name="modelName">Имя модели связи — по нему работает первое правило.</param>
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

                // Себя не считаем: интересно, сколько **чужих** разделов названо так же.
                return owners.Count(owner => !string.Equals(owner, code, StringComparison.OrdinalIgnoreCase));
            });
        }

        /// <summary>
        /// Кандидат с наибольшим счётом; ноль или ничья — null. Ничья тут не досадная мелочь,
        /// а единственный честный ответ: два одинаково подходящих набора значит «выбирает человек».
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
        /// Сколько слов имени набора, кроме самого кода раздела, встречается в имени модели.
        /// Так «01_Link_AR_B03» обгоняет «01_Link_AR_B01» на модели «MK3-VSC-B03-AR».
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

        /// <summary>Имя набора с вырезанным кодом: «01_Link_ES» + ES → «01_Link_·». Кода нет — null.</summary>
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
