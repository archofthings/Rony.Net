# Configuration Files

Describe a mock server in a JSON file instead of code: where it listens, how messages are framed, and the rules that
answer requests. `MockServer.FromFile` turns the file into an ordinary `MockServer`, so everything else (`Start()`,
`server.Should()`, adding rules in code) works as usual.

```csharp
// mock.json: { "version": 1, "rules": [ { "request": "PING", "reply": "PONG" } ] }
using var server = MockServer.FromFile("mock.json");   // listener and rules from the file
server.Start();
```

| Method | Description |
|---|---|
| `MockServer.FromFile(path)` | Reads the file. Relative paths inside it (the certificate) are resolved against the file's directory. A missing file throws `FileNotFoundException`, a missing directory `DirectoryNotFoundException`. |
| `MockServer.FromJson(json)` | The same for JSON text. Relative paths are resolved against the current directory. |
| `MockServer.FromJson(json, baseDirectory)` | The same, resolving relative paths against `baseDirectory` (`null` means the current directory). |

The server is created but not started. A null argument throws `ArgumentNullException`; anything wrong with the content
throws a `FormatException` (see [Errors](#errors)). The file format described here is **version 1**.

To run such a file without any .NET code, use the `rony` command-line tool: [Standalone Server](Standalone-Server).

## A complete example
```json
{
  "version": 1,
  "server": {
    "transport": "tcp",
    "port": 0,
    "framing": { "type": "delimiter", "delimiter": "\n" }
  },
  "onConnect": { "reply": "220 mail.test ready" },
  "onUnmatched": { "reply": "500 unknown command" },
  "rules": [
    { "request": "PING", "reply": "PONG" },
    { "match": "^HELO (\\w+)$", "reply": "250 hello $1" },
    { "json": { "type": "login", "user": { "name": "bob" } }, "reply": "{\"ok\":true}", "goTo": "authenticated" },
    { "request": "LIST", "state": "authenticated",
      "replies": [ { "reply": "a" }, { "reply": "b", "afterMs": 50 }, { "disconnect": true } ] },
    { "request": "LIST", "reply": "530 log in first" },
    { "request": "QUIT", "reply": "221 bye", "disconnect": true }
  ]
}
```
Plain JSON: no comments and no trailing commas. Remember to escape backslashes and quotes inside JSON strings (`\\w`, `\"`).

## Top level
| Property | Description |
|---|---|
| `version` | Required, must be `1`. |
| `server` | Optional object: the listener, see [Server](#server). Without it: TCP on `127.0.0.1`, port `0`, no framing. |
| `stateScope` | `"server"` (default) or `"connection"`: whether the [scenario state](Stateful-Scenarios) is shared or kept per connection. |
| `failOnUnmatched` | `true` sets `Mock.FailOnUnmatched` (see [unmatched requests](Request-Matching#unmatched-requests)). Default `false`. |
| `onConnect` | A [response](#responses) sent as soon as a client connects (TCP, TLS and Unix; not with UDP). Like `Mock.OnConnect()`. |
| `onUnmatched` | A [response](#responses) for requests no rule matches. Like `Mock.OnUnmatched()`. |
| `rules` | Array of [rules](#rules). Missing or empty is fine: a server with only `onUnmatched`, or one you configure further in code. |

Any other property is an error, so a typo (`"replys"`) is found instead of silently ignored. This is the opposite of
[recordings](Record-and-Replay), which ignore unknown properties.

## Server
| Property | Applies to | Description |
|---|---|---|
| `transport` | all | `"tcp"` (default), `"tls"`, `"udp"` or `"unix"`. |
| `address` | tcp, tls, udp | An IP address; default `127.0.0.1` (also for UDP). |
| `port` | tcp, tls, udp | `0` to `65535`; default `0`, the OS picks a free port: read `server.Port` after `Start()`. A `udp` server binds its port when the file is loaded (as with `new UdpServer(...)`), so a port in use fails at `FromFile`/`FromJson`. |
| `dualMode` | tcp, tls, udp | `true` also accepts IPv4 clients on an IPv6 address such as `"::"`. Needs an IPv6 `address`. |
| `path` | unix | The socket file. Omitted: a unique file in the temp directory. |
| `keepAlive` | tcp, tls, unix | Keep connections open after a response; default `true`. |
| `framing` | tcp, tls, unix | How the stream is split into messages, see [Framing](#framing). Default: none. |
| `tls` | tls | Optional object, see [TLS](#tls). |

A property that does not apply to the chosen transport is an error (`server.path: not allowed with transport "tcp"`).
For what the transports do, see [Servers](Servers), [SSL and TLS](SSL-and-TLS) and [Connections and Framing](Connections-and-Framing).

### Framing
| `type` | Other properties |
|---|---|
| `"none"` | none (the default) |
| `"delimiter"` | `delimiter`: a [body](#bodies), not empty |
| `"lengthPrefix"` | `prefixLength` 1, 2 or 4 (default 4), `bigEndian` (default `true`), `includesPrefix` (default `false`) |
| `"fixedLength"` | `length` (required, positive), `padding` 0 to 255 (default 0) |
| `"startEnd"` | `start` and `end`, both required, 0 to 255 |
| `"stxEtx"` | none (`start` 2, `end` 3) |

They are the framings of `MessageFraming`; custom framings are not available in files.

### TLS
```json
{ "version": 1,
  "server": { "transport": "tls", "tls": { "protocol": "tls12" } },
  "rules": [ { "request": "PING", "reply": "PONG" } ] }
```
| Property | Description |
|---|---|
| `certificate` | A PFX file with a private key, relative to the config file. Omitted: a self-signed `localhost` certificate is generated with `TestCertificate.CreateSelfSigned()`. |
| `password` | Password of the PFX file. Needs `certificate`. |
| `protocol` | `"none"` (default, the OS decides), `"tls12"` or `"tls13"`. |
| `requireClientCertificate` | `true` asks for a client certificate (mutual TLS); default `false`. |

The generated certificate is not reachable from the returned server, so a test client either accepts any certificate or
you use a PFX file you created and trust yourself. The certificate (generated or loaded) is disposed with the server.
A missing file (the message names the resolved path) and a file that cannot be loaded are errors.

## Rules
A rule has **exactly one matcher**, optionally a `state`, and a [response](#responses) or a [sequence](#sequences).

| Matcher | Matches | Same as |
|---|---|---|
| `"request"` | exactly this [body](#bodies); an empty request (`""`) matches any request | `Mock.Send("...")` / `Mock.Send(bytes)` |
| `"match"` | a regular expression (unanchored, so use `^` and `$`) on the request text; `$1`, `${name}` and `$0` in a text `reply` are replaced with the capture groups (.NET substitution syntax: a literal dollar before a digit or brace is written `$$`) | `Mock.Send(Regex)` with `ReceiveMatch` |
| `"json"` | a request that is JSON and contains every property given, recursively for objects; arrays and scalars must be equal; extra properties are fine | `Mock.SendJson(...)` |
| `"state"` | not a matcher: limits the rule to a scenario state | `Mock.InState("...")` |

```json
{
  "version": 1,
  "rules": [
    { "request": "PING", "reply": "PONG" },
    { "request": { "base64": "AQID" }, "reply": { "base64": "BAUG" } },
    { "match": "^LOGIN (\\w+)$", "reply": "WELCOME $1" },
    { "json": { "type": "login", "user": { "name": "bob" } }, "reply": "{\"ok\":true}" }
  ]
}
```
Matching order is the one of the code API: exact requests win over patterns and JSON rules (checked in file order),
which win over an "any request" rule, and a rule for the current state wins over one without a state; see
[Request Matching](Request-Matching). A regular expression gets a match timeout of one second, so a pattern that runs away
does not match (and is logged) instead of hanging the server. Two rules for the same exact request (in the same state)
are an error.

### Bodies
Everywhere a request, reply or delimiter is expected:

| Written as | Meaning |
|---|---|
| `"text"` | the string, UTF-8 encoded |
| `{ "text": "..." }` | the same |
| `{ "base64": "AQID" }` | the decoded bytes |

## Responses
A response is a set of these properties, used inline in a rule, in `onConnect` and in `onUnmatched`:

| Property | Description | Same as |
|---|---|---|
| `reply` | a [body](#bodies) to send; `"reply": ""` sends nothing, so the client waits as with `noReply` | `Receive(...)` |
| `disconnect` | `true` closes the connection after the reply, or right away without a `reply` | `AndDisconnect()` / `Disconnect()` |
| `reset` | `true` aborts the connection with a TCP reset, after the reply if there is one | `AndResetConnection()` / `ResetConnection()` |
| `noReply` | `true` accepts the request and stays silent | `NoReply()` |
| `afterMs` | wait this many milliseconds (whole number, zero or more) before the response | `After(...)` |
| `goTo` | move the [scenario](Stateful-Scenarios) to this state once the response is used | `GoTo(...)` |

A response needs one of `reply`, `disconnect`, `reset` or `noReply`. `reply` and `noReply` exclude each other, as do
`disconnect` and `reset`, and `noReply` cannot be combined with `disconnect` or `reset`.

### Sequences
`replies` is a non-empty array of responses used in order, one per matching request; the last one keeps repeating (like
`Then(...)`, see [Response Sequences](Response-Sequences)). Use either the inline properties or `replies`, not both. It
works in `onConnect` (a different greeting for every connection) and `onUnmatched` too.

```json
{
  "version": 1,
  "stateScope": "connection",
  "rules": [
    { "request": "LOGIN", "reply": "OK", "goTo": "authenticated" },
    { "request": "LIST", "state": "authenticated",
      "replies": [ { "reply": "first" }, { "reply": "second", "afterMs": 20 }, { "noReply": true } ] },
    { "request": "BYE", "reply": "bye", "disconnect": true }
  ]
}
```

## Using the server from code
The result is the ordinary `MockServer`: add rules, verify requests and read the port as always.
```csharp
using var server = MockServer.FromJson("""
    { "version": 1, "rules": [ { "request": "PING", "reply": "PONG" } ] }
    """);
server.Mock.Send("EXTRA").Receive("added in code");   // the mock is the ordinary one
server.Start();

// ... run the code under test ...

server.Should().HaveReceivedInOrder("PING", "EXTRA");
```

## Not available in files
Version 1 describes servers with fixed responses. These need code (or are not available): response functions and
predicates, truncated, corrupted, chunked and throttled responses, refused connections, failing TLS handshakes,
client certificate validators, custom framings and listeners, and `Log`.

## Errors
Mistakes throw a `FormatException` whose message starts with the place of the problem:

```csharp
// { "version": 1, "rules": [ { "request": "PING", "reply": "PONG" }, { "request": "LIST", "replys": "a" } ] }
MockServer.FromJson(json);   // FormatException: rules[1]: unknown property "replys"
```
Other examples:

| Message | Cause |
|---|---|
| `version: is missing; the only supported version is 1` | no `version` |
| `version: 2 is not supported; only version 1 is supported` | another value (here 2) |
| `rules[2]: "reply" and "replies" cannot both be set` | inline response and a sequence |
| `rules[0]: needs one of "request", "match" or "json"` | no matcher (two matchers: `"request" and "match" cannot both be set`) |
| `rules[0]: needs a response: "reply", "noReply", "disconnect" or "reset"` | a rule without a response |
| `rules[3].match: is not a valid regular expression: ...` | the regular expression error text follows |
| `rules[1].request.base64: is not valid base64` | |
| `rules[1]: A response is already configured for request ...` | the same request twice |
| `server.path: not allowed with transport "tcp"` | a server property for the wrong transport (also `tls` without the tls transport, `framing`, `keepAlive` and `onConnect` with UDP, `address`/`port`/`dualMode` with unix) |
| `server.tls.certificate: file not found: /full/path/server.pfx` | |
| `server.transport: "ftp" is not valid; use ...` | |

Text that is not JSON at all is reported by the JSON reader with the position of the error.
