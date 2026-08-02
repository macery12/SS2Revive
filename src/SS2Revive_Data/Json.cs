using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SS2ReviveData
{
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>
    /// A small JSON document model, written by hand rather than taken from a library.
    ///
    /// The reason is the writer, not the reader. Everything this assembly produces is eventually
    /// read by Bossa's own <c>ManualJsonDeserializer</c>, whose unescaper is a single case: on a
    /// backslash it skips that character and appends the next one literally. That means
    /// <c>\"</c> and <c>\\</c> survive the trip and <c>\n</c>, <c>\t</c> and <c>\uXXXX</c> do not -
    /// the last would arrive as the four literal characters <c>u</c>, <c>0</c>, <c>0</c>, ... .
    ///
    /// Raw non-ASCII is fine, because the response is handed over as a UTF-16 buffer and read a
    /// char at a time, so a Steam persona name in any script passes through untouched. Only
    /// *escape sequences* are the hazard. <see cref="Write"/> therefore escapes exactly two
    /// characters and drops control characters outright, which is not something a general-purpose
    /// serialiser can be asked to do.
    ///
    /// The reader is a normal, complete JSON parser: it has to cope with input we did not write,
    /// notably the game's shipped ProgressionConfig.json.
    /// </summary>
    public sealed class Json
    {
        private readonly List<Json> _array;
        private readonly List<KeyValuePair<string, Json>> _object;
        private readonly string _string;
        private readonly double _number;
        private readonly bool _bool;

        public JsonKind Kind { get; }

        private Json(JsonKind kind, string text = null, double number = 0, bool flag = false,
                     List<Json> array = null, List<KeyValuePair<string, Json>> members = null)
        {
            Kind = kind;
            _string = text;
            _number = number;
            _bool = flag;
            _array = array;
            _object = members;
        }

        // ------------------------------------------------------------- factories

        public static readonly Json Null = new Json(JsonKind.Null);

        public static Json Bool(bool value) => new Json(JsonKind.Bool, flag: value);

        public static Json Num(double value) => new Json(JsonKind.Number, number: value);

        public static Json Num(long value) => new Json(JsonKind.Number, number: value);

        public static Json Num(int value) => new Json(JsonKind.Number, number: value);

        /// <summary>A null string becomes an empty string, never a JSON null. Bossa's readers
        /// call <c>TryReadStringValue</c> and would treat a null as a missing key.</summary>
        public static Json Str(string value) => new Json(JsonKind.String, value ?? string.Empty);

        public static Json Object() =>
            new Json(JsonKind.Object, members: new List<KeyValuePair<string, Json>>());

        public static Json Array() => new Json(JsonKind.Array, array: new List<Json>());

        // ------------------------------------------------------------- accessors

        public int Count =>
            Kind == JsonKind.Array ? _array.Count : Kind == JsonKind.Object ? _object.Count : 0;

        /// <summary>Missing keys return null rather than throwing; callers branch on that.</summary>
        public Json this[string key]
        {
            get
            {
                if (Kind != JsonKind.Object || key == null) return null;
                for (var i = 0; i < _object.Count; i++)
                {
                    if (string.Equals(_object[i].Key, key, StringComparison.Ordinal))
                        return _object[i].Value;
                }
                return null;
            }
        }

        public Json this[int index] =>
            Kind == JsonKind.Array && index >= 0 && index < _array.Count ? _array[index] : null;

        public IEnumerable<Json> Items
        {
            get
            {
                if (Kind != JsonKind.Array) yield break;
                for (var i = 0; i < _array.Count; i++) yield return _array[i];
            }
        }

        public IEnumerable<KeyValuePair<string, Json>> Members
        {
            get
            {
                if (Kind != JsonKind.Object) yield break;
                for (var i = 0; i < _object.Count; i++) yield return _object[i];
            }
        }

        public string AsString(string fallback = "")
        {
            switch (Kind)
            {
                case JsonKind.String: return _string;
                case JsonKind.Number: return FormatNumber(_number);
                case JsonKind.Bool: return _bool ? "true" : "false";
                default: return fallback;
            }
        }

        public double AsDouble(double fallback = 0)
        {
            if (Kind == JsonKind.Number) return _number;
            if (Kind == JsonKind.String
                && double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
            return fallback;
        }

        public long AsLong(long fallback = 0)
        {
            var value = AsDouble(fallback);
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            return (long)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        public int AsInt(int fallback = 0)
        {
            var value = AsLong(fallback);
            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)value;
        }

        public bool AsBool(bool fallback = false)
        {
            if (Kind == JsonKind.Bool) return _bool;
            if (Kind == JsonKind.String) return string.Equals(_string, "true", StringComparison.OrdinalIgnoreCase);
            if (Kind == JsonKind.Number) return _number != 0;
            return fallback;
        }

        // -------------------------------------------------------------- mutators

        /// <summary>Appends. Duplicate keys are not checked for - use <see cref="Set"/> when a key
        /// may already be present.</summary>
        public Json Add(string key, Json value)
        {
            if (Kind != JsonKind.Object) throw new InvalidOperationException("Not a JSON object.");
            _object.Add(new KeyValuePair<string, Json>(key, value ?? Null));
            return this;
        }

        public Json Add(string key, string value) => Add(key, Str(value));

        public Json Add(string key, long value) => Add(key, Num(value));

        public Json Add(string key, double value) => Add(key, Num(value));

        public Json Add(string key, bool value) => Add(key, Bool(value));

        public Json Set(string key, Json value)
        {
            if (Kind != JsonKind.Object) throw new InvalidOperationException("Not a JSON object.");
            for (var i = 0; i < _object.Count; i++)
            {
                if (!string.Equals(_object[i].Key, key, StringComparison.Ordinal)) continue;
                _object[i] = new KeyValuePair<string, Json>(key, value ?? Null);
                return this;
            }
            return Add(key, value);
        }

        public Json Add(Json value)
        {
            if (Kind != JsonKind.Array) throw new InvalidOperationException("Not a JSON array.");
            _array.Add(value ?? Null);
            return this;
        }

        public bool Remove(string key)
        {
            if (Kind != JsonKind.Object) return false;
            for (var i = 0; i < _object.Count; i++)
            {
                if (!string.Equals(_object[i].Key, key, StringComparison.Ordinal)) continue;
                _object.RemoveAt(i);
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- writer

        public override string ToString()
        {
            var builder = new StringBuilder(256);
            Write(builder);
            return builder.ToString();
        }

        /// <summary>
        /// Emits compact JSON with exactly two escapes, <c>\"</c> and <c>\\</c>, and silently drops
        /// characters below U+0020. See the type comment: any other escape would arrive at
        /// ManualJsonDeserializer as literal text, and a raw control character would end the string
        /// early. Dropping is the only remaining option, and it only ever affects text that came
        /// from outside - Steam persona names, mostly, where a stray control character is not
        /// meaningful anyway.
        /// </summary>
        public void Write(StringBuilder output)
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    output.Append("null");
                    return;

                case JsonKind.Bool:
                    output.Append(_bool ? "true" : "false");
                    return;

                case JsonKind.Number:
                    output.Append(FormatNumber(_number));
                    return;

                case JsonKind.String:
                    WriteString(output, _string);
                    return;

                case JsonKind.Array:
                    output.Append('[');
                    for (var i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) output.Append(',');
                        _array[i].Write(output);
                    }
                    output.Append(']');
                    return;

                case JsonKind.Object:
                    output.Append('{');
                    for (var i = 0; i < _object.Count; i++)
                    {
                        if (i > 0) output.Append(',');
                        WriteString(output, _object[i].Key);
                        output.Append(':');
                        _object[i].Value.Write(output);
                    }
                    output.Append('}');
                    return;
            }
        }

        private static void WriteString(StringBuilder output, string value)
        {
            output.Append('"');
            if (value != null)
            {
                for (var i = 0; i < value.Length; i++)
                {
                    var c = value[i];
                    if (c == '"' || c == '\\') output.Append('\\').Append(c);
                    else if (c >= ' ') output.Append(c);
                    // else: dropped, deliberately. Nothing can represent it safely.
                }
            }
            output.Append('"');
        }

        /// <summary>
        /// Integral values are written without a decimal point, because several of these fields
        /// land in <c>int</c> members on the client and its number reader stops at the first
        /// character it does not recognise - "150.0" would parse as 150 here and 150 there, but
        /// only by accident, and timestamps in milliseconds are large enough that exponent
        /// notation would not survive at all.
        /// </summary>
        private static string FormatNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "0";

            if (Math.Abs(value) < 9.007199254740992E15 && value == Math.Floor(value))
                return ((long)value).ToString(CultureInfo.InvariantCulture);

            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- reader

        /// <summary>Returns null when the text is not valid JSON, rather than throwing. Every
        /// caller here is parsing something it did not create and has a sane fallback.</summary>
        public static Json TryParse(string text)
        {
            try
            {
                return Parse(text);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public static Json Parse(string text)
        {
            if (text == null) throw new FormatException("No JSON text.");

            var index = 0;
            var value = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index != text.Length) throw new FormatException("Trailing characters after JSON value.");
            return value;
        }

        private static Json ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return Str(ParseString(s, ref i));
                case 't': Expect(s, ref i, "true"); return Bool(true);
                case 'f': Expect(s, ref i, "false"); return Bool(false);
                case 'n': Expect(s, ref i, "null"); return Null;
                default: return Num(ParseNumber(s, ref i));
            }
        }

        private static Json ParseObject(string s, ref int i)
        {
            var result = Object();
            i++; // '{'
            SkipWhitespace(s, ref i);

            if (i < s.Length && s[i] == '}') { i++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("Expected an object key.");

                var key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' after an object key.");
                i++;

                result.Add(key, ParseValue(s, ref i));

                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new FormatException("Expected ',' or '}' in object.");
            }
        }

        private static Json ParseArray(string s, ref int i)
        {
            var result = Array();
            i++; // '['
            SkipWhitespace(s, ref i);

            if (i < s.Length && s[i] == ']') { i++; return result; }

            while (true)
            {
                result.Add(ParseValue(s, ref i));

                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new FormatException("Expected ',' or ']' in array.");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var builder = new StringBuilder();

            while (i < s.Length)
            {
                var c = s[i++];

                if (c == '"') return builder.ToString();

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (i >= s.Length) break;
                var escape = s[i++];
                switch (escape)
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
                        if (i + 4 > s.Length) throw new FormatException("Truncated \\u escape.");
                        builder.Append((char)ushort.Parse(s.Substring(i, 4), NumberStyles.HexNumber,
                                                          CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default:
                        // Matches what Bossa's own reader does with an unknown escape, so a
                        // round trip through both agrees.
                        builder.Append(escape);
                        break;
                }
            }

            throw new FormatException("Unterminated string.");
        }

        private static double ParseNumber(string s, ref int i)
        {
            var start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;

            while (i < s.Length)
            {
                var c = s[i];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') i++;
                else break;
            }

            if (i == start) throw new FormatException("Expected a number.");

            var text = s.Substring(start, i - start);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new FormatException("Malformed number '" + text + "'.");

            return value;
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length
                || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("Expected '" + literal + "'.");
            }
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                var c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }
    }
}
