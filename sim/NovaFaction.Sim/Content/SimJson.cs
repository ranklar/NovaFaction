using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Content
{
    /// <summary>
    /// Error in a JSON content file: bad syntax, a wrong type, or a value that breaks a content rule.
    /// <see cref="Line"/> and <see cref="Column"/> are 1-based and point at the offending character
    /// or value (0 when the error is about the document as a whole).
    /// </summary>
    public sealed class SimJsonException : Exception
    {
        public SimJsonException(string source, int line, int column, string detail)
            : base(FormatMessage(source, line, column, detail))
        {
            SourceName = source;
            Line = line;
            Column = column;
            Detail = detail;
        }

        /// <summary>Name of the document, e.g. "rules.json".</summary>
        public string SourceName { get; }
        public int Line { get; }
        public int Column { get; }
        /// <summary>The message without the location prefix.</summary>
        public string Detail { get; }

        private static string FormatMessage(string source, int line, int column, string detail)
        {
            if (line <= 0)
            {
                return source + ": " + detail;
            }
            return source + " (line " + line.ToString(CultureInfo.InvariantCulture)
                + ", column " + column.ToString(CultureInfo.InvariantCulture) + "): " + detail;
        }
    }

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
    /// A parsed JSON value. Numbers keep their original text and are only converted on request, to
    /// int/long or to <see cref="Fix"/> — never to float or double. Object members keep file order;
    /// duplicate keys are rejected at parse time.
    /// </summary>
    public sealed class JsonValue
    {
        private readonly bool _bool;
        private readonly string? _text; // string contents, or the number's literal text
        private readonly List<JsonValue>? _items;
        private readonly List<KeyValuePair<string, JsonValue>>? _members;
        private readonly Dictionary<string, JsonValue>? _lookup; // lookup only, never iterated

        private JsonValue(JsonKind kind, string source, int line, int column, bool boolValue, string? text,
            List<JsonValue>? items, List<KeyValuePair<string, JsonValue>>? members,
            Dictionary<string, JsonValue>? lookup)
        {
            Kind = kind;
            SourceName = source;
            Line = line;
            Column = column;
            _bool = boolValue;
            _text = text;
            _items = items;
            _members = members;
            _lookup = lookup;
        }

        public JsonKind Kind { get; }
        public string SourceName { get; }
        public int Line { get; }
        public int Column { get; }

        internal static JsonValue Null(string s, int l, int c) =>
            new JsonValue(JsonKind.Null, s, l, c, false, null, null, null, null);

        internal static JsonValue Bool(string s, int l, int c, bool v) =>
            new JsonValue(JsonKind.Bool, s, l, c, v, null, null, null, null);

        internal static JsonValue Number(string s, int l, int c, string literal) =>
            new JsonValue(JsonKind.Number, s, l, c, false, literal, null, null, null);

        internal static JsonValue String(string s, int l, int c, string v) =>
            new JsonValue(JsonKind.String, s, l, c, false, v, null, null, null);

        internal static JsonValue Array(string s, int l, int c, List<JsonValue> items) =>
            new JsonValue(JsonKind.Array, s, l, c, false, null, items, null, null);

        internal static JsonValue Object(string s, int l, int c,
            List<KeyValuePair<string, JsonValue>> members, Dictionary<string, JsonValue> lookup) =>
            new JsonValue(JsonKind.Object, s, l, c, false, null, null, members, lookup);

        /// <summary>Builds an error that points at this value.</summary>
        public SimJsonException Error(string detail) => new SimJsonException(SourceName, Line, Column, detail);

        public bool IsNull => Kind == JsonKind.Null;

        public bool AsBool()
        {
            Expect(JsonKind.Bool);
            return _bool;
        }

        public string AsString()
        {
            Expect(JsonKind.String);
            return _text!;
        }

        /// <summary>The number exactly as written in the file.</summary>
        public string NumberText
        {
            get
            {
                Expect(JsonKind.Number);
                return _text!;
            }
        }

        /// <summary>A whole number in the int range (no fraction or exponent).</summary>
        public int AsInt()
        {
            long value = AsLong();
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw Error("number " + _text + " is outside the int range.");
            }
            return (int)value;
        }

        /// <summary>A whole number in the long range (no fraction or exponent).</summary>
        public long AsLong()
        {
            Expect(JsonKind.Number);
            string text = _text!;
            bool negative = text[0] == '-';
            ulong magnitude = 0;
            for (int i = negative ? 1 : 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9')
                {
                    throw Error("expected a whole number but found " + text + ".");
                }
                ulong digit = (ulong)(c - '0');
                if (magnitude > (ulong.MaxValue - digit) / 10)
                {
                    throw Error("number " + text + " is outside the long range.");
                }
                magnitude = magnitude * 10 + digit;
            }
            const ulong maxNegative = 1UL << 63;
            if (negative)
            {
                if (magnitude > maxNegative)
                {
                    throw Error("number " + text + " is outside the long range.");
                }
                return unchecked(-(long)magnitude);
            }
            if (magnitude > long.MaxValue)
            {
                throw Error("number " + text + " is outside the long range.");
            }
            return (long)magnitude;
        }

        /// <summary>
        /// A fixed-point number via <see cref="Fix.TryParse"/>: plain decimals such as 3, 0.35, -1.5.
        /// Exponents are valid JSON but are rejected here.
        /// </summary>
        public Fix AsFix()
        {
            Expect(JsonKind.Number);
            if (!Fix.TryParse(_text, out Fix value))
            {
                throw Error("number " + _text + " is not a valid fixed-point value "
                    + "(write a plain decimal such as 1.5, without an exponent, within the Fix range).");
            }
            return value;
        }

        public IReadOnlyList<JsonValue> AsArray()
        {
            Expect(JsonKind.Array);
            return _items!;
        }

        /// <summary>Object members in file order.</summary>
        public IReadOnlyList<KeyValuePair<string, JsonValue>> Members
        {
            get
            {
                Expect(JsonKind.Object);
                return _members!;
            }
        }

        public bool TryGet(string key, out JsonValue value)
        {
            Expect(JsonKind.Object);
            if (_lookup!.TryGetValue(key, out JsonValue? found))
            {
                value = found;
                return true;
            }
            value = null!;
            return false;
        }

        /// <summary>Object member; throws if missing.</summary>
        public JsonValue Get(string key)
        {
            if (!TryGet(key, out JsonValue value))
            {
                throw Error("missing required key \"" + key + "\".");
            }
            return value;
        }

        private void Expect(JsonKind kind)
        {
            if (Kind != kind)
            {
                throw Error("expected " + Describe(kind) + " but found " + Describe(Kind) + ".");
            }
        }

        internal static string Describe(JsonKind kind)
        {
            switch (kind)
            {
                case JsonKind.Null: return "null";
                case JsonKind.Bool: return "a boolean";
                case JsonKind.Number: return "a number";
                case JsonKind.String: return "a string";
                case JsonKind.Array: return "an array";
                default: return "an object";
            }
        }
    }

    /// <summary>
    /// Strict JSON reader (RFC 8259) with no dependencies. Rejects comments, trailing commas,
    /// single quotes, unquoted keys, leading zeros, NaN/Infinity, control characters in strings,
    /// unpaired surrogate escapes, duplicate object keys, and anything after the root value.
    /// A single leading byte-order mark is tolerated. Nesting is limited to <see cref="MaxDepth"/>.
    /// </summary>
    public static class SimJson
    {
        public const int MaxDepth = 64;

        /// <param name="text">The JSON document.</param>
        /// <param name="sourceName">Name used in error messages, e.g. "rules.json".</param>
        public static JsonValue Parse(string text, string sourceName = "json")
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            var parser = new Parser(text, sourceName ?? "json");
            return parser.ParseDocument();
        }

        private sealed class Parser
        {
            private readonly string _text;
            private readonly string _source;
            private int _pos;
            private int _line = 1;
            private int _lineStart;
            private int _depth;

            public Parser(string text, string source)
            {
                _text = text;
                _source = source;
            }

            public JsonValue ParseDocument()
            {
                if (_text.Length > 0 && _text[0] == '﻿')
                {
                    _pos = 1;
                    _lineStart = 1;
                }
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Fail("document is empty.");
                }
                JsonValue root = ParseValue();
                SkipWhitespace();
                if (!AtEnd)
                {
                    throw Fail("unexpected " + DescribeChar(_text[_pos]) + " after the end of the document.");
                }
                return root;
            }

            private bool AtEnd => _pos >= _text.Length;

            private int Column => _pos - _lineStart + 1;

            private SimJsonException Fail(string detail) => new SimJsonException(_source, _line, Column, detail);

            private SimJsonException FailAt(int line, int column, string detail) =>
                new SimJsonException(_source, line, column, detail);

            private void SkipWhitespace()
            {
                while (!AtEnd)
                {
                    char c = _text[_pos];
                    if (c == '\n')
                    {
                        _pos++;
                        _line++;
                        _lineStart = _pos;
                    }
                    else if (c == ' ' || c == '\t' || c == '\r')
                    {
                        _pos++;
                    }
                    else
                    {
                        return;
                    }
                }
            }

            private JsonValue ParseValue()
            {
                if (AtEnd)
                {
                    throw Fail("unexpected end of document, expected a value.");
                }
                char c = _text[_pos];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"':
                    {
                        int line = _line, column = Column;
                        return JsonValue.String(_source, line, column, ParseString());
                    }
                    case 't': return ParseLiteral("true", JsonValue.Bool(_source, _line, Column, true));
                    case 'f': return ParseLiteral("false", JsonValue.Bool(_source, _line, Column, false));
                    case 'n': return ParseLiteral("null", JsonValue.Null(_source, _line, Column));
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ParseNumber();
                        }
                        if (c == '/')
                        {
                            throw Fail("comments are not allowed in JSON.");
                        }
                        if (c == '\'')
                        {
                            throw Fail("strings must use double quotes.");
                        }
                        throw Fail("unexpected " + DescribeChar(c) + ", expected a value.");
                }
            }

            private JsonValue ParseLiteral(string word, JsonValue result)
            {
                if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0 || IsWordChar(_pos + word.Length))
                {
                    throw Fail("invalid literal; expected " + word + ".");
                }
                _pos += word.Length;
                return result;
            }

            private bool IsWordChar(int index)
            {
                if (index >= _text.Length)
                {
                    return false;
                }
                char c = _text[index];
                return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
            }

            private void Enter()
            {
                if (++_depth > MaxDepth)
                {
                    throw Fail("nesting is deeper than " + MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels.");
                }
            }

            private JsonValue ParseObject()
            {
                int line = _line, column = Column;
                Enter();
                _pos++; // '{'
                var members = new List<KeyValuePair<string, JsonValue>>();
                var lookup = new Dictionary<string, JsonValue>(StringComparer.Ordinal);

                SkipWhitespace();
                if (!AtEnd && _text[_pos] == '}')
                {
                    _pos++;
                    _depth--;
                    return JsonValue.Object(_source, line, column, members, lookup);
                }

                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Fail("unexpected end of document inside an object (opened at line "
                            + line.ToString(CultureInfo.InvariantCulture) + ", column "
                            + column.ToString(CultureInfo.InvariantCulture) + ").");
                    }
                    if (_text[_pos] != '"')
                    {
                        if (_text[_pos] == '}')
                        {
                            throw Fail("trailing comma before '}' is not allowed.");
                        }
                        throw Fail("expected a double-quoted key but found " + DescribeChar(_text[_pos]) + ".");
                    }
                    int keyLine = _line, keyColumn = Column;
                    string key = ParseString();
                    if (lookup.ContainsKey(key))
                    {
                        throw FailAt(keyLine, keyColumn, "duplicate key \"" + key + "\".");
                    }

                    SkipWhitespace();
                    if (AtEnd || _text[_pos] != ':')
                    {
                        throw Fail("expected ':' after key \"" + key + "\".");
                    }
                    _pos++;
                    SkipWhitespace();
                    JsonValue value = ParseValue();
                    members.Add(new KeyValuePair<string, JsonValue>(key, value));
                    lookup.Add(key, value);

                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Fail("unexpected end of document, expected ',' or '}'.");
                    }
                    char c = _text[_pos];
                    if (c == ',')
                    {
                        _pos++;
                        continue;
                    }
                    if (c == '}')
                    {
                        _pos++;
                        _depth--;
                        return JsonValue.Object(_source, line, column, members, lookup);
                    }
                    throw Fail("expected ',' or '}' but found " + DescribeChar(c) + ".");
                }
            }

            private JsonValue ParseArray()
            {
                int line = _line, column = Column;
                Enter();
                _pos++; // '['
                var items = new List<JsonValue>();

                SkipWhitespace();
                if (!AtEnd && _text[_pos] == ']')
                {
                    _pos++;
                    _depth--;
                    return JsonValue.Array(_source, line, column, items);
                }

                while (true)
                {
                    SkipWhitespace();
                    if (!AtEnd && _text[_pos] == ']')
                    {
                        throw Fail("trailing comma before ']' is not allowed.");
                    }
                    items.Add(ParseValue());
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Fail("unexpected end of document, expected ',' or ']'.");
                    }
                    char c = _text[_pos];
                    if (c == ',')
                    {
                        _pos++;
                        continue;
                    }
                    if (c == ']')
                    {
                        _pos++;
                        _depth--;
                        return JsonValue.Array(_source, line, column, items);
                    }
                    throw Fail("expected ',' or ']' but found " + DescribeChar(c) + ".");
                }
            }

            private string ParseString()
            {
                int startLine = _line, startColumn = Column;
                _pos++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                    {
                        throw FailAt(startLine, startColumn, "unterminated string.");
                    }
                    char c = _text[_pos];
                    if (c == '"')
                    {
                        _pos++;
                        return sb.ToString();
                    }
                    if (c < 0x20)
                    {
                        throw Fail("control character " + DescribeChar(c) + " must be escaped inside a string.");
                    }
                    if (c == '\\')
                    {
                        ParseEscape(sb);
                        continue;
                    }
                    if (char.IsSurrogate(c))
                    {
                        // Raw surrogates must form a valid pair.
                        if (char.IsHighSurrogate(c) && _pos + 1 < _text.Length && char.IsLowSurrogate(_text[_pos + 1]))
                        {
                            sb.Append(c).Append(_text[_pos + 1]);
                            _pos += 2;
                            continue;
                        }
                        throw Fail("invalid UTF-16 surrogate in string.");
                    }
                    sb.Append(c);
                    _pos++;
                }
            }

            private void ParseEscape(StringBuilder sb)
            {
                _pos++; // backslash
                if (AtEnd)
                {
                    throw Fail("unterminated escape sequence.");
                }
                char e = _text[_pos];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                    {
                        int escapeColumn = Column - 1;
                        _pos++;
                        char unit = ReadHex4();
                        if (char.IsHighSurrogate(unit))
                        {
                            if (_pos + 1 < _text.Length && _text[_pos] == '\\' && _text[_pos + 1] == 'u')
                            {
                                _pos += 2;
                                char low = ReadHex4();
                                if (!char.IsLowSurrogate(low))
                                {
                                    throw FailAt(_line, escapeColumn, "\\u escape is an unpaired high surrogate.");
                                }
                                sb.Append(unit).Append(low);
                                return;
                            }
                            throw FailAt(_line, escapeColumn, "\\u escape is an unpaired high surrogate.");
                        }
                        if (char.IsLowSurrogate(unit))
                        {
                            throw FailAt(_line, escapeColumn, "\\u escape is an unpaired low surrogate.");
                        }
                        sb.Append(unit);
                        return; // ReadHex4 already advanced past the digits
                    }
                    default:
                        throw Fail("invalid escape sequence \\" + e + ".");
                }
                _pos++;
            }

            private char ReadHex4()
            {
                int value = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (AtEnd)
                    {
                        throw Fail("\\u escape needs 4 hex digits.");
                    }
                    char h = _text[_pos];
                    int digit;
                    if (h >= '0' && h <= '9') digit = h - '0';
                    else if (h >= 'a' && h <= 'f') digit = h - 'a' + 10;
                    else if (h >= 'A' && h <= 'F') digit = h - 'A' + 10;
                    else throw Fail("\\u escape needs 4 hex digits but found " + DescribeChar(h) + ".");
                    value = (value << 4) | digit;
                    _pos++;
                }
                return (char)value;
            }

            private JsonValue ParseNumber()
            {
                int line = _line, column = Column;
                int start = _pos;
                if (_text[_pos] == '-')
                {
                    _pos++;
                }

                // Integer part: "0" or [1-9][0-9]*
                if (AtEnd || !IsDigit(_text[_pos]))
                {
                    throw Fail("expected a digit in number.");
                }
                if (_text[_pos] == '0')
                {
                    _pos++;
                    if (!AtEnd && IsDigit(_text[_pos]))
                    {
                        throw FailAt(line, column, "numbers must not have leading zeros.");
                    }
                }
                else
                {
                    SkipDigits();
                }

                if (!AtEnd && _text[_pos] == '.')
                {
                    _pos++;
                    if (AtEnd || !IsDigit(_text[_pos]))
                    {
                        throw Fail("expected a digit after the decimal point.");
                    }
                    SkipDigits();
                }

                if (!AtEnd && (_text[_pos] == 'e' || _text[_pos] == 'E'))
                {
                    _pos++;
                    if (!AtEnd && (_text[_pos] == '+' || _text[_pos] == '-'))
                    {
                        _pos++;
                    }
                    if (AtEnd || !IsDigit(_text[_pos]))
                    {
                        throw Fail("expected a digit in the exponent.");
                    }
                    SkipDigits();
                }

                if (IsWordChar(_pos) || (!AtEnd && _text[_pos] == '.'))
                {
                    throw Fail("unexpected " + DescribeChar(_text[_pos]) + " in number.");
                }

                return JsonValue.Number(_source, line, column, _text.Substring(start, _pos - start));
            }

            private void SkipDigits()
            {
                while (!AtEnd && IsDigit(_text[_pos]))
                {
                    _pos++;
                }
            }

            private static bool IsDigit(char c) => c >= '0' && c <= '9';

            private static string DescribeChar(char c)
            {
                if (c < 0x20 || c == 0x7F)
                {
                    return "character U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
                }
                return "'" + c + "'";
            }
        }
    }
}
