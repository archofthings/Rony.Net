using Rony.Helpers;
using System;
using System.Collections.Generic;
using System.Text;

namespace Rony.Models
{
    /// <summary>The kind of a <see cref="JsonValue"/>.</summary>
    public enum JsonKind
    {
        /// <summary>No value: a missing property or array element, or text that is not valid JSON.</summary>
        Undefined,

        /// <summary>The JSON <c>null</c>.</summary>
        Null,

        /// <summary>The JSON <c>true</c> or <c>false</c>.</summary>
        Boolean,

        /// <summary>A JSON number.</summary>
        Number,

        /// <summary>A JSON string.</summary>
        String,

        /// <summary>A JSON array.</summary>
        Array,

        /// <summary>A JSON object.</summary>
        Object
    }

    /// <summary>
    /// A small, immutable, read-only JSON value, for matching requests with <c>SendJson(...)</c> or inspecting them
    /// in a response function. Indexing never throws: a missing property or element is <see cref="JsonKind.Undefined"/>,
    /// so <c>json["user"]["roles"][0].AsString()</c> is null when any part is missing.
    /// </summary>
    public sealed class JsonValue
    {
        private static readonly IReadOnlyList<JsonValue> NoItems = new JsonValue[0];
        private static readonly IReadOnlyDictionary<string, JsonValue> NoProperties = new Dictionary<string, JsonValue>();

        /// <summary>The value of a missing property or element.</summary>
        internal static readonly JsonValue Missing = new JsonValue(JsonKind.Undefined, null, null);

        private readonly object _value;
        private readonly string _text;

        private JsonValue(JsonKind kind, object value, string text)
        {
            Kind = kind;
            _value = value;
            _text = text;
        }

        internal static JsonValue Null() => new JsonValue(JsonKind.Null, null, "null");

        internal static JsonValue FromBoolean(bool value) => new JsonValue(JsonKind.Boolean, value, value ? "true" : "false");

        /// <param name="number">The parsed number.</param>
        /// <param name="text">The number as written in the document.</param>
        internal static JsonValue FromNumber(double number, string text) => new JsonValue(JsonKind.Number, number, text);

        internal static JsonValue FromString(string value) => new JsonValue(JsonKind.String, value, null);

        internal static JsonValue FromArray(List<JsonValue> items) => new JsonValue(JsonKind.Array, items, null);

        internal static JsonValue FromObject(Dictionary<string, JsonValue> properties) => new JsonValue(JsonKind.Object, properties, null);

        /// <summary>Parses JSON text (RFC 8259).</summary>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
        /// <exception cref="FormatException">The text is not valid JSON; the message gives the position.</exception>
        public static JsonValue Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            return JsonParser.Parse(json);
        }

        /// <summary>Parses JSON text without throwing. <paramref name="value"/> is <see cref="JsonKind.Undefined"/> when it fails.</summary>
        /// <returns>True if <paramref name="json"/> is valid JSON.</returns>
        public static bool TryParse(string json, out JsonValue value)
        {
            value = Missing;
            if (json == null) return false;
            try
            {
                value = JsonParser.Parse(json);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>What kind of value this is.</summary>
        public JsonKind Kind { get; }

        /// <summary>False for <see cref="JsonKind.Undefined"/>, true for every value that exists in the document (including <c>null</c>).</summary>
        public bool Exists => Kind != JsonKind.Undefined;

        /// <summary>The property of an object (case-sensitive; the last one wins for duplicate names), or Undefined if this is not an object or has no such property.</summary>
        public JsonValue this[string name] =>
            name != null && Kind == JsonKind.Object && ((Dictionary<string, JsonValue>)_value).TryGetValue(name, out var property) ? property : Missing;

        /// <summary>The element of an array, or Undefined if this is not an array or the index is out of range.</summary>
        public JsonValue this[int index] =>
            Kind == JsonKind.Array && index >= 0 && index < ((List<JsonValue>)_value).Count ? ((List<JsonValue>)_value)[index] : Missing;

        /// <summary>The number of elements of an array or properties of an object; otherwise 0.</summary>
        public int Count => Kind == JsonKind.Array ? ((List<JsonValue>)_value).Count : Kind == JsonKind.Object ? ((Dictionary<string, JsonValue>)_value).Count : 0;

        /// <summary>The elements of an array; empty for other kinds.</summary>
        public IReadOnlyList<JsonValue> Items => Kind == JsonKind.Array ? (List<JsonValue>)_value : NoItems;

        /// <summary>The properties of an object; empty for other kinds.</summary>
        public IReadOnlyDictionary<string, JsonValue> Properties => Kind == JsonKind.Object ? (Dictionary<string, JsonValue>)_value : NoProperties;

        /// <summary>The string, or null if this is not a string.</summary>
        public string AsString() => Kind == JsonKind.String ? (string)_value : null;

        /// <summary>The number, or null if this is not a number.</summary>
        public double? AsNumber() => Kind == JsonKind.Number ? (double)_value : (double?)null;

        /// <summary>The boolean, or null if this is not a boolean.</summary>
        public bool? AsBoolean() => Kind == JsonKind.Boolean ? (bool)_value : (bool?)null;

        /// <summary>The compact JSON text of the value, or <c>undefined</c> for <see cref="JsonKind.Undefined"/>.</summary>
        public override string ToString()
        {
            switch (Kind)
            {
                case JsonKind.Undefined:
                    return "undefined";
                case JsonKind.String:
                    return JsonParser.Quote((string)_value);
                case JsonKind.Array:
                case JsonKind.Object:
                    var builder = new StringBuilder();
                    Write(builder);
                    return builder.ToString();
                default:
                    return _text;
            }
        }

        private void Write(StringBuilder builder)
        {
            switch (Kind)
            {
                case JsonKind.Array:
                    builder.Append('[');
                    var first = true;
                    foreach (var item in (List<JsonValue>)_value)
                    {
                        if (!first) builder.Append(',');
                        first = false;
                        item.Write(builder);
                    }
                    builder.Append(']');
                    break;
                case JsonKind.Object:
                    builder.Append('{');
                    first = true;
                    foreach (var property in (Dictionary<string, JsonValue>)_value)
                    {
                        if (!first) builder.Append(',');
                        first = false;
                        builder.Append(JsonParser.Quote(property.Key)).Append(':');
                        property.Value.Write(builder);
                    }
                    builder.Append('}');
                    break;
                default:
                    builder.Append(ToString());
                    break;
            }
        }
    }
}
