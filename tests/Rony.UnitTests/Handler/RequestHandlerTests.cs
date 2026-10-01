using Rony.Handlers;
using System;
using System.Linq;
using System.Text;
using Xunit;

namespace Rony.Tests.Handler
{
    public class RequestHandlerTests
    {
        private readonly RequestHandler _handler;

        public RequestHandlerTests()
        {
            //Arrange
            _handler = new RequestHandler();
        }

        [Fact]
        public void Constructor_Should_Work_Correctly()
        {
            //Assert
            Assert.NotNull(_handler.Configs);
            Assert.Empty(_handler.Configs);
        }

        [Theory]
        [InlineData("TestData", "TestRecieveData")]
        [InlineData("1234", "2345")]
        [InlineData("ABCD", "EFGHI")]
        [InlineData("4321", "HGHST")]
        [InlineData(",#$@", "*&%$#@")]
        public void Config_With_String_Should_Add_Config(string request, string response)
        {
            //Act
            _handler.Send(request).Receive(response);

            //Assert
            Assert.Single(_handler.Configs);
            Assert.Equal(response, _handler.Match(request).GetString());
        }

        [Theory]
        [InlineData("TestData", "TestRecieveData")]
        [InlineData("1234", "2345")]
        [InlineData("ABCD", "EFGHI")]
        [InlineData("4321", "HGHST")]
        [InlineData(",#$@", "*&%$#@")]
        public void Config_With_Byte_Array_Should_Add_Config(string request, string response)
        {
            //Arrange
            var requestByte = Encoding.UTF8.GetBytes(request);
            var responseByte = Encoding.UTF8.GetBytes(response);

            //Act
            _handler.Send(requestByte).Receive(responseByte);
            var received = _handler.Match(requestByte);

            //Assert
            Assert.Single(_handler.Configs);
            Assert.Equal(responseByte, received);
        }

        [Fact]
        public void Config_With_Binary_Data_Should_Match_Exact_Bytes()
        {
            //Arrange
            // Not valid UTF-8: these used to collapse into the same string key.
            var first = new byte[] { 0xFF, 0x00 };
            var second = new byte[] { 0xFE, 0x00 };

            //Act
            _handler.Send(first).Receive(new byte[] { 0x80, 0x81 });
            _handler.Send(second).Receive(new byte[] { 0x90 });

            //Assert
            Assert.Equal(2, _handler.Configs.Count);
            Assert.Equal(new byte[] { 0x80, 0x81 }, _handler.Match(first));
            Assert.Equal(new byte[] { 0x90 }, _handler.Match(second));
        }

        [Fact]
        public void Func_Of_Byte_Array_Should_Receive_Raw_Request_Bytes()
        {
            //Arrange
            var request = new byte[] { 0xFF, 0xFE, 0x01 };

            //Act
            _handler.Send("").Receive(x => x.Reverse().ToArray());

            //Assert
            Assert.Equal(new byte[] { 0x01, 0xFE, 0xFF }, _handler.Match(request));
        }

        [Theory]
        [InlineData("TestData", "TESTDATA")]
        [InlineData("1234", "1234")]
        [InlineData("ABCD", "ABCD")]
        [InlineData("asdfgh", "ASDFGH")]
        public void Config_With_Func_Of_String_Should_Add_Config(string request, string response)
        {
            //Act
            _handler.Send(request).Receive(x => x.ToUpper());

            //Assert
            Assert.Single(_handler.Configs);
            Assert.Equal(response, _handler.Match(request).GetString());
        }

        [Fact]
        public void Config_With_Func_Of_Byte_Array_Should_Add_Config()
        {
            //Arrange
            var request = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            var expectedResponse = new byte[] { 1, 3, 5, 7, 9 };

            //Act
            _handler.Send(request).Receive(x => x.Where((_, i) => i % 2 == 0).ToArray());
            var received = _handler.Match(request);

            //Assert
            Assert.Single(_handler.Configs);
            Assert.Equal(expectedResponse, received);
        }

        [Theory]
        [InlineData("Try Me")]
        [InlineData("123456")]
        [InlineData("Try @76453")]
        [InlineData(" ")]
        [InlineData("&#^@%")]
        public void Config_With_Empty_String_Should_Match_Any_Request(string request)
        {
            //Act
            _handler.Send("").Receive("I match Everything");
            var received = _handler.Match(request);

            //Assert
            Assert.Single(_handler.Configs);
            Assert.Equal("I match Everything", received.GetString());
        }

        [Fact]
        public void Exact_Match_Should_Take_Precedence_Over_Any_Request()
        {
            //Act
            _handler.Send("").Receive("Any");
            _handler.Send("Exact").Receive("Exact Response");

            //Assert
            Assert.Equal("Exact Response", _handler.Match("Exact").GetString());
            Assert.Equal("Any", _handler.Match("Other").GetString());
        }

        [Theory]
        [InlineData("Try Me")]
        [InlineData("123456")]
        [InlineData("Try @76453")]
        [InlineData(" ")]
        [InlineData("&#^@%")]
        public void Config_With_Empty_String_Should_Match_Any_Request_With_Func_Of_String(string request)
        {
            //Act
            _handler.Send("").Receive(x => x.ToUpper());
            var received = _handler.Match(request);

            //Assert
            Assert.Single(_handler.Configs);
            Assert.Equal(request.ToUpper(), received.GetString());
        }

        [Theory]
        [InlineData("Try Me")]
        [InlineData("123456")]
        [InlineData("Try @76453")]
        [InlineData(" 33")]
        [InlineData("&#^@%")]
        public void Config_With_Empty_String_Should_Match_Any_Request_With_Func_Of_Byte(string request)
        {
            //Act
            _handler.Send("").Receive(x => x.Take(3).ToArray());
            var received = _handler.Match(request);

            //Assert
            Assert.Single(_handler.Configs);
            Assert.Equal(request.Substring(0, 3), received.GetString());
        }

        [Fact]
        public void Match_Should_Return_Empty_Response_When_Nothing_Matches()
        {
            //Act
            _handler.Send("Request").Receive("Response");

            //Assert
            Assert.Empty(_handler.Match("Other"));
        }

        [Fact]
        public void Match_Should_Return_Empty_Response_When_Func_Throws()
        {
            //Act
            _handler.Send("").Receive(x => new byte[] { x[10] });

            //Assert
            Assert.Empty(_handler.Match("short"));
        }

        [Fact]
        public void Receive_Without_Send_Should_Throw()
        {
            //Assert
            Assert.Throws<InvalidOperationException>(() => _handler.Receive("Response"));
        }

        [Fact]
        public void Receive_Should_Not_Reuse_Previous_Send()
        {
            //Act
            _handler.Send("Request").Receive("Response");

            //Assert
            Assert.Throws<InvalidOperationException>(() => _handler.Receive("Another Response"));
        }

        [Fact]
        public void Adding_Duplicate_Request_Should_Throw()
        {
            //Act
            _handler.Send(new byte[] { 1, 2, 3 }).Receive("Response");

            //Assert
            Assert.Throws<ArgumentException>(() => _handler.Send(new byte[] { 1, 2, 3 }).Receive("test"));
        }
    }
}
