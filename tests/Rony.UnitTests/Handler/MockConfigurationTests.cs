using Rony.Models;
using Rony.Net;
using System;
using System.IO;
using System.Net;
using Xunit;

namespace Rony.Tests.Handler
{
    public class MockConfigurationTests
    {
        // Single quotes keep the JSON readable in C#.
        private static string Json(string text) => text.Replace('\'', '"');

        private static MockServer FromText(string json) => MockServer.FromJson(Json(json));

        private static string Reply(MockServer server, string request) => server.Mock.Match(request).GetString();

        [Fact]
        public void FromJson_Should_Create_An_Unstarted_Server_From_The_Minimal_File()
        {
            using var server = MockServer.FromJson("{ \"version\": 1 }");

            Assert.False(server.Active);
            Assert.Equal(IPAddress.Loopback, server.Address);
            Assert.Empty(server.Mock.Configs);
        }

        [Fact]
        public void FromJson_Should_Build_Exact_Regex_And_State_Rules()
        {
            using var server = FromText(@"{
              'version': 1,
              'onUnmatched': { 'reply': 'ERR' },
              'rules': [
                { 'request': 'PING', 'reply': 'PONG' },
                { 'request': { 'base64': 'AQID' }, 'reply': { 'base64': 'BAUG' } },
                { 'match': '^HELLO (\\w+)$', 'reply': 'HI $1 ${1}', 'goTo': 'known' },
                { 'request': 'WHO', 'state': 'known', 'replies': [ { 'reply': 'a' }, { 'reply': 'b' } ] }
              ] }");

            Assert.Equal("PONG", Reply(server, "PING"));
            Assert.Equal(new byte[] { 4, 5, 6 }, server.Mock.Match(new byte[] { 1, 2, 3 }));
            Assert.Equal("ERR", Reply(server, "WHO"));
            Assert.Equal("HI bob bob", Reply(server, "HELLO bob"));
            Assert.Equal(new[] { "a", "b", "b" }, new[] { Reply(server, "WHO"), Reply(server, "WHO"), Reply(server, "WHO") });
        }

        [Theory]
        [InlineData("{'type':'login','user':{'name':'bob','id':7},'extra':1,'roles':['a','b']}", true)]
        [InlineData("{'type':'login','user':{'name':'bob'}}", false)]
        [InlineData("{'type':'login','user':{'name':'alice'}}", false)]
        [InlineData("{'type':'login'}", false)]
        [InlineData("{'type':'login','user':{'name':'bob'},'roles':['a']}", false)]
        [InlineData("{'type':'login','user':{'name':'bob'},'roles':['a','b']}", true)]
        [InlineData("not json", false)]
        public void Json_Rule_Should_Match_Requests_Containing_The_Given_Properties(string request, bool matches)
        {
            using var server = FromText(@"{ 'version': 1, 'rules': [
                { 'json': { 'type': 'login', 'user': { 'name': 'bob' }, 'roles': ['a', 'b'] }, 'reply': 'ok' } ] }");

            Assert.Equal(matches ? "ok" : string.Empty, Reply(server, Json(request)));
        }

        [Theory]
        [InlineData("{ }", "version: is missing")]
        [InlineData("{ 'version': 2 }", "version: 2 is not supported")]
        [InlineData("{ 'version': 1, 'rules': {} }", "rules: must be an array")]
        [InlineData("{ 'version': 1, 'rules': [ { 'reply': 'x' } ] }", "rules[0]: needs one of")]
        [InlineData("{ 'version': 1, 'rules': [ { 'request': 'a', 'match': 'b', 'reply': 'x' } ] }", "rules[0]: \"request\" and \"match\" cannot both be set")]
        [InlineData("{ 'version': 1, 'rules': [ { 'request': 'a' } ] }", "rules[0]: needs a response")]
        [InlineData("{ 'version': 1, 'rules': [ { 'request': 'a', 'reply': 'x', 'replies': [ { 'reply': 'y' } ] } ] }", "rules[0]: \"reply\" and \"replies\" cannot both be set")]
        [InlineData("{ 'version': 1, 'rules': [ { 'request': 'a', 'replies': [] } ] }", "rules[0].replies: must be a non-empty array")]
        [InlineData("{ 'version': 1, 'rules': [ { 'request': 'a', 'noReply': true, 'disconnect': true } ] }", "rules[0]: \"noReply\" and \"disconnect\" cannot both be set")]
        [InlineData("{ 'version': 1, 'rules': [ { 'request': 'a', 'disconnect': true, 'reset': true } ] }", "rules[0]: \"disconnect\" and \"reset\" cannot both be set")]
        [InlineData("{ 'version': 1, 'rules': [ { 'request': 'a', 'reply': 'x', 'afterMs': -1 } ] }", "rules[0].afterMs: must be a whole number")]
        [InlineData("{ 'version': 1, 'rules': [ { 'match': '(', 'reply': 'x' } ] }", "rules[0].match: is not a valid regular expression")]
        [InlineData("{ 'version': 1, 'rules': [ { 'request': { 'base64': '***' }, 'reply': 'x' } ] }", "rules[0].request.base64: is not valid base64")]
        [InlineData("{ 'version': 1, 'rules': [ { 'request': 'a', 'reply': 'x' }, { 'request': 'a', 'reply': 'y' } ] }", "rules[1]: A response is already configured")]
        [InlineData("{ 'version': 1, 'rules': [ { 'request': 'a', 'reply': 'x', 'replyy': 'y' } ] }", "rules[0]: unknown property \"replyy\"")]
        [InlineData("{ 'version': 1, 'rules': [ { 'request': 'a', 'replies': [ { 'reply': 'x', 'goto': 's' } ] } ] }", "rules[0].replies[0]: unknown property \"goto\"")]
        [InlineData("{ 'version': 1, 'server': { 'port': 1, 'colour': 'red' } }", "server: unknown property \"colour\"")]
        [InlineData("{ 'version': 1, 'server': { 'transport': 'ftp' } }", "server.transport: \"ftp\" is not valid")]
        [InlineData("{ 'version': 1, 'server': { 'path': '/tmp/x' } }", "server.path: not allowed with transport \"tcp\"")]
        [InlineData("{ 'version': 1, 'server': { 'transport': 'unix', 'port': 1 } }", "server.port: not allowed with transport \"unix\"")]
        [InlineData("{ 'version': 1, 'server': { 'transport': 'tcp', 'tls': {} } }", "server.tls: not allowed with transport \"tcp\"")]
        [InlineData("{ 'version': 1, 'server': { 'transport': 'udp', 'framing': { 'type': 'none' } } }", "server.framing: not allowed with transport \"udp\"")]
        [InlineData("{ 'version': 1, 'server': { 'transport': 'udp' }, 'onConnect': { 'reply': 'hi' } }", "onConnect: not allowed with transport \"udp\"")]
        [InlineData("{ 'version': 1, 'server': { 'dualMode': true } }", "server.dualMode: needs an IPv6 address")]
        [InlineData("{ 'version': 1, 'server': { 'framing': { 'type': 'lengthPrefix', 'prefixLength': 3 } } }", "server.framing.prefixLength: must be 1, 2 or 4")]
        [InlineData("{ 'version': 1, 'server': { 'transport': 'tls', 'tls': { 'certificate': 'missing-file.pfx' } } }", "server.tls.certificate: file not found")]
        public void FromJson_Should_Reject_Invalid_Configurations_Naming_The_Problem_And_Where(string json, string message)
        {
            var exception = Assert.Throws<FormatException>(() => MockServer.FromJson(Json(json)));

            Assert.Contains(message, exception.Message);
        }

        [Fact]
        public void FromJson_Should_Name_The_Resolved_Path_Of_A_Missing_Certificate_And_Reject_An_Unreadable_One()
        {
            var directory = Path.Combine(Path.GetTempPath(), "rony-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var json = Json("{ 'version': 1, 'server': { 'transport': 'tls', 'tls': { 'certificate': 'server.pfx' } } }");
                var missing = Assert.Throws<FormatException>(() => MockServer.FromJson(json, directory));
                Assert.Contains(Path.Combine(directory, "server.pfx"), missing.Message);

                File.WriteAllText(Path.Combine(directory, "server.pfx"), "not a certificate");
                var broken = Assert.Throws<FormatException>(() => MockServer.FromJson(json, directory));
                Assert.Contains("server.tls.certificate: cannot load the certificate", broken.Message);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void Null_Arguments_Should_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => MockServer.FromJson(null));
            Assert.Throws<ArgumentNullException>(() => MockServer.FromFile(null));
            Assert.Throws<FileNotFoundException>(() => MockServer.FromFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        }

        [Fact]
        public void FromJson_Should_Apply_Scope_And_Fail_On_Unmatched()
        {
            using var server = FromText("{ 'version': 1, 'stateScope': 'connection', 'failOnUnmatched': true }");

            Assert.Equal(StateScope.Connection, server.Mock.StateScope);
            Assert.True(server.Mock.FailOnUnmatched);
        }
    }
}
