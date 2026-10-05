# Standalone Server

The `rony` command-line tool runs the mock servers of Rony.Net without any .NET test code: start the server described by a
[configuration file](Configuration-Files), record a conversation with a real server and replay it. Use it to give a
front-end team, a script, a CI job or a Docker Compose setup a fake TCP, TLS, UDP or Unix socket service. [Scenarios](#scenarios)
below show the common uses step by step.

## Install
```console
dotnet tool install --global Rony.Net.Cli
rony --version
```
It needs the .NET 8 runtime or a newer one. `rony --help` and `rony <command> --help` list the options.

## Exit codes
| Code | Meaning |
|---|---|
| 0 | Success (for the long-running commands: stopped with Ctrl+C or SIGTERM) |
| 1 | Runtime failure, for example the port is already in use |
| 2 | Usage error (unknown command or option, missing value), an invalid or missing input file, or a Unix socket path that is too long or already exists |

Messages go to the error output, results and log lines to the standard output. The tool listens on `127.0.0.1` unless the
configuration file (or `--address`) says otherwise. Options are written `--name value` or `--name=value`.

## rony run
```console
rony run mock.json [--port <N>] [--address <ip>] [--journal <file>] [--keep <N>] [--control <N>] [--watch] [--quiet]
```
Loads the file with `MockServer.FromFile`, starts the server, prints where it listens and then every log line
(prefixed with the time) until you press Ctrl+C or send SIGTERM; then it stops the server and exits with 0. A second Ctrl+C (or SIGTERM) while it
is stopping ends the tool at once.
A file that is missing or invalid exits with 2 and the message of the [error](Configuration-Files#errors).

| Option | Description |
|---|---|
| `--port <N>` | Port, replacing `server.port` of the file (default: from the file); `0` lets the system pick one (it is printed). |
| `--address <ip>` | Address, replacing `server.address` of the file (default: from the file). |
| `--journal <file>` | Append every received request to the file, one JSON object per line: see [Journal](#journal). |
| `--keep <N>` | How many received requests and connection records the server keeps in memory (default 10000; `0` is unlimited). |
| `--control <N>` | Start the [control endpoint](#control-endpoint) on `127.0.0.1:<N>` (`0` lets the system pick one). |
| `--watch` | Reload the rules when the file changes: see [Reloading](#reloading-the-file). |
| `--quiet` | Do not print the log lines. |

So one file can serve on different ports. `--port` and `--address` are not allowed for a `unix` configuration (exit 2), and
`--address` must be an IPv6 address when the file sets `server.dualMode`. A `unix` configuration without `server.path` gets a
generated socket file in the temp directory, which is printed (`Listening on unix <path>`).

```json
{
  "version": 1,
  "server": { "transport": "tcp", "port": 0, "framing": { "type": "delimiter", "delimiter": "\n" } },
  "rules": [ { "request": "PING", "reply": "PONG" } ]
}
```
```console
$ rony run mock.json
Listening on tcp 127.0.0.1:58182
10:25:47.097 [Rony 10:25:47.092] #1 connected from 127.0.0.1:58183
10:25:47.105 [Rony 10:25:47.105] #1 received "PING" (matched "PING")
10:25:47.112 [Rony 10:25:47.112] #1 sent "PONG"
```

### Reloading the file
With `--watch` the tool reads the file every half second (polling, so it also works for a file bind-mounted into a container and
with editors that replace the file) and, when its text changed and has been the same for two checks (about a second after the save), calls `MockServer.ReloadFile`: the new rules apply at once, open
connections stay, and received requests and the scenario state are kept (see [Reloading](Configuration-Files#reloading)). It prints
`Reloaded <file>` (also with `--quiet`). A file with a mistake prints `<file>: <message>; keeping the previous rules.` once on the
error output and the old rules stay; fix the file and it is reloaded. A file that cannot be read at that moment is tried again.
The `server` section is not applied on reload: change of address, port, transport or framing needs a restart. `--watch` is for `run`, not `replay`.

## rony validate
```console
rony validate mock.json
```
Checks the file (`MockServer.ValidateFile`) without starting anything or opening a socket, so a `udp` port that is in use
is no problem. Prints `OK` and exits with 0, or prints the error and exits with 2. Handy in CI.

## rony record
```console
rony record --target api.test:5000 --out login.json --delimiter "\n"
```
Starts a [`RecordingProxy`](Record-and-Replay) to the target, prints `Recording on 127.0.0.1:41208 -> api.test:5000`, and
relays and logs everything until Ctrl+C or SIGTERM. Then it stops the proxy and saves the recording to `--out` (when no client connected, it prints a message and writes nothing). Point your
client at the printed port while it runs. The recording and the log contain everything sent through the proxy, including
credentials and tokens: review them before committing or sharing them. On Linux and macOS the output file is created readable by its owner only.

| Option | Description |
|---|---|
| `--target <host:port>` | The real server. Required. |
| `--out <file.json>` | Where to save the recording. Required. An existing file is not overwritten (exit 2) unless `--force` is given. |
| `--port <N>`, `--address <ip>` | Where the proxy listens; default a free port on `127.0.0.1`. |
| `--tls` | Clients connect to the proxy with TLS, using a generated self-signed certificate (clients must accept it). |
| `--target-tls` | The proxy connects to the target with TLS and validates its certificate normally. |
| `--target-insecure` | With `--target-tls`: accept any certificate of the target (a warning is printed). |
| `--quiet` | Do not print the log lines. |
| framing options | See below. Use the framing of the protocol to record one message per protocol message. |

## rony replay
```console
rony replay login.json --delimiter "\n"
```
Serves a recording as a mock server, like `server.Replay(Recording.Load(...))`, and prints `Listening on tcp 127.0.0.1:41209`.
Options: `--port <N>`, `--address <ip>`, `--tls` (a generated self-signed certificate), `--journal <file>`, `--keep <N>` and `--control <N>` (as for
`run`), `--quiet` and the framing options.
Use the same framing as when recording. An invalid recording exits with 2.

## Journal
`rony run` and `rony replay` write every received request to the `--journal` file as one line of JSON, so a test in any
language can check what the mock received:
```json
{"time":"2026-10-04T12:34:56.789+02:00","connection":1,"remote":"127.0.0.1:50123","matched":true,"text":"PING"}
```
| Property | Meaning |
|---|---|
| `time` | When the request was received, ISO 8601 with milliseconds and offset |
| `connection` | The connection number (the `#1` of the log); left out for UDP |
| `remote` | The client's address and port; left out when unknown |
| `matched` | Whether a rule answered the request |
| `text` or `base64` | The request body as text when it is valid UTF-8 without control characters other than CR, LF and tab, otherwise as Base64 |

The file is created if it is missing and appended to if it exists, UTF-8 without a BOM, and every line is flushed at once, so
another process can read it while the server runs (on Linux and macOS a new file is readable by its owner only; on Windows
a reader must open the file allowing a writer, for example `Get-Content` or `tail`, in .NET `FileShare.ReadWrite`). A file that
cannot be opened exits with 2 before anything listens; a journal file that this run created and that is still empty is deleted
when the server fails to load or start. If a write fails later, the tool reports it once on the error output and
stops journaling; the server keeps running. The journal contains everything the clients sent, including credentials, so
treat it like the log. `rony record` has no journal: it saves its recording.

## Control endpoint
`rony run` and `rony replay` with `--control <N>` open a second, small server for your test: over one TCP connection it can
ask what the mock received, forget that, and read or set the scenario state, from any language. The tool prints it after the
`Listening on` line:
```console
Listening on tcp 127.0.0.1:41209
Control on 127.0.0.1:41210
```
The endpoint listens on `127.0.0.1` only, whatever `--address` says, and has no authentication, so every program on the machine
can use it; in a container it is reachable only from inside the container. It starts before the tool prints anything and
stops before the mock server does. `--control` must differ from the port of the mock server (a TCP or TLS server; exit 2). A control port that cannot be bound exits with 1 and the mock server is stopped too.

The protocol is plain TCP, UTF-8: one JSON object per line is a command, one JSON object per line is the reply. The connection
stays open for more commands, and several may be open at once. Every reply has `ok`; an error is
`{"ok":false,"error":"<message>"}` and does not close the connection. A trailing `\r` is ignored, and a command line longer than 1 MiB closes that control connection.

| Command | Reply |
|---|---|
| `{"command":"requests"}` | `{"ok":true,"requests":[<entry>,...],"last":<seq>}`: all kept requests, oldest first |
| `{"command":"requests","after":<seq>}` | the same, only the entries with a sequence number greater than `<seq>` |
| `{"command":"clear"}` | `{"ok":true}`; forgets the kept requests, the sequence numbers keep counting |
| `{"command":"state"}` | `{"ok":true,"state":"<current>"}` |
| `{"command":"state","set":"<name>"}` | sets the scenario state, then replies like the line above |

An entry is the [journal](#journal) line of the request with `seq` as its first property. `seq` starts at 1 and counts every
request since the tool started, also across `clear`; `last` is the number of the newest request ever received (0 when there
is none), so `"after": <last>` returns only what arrives later. At most `--keep` entries are kept (`0` is unlimited). The state
commands use the server-wide state; with `"stateScope": "connection"` they reply with an error. Unknown commands, unknown
properties, invalid JSON and an `after` that is not a non-negative whole number are error replies.

```text
$ printf '%s\n' '{"command":"requests"}' '{"command":"state","set":"authenticated"}' | nc 127.0.0.1 41210
{"ok":true,"requests":[{"seq":1,"time":"2026-10-04T12:34:56.789+02:00","connection":1,"remote":"127.0.0.1:50123","matched":true,"text":"PING"}],"last":1}
{"ok":true,"state":"authenticated"}
```

## Framing options
For `record` and `replay`, at most one; the default is no framing.

| Option | Messages |
|---|---|
| `--delimiter <text>` | end with this text. `\n`, `\r`, `\t`, `\\`, `\0` and `\xNN` (one byte in hex) are interpreted. |
| `--length-prefix <1\|2\|4>` | start with a big-endian length of 1, 2 or 4 bytes |
| `--stx-etx` | are wrapped in STX (0x02) and ETX (0x03) |

For other framings in a `run` configuration, see [Configuration Files](Configuration-Files#framing).

## Scenarios
Complete walk-throughs for what the tool is used for. The commands are for a POSIX shell (Linux, macOS, Git Bash, WSL); the
differences for PowerShell are noted where they matter. The configuration files are exercised by the tests of the tool.

### Stand in for a dependency during local development
Your application needs a TCP service (a price service, a device, a legacy system) that is not available on your machine. Describe
the part of it your application uses and run that.

`mocks/shop.json`:
```json
{
  "version": 1,
  "server": { "port": 4000, "framing": { "type": "delimiter", "delimiter": "\n" } },
  "onConnect": { "reply": "READY" },
  "rules": [
    { "request": "GET price:42", "reply": "19.99" },
    { "match": "^GET (\\S+)$", "reply": "NOT FOUND $1" }
  ]
}
```
```console
$ rony run mocks/shop.json
Listening on tcp 127.0.0.1:4000
```
Point your application at `127.0.0.1:4000`. To try the server by hand, use `nc` (or `telnet`) in a second terminal and type
the lines after `READY`:
```console
$ nc 127.0.0.1 4000
READY
GET price:42
19.99
GET color
NOT FOUND color
```
The first terminal shows what happened to every connection and request, so you also see what your application sends:
```console
11:16:58.959 [Rony 11:16:58.959] #1 connected from 127.0.0.1:54896
11:16:58.970 [Rony 11:16:58.970] #1 received "GET price:42" (matched "GET price:42")
11:16:58.971 [Rony 11:16:58.971] #1 sent "19.99"
11:16:58.972 [Rony 11:16:58.972] #1 received "GET color" (matched /^GET (\S+)$/)
```
Edit the file and start the tool again to change an answer (the file is read once at start). The exact request wins over the
pattern; see [Request Matching](Request-Matching) for the order.

### A mock for a team that does not use .NET
The mock is a file, so the team needs the tool, not .NET knowledge. Commit `mocks/shop.json` to the repository as the shared
artifact; everybody starts it the same way.
- With the .NET runtime on the machine: `dotnet tool install --global Rony.Net.Cli`, then `rony run mocks/shop.json`. To pin the
  version for the whole repository, use a local tool: `dotnet new tool-manifest`, `dotnet tool install Rony.Net.Cli`, commit
  `.config/dotnet-tools.json`, and everybody runs `dotnet tool restore` once and `dotnet rony run mocks/shop.json`.
- Without any .NET on the machine, use [Docker](#docker): build the image once, then
  `docker run --rm -p 127.0.0.1:4000:4000 -v "$PWD/mocks:/config" rony run /config/shop.json`. For this, the file must
  set `"address": "0.0.0.0"` (see [In a container](#in-a-container-next-to-the-system-under-test)); without an address the server is unreachable from outside the container.

There is no published image yet (see [Known Issues](Known-Issues#standalone-server-rony)): build it from the `Dockerfile` of the
Rony.Net repository, for example `docker build -t rony https://github.com/archofthings/Rony.Net.git`.

### Record a real server once, replay it offline
You have access to the real service only now (a VPN, a lab device, a test environment). Record a session with your application,
and work against the recording later, offline.
```console
$ rony record --target api.test:5000 --out login.json --port 5001 --delimiter "\n"
Recording on 127.0.0.1:5001 -> api.test:5000
```
Point your application at `127.0.0.1:5001`, do what you want to capture, and press Ctrl+C:
```console
Saved 1 connection to login.json
```
Later, without the real server:
```console
$ rony replay login.json --port 5001 --delimiter "\n"
Listening on tcp 127.0.0.1:5001
```
- Give `record` and `replay` the same [framing option](#framing-options) (here `--delimiter "\n"`, which a POSIX shell
  and PowerShell both pass as the two characters backslash and `n`; the tool turns them into a newline). Without it, every read is a recorded message
  and the replay only works when the client sends its bytes in the same pieces.
- Use the client as it normally behaves. A client that speaks before the greeting of the server has arrived gets that greeting
  recorded as the answer to its first message.
- **Review `login.json` before you commit it.** It contains everything that went through the proxy, including passwords and
  tokens. Edit the file by hand to remove or change them: the format is described in [Record and Replay](Record-and-Replay#the-file-format).
- A replay answers every request on its own. When the answers depend on what happened before (a login, a counter), the replay
  is right only if the client sends its requests in the recorded order. For anything else, write the exchanges as rules of a
  [configuration file](Configuration-Files) with states (see the stateful scenario below); there is no converter.

### Record or replay a TLS service
For a service that speaks TLS, tell the proxy to use TLS on both sides:
```console
$ rony record --target api.test:5443 --target-tls --tls --out api.json --port 5001
Recording on 127.0.0.1:5001 -> api.test:5443
```
- `--target-tls` makes the proxy connect to the real server with TLS and validate its certificate normally. A target with a
  private or self-signed certificate fails with `UntrustedRoot` in the log (the client is disconnected). Only for a service you
  trust on a network you trust, add `--target-insecure`; the tool prints `Warning: --target-insecure accepts any certificate of the target.`
- `--tls` makes the proxy (and `rony replay --tls`) speak TLS to your client. The certificate is generated at every start: a
  self-signed one for `CN=localhost`, valid for about a week, that no system trusts. Your client has to accept it: in its
  test configuration, switch off certificate validation (for example `curl -k` or `NODE_TLS_REJECT_UNAUTHORIZED=0`; `openssl s_client`
  reports `self-signed certificate` and connects anyway). Do not switch it off in production code. You cannot
  choose the certificate of `--tls`; for a trusted certificate use a [configuration file](Configuration-Files#tls) with your own PFX file.
- Serve a recording over TLS the same way: `rony replay api.json --tls --port 5001`.

### In CI
Check that the configuration files in the repository are valid, so a typo is found by the pipeline and not at the first run:
```console
for f in mocks/*.json; do rony validate "$f" || exit 1; done
```
`validate` prints `OK` and exits with 0, or prints the error and exits with 2 (see [exit codes](#exit-codes)). It does not
start anything and opens no socket.

To test a client that is not written in .NET against the mock, start the tool in the background, wait until it listens, run
the tests and stop it with SIGTERM:
```console
rony run mocks/shop.json --quiet > rony.log 2>&1 &
RONY_PID=$!
until grep -q "Listening on" rony.log || ! kill -0 $RONY_PID; do sleep 0.2; done   # also printed with --quiet
./run-client-tests.sh                       # connects to 127.0.0.1:4000
kill -TERM $RONY_PID; wait $RONY_PID        # exits with 0 after stopping the server
```
Use a fixed port in the file (the tests need to know it) and make sure no other job on the agent uses it. In a
pipeline, install the tool first: `dotnet tool install --global Rony.Net.Cli --version <version>`. If the tool cannot start
(port in use, invalid file), it exits with 1 or 2, and the loop above ends instead of waiting.

For tests written in .NET, do not start the tool: use the library, which gives you a free port per test and `Should()`
assertions (see [Getting Started](Getting-Started) and [Configuration Files](Configuration-Files)).

### In a container next to the system under test
Build the image once from the `Dockerfile` (see [Docker](#docker)). The server in the file must listen on all interfaces, or
the other containers cannot reach it: `mocks/shop.json` with `"address": "0.0.0.0"` (or pass `--address 0.0.0.0` to `run`).
```console
docker run --rm -p 127.0.0.1:4000:4000 -v "$PWD/mocks:/config" rony run /config/shop.json
```
In PowerShell write the volume as `-v "${PWD}/mocks:/config"` (`"$PWD:/config"` is read as a drive-qualified variable).
With Docker Compose, the application reaches the mock by the name of its service and the port needs no publishing:
```yaml
services:
  shop-mock:
    image: rony                      # docker build -t rony <checkout of Rony.Net>
    command: ["run", "/config/shop.json"]
    volumes:
      - ./mocks:/config:ro
  app:
    build: .
    depends_on:
      - shop-mock
    environment:
      SHOP_ADDRESS: shop-mock:4000   # the port of the file
```
`depends_on` waits for the container to start, not for the tool to listen (which takes a moment), so the application should
retry its first connection. Add `ports: ["127.0.0.1:4000:4000"]` to the mock to reach it from the host too. Stop it with
`docker compose down` (SIGTERM).

The Docker and Docker Compose instructions have not been run by the authors of this page (Docker is not part of the test setup of the repository); check them once in your environment.

### A stateful or failing server
To see how a client copes with a server that needs a login, answers "busy" a few times, answers slowly and drops the connection,
describe that server in a file. `stateScope: connection` gives every connection its own login state.

`mocks/flaky.json`:
```json
{
  "version": 1,
  "stateScope": "connection",
  "server": { "port": 4001, "framing": { "type": "delimiter", "delimiter": "\n" } },
  "onConnect": { "reply": "HELLO" },
  "onUnmatched": { "reply": "ERR login first" },
  "rules": [
    { "request": "LOGIN bob secret", "reply": "OK", "goTo": "authenticated" },
    { "request": "FETCH", "state": "authenticated",
      "replies": [ { "reply": "ERR busy" }, { "reply": "ERR busy" }, { "reply": "DATA 42", "afterMs": 2000 } ] },
    { "request": "CRASH", "state": "authenticated", "disconnect": true }
  ]
}
```
```console
$ nc 127.0.0.1 4001
HELLO
FETCH
ERR login first
LOGIN bob secret
OK
FETCH
ERR busy
FETCH
ERR busy
FETCH
DATA 42
CRASH
```
The last `FETCH` is answered after two seconds, and `CRASH` closes the connection without an answer. The answers of a
sequence are used in order across all connections (the last one repeats), so restart the tool to start again from `ERR busy`.
Point a client at it to see its retry, timeout and reconnect behaviour. More on the ideas in [Stateful Scenarios](Stateful-Scenarios),
[Response Sequences](Response-Sequences) and [Simulating Failures](Simulating-Failures); the faults that need code
(truncated or throttled responses, refused connections) are not available here.

### A Unix domain socket or UDP
The same file format serves other transports. A Unix domain socket (without `path`, the tool generates one and prints it):
```json
{
  "version": 1,
  "server": { "transport": "unix", "path": "/tmp/rony-demo.sock", "framing": { "type": "delimiter", "delimiter": "\n" } },
  "rules": [ { "request": "PING", "reply": "PONG" } ]
}
```
```console
$ rony run unix.json
Listening on unix /tmp/rony-demo.sock
$ nc -U /tmp/rony-demo.sock
PING
PONG
```
The socket file is removed when the tool stops. Without `"path"`, the line reads `Listening on unix /tmp/rony-1a2b3c4d.sock`. A UDP server, where every datagram is one message (there is no framing):
```json
{
  "version": 1,
  "server": { "transport": "udp", "port": 5000 },
  "rules": [ { "request": "PING", "reply": "PONG" } ]
}
```
```console
$ rony run udp.json
Listening on udp 127.0.0.1:5000
$ printf PING | nc -u -w1 127.0.0.1 5000
PONG
```
Use `printf`, not `echo`: `echo` adds a newline, which makes the datagram `PING\n` and the rule does not match it.
Docker publishes UDP ports with `-p 5000:5000/udp`. Recording and replay are for TCP and TLS only.

### Share one file between the tool and your tests
The same file can start the standalone server for the other team and be the server of a .NET test, so the two never disagree.
Use `"port": 0` in a file that is shared this way (the file of the first scenario with `"port": 0`): a test then gets a free
port of its own (`server.Port`) and several tests can run at once, while the tool prints the port it got. A container needs a
fixed port and `"address": "0.0.0.0"`, so it gets a second file.
```csharp
using var server = MockServer.FromFile("mocks/shop.json");   // the file that `rony run mocks/shop.json` serves, with "port": 0
server.Start();

// ... run the code under test against 127.0.0.1:server.Port ...

server.Should().HaveReceived("GET price:42", Times.Once());
```
Rules added in code are possible too, see [Configuration Files](Configuration-Files#using-the-server-from-code).

## Docker
The repository has a `Dockerfile` for the tool (an SDK image builds it, the small .NET runtime image runs it as the
non-root user of the image):
```console
docker build -t rony .
docker run --rm -p 127.0.0.1:4000:4000 -v "$PWD:/config" rony             # runs /config/mock.json
docker run --rm -v "$PWD:/config" rony validate /config/mock.json
```
The default command is `run /config/mock.json`; mount the folder with your files at `/config`. The server must listen on
all interfaces inside the container, because the default `127.0.0.1` cannot be reached from outside it:
```json
{ "version": 1, "server": { "address": "0.0.0.0", "port": 4000 }, "rules": [ { "request": "PING", "reply": "PONG" } ] }
```
The image exposes no port by itself because the port comes from the file: publish it with `-p 127.0.0.1:<host port>:<port in the file>`,
which keeps it on the host's loopback. Drop the `127.0.0.1:` part (`-p 4000:4000`) only when other machines should reach it, and
see [Limits and security](#limits-and-security) first. A fixed port is needed here, not `0`. Stop the container with `docker stop` (SIGTERM) or Ctrl+C.

To record inside a container, pass `--address 0.0.0.0` so the proxy is reachable from outside, and mount a writable folder
for `--out`. The tool checks before it starts that `--out` can be written (exit 2 otherwise) and saves through a new temporary
file with a random name next to `--out` (`<out>.<random>.tmp`, created exclusively and readable by its owner only on Linux and macOS), which is
then moved over `--out`, so a failed save never destroys an existing file and a file or symlink that already exists is never followed.

## Limits and security
The tool is for development and test networks, not for hostile ones.
- It listens on loopback unless the file or `--address` says otherwise.
- `run` and `replay` keep the last 10000 received requests and connection records in memory (`--keep <N>` changes it, `0` is
  unlimited); `record` keeps its whole recording in memory until it stops, so a long recording grows. There is no limit on
  the number of connections, on idle time or on the handshake time. With per-connection state on UDP, one small entry per
  distinct client address is kept for the whole run.
- A server from a configuration file limits a single buffered message to 16 MiB (`server.maxBufferedBytes`); `rony replay` does the same.
- The log contains the full request and response bodies unless you pass `--quiet`.
- With `--tls`, or `tls` and `requireClientCertificate` in a file, any client certificate is accepted.
- `rony record` on a non-loopback address is an open relay to the target.
- The `--control` endpoint is loopback only and has no authentication: any local program can read the received requests (and
  with them credentials) and change the state.

## What the tool cannot do
It can only do what a configuration file or a recording can express. Response functions and predicates, truncated, corrupted,
chunked or throttled responses, refused connections, failing TLS handshakes, client certificate validators, custom framings and
listeners, and assertions (`Should()`) need code; see [Not available in files](Configuration-Files#not-available-in-files).
The TLS certificate of `--tls` is generated and cannot be chosen (use `"transport": "tls"` with a PFX file in a configuration
file for that), and there is no UDP recording or replay. Only the address and port of the file can be overridden (`--port`, `--address`); edit the file for anything else.

Tests of the tool: `tests/Rony.Net.Cli.Tests` (every configuration on this page is one of them; the scenario tests use port `0` and a temporary socket path instead of the fixed ones shown).
