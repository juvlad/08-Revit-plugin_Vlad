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
            "APS", "AUPT", "APT", "OS", "PT", "SPS",
            "TX", "GS", "GSN", "GSV", "HC", "PB", "OOS", "POS", "PPO"
        };

        /// <summary>
        /// Код раздела из имени модели или пустая строка. Имя дробится на токены по всем
        /// не-буквенно-цифровым знакам (так <c>_R22</c>, точки, дефисы и пробелы уходят сами),
        /// и берётся последний токен, совпавший с кодом из списка, — он ближе к концу имени,
        /// где раздел и ставят.
        /// </summary>
        public static string Detect(string modelName, IEnumerable<string> codes)
        {
            var known = new HashSet<string>(
                (codes ?? Defaults).Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code.Trim()),
                StringComparer.OrdinalIgnoreCase);

            string found = string.Empty;
            foreach (var token in Tokenize(modelName))
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
                .Where(name => Tokenize(name).Any(token => string.Equals(token, code, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private static IEnumerable<string> Tokenize(string text)
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
