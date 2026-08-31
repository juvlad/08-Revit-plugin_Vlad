using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Разбор JSON: объект становится <c>Dictionary&lt;string, object&gt;</c>, массив —
    /// <c>object[]</c>, строка — <c>string</c>, число — <c>double</c>.
    ///
    /// Разборщик свой, на полторы сотни строк. Готовый в .NET Framework есть
    /// (<c>JavaScriptSerializer</c>), но он тянет за собой System.Web.Extensions,
    /// а надстройка живёт в чужом процессе, где лишняя сборка — лишний способ
    /// не загрузиться. Разбирать нужно чужие ответы «только на чтение», и этого хватает.
    ///
    /// Ответы Autodesk и Revit Server большие и меняются от версии к версии, поэтому
    /// правило одно: нет ключа или тип не тот — вернуть пусто, а не бросить исключение.
    /// Читаются ровно нужные поля, всё остальное молча игнорируется.
    /// </summary>
    internal static class Json
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var position = 0;

            try
            {
                return ReadValue(text, ref position);
            }
            catch (Exception)
            {
                // Ответ не разобрался целиком — считаем, что данных нет.
                return null;
            }
        }

        /// <summary>Спуск по вложенным объектам: At(node, "attributes", "extension", "data").</summary>
        public static object At(object node, params string[] keys)
        {
            foreach (var key in keys)
            {
                var map = node as Dictionary<string, object>;
                if (map == null || !map.TryGetValue(key, out node))
                    return null;
            }

            return node;
        }

        /// <summary>Строковое значение по пути; нет его — пустая строка.</summary>
        public static string Str(object node, params string[] keys)
        {
            var value = At(node, keys);
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>Элементы массива по пути; не массив — пустая последовательность.</summary>
        public static IEnumerable<object> Items(object node, params string[] keys)
        {
            var array = At(node, keys) as object[];
            if (array == null)
                yield break;

            foreach (var item in array)
                yield return item;
        }

        // ───────────────────────────── сам разбор ─────────────────────────────

        private static object ReadValue(string text, ref int position)
        {
            SkipSpace(text, ref position);

            if (position >= text.Length)
                throw new FormatException("Ответ оборван.");

            switch (text[position])
            {
                case '{':
                    return ReadObject(text, ref position);

                case '[':
                    return ReadArray(text, ref position);

                case '"':
                    return ReadString(text, ref position);

                case 't':
                    Expect(text, ref position, "true");
                    return true;

                case 'f':
                    Expect(text, ref position, "false");
                    return false;

                case 'n':
                    Expect(text, ref position, "null");
                    return null;

                default:
                    return ReadNumber(text, ref position);
            }
        }

        private static Dictionary<string, object> ReadObject(string text, ref int position)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            position++;

            SkipSpace(text, ref position);
            if (position < text.Length && text[position] == '}')
            {
                position++;
                return map;
            }

            while (true)
            {
                SkipSpace(text, ref position);
                var key = ReadString(text, ref position);

                SkipSpace(text, ref position);
                if (position >= text.Length || text[position] != ':')
                    throw new FormatException("Ожидалось двоеточие.");

                position++;
                map[key] = ReadValue(text, ref position);

                SkipSpace(text, ref position);
                if (position >= text.Length)
                    throw new FormatException("Объект не закрыт.");

                if (text[position] == ',')
                {
                    position++;
                    continue;
                }

                if (text[position] == '}')
                {
                    position++;
                    return map;
                }

                throw new FormatException("Ожидалась запятая или закрывающая скобка.");
            }
        }

        private static object[] ReadArray(string text, ref int position)
        {
            var items = new List<object>();
            position++;

            SkipSpace(text, ref position);
            if (position < text.Length && text[position] == ']')
            {
                position++;
                return items.ToArray();
            }

            while (true)
            {
                items.Add(ReadValue(text, ref position));

                SkipSpace(text, ref position);
                if (position >= text.Length)
                    throw new FormatException("Массив не закрыт.");

                if (text[position] == ',')
                {
                    position++;
                    continue;
                }

                if (text[position] == ']')
                {
                    position++;
                    return items.ToArray();
                }

                throw new FormatException("Ожидалась запятая или закрывающая скобка.");
            }
        }

        private static string ReadString(string text, ref int position)
        {
            if (position >= text.Length || text[position] != '"')
                throw new FormatException("Ожидалась строка.");

            position++;
            var builder = new StringBuilder();

            while (position < text.Length)
            {
                var symbol = text[position++];

                if (symbol == '"')
                    return builder.ToString();

                if (symbol != '\\')
                {
                    builder.Append(symbol);
                    continue;
                }

                if (position >= text.Length)
                    break;

                var escaped = text[position++];
                switch (escaped)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;

                    case 'u':
                        if (position + 4 > text.Length)
                            throw new FormatException("Оборванный код символа.");

                        builder.Append((char)int.Parse(text.Substring(position, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture));
                        position += 4;
                        break;

                    default:
                        builder.Append(escaped);
                        break;
                }
            }

            throw new FormatException("Строка не закрыта.");
        }

        private static double ReadNumber(string text, ref int position)
        {
            var start = position;

            while (position < text.Length && "+-.eE0123456789".IndexOf(text[position]) >= 0)
                position++;

            if (position == start)
                throw new FormatException("Ожидалось число.");

            return double.Parse(text.Substring(start, position - start), CultureInfo.InvariantCulture);
        }

        private static void Expect(string text, ref int position, string literal)
        {
            if (position + literal.Length > text.Length ||
                string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0)
                throw new FormatException("Ожидалось «" + literal + "».");

            position += literal.Length;
        }

        private static void SkipSpace(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
                position++;
        }
    }

    /// <summary>
    /// GET по HTTP с заголовками и разбором JSON. Обе службы, из которых кнопка «Link Manager»
    /// берёт списки моделей (Revit Server и Autodesk Platform Services), устроены одинаково:
    /// GET, заголовки, JSON в ответе.
    /// </summary>
    internal static class Http
    {
        /// <summary>Сколько ждать ответа. Revit на это время замирает, дольше держать нельзя.</summary>
        private const int TimeoutMilliseconds = 30000;

        /// <summary>
        /// Запрашивает адрес и разбирает ответ как JSON.
        /// Ошибка сети или код ответа не 2xx — исключение с текстом, который не стыдно показать.
        /// </summary>
        public static object GetJson(string url, IDictionary<string, string> headers)
        {
            return Json.Parse(GetString(url, headers));
        }

        public static string GetString(string url, IDictionary<string, string> headers)
        {
            // На net48 по умолчанию может быть выключен TLS 1.2, а Autodesk отвечает только по нему.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.Accept = "application/json";
            request.UserAgent = "VladTools";

            if (headers != null)
            {
                foreach (var header in headers)
                    request.Headers[header.Key] = header.Value;
            }

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                {
                    if (stream == null)
                        return string.Empty;

                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            catch (WebException exception)
            {
                throw new InvalidOperationException(Describe(exception, url), exception);
            }
        }

        /// <summary>
        /// Код ответа сам по себе пользователю ничего не говорит, поэтому у частых случаев
        /// есть свой текст: 401 — не вошёл в Autodesk, 403 — нет прав на проект,
        /// 404 — не та версия службы Revit Server или папку убрали.
        /// </summary>
        private static string Describe(WebException exception, string url)
        {
            var response = exception.Response as HttpWebResponse;
            if (response == null)
                return "Не удалось связаться с " + Host(url) + ": " + exception.Message;

            switch ((int)response.StatusCode)
            {
                case 401:
                    return "Autodesk не принял сеанс Revit (401). Войдите в учётную запись Autodesk в Revit и повторите.";
                case 403:
                    return "Доступ запрещён (403): у учётной записи нет прав на этот проект или папку.";
                case 404:
                    return "Адрес не найден (404): " + url + ".\n" +
                           "Для Revit Server это обычно значит, что на сервере нет службы нужной версии.";
                default:
                    return "Служба " + Host(url) + " ответила " + (int)response.StatusCode +
                           " (" + response.StatusDescription + ").";
            }
        }

        private static string Host(string url)
        {
            try
            {
                return new Uri(url).Host;
            }
            catch (UriFormatException)
            {
                return url;
            }
        }
    }
}
