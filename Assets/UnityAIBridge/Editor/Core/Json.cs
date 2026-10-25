using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityAIBridge
{
    /// <summary>Raised for expected, user-facing bridge failures.</summary>
    public class BridgeException : Exception
    {
        public BridgeException(string message) : base(message) { }
        public BridgeException(string message, Exception inner) : base(message, inner) { }
    }

    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// Small dependency-free JSON DOM. Unity's JsonUtility cannot represent dynamic
    /// JSON-RPC payloads, and the project does not ship a JSON package, so the bridge
    /// carries its own strict parser/serializer.
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind = JsonKind.Null;
        public bool BoolValue;
        public double NumberValue;
        public string StringValue;
        public List<JsonValue> ArrayValue;
        public Dictionary<string, JsonValue> ObjectValue;

        public static JsonValue Null() { return new JsonValue(); }
        public static JsonValue NewObject() { return new JsonValue { Kind = JsonKind.Object, ObjectValue = new Dictionary<string, JsonValue>(StringComparer.Ordinal) }; }
        public static JsonValue NewArray() { return new JsonValue { Kind = JsonKind.Array, ArrayValue = new List<JsonValue>() }; }
        public static JsonValue Of(string value) { return new JsonValue { Kind = JsonKind.String, StringValue = value ?? string.Empty }; }
        public static JsonValue Of(double value) { return new JsonValue { Kind = JsonKind.Number, NumberValue = value }; }
        public static JsonValue Of(int value) { return new JsonValue { Kind = JsonKind.Number, NumberValue = value }; }
        public static JsonValue Of(bool value) { return new JsonValue { Kind = JsonKind.Bool, BoolValue = value }; }

        public static implicit operator JsonValue(string value) { return Of(value); }
        public static implicit operator JsonValue(bool value) { return Of(value); }
        public static implicit operator JsonValue(int value) { return Of(value); }
        public static implicit operator JsonValue(long value) { return Of((double)value); }
        public static implicit operator JsonValue(float value) { return Of((double)value); }
        public static implicit operator JsonValue(double value) { return Of(value); }

        public bool IsNull { get { return Kind == JsonKind.Null; } }
        public bool IsObject { get { return Kind == JsonKind.Object; } }
        public bool IsArray { get { return Kind == JsonKind.Array; } }
        public bool IsString { get { return Kind == JsonKind.String; } }
        public bool IsNumber { get { return Kind == JsonKind.Number; } }
        public bool IsBool { get { return Kind == JsonKind.Bool; } }

        public int Count
        {
            get
            {
                if (Kind == JsonKind.Array) return ArrayValue.Count;
                if (Kind == JsonKind.Object) return ObjectValue.Count;
                return 0;
            }
        }

        public JsonValue this[string key]
        {
            get
            {
                JsonValue found;
                if (Kind == JsonKind.Object && key != null && ObjectValue.TryGetValue(key, out found) && found != null)
                    return found;
                return Null();
            }
            set
            {
                if (Kind != JsonKind.Object)
                {
                    Kind = JsonKind.Object;
                    ObjectValue = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                }
                ObjectValue[key] = value ?? Null();
            }
        }

        public JsonValue this[int index]
        {
            get
            {
                if (Kind == JsonKind.Array && index >= 0 && index < ArrayValue.Count) return ArrayValue[index];
                return Null();
            }
        }

        public IEnumerable<KeyValuePair<string, JsonValue>> Properties
        {
            get
            {
                if (Kind != JsonKind.Object) return new KeyValuePair<string, JsonValue>[0];
                return ObjectValue;
            }
        }

        public JsonValue Add(JsonValue value)
        {
            if (Kind != JsonKind.Array)
            {
                Kind = JsonKind.Array;
                ArrayValue = new List<JsonValue>();
            }
            ArrayValue.Add(value ?? Null());
            return this;
        }

        public JsonValue Set(string key, JsonValue value) { this[key] = value; return this; }
        public JsonValue Set(string key, string value) { this[key] = Of(value); return this; }
        public JsonValue Set(string key, double value) { this[key] = Of(value); return this; }
        public JsonValue Set(string key, bool value) { this[key] = Of(value); return this; }

        public string AsString(string fallback = null)
        {
            switch (Kind)
            {
                case JsonKind.String: return StringValue;
                case JsonKind.Number: return NumberValue.ToString("R", CultureInfo.InvariantCulture);
                case JsonKind.Bool: return BoolValue ? "true" : "false";
                default: return fallback;
            }
        }

        public bool AsBool(bool fallback = false)
        {
            if (Kind == JsonKind.Bool) return BoolValue;
            if (Kind == JsonKind.Number) return Math.Abs(NumberValue) > double.Epsilon;
            if (Kind == JsonKind.String)
            {
                var s = StringValue;
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return fallback;
        }

        public double AsNumber(double fallback = 0)
        {
            if (Kind == JsonKind.Number) return NumberValue;
            if (Kind == JsonKind.Bool) return BoolValue ? 1 : 0;
            if (Kind == JsonKind.String)
            {
                double parsed;
                if (double.TryParse(StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            }
            return fallback;
        }

        public int AsInt(int fallback = 0)
        {
            var d = AsNumber(fallback);
            if (double.IsNaN(d) || double.IsInfinity(d)) return fallback;
            return (int)Math.Round(d);
        }

        public string ToJson(bool indented = false)
        {
            var sb = new StringBuilder(256);
            Write(sb, indented, 0);
            return sb.ToString();
        }

        public override string ToString() { return ToJson(false); }

        void Write(StringBuilder sb, bool indented, int depth)
        {
            switch (Kind)
            {
                case JsonKind.Null: sb.Append("null"); break;
                case JsonKind.Bool: sb.Append(BoolValue ? "true" : "false"); break;
                case JsonKind.Number:
                    if (double.IsNaN(NumberValue) || double.IsInfinity(NumberValue)) sb.Append("null");
                    else if (Math.Abs(NumberValue) < 9.007199254740992E15 && Math.Abs(NumberValue - Math.Round(NumberValue)) < double.Epsilon)
                        sb.Append(((long)Math.Round(NumberValue)).ToString(CultureInfo.InvariantCulture));
                    else sb.Append(NumberValue.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonKind.String: WriteString(sb, StringValue); break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < ArrayValue.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (indented) { sb.Append('\n'); Indent(sb, depth + 1); }
                        ArrayValue[i].Write(sb, indented, depth + 1);
                    }
                    if (indented && ArrayValue.Count > 0) { sb.Append('\n'); Indent(sb, depth); }
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in ObjectValue)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        if (indented) { sb.Append('\n'); Indent(sb, depth + 1); }
                        WriteString(sb, kv.Key);
                        sb.Append(indented ? ": " : ":");
                        kv.Value.Write(sb, indented, depth + 1);
                    }
                    if (indented && ObjectValue.Count > 0) { sb.Append('\n'); Indent(sb, depth); }
                    sb.Append('}');
                    break;
            }
        }

        static void Indent(StringBuilder sb, int depth) { sb.Append(' ', depth * 2); }

        static void WriteString(StringBuilder sb, string value)
        {
            if (value == null) { sb.Append("null"); return; }
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == 0x7F)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static JsonValue Parse(string text)
        {
            if (text == null) throw new BridgeException("JSON input was null.");
            var parser = new Parser(text);
            parser.SkipWhitespace();
            var value = parser.ParseValue(0);
            parser.SkipWhitespace();
            if (!parser.AtEnd) throw new BridgeException("Unexpected trailing characters in JSON at position " + parser.Position + ".");
            return value;
        }

        public static bool TryParse(string text, out JsonValue value, out string error)
        {
            try { value = Parse(text); error = null; return true; }
            catch (Exception e) { value = Null(); error = e.Message; return false; }
        }

        /// <summary>Builds an object from alternating key/value arguments.</summary>
        public static JsonValue Obj(params object[] pairs)
        {
            if (pairs == null || pairs.Length % 2 != 0) throw new BridgeException("JsonValue.Obj requires key/value pairs.");
            var result = NewObject();
            for (int i = 0; i < pairs.Length; i += 2)
                result[pairs[i] as string] = Convert(pairs[i + 1]);
            return result;
        }

        public static JsonValue Arr(params object[] items)
        {
            var result = NewArray();
            if (items == null) return result;
            foreach (var item in items) result.Add(Convert(item));
            return result;
        }

        static JsonValue Convert(object value)
        {
            if (value == null) return Null();
            var json = value as JsonValue;
            if (json != null) return json;
            if (value is string) return Of((string)value);
            if (value is bool) return Of((bool)value);
            if (value is int) return Of((int)value);
            if (value is long) return Of((double)(long)value);
            if (value is float) return Of((double)(float)value);
            if (value is double) return Of((double)value);
            if (value is decimal) return Of((double)(decimal)value);
            var map = value as IDictionary<string, JsonValue>;
            if (map != null)
            {
                var o = NewObject();
                foreach (var kv in map) o[kv.Key] = kv.Value;
                return o;
            }
            var list = value as IEnumerable;
            if (list != null)
            {
                var a = NewArray();
                foreach (var item in list) a.Add(Convert(item));
                return a;
            }
            return Of(value.ToString());
        }

        sealed class Parser
        {
            readonly string _text;
            int _index;

            public Parser(string text) { _text = text; }
            public int Position { get { return _index; } }
            public bool AtEnd { get { return _index >= _text.Length; } }

            public void SkipWhitespace()
            {
                while (_index < _text.Length)
                {
                    char c = _text[_index];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { _index++; continue; }
                    break;
                }
            }

            public JsonValue ParseValue(int depth)
            {
                if (depth > 200) throw new BridgeException("JSON nesting is too deep.");
                SkipWhitespace();
                if (AtEnd) throw new BridgeException("Unexpected end of JSON.");
                char c = _text[_index];
                switch (c)
                {
                    case '{': return ParseObject(depth);
                    case '[': return ParseArray(depth);
                    case '"': return Of(ParseString());
                    case 't': Expect("true"); return Of(true);
                    case 'f': Expect("false"); return Of(false);
                    case 'n': Expect("null"); return Null();
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                        throw new BridgeException("Unexpected character '" + c + "' in JSON at position " + _index + ".");
                }
            }

            JsonValue ParseObject(int depth)
            {
                _index++;
                var result = NewObject();
                SkipWhitespace();
                if (!AtEnd && _text[_index] == '}') { _index++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _text[_index] != '"') throw new BridgeException("Expected a JSON object key at position " + _index + ".");
                    string key = ParseString();
                    SkipWhitespace();
                    if (AtEnd || _text[_index] != ':') throw new BridgeException("Expected ':' after JSON key at position " + _index + ".");
                    _index++;
                    result[key] = ParseValue(depth + 1);
                    SkipWhitespace();
                    if (AtEnd) throw new BridgeException("Unterminated JSON object.");
                    if (_text[_index] == ',') { _index++; continue; }
                    if (_text[_index] == '}') { _index++; return result; }
                    throw new BridgeException("Expected ',' or '}' in JSON object at position " + _index + ".");
                }
            }

            JsonValue ParseArray(int depth)
            {
                _index++;
                var result = NewArray();
                SkipWhitespace();
                if (!AtEnd && _text[_index] == ']') { _index++; return result; }
                while (true)
                {
                    result.Add(ParseValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd) throw new BridgeException("Unterminated JSON array.");
                    if (_text[_index] == ',') { _index++; continue; }
                    if (_text[_index] == ']') { _index++; return result; }
                    throw new BridgeException("Expected ',' or ']' in JSON array at position " + _index + ".");
                }
            }

            string ParseString()
            {
                _index++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw new BridgeException("Unterminated JSON string.");
                    char c = _text[_index++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw new BridgeException("Unterminated JSON escape.");
                    char esc = _text[_index++];
                    switch (esc)
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
                            if (_index + 4 > _text.Length) throw new BridgeException("Truncated \\u escape in JSON.");
                            int code;
                            if (!int.TryParse(_text.Substring(_index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                throw new BridgeException("Invalid \\u escape in JSON.");
                            sb.Append((char)code);
                            _index += 4;
                            break;
                        default: throw new BridgeException("Invalid JSON escape '\\" + esc + "'.");
                    }
                }
            }

            JsonValue ParseNumber()
            {
                int start = _index;
                if (_text[_index] == '-') _index++;
                while (!AtEnd && _text[_index] >= '0' && _text[_index] <= '9') _index++;
                if (!AtEnd && _text[_index] == '.')
                {
                    _index++;
                    while (!AtEnd && _text[_index] >= '0' && _text[_index] <= '9') _index++;
                }
                if (!AtEnd && (_text[_index] == 'e' || _text[_index] == 'E'))
                {
                    _index++;
                    if (!AtEnd && (_text[_index] == '+' || _text[_index] == '-')) _index++;
                    while (!AtEnd && _text[_index] >= '0' && _text[_index] <= '9') _index++;
                }
                double number;
                if (!double.TryParse(_text.Substring(start, _index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    throw new BridgeException("Invalid JSON number at position " + start + ".");
                return Of(number);
            }

            void Expect(string literal)
            {
                if (_index + literal.Length > _text.Length || string.CompareOrdinal(_text, _index, literal, 0, literal.Length) != 0)
                    throw new BridgeException("Invalid JSON literal at position " + _index + ".");
                _index += literal.Length;
            }
        }
    }
}
