using Rony.Handlers;
using Rony.Net;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Rony.Tests.Handler
{
    public class RequestHandlerScenarioTests
    {
        private readonly RequestHandler _handler = new RequestHandler();

        [Fact]
        public void State_Should_Start_As_Initial()
        {
            //Assert
            Assert.Equal(RequestHandler.InitialState, _handler.State);
        }

        [Fact]
        public void GoTo_Should_Switch_The_Rules_That_Apply()
        {
            //Arrange
            _handler.Send("LOGIN bob").Receive("OK").GoTo("loggedIn");
            _handler.InState("loggedIn").Send("LIST").Receive("a,b,c");
            _handler.Send("LIST").Receive("ERR not logged in");

            //Act
            var before = _handler.Match("LIST").GetString();
            var login = _handler.Match("LOGIN bob").GetString();
            var after = _handler.Match("LIST").GetString();

            //Assert
            Assert.Equal("ERR not logged in", before);
            Assert.Equal("OK", login);
            Assert.Equal("a,b,c", after);
            Assert.Equal("loggedIn", _handler.State);
        }

        [Fact]
        public void State_Rules_Should_Win_At_Every_Level()
        {
            //Arrange
            _handler.SendMatching(t => t.StartsWith("GET")).Receive("any state");
            _handler.InState("ready").SendMatching(t => t.StartsWith("GET")).Receive("ready");
            _handler.Send("").Receive("default");
            _handler.InState("ready").Send("").Receive("ready default");
            _handler.State = "ready";

            //Assert
            Assert.Equal("ready", _handler.Match("GET x").GetString());
            Assert.Equal("ready default", _handler.Match("other").GetString());
        }

        [Fact]
        public void Exact_Rule_Without_State_Should_Still_Win_Over_State_Pattern()
        {
            //Arrange
            _handler.Send("PING").Receive("PONG");
            _handler.InState("busy").Send(new Regex(".*")).Receive("BUSY");
            _handler.State = "busy";

            //Assert
            Assert.Equal("PONG", _handler.Match("PING").GetString());
            Assert.Equal("BUSY", _handler.Match("other").GetString());
        }

        [Fact]
        public void GoTo_Should_Apply_To_Each_Step_Of_A_Sequence()
        {
            //Arrange
            _handler.Send("next").Receive("1").GoTo("one").Then("2").GoTo("two").Then("3");

            //Act & Assert
            _handler.Match("next");
            Assert.Equal("one", _handler.State);
            _handler.Match("next");
            Assert.Equal("two", _handler.State);
            _handler.Match("next");
            Assert.Equal("two", _handler.State);
        }

        [Fact]
        public void Same_Request_In_The_Same_State_Should_Throw()
        {
            //Arrange
            _handler.InState("a").Send("x").Receive("1");
            _handler.InState("b").Send("x").Receive("2");
            _handler.Send("x").Receive("3");

            //Act & Assert
            Assert.Throws<ArgumentException>(() => _handler.InState("a").Send("x").Receive("4"));
        }

        [Fact]
        public void Reset_Should_Clear_States_And_Special_Rules()
        {
            //Arrange
            _handler.OnConnect().Receive("hello");
            _handler.OnUnmatched().Receive("ERR");
            _handler.Send("go").Receive("ok").GoTo("gone");
            _handler.Match("go");

            //Act
            _handler.Reset();

            //Assert
            Assert.Equal(RequestHandler.InitialState, _handler.State);
            Assert.Empty(_handler.Match("anything"));
            _handler.OnConnect().Receive("hello again");
            _handler.OnUnmatched().Receive("ERR again");
        }

        [Fact]
        public void OnUnmatched_Should_Reply_But_Record_The_Request_As_Unmatched()
        {
            //Arrange
            _handler.Send("PING").Receive("PONG");
            _handler.OnUnmatched().Receive(text => $"ERR unknown command {text}");

            //Act
            var response = _handler.Match("FOO").GetString();

            //Assert
            Assert.Equal("ERR unknown command FOO", response);
            Assert.Single(_handler.UnmatchedRequests);
            Assert.Throws<MockVerificationException>(() => _handler.VerifyAllRequestsMatched());
        }

        [Fact]
        public void OnConnect_And_OnUnmatched_Can_Only_Be_Configured_Once()
        {
            //Arrange
            _handler.OnConnect().Receive("hello");
            _handler.OnUnmatched().NoReply();

            //Act & Assert
            Assert.Throws<ArgumentException>(() => _handler.OnConnect().Receive("again"));
            Assert.Throws<ArgumentException>(() => _handler.OnUnmatched().Receive("again"));
        }

        [Fact]
        public void FailOnUnmatched_Should_Make_Verify_Throw()
        {
            //Arrange
            _handler.FailOnUnmatched = true;
            _handler.Send("PING").Receive("PONG");
            _handler.Match("PING");
            _handler.Verify("PING", Times.Once());

            //Act
            _handler.Match("PNIG");

            //Assert
            var exception = Assert.Throws<MockVerificationException>(() => _handler.Verify("PING", Times.Once()));
            Assert.Contains("\"PNIG\"", exception.Message);
        }

        [Fact]
        public async Task FailOnUnmatched_Should_Stop_A_Wait_Right_Away()
        {
            //Arrange
            _handler.FailOnUnmatched = true;
            var wait = _handler.WaitForRequestAsync("HELLO", TimeSpan.FromSeconds(30));

            //Act
            _handler.Match("GOODBYE");

            //Assert
            var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(wait, completed);
            await Assert.ThrowsAsync<MockVerificationException>(() => wait);
        }

        [Fact]
        public void VerifyInOrder_Should_Allow_Requests_In_Between()
        {
            //Arrange
            foreach (var request in new[] { "HELLO", "LOGIN", "NOOP", "LIST", "QUIT" })
                _handler.Match(request);

            //Act & Assert
            _handler.VerifyInOrder("LOGIN", "LIST", "QUIT");
            _handler.VerifyInOrder(r => r.BodyString == "HELLO", r => r.BodyString.StartsWith("L"));
            _handler.VerifyInOrder(new byte[] { (byte)'N', (byte)'O', (byte)'O', (byte)'P' });
        }

        [Fact]
        public void VerifyInOrder_Should_Explain_What_Was_Out_Of_Order()
        {
            //Arrange
            _handler.Match("LIST");
            _handler.Match("LOGIN");

            //Act
            var exception = Assert.Throws<MockVerificationException>(() => _handler.VerifyInOrder("LOGIN", "LIST"));

            //Assert
            Assert.Contains("\"LIST\" was not received after \"LOGIN\"", exception.Message);
            Assert.Contains("1. \"LIST\"", exception.Message);
        }

        [Fact]
        public void VerifyInOrder_Should_Need_A_Request_For_Every_Item()
        {
            //Arrange
            _handler.Match("PING");

            //Act & Assert
            Assert.Throws<MockVerificationException>(() => _handler.VerifyInOrder("PING", "PING"));
            Assert.Throws<MockVerificationException>(() => _handler.VerifyInOrder("PONG"));
        }

        [Fact]
        public void Without_A_Pending_Request_Receive_Should_Throw()
        {
            //Act & Assert
            Assert.Throws<InvalidOperationException>(() => _handler.Receive("x"));
        }
    }
}
