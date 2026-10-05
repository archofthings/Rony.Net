# Rony.Net.Cli

The `rony` command-line tool of [Rony.Net](https://github.com/archofthings/Rony.Net): run a TCP, TLS, UDP or Unix socket
mock server from a JSON file, without writing any code. The repository has a Dockerfile to build a Docker image.

```console
dotnet tool install --global Rony.Net.Cli

rony run mock.json                  # start the server described by the file (Ctrl+C stops it); --port and --address override the file
rony run mock.json --journal requests.jsonl   # also append every received request to a file, one JSON line each; --keep <N> limits what is kept in memory
rony run mock.json --control 0      # a control port on 127.0.0.1 (printed): ask for the received requests, clear them, read or set the state
rony validate mock.json             # check the file without starting anything or opening a socket
rony record --target api.test:5000 --out login.json --delimiter "\n"   # record a real server through a proxy
rony replay login.json --delimiter "\n"                                # serve the recording as a mock
```

Typical uses (walk-throughs in the wiki):
- **Stand in for a service during development:** write a JSON file, `rony run mock.json`, point your application at the printed port and try it with `nc`.
- **Record once, replay offline:** `rony record` while your application talks to the real server (Ctrl+C saves), then `rony replay` the file; add `--tls` / `--target-tls` for TLS services.
- **Check what the mock received from any language:** `--journal <file>` (also for `replay`) appends every received request as one line of JSON; `--control <N>` opens a loopback control port that answers JSON commands for the received requests and the scenario state.
- **In CI or Docker:** `rony validate` the files in a pipeline, or run the server in a container next to the application (`docker run`, Docker Compose).

Needs the .NET 8 runtime or newer. Exit codes: 0 success, 1 runtime failure (port in use), 2 usage error or invalid file.
The server listens on `127.0.0.1` unless the configuration file (or `--address`) says otherwise.

Documentation: [Standalone Server](https://github.com/archofthings/Rony.Net/wiki/Standalone-Server) (commands, options and scenarios) and
[Configuration Files](https://github.com/archofthings/Rony.Net/wiki/Configuration-Files).
