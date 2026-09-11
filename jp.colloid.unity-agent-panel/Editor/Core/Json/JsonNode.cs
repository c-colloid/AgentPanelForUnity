using System;
using System.Collections.Generic;
using System.Globalization;

namespace Colloid.AgentPanel.Core.Json
{
    /// <summary>Discriminator for <see cref="JsonNode"/>.</summary>
    public enum JsonNodeType
    {
        Object,
        Array,
        String,
        Number,
        Bool,
        Null
    }

    /// <summary>
    /// Lightweight JSON DOM node. Pure C# (no Unity dependencies).
    /// Missing-key/index lookups return the shared immutable Null node so
    /// chained access never throws: node["a"]["b"][3].AsString("fallback").
    /// Also serves as a fluent builder for outbound payloads
    /// (see <see cref="NewObject"/> / <see cref="NewArray"/> / Set / Add).
    /// </summary>
    public sealed class JsonNode
    {
        /// <summary>Shared immutable null node returned for all missing lookups.</summary>
        public static readonly JsonNode Null = new JsonNode(JsonNodeType.Null, immutable: true);

        private readonly JsonNodeType _type;
        private readonly bool _immutable;
        private Dictionary<string, JsonNode> _object;   // Object
        private List<JsonNode> _array;                  // Array
        private string _string;                         // String value, or raw numeric text for Number
        private double _number;                         // Number
        private bool _bool;                             // Bool

        private JsonNode(JsonNodeType type, bool immutable = false)
        {
            _type = type;
            _immutable = immutable;
        }

        public JsonNodeType Type { get { return _type; } }
        public bool IsNull { get { return _type == JsonNodeType.Null; } }
        public bool IsObject { get { return _type == JsonNodeType.Object; } }
        public bool IsArray { get { return _type == JsonNodeType.Array; } }
        public bool IsString { get { return _type == JsonNodeType.String; } }
        public bool IsNumber { get { return _type == JsonNodeType.Number; } }
        public bool IsBool { get { return _type == JsonNodeType.Bool; } }

        // ---------------------------------------------------------------
        // Factories
        // ---------------------------------------------------------------

        public static JsonNode NewObject()
        {
            var n = new JsonNode(JsonNodeType.Object);
            n._object = new Dictionary<string, JsonNode>();
            return n;
        }

        public static JsonNode NewArray()
        {
            var n = new JsonNode(JsonNodeType.Array);
            n._array = new List<JsonNode>();
            return n;
        }

        public static JsonNode Of(string value)
        {
            if (value == null)
            {
                return Null;
            }
            var n = new JsonNode(JsonNodeType.String);
            n._string = value;
            return n;
        }

        public static JsonNode Of(double value)
        {
            var n = new JsonNode(JsonNodeType.Number);
            n._number = value;
            return n;
        }

        public static JsonNode Of(long value)
        {
            var n = new JsonNode(JsonNodeType.Number);
            n._number = value;
            n._string = value.ToString(CultureInfo.InvariantCulture);
            return n;
        }

        public static JsonNode Of(int value)
        {
            return Of((long)value);
        }

        public static JsonNode Of(bool value)
        {
            var n = new JsonNode(JsonNodeType.Bool);
            n._bool = value;
            return n;
        }

        /// <summary>Internal factory used by the parser to preserve the raw numeric text.</summary>
        internal static JsonNode OfNumberRaw(double value, string rawText)
        {
            var n = new JsonNode(JsonNodeType.Number);
            n._number = value;
            n._string = rawText;
            return n;
        }

        // ---------------------------------------------------------------
        // Lookup (never throws, never returns C# null)
        // ---------------------------------------------------------------

        /// <summary>Object member lookup. Returns the Null node when absent or when this is not an object.</summary>
        public JsonNode this[string key]
        {
            get
            {
                if (_type == JsonNodeType.Object && key != null)
                {
                    JsonNode value;
                    if (_object.TryGetValue(key, out value))
                    {
                        return value ?? Null;
                    }
                }
                return Null;
            }
        }

        /// <summary>Array element lookup. Returns the Null node when out of range or when this is not an array.</summary>
        public JsonNode this[int index]
        {
            get
            {
                if (_type == JsonNodeType.Array && index >= 0 && index < _array.Count)
                {
                    return _array[index] ?? Null;
                }
                return Null;
            }
        }

        /// <summary>Element/member count. Zero for scalar nodes.</summary>
        public int Count
        {
            get
            {
                if (_type == JsonNodeType.Array)
                {
                    return _array.Count;
                }
                if (_type == JsonNodeType.Object)
                {
                    return _object.Count;
                }
                return 0;
            }
        }

        public bool HasKey(string key)
        {
            return _type == JsonNodeType.Object && key != null && _object.ContainsKey(key);
        }

        // ---------------------------------------------------------------
        // Scalar accessors with defaults
        // ---------------------------------------------------------------

        public string AsString(string defaultValue = null)
        {
            switch (_type)
            {
                case JsonNodeType.String:
                    return _string;
                case JsonNodeType.Number:
                    return _string ?? _number.ToString("R", CultureInfo.InvariantCulture);
                case JsonNodeType.Bool:
                    return _bool ? "true" : "false";
                default:
                    return defaultValue;
            }
        }

        public bool AsBool(bool defaultValue = false)
        {
            switch (_type)
            {
                case JsonNodeType.Bool:
                    return _bool;
                case JsonNodeType.String:
                    if (_string == "true") { return true; }
                    if (_string == "false") { return false; }
                    return defaultValue;
                case JsonNodeType.Number:
                    return _number != 0.0;
                default:
                    return defaultValue;
            }
        }

        public double AsDouble(double defaultValue = 0.0)
        {
            switch (_type)
            {
                case JsonNodeType.Number:
                    return _number;
                case JsonNodeType.String:
                    double parsed;
                    if (double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    {
                        return parsed;
                    }
                    return defaultValue;
                case JsonNodeType.Bool:
                    return _bool ? 1.0 : 0.0;
                default:
                    return defaultValue;
            }
        }

        public long AsLong(long defaultValue = 0L)
        {
            if (_type == JsonNodeType.Number)
            {
                // Prefer the raw text so 64-bit integers survive intact.
                long parsed;
                if (_string != null && long.TryParse(_string, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
                return (long)_number;
            }
            if (_type == JsonNodeType.String)
            {
                long parsed;
                if (long.TryParse(_string, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }
            return defaultValue;
        }

        public int AsInt(int defaultValue = 0)
        {
            long value = AsLong(long.MinValue);
            if (value == long.MinValue && !(IsNumber || IsString))
            {
                return defaultValue;
            }
            if (value < int.MinValue || value > int.MaxValue)
            {
                return defaultValue;
            }
            return (int)value;
        }

        // ---------------------------------------------------------------
        // Enumeration helpers
        // ---------------------------------------------------------------

        /// <summary>Array items. Empty for non-arrays.</summary>
        public IEnumerable<JsonNode> Items
        {
            get
            {
                if (_type == JsonNodeType.Array)
                {
                    for (int i = 0; i < _array.Count; i++)
                    {
                        yield return _array[i] ?? Null;
                    }
                }
            }
        }

        /// <summary>Object members. Empty for non-objects.</summary>
        public IEnumerable<KeyValuePair<string, JsonNode>> Properties
        {
            get
            {
                if (_type == JsonNodeType.Object)
                {
                    foreach (var pair in _object)
                    {
                        yield return new KeyValuePair<string, JsonNode>(pair.Key, pair.Value ?? Null);
                    }
                }
            }
        }

        /// <summary>Object keys. Empty for non-objects.</summary>
        public IEnumerable<string> Keys
        {
            get
            {
                if (_type == JsonNodeType.Object)
                {
                    foreach (var key in _object.Keys)
                    {
                        yield return key;
                    }
                }
            }
        }

        /// <summary>Copies a string array (e.g. "tools":[...]) skipping non-string entries.</summary>
        public string[] AsStringArray()
        {
            if (_type != JsonNodeType.Array)
            {
                return Array.Empty<string>();
            }
            var result = new List<string>(_array.Count);
            for (int i = 0; i < _array.Count; i++)
            {
                var item = _array[i];
                if (item != null && item.IsString)
                {
                    result.Add(item.AsString());
                }
            }
            return result.ToArray();
        }

        // ---------------------------------------------------------------
        // Fluent builders (used by JsonWriter clients / OutboundMessages)
        // ---------------------------------------------------------------

        /// <summary>Sets an object member. Returns this for chaining.</summary>
        public JsonNode Set(string key, JsonNode value)
        {
            RequireMutable(JsonNodeType.Object);
            if (key == null)
            {
                throw new ArgumentNullException("key");
            }
            _object[key] = value ?? Null;
            return this;
        }

        public JsonNode Set(string key, string value) { return Set(key, Of(value)); }
        public JsonNode Set(string key, double value) { return Set(key, Of(value)); }
        public JsonNode Set(string key, long value) { return Set(key, Of(value)); }
        public JsonNode Set(string key, int value) { return Set(key, Of((long)value)); }
        public JsonNode Set(string key, bool value) { return Set(key, Of(value)); }

        /// <summary>Appends an array element. Returns this for chaining.</summary>
        public JsonNode Add(JsonNode value)
        {
            RequireMutable(JsonNodeType.Array);
            _array.Add(value ?? Null);
            return this;
        }

        public JsonNode Add(string value) { return Add(Of(value)); }
        public JsonNode Add(double value) { return Add(Of(value)); }
        public JsonNode Add(long value) { return Add(Of(value)); }
        public JsonNode Add(bool value) { return Add(Of(value)); }

        private void RequireMutable(JsonNodeType expected)
        {
            if (_immutable)
            {
                throw new InvalidOperationException("The shared Null node is immutable.");
            }
            if (_type != expected)
            {
                throw new InvalidOperationException(
                    "Node of type " + _type + " does not support " + expected + " mutation.");
            }
        }

        /// <summary>Raw numeric text captured by the parser (may be null when built in code).</summary>
        internal string RawNumberText { get { return _type == JsonNodeType.Number ? _string : null; } }
        internal double NumberValue { get { return _number; } }
        internal bool BoolValue { get { return _bool; } }
        internal string StringValue { get { return _string; } }
    }
}
