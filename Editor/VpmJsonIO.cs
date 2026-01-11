using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;

namespace hackebein.vpm.packager.editor
{
    internal static class VpmJsonIO
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        internal static object Deserialize(string json)
        {
            return Json.Deserialize(json);
        }

        internal static string Serialize(object obj, bool pretty)
        {
            return Json.Serialize(obj, pretty);
        }

        internal static bool TryReadJsonObjectAtAssetPath(string assetPath, out Dictionary<string, object> root, out string error)
        {
            root = null;
            error = null;

            try
            {
                var fullPath = ToFullPath(assetPath);
                var json = File.ReadAllText(fullPath, Utf8NoBom);
                var parsed = Json.Deserialize(json);
                root = parsed as Dictionary<string, object>;
                if (root == null)
                {
                    error = "Root JSON value is not an object.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool TryWriteJsonObjectToAssetPath(string assetPath, Dictionary<string, object> root, out string error)
        {
            error = null;

            try
            {
                var fullPath = ToFullPath(assetPath);
                var json = Json.Serialize(root, pretty: true);
                if (!json.EndsWith("\n"))
                    json += "\n";
                File.WriteAllText(fullPath, json, Utf8NoBom);

                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool TryReadJsonObjectAtPath(string path, out Dictionary<string, object> root, out string error)
        {
            root = null;
            error = null;
            try
            {
                var fullPath = Path.GetFullPath(path);
                var json = File.ReadAllText(fullPath, Utf8NoBom);
                var parsed = Json.Deserialize(json);
                root = parsed as Dictionary<string, object>;
                if (root == null)
                {
                    error = "Root JSON value is not an object.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool TryWriteJsonObjectToPath(string path, Dictionary<string, object> root, out string error)
        {
            error = null;
            try
            {
                var fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                var json = Json.Serialize(root, pretty: true);
                if (!json.EndsWith("\n"))
                    json += "\n";
                File.WriteAllText(fullPath, json, Utf8NoBom);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string ToFullPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                throw new ArgumentException("assetPath is empty.");

            // Unity's current working directory is typically the project root, so this works for both Assets/* and Packages/*.
            return Path.GetFullPath(assetPath);
        }

        /// <summary>
        /// Minimal JSON parser/serializer (supports objects, arrays, strings, numbers, booleans, null).
        /// </summary>
        private static class Json
        {
            public static object Deserialize(string json)
            {
                if (json == null) return null;
                using (var parser = new Parser(json))
                {
                    return parser.ParseValue();
                }
            }

            public static string Serialize(object obj, bool pretty)
            {
                var sb = new StringBuilder(1024);
                var serializer = new Serializer(sb, pretty);
                serializer.SerializeValue(obj, indentLevel: 0);
                return sb.ToString();
            }

            private sealed class Parser : IDisposable
            {
                private readonly string _json;
                private int _index;

                public Parser(string json)
                {
                    _json = json ?? "";
                    _index = 0;
                }

                public void Dispose() { }

                public object ParseValue()
                {
                    EatWhitespace();
                    if (_index >= _json.Length) return null;

                    var c = _json[_index];
                    switch (c)
                    {
                        case '{':
                            return ParseObject();
                        case '[':
                            return ParseArray();
                        case '"':
                            return ParseString();
                        case 't':
                            return ConsumeLiteral("true", true);
                        case 'f':
                            return ConsumeLiteral("false", false);
                        case 'n':
                            return ConsumeLiteral("null", null);
                        default:
                            if (c == '-' || char.IsDigit(c))
                                return ParseNumber();
                            throw new FormatException($"Unexpected character '{c}' at index {_index}.");
                    }
                }

                private Dictionary<string, object> ParseObject()
                {
                    // '{'
                    _index++;
                    EatWhitespace();

                    var dict = new Dictionary<string, object>();
                    if (TryConsume('}'))
                        return dict;

                    while (true)
                    {
                        EatWhitespace();
                        var key = ParseString();
                        EatWhitespace();
                        Consume(':');

                        var value = ParseValue();
                        dict[key] = value;

                        EatWhitespace();
                        if (TryConsume('}'))
                            break;
                        Consume(',');
                    }

                    return dict;
                }

                private List<object> ParseArray()
                {
                    // '['
                    _index++;
                    EatWhitespace();

                    var list = new List<object>();
                    if (TryConsume(']'))
                        return list;

                    while (true)
                    {
                        var value = ParseValue();
                        list.Add(value);

                        EatWhitespace();
                        if (TryConsume(']'))
                            break;
                        Consume(',');
                    }

                    return list;
                }

                private string ParseString()
                {
                    Consume('"');
                    var sb = new StringBuilder();

                    while (_index < _json.Length)
                    {
                        var c = _json[_index++];
                        if (c == '"')
                            return sb.ToString();

                        if (c == '\\')
                        {
                            if (_index >= _json.Length)
                                throw new FormatException("Unexpected end of string escape.");

                            var esc = _json[_index++];
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
                                    if (_index + 4 > _json.Length)
                                        throw new FormatException("Invalid unicode escape sequence.");

                                    var hex = _json.Substring(_index, 4);
                                    _index += 4;
                                    if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                        throw new FormatException("Invalid unicode escape sequence.");
                                    sb.Append((char)code);
                                    break;
                                default:
                                    throw new FormatException($"Invalid escape character '\\{esc}'.");
                            }
                        }
                        else
                        {
                            sb.Append(c);
                        }
                    }

                    throw new FormatException("Unterminated string.");
                }

                private object ParseNumber()
                {
                    var start = _index;
                    if (_json[_index] == '-') _index++;
                    while (_index < _json.Length && char.IsDigit(_json[_index])) _index++;

                    if (_index < _json.Length && _json[_index] == '.')
                    {
                        _index++;
                        while (_index < _json.Length && char.IsDigit(_json[_index])) _index++;
                    }

                    if (_index < _json.Length && (_json[_index] == 'e' || _json[_index] == 'E'))
                    {
                        _index++;
                        if (_index < _json.Length && (_json[_index] == '+' || _json[_index] == '-')) _index++;
                        while (_index < _json.Length && char.IsDigit(_json[_index])) _index++;
                    }

                    var slice = _json.Substring(start, _index - start);
                    if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        throw new FormatException($"Invalid number '{slice}'.");
                    return value;
                }

                private object ConsumeLiteral(string literal, object value)
                {
                    if (_index + literal.Length > _json.Length)
                        throw new FormatException("Unexpected end of JSON.");

                    for (var i = 0; i < literal.Length; i++)
                    {
                        if (_json[_index + i] != literal[i])
                            throw new FormatException($"Unexpected token at index {_index}.");
                    }

                    _index += literal.Length;
                    return value;
                }

                private void EatWhitespace()
                {
                    while (_index < _json.Length)
                    {
                        var c = _json[_index];
                        if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                        {
                            _index++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                private void Consume(char expected)
                {
                    if (_index >= _json.Length)
                        throw new FormatException("Unexpected end of JSON.");
                    var c = _json[_index];
                    if (c != expected)
                        throw new FormatException($"Expected '{expected}' but found '{c}' at index {_index}.");
                    _index++;
                }

                private bool TryConsume(char expected)
                {
                    if (_index >= _json.Length) return false;
                    if (_json[_index] != expected) return false;
                    _index++;
                    return true;
                }
            }

            private sealed class Serializer
            {
                private readonly StringBuilder _sb;
                private readonly bool _pretty;
                private const int IndentSize = 2;

                public Serializer(StringBuilder sb, bool pretty)
                {
                    _sb = sb;
                    _pretty = pretty;
                }

                public void SerializeValue(object obj, int indentLevel)
                {
                    if (obj == null)
                    {
                        _sb.Append("null");
                        return;
                    }

                    if (obj is string s)
                    {
                        SerializeString(s);
                        return;
                    }

                    if (obj is bool b)
                    {
                        _sb.Append(b ? "true" : "false");
                        return;
                    }

                    if (obj is double d)
                    {
                        _sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                        return;
                    }

                    if (obj is float f)
                    {
                        _sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                        return;
                    }

                    if (obj is int || obj is long || obj is short || obj is byte ||
                        obj is uint || obj is ulong || obj is ushort || obj is sbyte)
                    {
                        _sb.Append(Convert.ToString(obj, CultureInfo.InvariantCulture));
                        return;
                    }

                    if (obj is IDictionary dict)
                    {
                        SerializeObject(dict, indentLevel);
                        return;
                    }

                    if (obj is IList list)
                    {
                        SerializeArray(list, indentLevel);
                        return;
                    }

                    // Fallback: string
                    SerializeString(obj.ToString());
                }

                private void SerializeObject(IDictionary dict, int indentLevel)
                {
                    _sb.Append('{');
                    var first = true;

                    foreach (DictionaryEntry entry in dict)
                    {
                        if (!(entry.Key is string key))
                            continue;

                        if (!first) _sb.Append(',');
                        NewLineAndIndent(indentLevel + 1);

                        SerializeString(key);
                        _sb.Append(_pretty ? ": " : ":");
                        SerializeValue(entry.Value, indentLevel + 1);

                        first = false;
                    }

                    if (!first)
                        NewLineAndIndent(indentLevel);
                    _sb.Append('}');
                }

                private void SerializeArray(IList list, int indentLevel)
                {
                    _sb.Append('[');
                    var first = true;

                    for (var i = 0; i < list.Count; i++)
                    {
                        if (!first) _sb.Append(',');
                        NewLineAndIndent(indentLevel + 1);
                        SerializeValue(list[i], indentLevel + 1);
                        first = false;
                    }

                    if (!first)
                        NewLineAndIndent(indentLevel);
                    _sb.Append(']');
                }

                private void SerializeString(string s)
                {
                    _sb.Append('"');
                    for (var i = 0; i < s.Length; i++)
                    {
                        var c = s[i];
                        switch (c)
                        {
                            case '"': _sb.Append("\\\""); break;
                            case '\\': _sb.Append("\\\\"); break;
                            case '\b': _sb.Append("\\b"); break;
                            case '\f': _sb.Append("\\f"); break;
                            case '\n': _sb.Append("\\n"); break;
                            case '\r': _sb.Append("\\r"); break;
                            case '\t': _sb.Append("\\t"); break;
                            default:
                                if (c < 32)
                                {
                                    _sb.Append("\\u");
                                    _sb.Append(((int)c).ToString("x4"));
                                }
                                else
                                {
                                    _sb.Append(c);
                                }
                                break;
                        }
                    }
                    _sb.Append('"');
                }

                private void NewLineAndIndent(int indentLevel)
                {
                    if (!_pretty) return;
                    _sb.Append('\n');
                    _sb.Append(' ', indentLevel * IndentSize);
                }
            }
        }
    }
}

