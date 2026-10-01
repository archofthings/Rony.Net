# Custom Listeners

`MockServer` gets its requests from an `IListener`. The built-in listeners are `TcpServer`, `TcpServerSsl` and
`UdpServer`. To use the matching, sequences, failures and verification features over a different transport, such as
a named pipe, a serial port, a WebSocket or memory, implement `IListener` yourself.

## The interface
```csharp
public interface IListener : IDisposable
{
    IPAddress Address { get; }
    int Port { get; }
    bool Active { get; }

    Task<Message> ReceiveAsync();                       // wait for the next request
    Task ReplyAsync(string response, object sender);    // answer it
    Task ReplyAsync(byte[] response, object sender);
    Task CloseAsync(object sender);                     // hang up on that sender

    void Start();
    void Stop();
}
```

What `MockServer` expects:
- **`ReceiveAsync()`** returns the next request as a `Message`. `Message.Body` holds the bytes, `Message.Sender` is any
  object that tells you who to answer, and `Message.RemoteEndPoint` (optional) is recorded with the request.
  Once the listener is stopped, it should throw `ObjectDisposedException`; that ends the server's receive loop.
- **`ReplyAsync(response, sender)`** sends a response to the sender of a request. The response can be empty.
- **`CloseAsync(sender)`** ends the conversation with that sender. If your transport has no connections, do nothing.
- **Order:** requests with the same `Sender` are handled one at a time, in order. Requests from different senders are handled concurrently.
- An exception from `ReceiveAsync()` other than `ObjectDisposedException` is ignored, and the server keeps receiving.

## Example: a server with no network
This listener passes requests and responses through memory. Each request's sender is the `TaskCompletionSource`
the caller is waiting on:

```csharp
public sealed class InMemoryListener : IListener
{
    private readonly Channel<Message> _requests = Channel.CreateUnbounded<Message>();
    private CancellationTokenSource _stopped = new();

    public IPAddress Address => IPAddress.None;
    public int Port => 0;
    public bool Active { get; private set; }

    public void Start()
    {
        _stopped = new CancellationTokenSource();
        Active = true;
    }

    public void Stop()
    {
        Active = false;
        _stopped.Cancel();
    }

    public void Dispose() => Stop();

    public async Task<Message> ReceiveAsync()
    {
        try
        {
            return await _requests.Reader.ReadAsync(_stopped.Token);
        }
        catch (OperationCanceledException)
        {
            // MockServer stops its receive loop on ObjectDisposedException.
            throw new ObjectDisposedException(nameof(InMemoryListener));
        }
    }

    // The sender of each request is the TaskCompletionSource its caller is waiting on.
    public Task ReplyAsync(string response, object sender) => ReplyAsync(response.GetBytes(), sender);

    public Task ReplyAsync(byte[] response, object sender)
    {
        ((TaskCompletionSource<byte[]>)sender).TrySetResult(response);
        return Task.CompletedTask;
    }

    public Task CloseAsync(object sender)
    {
        ((TaskCompletionSource<byte[]>)sender).TrySetResult(Array.Empty<byte>());
        return Task.CompletedTask;
    }

    /// <summary>The "client" side: send a request and wait for the response.</summary>
    public async Task<string> SendAsync(string request)
    {
        var reply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _requests.Writer.WriteAsync(new Message(request.GetBytes(), reply));
        return (await reply.Task.WaitAsync(TimeSpan.FromSeconds(5))).GetString();
    }
}
```

Use it like any other listener:
```csharp
var listener = new InMemoryListener();
using var server = new MockServer(listener);
server.Mock.Send("ping").Receive("pong");
server.Start();

Assert.Equal("pong", await listener.SendAsync("ping"));
server.Mock.Verify("ping", Times.Once());
```

## Connections, greetings and pushed messages
A listener that only implements `IListener` gets everything above, but not the [connection features](Connections-and-Push):
`server.Connections`, `OnConnect()` greetings, `SendAsync`/`BroadcastAsync` and connection lines in the
[log](Logging-and-Diagnostics). For those, implement `IConnectionListener` as well:

```csharp
public interface IConnectionListener : IListener
{
    event Action<object, EndPoint> ConnectionOpened;    // a client connected (sender handle, client address)
    event Action<object> ConnectionClosed;              // a connection closed, by either side (sender handle)
    event Action<EndPoint, Exception> ConnectionFailed; // a handshake failed or the connection broke (logged)

    Task SendAsync(byte[] data, object sender);         // push a message the client didn't ask for
    void CompleteWithoutReply(object sender);           // a request got no reply (NoReply, Disconnect)
}
```

- Raise **`ConnectionOpened`** before any of that connection's requests come out of `ReceiveAsync()`, with the same
  object you later use as `Message.Sender`. Greetings are sent from the event, so they come before any response.
- Raise **`ConnectionClosed`** once per connection, whoever closed it.
- **`SendAsync`** may run at the same time as a reply on the same connection, so serialize writes if your transport needs it.
- **`CompleteWithoutReply`** is called instead of `ReplyAsync` for requests that get no reply, in case you count
  pending requests (as `TcpServerBase` does, to close a connection once the client is done and every request is handled).

## A TCP variation
To customise TCP itself, for example how streams are opened, derive from `TcpServerBase` instead and override
`OpenStreamAsync(TcpClient)`. That is how `TcpServerSsl` adds TLS. You keep persistent connections, framing,
`KeepAlive`, port `0` support and the connection features.

```csharp
public class LoggingTcpServer : TcpServerBase
{
    public LoggingTcpServer(int port) : base(IPAddress.Loopback, port) { }

    protected override Task<Stream> OpenStreamAsync(TcpClient client)
    {
        Console.WriteLine($"Client connected from {client.Client.RemoteEndPoint}");
        return Task.FromResult<Stream>(client.GetStream());
    }
}
```

Runnable code: [`CustomListenerSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/CustomListenerSamples.cs)
