using Rony.Models;
using System;
using Xunit;

namespace Rony.Tests.Models
{
    public class JsonValueTests
    {
        [Theory]
        [InlineData("null", JsonKind.Null)]
        [InlineData(" true ", JsonKind.Boolean)]
        [InlineData("-12.5e+2", JsonKind.Number)]
        [InlineData("\"a\\n\\u00e9\\ud83d\\ude00\\\"\"", JsonKind.String)]
        [InlineData("[1, [2], {}]", JsonKind.Array)]
        [InlineData("{\"a\" : {\"b\":[true]}}", JsonKind.Object)]
        public void Parse_Should_Read_Every_Kind(string json, JsonKind kind)
        {
            var value = JsonValue.Parse(json);

            Assert.Equal(kind, value.Kind);
            Assert.True(value.Exists);
            Assert.True(JsonValue.TryParse(json, out var tried));
            Assert.Equal(kind, tried.Kind);
        }

        [Fact]
        public void Values_Should_Be_Readable()
        {
            var json = JsonValue.Parse("{\"s\":\"a\\n\\u00e9\\ud83d\\ude00\",\"n\":-12.5e+2,\"b\":false,\"o\":{\"k\":[10,20]},\"d\":1,\"d\":2}");

            Assert.Equal("a\n\u00e9\U0001F600", json["s"].AsString());
            Assert.Equal(-1250d, json["n"].AsNumber());
            Assert.False(json["b"].AsBoolean());
            Assert.Equal(20, json["o"]["k"][1].AsNumber());
            Assert.Equal(2, json["d"].AsNumber());
            Assert.Equal(5, json.Count);
            Assert.Equal(2, json["o"]["k"].Items.Count);
            Assert.Contains("o", json.Properties.Keys);
            Assert.Null(json["s"].AsNumber());
        }

        [Fact]
        public void Missing_Parts_Should_Be_Undefined_Without_Throwing()
        {
            var json = JsonValue.Parse("{\"user\":{\"roles\":[]}}");

            Assert.Null(json["user"]["roles"][0].AsString());
            Assert.Equal(JsonKind.Undefined, json["nope"]["deeper"][3].Kind);
            Assert.False(json["User"].Exists);
            Assert.Equal(JsonKind.Undefined, json[-1].Kind);
            Assert.Equal(0, json["nope"].Count);
            Assert.Empty(json["nope"].Items);
            Assert.Empty(json["nope"].Properties);
            Assert.Equal("undefined", json["nope"].ToString());
        }

        [Fact]
        public void ToString_Should_Return_Compact_Json()
        {
            var text = "{\"a\":[1,2.50,\"x\\\"y\\n\",null,true],\"b\":{}}";

            Assert.Equal(text, JsonValue.Parse(" {\"a\" : [1, 2.50, \"x\\\"y\\n\", null, true], \"b\": { }} ").ToString());
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("[1,]")]
        [InlineData("{\"a\":1,}")]
        [InlineData("{} {}")]
        [InlineData("\"abc")]
        [InlineData("\"\\x\"")]
        [InlineData("\"\\u12\"")]
        [InlineData("01")]
        [InlineData("1.")]
        [InlineData("// c\n1")]
        public void Invalid_Json_Should_Not_Parse(string json)
        {
            Assert.False(JsonValue.TryParse(json, out var value));
            Assert.Equal(JsonKind.Undefined, value.Kind);
            Assert.Throws<FormatException>(() => JsonValue.Parse(json));
        }

        [Fact]
        public void Too_Deep_Json_Should_Not_Parse()
        {
            var deep = new string('[', 300) + new string(']', 300);

            Assert.False(JsonValue.TryParse(deep, out _));
            Assert.Throws<FormatException>(() => JsonValue.Parse(deep));
            Assert.True(JsonValue.TryParse(new string('[', 200) + new string(']', 200), out _));
        }

        [Fact]
        public void Null_Input_Should_Be_Handled()
        {
            Assert.False(JsonValue.TryParse(null, out _));
            Assert.Throws<ArgumentNullException>(() => JsonValue.Parse(null));
        }
    }
}
