# Rony.Net.Cli

The `rony` command-line tool of [Rony.Net](https://github.com/archofthings/Rony.Net): run a TCP, TLS, UDP or Unix socket
mock server from a JSON file, without writing any code. The repository has a Dockerfile to build a Docker image.

```console
dotnet tool install --global Rony.Net.Cli

rony run mock.json                  # start the server described by the file (Ctrl+C stops it)
rony validate mock.json             # check the file without starting anything
rony record --target api.test:5000 --out login.json --delimiter "\n"   # record a real server through a proxy
rony replay login.json --delimiter "\n"                                # serve the recording as a mock
```

Needs the .NET 8 runtime or newer. Exit codes: 0 success, 1 runtime failure (port in use), 2 usage error or invalid file.
The server listens on `127.0.0.1` unless the configuration file (or `--address`) says otherwise.

Documentation: [Standalone Server](https://github.com/archofthings/Rony.Net/wiki/Standalone-Server) and
[Configuration Files](https://github.com/archofthings/Rony.Net/wiki/Configuration-Files).
