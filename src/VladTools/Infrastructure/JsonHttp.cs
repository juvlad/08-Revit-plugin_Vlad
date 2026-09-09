using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// JSON parsing: an object becomes a <c>Dictionary&lt;string, object&gt;</c>, an array an
    /// <c>object[]</c>, a string a <c>string</c>, a number a <c>double</c>.
    ///
    /// The parser is our own, about a hundred and fifty lines. A ready-made one does exist in the
    /// .NET Framework (<c>JavaScriptSerializer</c>), but it drags System.Web.Extensions along, and the
    /// add-in lives inside somebody else's process, where an extra assembly is one more way of failing
    /// to load. What has to be parsed is somebody else's responses, read-only, and that is enough.
    ///
    /// The Autodesk and Revit Server responses are large and change from version to version, so there
    /// is one rule: a missing key or a wrong type returns empty rather than throwing.
    /// Exactly the fields that are needed get read, everything else is silently ignored.
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
                // The response did not parse as a whole — we treat it as having no data.
                return null;
            }
        }

        /// <summary>Descending through nested objects: At(node, "attributes", "extension", "data").</summary>
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

        /// <summary>The string value at a path; an empty string if there is none.</summary>
        public static string Str(object node, params string[] keys)
        {
            var value = At(node, keys);
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>The array items at a path; an empty sequence if it is not an array.</summary>
        public static IEnumerable<object> Items(object node, params string[] keys)
        {
            var array = At(node, keys) as object[];
            if (array == null)
                yield break;

            foreach (var item in array)
                yield return item;
        }

        // ───────────────────────────── the parsing itself ─────────────────────────────

        private static object ReadValue(string text, ref int position)
        {
            SkipSpace(text, ref position);

            if (position >= text.Length)
                throw new FormatException("The response is truncated.");

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
                    throw new FormatException("A colon was expected.");

                position++;
                map[key] = ReadValue(text, ref position);

                SkipSpace(text, ref position);
                if (position >= text.Length)
                    throw new FormatException("The object is not closed.");

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

                throw new FormatException("A comma or a closing bracket was expected.");
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
                    throw new FormatException("The array is not closed.");

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

                throw new FormatException("A comma or a closing bracket was expected.");
            }
        }

        private static string ReadString(string text, ref int position)
        {
            if (position >= text.Length || text[position] != '"')
                throw new FormatException("A string was expected.");

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
                            throw new FormatException("A truncated character escape.");

                        builder.Append((char)int.Parse(text.Substring(position, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture));
                        position += 4;
                        break;

                    default:
                        builder.Append(escaped);
                        break;
                }
            }

            throw new FormatException("The string is not closed.");
        }

        private static double ReadNumber(string text, ref int position)
        {
            var start = position;

            while (position < text.Length && "+-.eE0123456789".IndexOf(text[position]) >= 0)
                position++;

            if (position == start)
                throw new FormatException("A number was expected.");

            return double.Parse(text.Substring(start, position - start), CultureInfo.InvariantCulture);
        }

        private static void Expect(string text, ref int position, string literal)
        {
            if (position + literal.Length > text.Length ||
                string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0)
                throw new FormatException("\"" + literal + "\" was expected.");

            position += literal.Length;
        }

        private static void SkipSpace(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
                position++;
        }
    }

    /// <summary>
    /// An HTTP GET with headers and JSON parsing. Both services the "Link Manager" button takes its
    /// model lists from (Revit Server and Autodesk Platform Services) are built the same way:
    /// a GET, some headers, JSON in the response.
    /// </summary>
    internal static class Http
    {
        /// <summary>How long to wait for a response. Revit freezes for that long, so it must not be held longer.</summary>
        private const int TimeoutMilliseconds = 30000;

        /// <summary>
        /// Requests the address and parses the response as JSON.
        /// A network error or a non-2xx status raises an exception with text fit to be shown.
        /// </summary>
        public static object GetJson(string url, IDictionary<string, string> headers)
        {
            return Json.Parse(GetString(url, headers));
        }

        public static string GetString(string url, IDictionary<string, string> headers)
        {
            // On net48 TLS 1.2 may be off by default, and Autodesk answers over nothing else.
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
        /// A status code on its own tells the user nothing, so the common cases get their own text:
        /// 401 — not signed in to Autodesk, 403 — no rights to the project,
        /// 404 — the wrong Revit Server service version, or the folder was removed.
        /// </summary>
        private static string Describe(WebException exception, string url)
        {
            var response = exception.Response as HttpWebResponse;
            if (response == null)
                return "Could not reach " + Host(url) + ": " + exception.Message;

            switch ((int)response.StatusCode)
            {
                case 401:
                    return "Autodesk did not accept the Revit session (401). Sign in to your Autodesk account in Revit and try again.";
                case 403:
                    return "Access denied (403): the account has no rights to this project or folder.";
                case 404:
                    return "Address not found (404): " + url + ".\n" +
                           "For Revit Server this usually means the server has no service of the required version.";
                default:
                    return "The " + Host(url) + " service answered " + (int)response.StatusCode +
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
