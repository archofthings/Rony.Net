using Rony.Interfaces;
using Rony.Listeners;
using Rony.Net;
using Rony.Net.Xunit;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;

namespace Rony.Net.Xunit.V3.Tests
{
    /// <summary>Uses the base class the way a user would.</summary>
    public class PingTests : MockServerTest
    {
        public PingTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task Client_Gets_Pong()
        {
            //Arrange
            Server.Mock.Send("PING").Receive("PONG");

            //Act
            var response = await TestClient.SendAsync(Server.Port, "PING");

            //Assert
            Assert.Equal("PONG", response);
            Server.Should().HaveReceived("PING", Times.Once());
        }
    }

    public class MockServerTestTests
    {
        [Fact]
        public async Task Server_Should_Start_On_First_Use_And_Log_To_The_Output()
        {
            //Arrange
            var output = new RecordingOutput();
            using var test = new SampleTest(output);
            test.Mock.Send("PING").Receive("PONG");

            //Act
            await TestClient.SendAsync(test.Port, "PING");
            await test.Mock.WaitForRequestAsync("PING", cancellationToken: TestContext.Current.CancellationToken);

            //Assert
            Assert.Contains(output.Lines, l => l.Contains("listening on 127.0.0.1"));
            Assert.Contains(output.Lines, l => l.Contains("received \"PING\""));
        }

        [Fact]
        public void Dispose_Should_Stop_The_Server()
        {
            //Arrange
            var test = new SampleTest(new RecordingOutput());
            var server = test.Exposed;

            //Act
            test.Dispose();

            //Assert
            Assert.False(server.Active);
            test.Dispose();
        }

        [Fact]
        public void Dispose_Should_Fail_On_Unmatched_Requests_When_Asked()
        {
            //Arrange
            var test = new SampleTest(new RecordingOutput()) { Strict = true };
            test.Mock.Match("nobody configured this");
            var server = test.Exposed;

            //Act & Assert
            Assert.Throws<MockVerificationException>(() => test.Dispose());
            Assert.False(server.Active);
        }

        [Fact]
        public void A_Test_That_Never_Uses_The_Server_Should_Not_Start_One()
        {
            //Arrange
            var test = new SampleTest(new RecordingOutput()) { Strict = true };

            //Act & Assert
            test.Dispose();
            Assert.Equal(0, test.ListenersCreated);
        }

        [Fact]
        public void CreateListener_Should_Choose_The_Transport()
        {
            //Arrange
            using var test = new UdpTest(new RecordingOutput());

            //Act & Assert
            Assert.True(test.Exposed.Active);
            Assert.Throws<NotSupportedException>(() => test.Exposed.VerifyConnections(Times.Never()));
        }

        [Fact]
        public void LogTo_Should_Ignore_Writes_That_Throw()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0)).LogTo(new ThrowingOutput());

            //Act & Assert
            server.Start();
            server.Stop();
        }

        private sealed class SampleTest : MockServerTest
        {
            public SampleTest(ITestOutputHelper output) : base(output)
            {
            }

            public int ListenersCreated { get; private set; }
            public MockServer Exposed => Server;
            public Handlers.RequestHandler Mock => Server.Mock;
            public int Port => Server.Port;

            public bool Strict
            {
                set => VerifyAllRequestsMatchedAfterTest = value;
            }

            protected override IListener CreateListener()
            {
                ListenersCreated++;
                return base.CreateListener();
            }
        }

        private sealed class UdpTest : MockServerTest
        {
            public UdpTest(ITestOutputHelper output) : base(output)
            {
            }

            public MockServer Exposed => Server;

            protected override IListener CreateListener() => new UdpServer("127.0.0.1", 0);
        }

        private sealed class RecordingOutput : ITestOutputHelper
        {
            public ConcurrentQueue<string> Lines { get; } = new ConcurrentQueue<string>();
            public void WriteLine(string message) => Lines.Enqueue(message);
            public void WriteLine(string format, params object[] args) => Lines.Enqueue(string.Format(format, args));
            public void Write(string message) => throw new NotSupportedException();
            public void Write(string format, params object[] args) => throw new NotSupportedException();
            public string Output => string.Join(Environment.NewLine, Lines);
        }

        private sealed class ThrowingOutput : ITestOutputHelper
        {
            public void WriteLine(string message) => throw new InvalidOperationException("There is no currently active test.");
            public void WriteLine(string format, params object[] args) => throw new InvalidOperationException("There is no currently active test.");
            public void Write(string message) => throw new InvalidOperationException("There is no currently active test.");
            public void Write(string format, params object[] args) => throw new InvalidOperationException("There is no currently active test.");
            public string Output => string.Empty;
        }
    }

    internal static class TestClient
    {
        public static async Task<string> SendAsync(int port, string request)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await stream.WriteAsync(request.GetBytes());
            var buffer = new byte[1024];
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            return buffer[..read].GetString();
        }
    }
}
