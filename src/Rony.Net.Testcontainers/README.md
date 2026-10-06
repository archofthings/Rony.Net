# Rony.Net.Testcontainers

A [Testcontainers for .NET](https://dotnet.testcontainers.org) module for [Rony.Net](https://github.com/archofthings/Rony.Net): it starts
the `rony` tool (image `ghcr.io/archofthings/rony`, at the version of this package) from a configuration file and reads what the mock
received through the tool's control endpoint. Needs Docker where the test runs; built on Testcontainers 4.15.0 or later.

```csharp
await using var rony = new RonyBuilder()
    .WithConfigurationFile("mocks/shop.json")
    .Build();
await rony.StartAsync();

using var client = new TcpClient(rony.Hostname, rony.Port);
// ... the system under test talks to rony.Hostname:rony.Port ...
var requests = await rony.GetReceivedRequestsAsync();   // also ClearReceivedRequestsAsync, GetStateAsync, SetStateAsync
await rony.SetStateAsync("outage");
```

The configuration is copied into the container as `/config/mock.json`; the tool's address and port are overridden. `unix` configurations
are not supported, `udp` needs its own port binding, and files named in the configuration (a certificate) need `WithResourceMapping(...)`.

Documentation: [Standalone Server, "From a .NET test with Testcontainers"](https://github.com/archofthings/Rony.Net/wiki/Standalone-Server#from-a-net-test-with-testcontainers).
