# Standalone Server

The `rony` command-line tool runs the mock servers of Rony.Net without any .NET test code: start the server described by a
[configuration file](Configuration-Files), record a conversation with a real server and replay it. Use it to give a
front-end team, a script, a CI job or a Docker Compose setup a fake TCP, TLS, UDP or Unix socket service.

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
| 2 | Usage error (unknown command or option, missing value) or an invalid or missing input file |

Messages go to the error output, results and log lines to the standard output. The tool listens on `127.0.0.1` unless the
configuration file (or `--address`) says otherwise. Options are written `--name value` or `--name=value`.

## rony run
```console
rony run mock.json [--quiet]
```
Loads the file with `MockServer.FromFile`, starts the server, prints where it listens and then every log line
(prefixed with the time) until you press Ctrl+C or send SIGTERM; then it stops the server and exits with 0. A second Ctrl+C (or SIGTERM) while it
is stopping ends the tool at once.
`--quiet` hides the log lines. The address, port and transport come from the file only; use `"port": 0` to let the system pick one
(it is printed). A `unix` configuration must set `server.path` (exit 2 otherwise), because the tool cannot show a generated temporary path. A file that is missing or invalid exits with 2 and the message of the [error](Configuration-Files#errors).

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

## rony validate
```console
rony validate mock.json
```
Loads the file without starting anything. Prints `OK` and exits with 0, or prints the error and exits with 2. Handy in CI.
A `udp` configuration binds its port when it is loaded (like `new UdpServer(...)`), so `validate` reports
`The file is valid, but its UDP port is in use: <error>` and exits with 1 when the port is taken.

## rony record
```console
rony record --target api.test:5000 --out login.json --delimiter "\n"
```
Starts a [`RecordingProxy`](Record-and-Replay) to the target, prints `Recording on 127.0.0.1:41208 -> api.test:5000`, and
relays and logs everything until Ctrl+C or SIGTERM. Then it stops the proxy and saves the recording to `--out`. Point your
client at the printed port while it runs.

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
Options: `--port <N>`, `--address <ip>`, `--tls` (a generated self-signed certificate), `--quiet` and the framing options.
Use the same framing as when recording. An invalid recording exits with 2.

## Framing options
For `record` and `replay`, at most one; the default is no framing.

| Option | Messages |
|---|---|
| `--delimiter <text>` | end with this text. `\n`, `\r`, `\t`, `\\`, `\0` and `\xNN` (one byte in hex) are interpreted. |
| `--length-prefix <1\|2\|4>` | start with a big-endian length of 1, 2 or 4 bytes |
| `--stx-etx` | are wrapped in STX (0x02) and ETX (0x03) |

For other framings in a `run` configuration, see [Configuration Files](Configuration-Files#framing).

## Docker
The repository has a `Dockerfile` for the tool (an SDK image builds it, the small .NET runtime image runs it as the
non-root user of the image):
```console
docker build -t rony .
docker run --rm -p 4000:4000 -v "$PWD:/config" rony             # runs /config/mock.json
docker run --rm -v "$PWD:/config" rony validate /config/mock.json
```
The default command is `run /config/mock.json`; mount the folder with your files at `/config`. The server must listen on
all interfaces inside the container, because the default `127.0.0.1` cannot be reached from outside it:
```json
{ "version": 1, "server": { "address": "0.0.0.0", "port": 4000 }, "rules": [ { "request": "PING", "reply": "PONG" } ] }
```
The image exposes no port by itself because the port comes from the file: publish it with `-p <host port>:<port in the file>`.
A fixed port is needed here, not `0`. Stop the container with `docker stop` (SIGTERM) or Ctrl+C.

To record inside a container, pass `--address 0.0.0.0` so the proxy is reachable from outside, and mount a writable folder
for `--out`. The tool checks before it starts that `--out` can be written (exit 2 otherwise) and saves through a temporary
file `<out>.tmp`, so a failed save never destroys an existing file.

## What the tool cannot do
It can only do what a configuration file or a recording can express. Response functions and predicates, truncated, corrupted,
chunked or throttled responses, refused connections, failing TLS handshakes, client certificate validators, custom framings and
listeners, and assertions (`Should()`) need code; see [Not available in files](Configuration-Files#not-available-in-files).
The TLS certificate of `--tls` is generated and cannot be chosen (use `"transport": "tls"` with a PFX file in a configuration
file for that), and there is no UDP recording or replay. There are no options to override the file: edit the file instead.

Tests of the tool: `tests/Rony.Net.Cli.Tests` (the configuration above is one of them).
