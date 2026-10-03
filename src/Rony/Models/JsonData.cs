using Rony.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace Rony.Models
{
    /// <summary>The kind of a <see cref="JsonData"/>.</summary>
    public enum JsonDataKind
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
    /// in a response function. Indexing never throws: a missing property or element is <see cref="JsonDataKind.Undefined"/>,
    /// so <c>json["user"]["roles"][0].AsString()</c> is null when any part is missing.
    /// </summary>
    public sealed class JsonData
    {
        private static readonly IReadOnlyList<JsonData> NoItems = new JsonData[0];
        private static readonly IReadOnlyDictionary<string, JsonData> NoProperties = new ReadOnlyDictionary<string, JsonData>(new Dictionary<string, JsonData>());

        /// <summary>The value of a missing property or element.</summary>
        internal static readonly JsonData Missing = new JsonData(JsonDataKind.Undefined, null, null);

        private readonly object _value;
        private readonly string _text;
        private readonly IReadOnlyList<JsonData> _items;
        private readonly IReadOnlyDictionary<string, JsonData> _properties;

        private JsonData(JsonDataKind kind, object value, string text)
        {
            Kind = kind;
            _value = value;
            _text = text;
            if (value is List<JsonData> items) _items = new ReadOnlyCollection<JsonData>(items);
            else if (value is Dictionary<string, JsonData> properties) _properties = new ReadOnlyDictionary<string, JsonData>(properties);
        }

        internal static JsonData Null() => new JsonData(JsonDataKind.Null, null, "null");

        internal static JsonData FromBoolean(bool value) => new JsonData(JsonDataKind.Boolean, value, value ? "true" : "false");

        /// <param name="number">The parsed number.</param>
        /// <param name="text">The number as written in the document.</param>
        internal static JsonData FromNumber(double number, string text) => new JsonData(JsonDataKind.Number, number, text);

        internal static JsonData FromString(string value) => new JsonData(JsonDataKind.String, value, null);

        internal static JsonData FromArray(List<JsonData> items) => new JsonData(JsonDataKind.Array, items, null);

        internal static JsonData FromObject(Dictionary<string, JsonData> properties) => new JsonData(JsonDataKind.Object, properties, null);

        /// <summary>Parses JSON text (RFC 8259).</summary>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
        /// <exception cref="FormatException">The text is not valid JSON; the message gives the position.</exception>
        public static JsonData Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            return JsonParser.Parse(json);
        }

        /// <summary>Parses JSON text without throwing. <paramref name="value"/> is <see cref="JsonDataKind.Undefined"/> when it fails.</summary>
        /// <returns>True if <paramref name="json"/> is valid JSON.</returns>
        public static bool TryParse(string json, out JsonData value)
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
        public JsonDataKind Kind { get; }

        /// <summary>False for <see cref="JsonDataKind.Undefined"/>, true for every value that exists in the document (including <c>null</c>).</summary>
        public bool Exists => Kind != JsonDataKind.Undefined;

        /// <summary>The property of an object (case-sensitive; the last one wins for duplicate names), or Undefined if this is not an object or has no such property.</summary>
        public JsonData this[string name] =>
            name != null && Kind == JsonDataKind.Object && ((Dictionary<string, JsonData>)_value).TryGetValue(name, out var property) ? property : Missing;

        /// <summary>The element of an array, or Undefined if this is not an array or the index is out of range.</summary>
        public JsonData this[int index] =>
            Kind == JsonDataKind.Array && index >= 0 && index < ((List<JsonData>)_value).Count ? ((List<JsonData>)_value)[index] : Missing;

        /// <summary>The number of elements of an array or properties of an object; otherwise 0.</summary>
        public int Count => Kind == JsonDataKind.Array ? ((List<JsonData>)_value).Count : Kind == JsonDataKind.Object ? ((Dictionary<string, JsonData>)_value).Count : 0;

        /// <summary>The elements of an array; empty for other kinds.</summary>
        public IReadOnlyList<JsonData> Items => _items ?? NoItems;

        /// <summary>The properties of an object; empty for other kinds.</summary>
        public IReadOnlyDictionary<string, JsonData> Properties => _properties ?? NoProperties;

        /// <summary>The string, or null if this is not a string.</summary>
        public string AsString() => Kind == JsonDataKind.String ? (string)_value : null;

        /// <summary>
        /// The number, or null if this is not a number. A number outside the range of <see cref="double"/> is ±Infinity on .NET Core
        /// and .NET 5+; <see cref="ToString"/> keeps the number as written.
        /// </summary>
        public double? AsNumber() => Kind == JsonDataKind.Number ? (double)_value : (double?)null;

        /// <summary>The boolean, or null if this is not a boolean.</summary>
        public bool? AsBoolean() => Kind == JsonDataKind.Boolean ? (bool)_value : (bool?)null;

        /// <summary>The compact JSON text of the value, or <c>undefined</c> for <see cref="JsonDataKind.Undefined"/>.</summary>
        public override string ToString()
        {
            switch (Kind)
            {
                case JsonDataKind.Undefined:
                    return "undefined";
                case JsonDataKind.String:
                    return JsonParser.Quote((string)_value);
                case JsonDataKind.Array:
                case JsonDataKind.Object:
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
                case JsonDataKind.Array:
                    builder.Append('[');
                    var first = true;
                    foreach (var item in (List<JsonData>)_value)
                    {
                        if (!first) builder.Append(',');
                        first = false;
                        item.Write(builder);
                    }
                    builder.Append(']');
                    break;
                case JsonDataKind.Object:
                    builder.Append('{');
                    first = true;
                    foreach (var property in (Dictionary<string, JsonData>)_value)
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
