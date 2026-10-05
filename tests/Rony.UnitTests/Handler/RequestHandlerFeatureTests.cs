using Rony.Handlers;
using Rony.Net;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Rony.Tests.Handler
{
    public class RequestHandlerFeatureTests
    {
        private readonly RequestHandler _handler = new RequestHandler();

        [Fact]
        public void Truncated_And_Corrupted_Should_Validate_Their_Arguments()
        {
            //Arrange
            var builder = _handler.Send("X").Receive("HELLO");

            //Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.Truncated(-1));
            Assert.Throws<ArgumentNullException>(() => builder.Corrupted(null));
        }

        [Theory]
        [InlineData("chunks-size")]
        [InlineData("chunks-delay")]
        [InlineData("throttled")]
        [InlineData("chunks-noreply")]
        [InlineData("throttled-noreply")]
        public void InChunks_And_Throttled_Should_Validate_Their_Arguments_And_Need_A_Reply(string case_)
        {
            //Arrange
            var reply = _handler.Send("X").Receive("HELLO");
            var noReply = _handler.Send("Y").NoReply();

            //Act
            Action action = case_ switch
            {
                "chunks-size" => () => reply.InChunks(0),
                "chunks-delay" => () => reply.InChunks(1, TimeSpan.FromMilliseconds(-1)),
                "throttled" => () => reply.Throttled(0),
                "chunks-noreply" => () => noReply.InChunks(1),
                _ => () => noReply.Throttled(1)
            };

            //Assert
            if (case_.EndsWith("noreply"))
                Assert.Throws<InvalidOperationException>(action);
            else
                Assert.Throws<ArgumentOutOfRangeException>(action);
        }

        [Theory]
        [InlineData("disconnect")]
        [InlineData("noreply")]
        [InlineData("reset")]
        public void Truncated_And_Corrupted_Should_Throw_When_The_Previous_Step_Sends_No_Reply(string step)
        {
            //Arrange
            var builder = step switch
            {
                "disconnect" => _handler.Send("X").Disconnect(),
                "noreply" => _handler.Send("X").NoReply(),
                _ => _handler.Send("X").ResetConnection()
            };

            //Assert
            Assert.Throws<InvalidOperationException>(() => builder.Truncated(1));
            Assert.Throws<InvalidOperationException>(() => builder.Corrupted(bytes => bytes));
        }

        [Fact]
        public void Regex_Should_Match_Request_Text()
        {
            //Act
            _handler.Send(new Regex(@"^LOGIN \w+$")).Receive("WELCOME");

            //Assert
            Assert.Equal("WELCOME", _handler.Match("LOGIN alice").GetString());
            Assert.Empty(_handler.Match("LOGIN"));
        }

        [Fact]
        public void Predicates_Should_Match_Text_And_Bytes()
        {
            //Act
            _handler.SendMatching(x => x.StartsWith("GET ")).Receive("200 OK");
            _handler.SendMatchingBytes(x => x.Length > 0 && x[0] == 0x02).Receive(new byte[] { 0x06 });

            //Assert
            Assert.Equal("200 OK", _handler.Match("GET /index").GetString());
            Assert.Equal(new byte[] { 0x06 }, _handler.Match(new byte[] { 0x02, 0x10, 0x03 }));
        }

        [Fact]
        public void Exact_Match_Should_Win_Over_Predicate_And_Predicate_Over_Any()
        {
            //Act
            _handler.Send("").Receive("any");
            _handler.SendMatching(x => x.StartsWith("A")).Receive("predicate");
            _handler.Send("ABC").Receive("exact");

            //Assert
            Assert.Equal("exact", _handler.Match("ABC").GetString());
            Assert.Equal("predicate", _handler.Match("AXY").GetString());
            Assert.Equal("any", _handler.Match("XYZ").GetString());
        }

        [Fact]
        public void Predicates_Should_Be_Checked_In_Order()
        {
            //Act
            _handler.SendMatching(x => x.Length > 2).Receive("first");
            _handler.SendMatching(x => x.Length > 1).Receive("second");

            //Assert
            Assert.Equal("first", _handler.Match("abc").GetString());
            Assert.Equal("second", _handler.Match("ab").GetString());
        }

        [Fact]
        public void Throwing_Predicate_Should_Not_Match()
        {
            //Act
            _handler.SendMatchingBytes(x => x[100] == 1).Receive("never");

            //Assert
            Assert.Empty(_handler.Match("short"));
        }

        [Fact]
        public void Sequence_Should_Return_Responses_In_Order_And_Repeat_The_Last()
        {
            //Act
            _handler.Send("A").Receive("busy").Then("busy").Then("ok");

            //Assert
            var responses = Enumerable.Range(0, 5).Select(_ => _handler.Match("A").GetString());
            Assert.Equal(new[] { "busy", "busy", "ok", "ok", "ok" }, responses);
            Assert.Equal(5, _handler.Configs["A".GetBytes()].CallCount);
        }

        [Fact]
        public void Sequence_Should_Support_Functions()
        {
            //Act
            _handler.Send("").Receive(x => x.ToUpper()).Then(x => x.Reverse().ToArray());

            //Assert
            Assert.Equal("AB", _handler.Match("ab").GetString());
            Assert.Equal("ba", _handler.Match("ab").GetString());
        }

        [Fact]
        public void Disconnect_And_NoReply_Should_Produce_Empty_Response()
        {
            //Act
            _handler.Send("D").Disconnect();
            _handler.Send("N").NoReply();
            _handler.Send("S").Receive("first").ThenNoReply().ThenDisconnect();

            //Assert
            Assert.Empty(_handler.Match("D"));
            Assert.Empty(_handler.Match("N"));
            Assert.Equal("first", _handler.Match("S").GetString());
            Assert.Empty(_handler.Match("S"));
            Assert.Empty(_handler.Match("S"));
        }

        [Fact]
        public void After_Should_Reject_Negative_Delay()
        {
            //Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => _handler.Send("A").Receive("B").After(TimeSpan.FromSeconds(-1)));
        }

        [Fact]
        public void MaxReceivedRequests_Should_Keep_Only_The_Newest_Requests()
        {
            //Arrange
            _handler.MaxReceivedRequests = 2;

            //Act
            _handler.Match("one");
            _handler.Match("two");
            _handler.Match("three");

            //Assert
            Assert.Equal(new[] { "two", "three" }, _handler.ReceivedRequests.Select(r => r.BodyString));
            Assert.Throws<ArgumentOutOfRangeException>(() => _handler.MaxReceivedRequests = -1);
        }

        [Fact]
        public void Received_Requests_Should_Be_Recorded_With_Match_Result()
        {
            //Arrange
            _handler.Send("known").Receive("yes");

            //Act
            _handler.Match("known");
            _handler.Match("unknown");

            //Assert
            Assert.Equal(new[] { "known", "unknown" }, _handler.ReceivedRequests.Select(r => r.BodyString));
            Assert.True(_handler.ReceivedRequests[0].Matched);
            Assert.False(_handler.ReceivedRequests[1].Matched);
            Assert.Single(_handler.UnmatchedRequests);
        }

        [Fact]
        public void Verify_Should_Pass_When_Count_Matches()
        {
            //Arrange
            _handler.Send("PING").Receive("PONG");

            //Act
            _handler.Match("PING");
            _handler.Match("PING");

            //Assert
            _handler.Verify("PING");
            _handler.Verify("PING", Times.Exactly(2));
            _handler.Verify("PONG", Times.Never());
            _handler.Verify(r => r.BodyString.StartsWith("PI"), Times.AtLeast(2));
        }

        [Fact]
        public void Verify_Should_Throw_With_Received_Requests_In_Message()
        {
            //Act
            _handler.Match("PING");

            //Assert
            var exception = Assert.Throws<MockVerificationException>(() => _handler.Verify("PING", Times.Exactly(3)));
            Assert.Contains("Expected request \"PING\" exactly 3 times, but it was received 1 time.", exception.Message);
            Assert.Contains("1. \"PING\" (unmatched)", exception.Message);
        }

        [Fact]
        public void Verify_Should_Describe_Binary_Requests_As_Hex()
        {
            //Assert
            var exception = Assert.Throws<MockVerificationException>(() => _handler.Verify(new byte[] { 0xFF, 0x01 }));
            Assert.Contains("0xFF 0x01", exception.Message);
        }

        [Fact]
        public void VerifyAllRequestsMatched_Should_List_Unmatched_Requests()
        {
            //Arrange
            _handler.Send("known").Receive("yes");
            _handler.Match("known");
            _handler.VerifyAllRequestsMatched();

            //Act
            _handler.Match("unknown");

            //Assert
            var exception = Assert.Throws<MockVerificationException>(() => _handler.VerifyAllRequestsMatched());
            Assert.Contains("1 request had no configured response", exception.Message);
            Assert.Contains("\"unknown\"", exception.Message);
        }

        [Fact]
        public async Task WaitForRequestAsync_Should_Return_Already_Received_Request()
        {
            //Arrange
            _handler.Match("early");

            //Act
            var request = await _handler.WaitForRequestAsync("early", TimeSpan.FromSeconds(1));

            //Assert
            Assert.Equal("early", request.BodyString);
        }

        [Fact]
        public async Task WaitForRequestAsync_Should_Wait_For_A_Later_Request()
        {
            //Act
            var waiting = _handler.WaitForRequestAsync(r => r.BodyString == "late", TimeSpan.FromSeconds(5));
            _handler.Match("other");
            Assert.False(waiting.IsCompleted);
            _handler.Match("late");
            var request = await waiting;

            //Assert
            Assert.Equal("late", request.BodyString);
        }

        [Fact]
        public async Task WaitForRequestAsync_Should_Throw_On_Timeout()
        {
            //Act
            _handler.Match("other");

            //Assert
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => _handler.WaitForRequestAsync("missing", TimeSpan.FromMilliseconds(100)));
            Assert.Contains("\"missing\"", exception.Message);
            Assert.Contains("\"other\"", exception.Message);
        }

        [Fact]
        public async Task WaitForRequestAsync_Should_Honor_Cancellation()
        {
            //Arrange
            using var cancellation = new CancellationTokenSource(100);

            //Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _handler.WaitForRequestAsync(TimeSpan.FromSeconds(10), cancellation.Token));
        }

        [Fact]
        public async Task WaitForRequestsAsync_Should_Wait_For_Count()
        {
            //Act
            var waiting = _handler.WaitForRequestsAsync(3, TimeSpan.FromSeconds(5));
            _handler.Match("1");
            _handler.Match("2");
            Assert.False(waiting.IsCompleted);
            _handler.Match("3");
            var requests = await waiting;

            //Assert
            Assert.Equal(3, requests.Count);
        }

        [Fact]
        public void Reset_And_Clear_Should_Remove_State()
        {
            //Arrange
            _handler.Send("A").Receive("B");
            _handler.SendMatching(_ => true).Receive("C");
            _handler.Match("A");

            //Act
            _handler.ClearReceivedRequests();

            //Assert
            Assert.Empty(_handler.ReceivedRequests);
            Assert.Single(_handler.Configs);

            //Act
            _handler.Reset();

            //Assert
            Assert.Empty(_handler.Configs);
            Assert.Empty(_handler.Match("anything"));
        }
    }
}
