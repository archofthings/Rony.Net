using System.Net;
using System.Threading.Channels;
using Rony;
using Rony.Interfaces;
using Rony.Models;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Custom-Listeners

/// <summary>
/// A listener with no network at all: requests and responses travel through memory.
/// Useful when the code under test lets you swap its transport.
/// </summary>
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

public class CustomListenerSamples
{
    [Fact]
    public async Task Mock_server_without_a_network()
    {
        var listener = new InMemoryListener();
        using var server = new MockServer(listener);
        server.Mock.Send("ping").Receive("pong");
        server.Start();

        Assert.Equal("pong", await listener.SendAsync("ping"));
        server.Mock.Verify("ping", Times.Once());
    }
}
