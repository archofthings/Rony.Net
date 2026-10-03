using Rony.Handlers;
using System;
using System.Text.RegularExpressions;
using Xunit;

namespace Rony.Tests.Handler
{
    public class RequestHandlerPartialMatchingTests
    {
        private readonly RequestHandler _handler = new RequestHandler();

        [Fact]
        public void SendJson_Should_Match_On_A_Field_And_Skip_Non_Json()
        {
            var calls = 0;
            _handler.SendJson(j => { calls++; return j["type"].AsString() == "login"; }).Receive("ok");

            Assert.Equal("ok", _handler.Match("{\"type\":\"login\"}").GetString());
            Assert.Empty(_handler.Match("{\"type\":\"logout\"}"));
            Assert.Empty(_handler.Match("login"));
            Assert.Equal(2, calls);
            Assert.Equal(2, _handler.UnmatchedRequests.Count);
        }

        [Fact]
        public void SendJson_Should_Respect_InState()
        {
            _handler.InState("a").SendJson(j => j["x"].Exists).Receive("in a");

            Assert.Empty(_handler.Match("{\"x\":1}"));
            _handler.State = "a";
            Assert.Equal("in a", _handler.Match("{\"x\":1}").GetString());
        }

        [Fact]
        public void SendJson_Should_Not_Match_When_The_Predicate_Throws()
        {
            _handler.SendJson(j => throw new InvalidOperationException("boom")).Receive("never");

            Assert.Empty(_handler.Match("{}"));
            Assert.Single(_handler.UnmatchedRequests);
        }

        [Fact]
        public void Exact_Request_Should_Win_Over_SendJson()
        {
            _handler.SendJson(j => true).Receive("json");
            _handler.Send("{}").Receive("exact");

            Assert.Equal("exact", _handler.Match("{}").GetString());
            Assert.Equal("json", _handler.Match("[]").GetString());
        }

        [Fact]
        public void SendJson_Should_Reject_A_Null_Predicate()
        {
            Assert.Throws<ArgumentNullException>(() => _handler.SendJson(null));
            Assert.Throws<ArgumentNullException>(() => _handler.InState("a").SendJson(null));
        }

        [Fact]
        public void ReceiveMatch_Should_Use_Capture_Groups()
        {
            _handler.Send(new Regex(@"^HELLO (\w+)$")).ReceiveMatch(m => $"HI {m.Groups[1].Value}");
            _handler.InState("s").Send(new Regex(@"^BYE (\w+)$")).ReceiveMatch(m => null);

            Assert.Equal("HI bob", _handler.Match("HELLO bob").GetString());
            _handler.State = "s";
            Assert.Empty(_handler.Match("BYE bob"));
        }

        [Fact]
        public void ThenMatch_Should_Work_In_A_Sequence_With_Modifiers()
        {
            _handler.Send(new Regex(@"^N (\d+)$")).Receive("first").ThenMatch(m => "second " + m.Groups[1].Value).After(TimeSpan.Zero);

            Assert.Equal("first", _handler.Match("N 1").GetString());
            Assert.Equal("second 2", _handler.Match("N 2").GetString());
        }

        [Fact]
        public void ReceiveMatch_Should_Return_An_Empty_Response_When_The_Function_Throws()
        {
            _handler.Send(new Regex("x")).ReceiveMatch(m => throw new InvalidOperationException());

            Assert.Empty(_handler.Match("x"));
        }

        [Fact]
        public void ReceiveMatch_And_ThenMatch_Should_Validate_Their_Arguments()
        {
            Assert.Throws<ArgumentNullException>(() => _handler.Send(new Regex("x")).ReceiveMatch(null));
            Assert.Throws<ArgumentNullException>(() => _handler.Send(new Regex("y")).Receive("a").ThenMatch(null));
        }

        [Theory]
        [InlineData("exact")]
        [InlineData("text")]
        [InlineData("bytes")]
        [InlineData("json")]
        [InlineData("connect")]
        [InlineData("unmatched")]
        public void ReceiveMatch_And_ThenMatch_Should_Throw_Without_A_Regex_Rule(string rule)
        {
            RequestHandler Start() => rule switch
            {
                "exact" => _handler.Send("a"),
                "text" => _handler.SendMatching(t => true),
                "bytes" => _handler.SendMatchingBytes(b => true),
                "json" => _handler.SendJson(j => true),
                "connect" => _handler.OnConnect(),
                _ => _handler.OnUnmatched()
            };

            var exception = Assert.Throws<InvalidOperationException>(() => Start().ReceiveMatch(m => "x"));
            Assert.Contains("Send(Regex)", exception.Message);

            var builder = Start().Receive("x");
            Assert.Throws<InvalidOperationException>(() => builder.ThenMatch(m => "x"));
        }
    }
}
