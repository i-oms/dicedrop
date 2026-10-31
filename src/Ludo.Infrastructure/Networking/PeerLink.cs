using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using Ludo.Domain.Players;

namespace Ludo.Infrastructure.Networking;

/// <summary>One TCP link to a peer: length-prefixed frames, a dedicated writer, and ordered reads.</summary>
internal sealed class PeerLink
{
    public const int MaxFrameBytes = 1 << 20;

    // A PC that loses power or Wi-Fi sends no goodbye, and a game waiting on its dice share sends nothing
    // that could fail. These probes end such a link after about 20 seconds of silence.
    private const int KeepAliveIdleSeconds = 10;
    private const int KeepAliveIntervalSeconds = 2;
    private const int KeepAliveRetries = 5;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cancellation = new();
    private int _closed;

    public PeerLink(TcpClient client, PlayerId remoteId, string remoteHost, int remoteListenPort)
    {
        _client = client;
        _stream = client.GetStream();
        var socket = client.Client;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveIdleSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, KeepAliveIntervalSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveRetries);
        RemoteId = remoteId;
        RemoteHost = remoteHost;
        RemoteListenPort = remoteListenPort;
    }

    public PlayerId RemoteId { get; }

    public string RemoteHost { get; }

    public int RemoteListenPort { get; }

    public void Enqueue(byte[] payload) => _outbox.Writer.TryWrite(payload);

    /// <summary>Runs until the link closes. Frames are handed over strictly in arrival order.</summary>
    public async Task RunAsync(Action<byte[]> onFrame)
    {
        var writer = WriteLoopAsync();
        try
        {
            var header = new byte[sizeof(int)];
            while (!_cancellation.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _cancellation.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaxFrameBytes)
                {
                    break;
                }

                var payload = new byte[length];
                await _stream.ReadExactlyAsync(payload, _cancellation.Token).ConfigureAwait(false);
                onFrame(payload);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or EndOfStreamException)
        {
            // Remote closed or we were asked to stop.
        }
        finally
        {
            Abort();
            await writer.ConfigureAwait(false);
        }
    }

    /// <summary>Lets queued frames go out, then closes. Used so a final message (e.g. a rejection) is delivered.</summary>
    public void CloseGracefully()
    {
        if (_outbox.Writer.TryComplete())
        {
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => Abort(), TaskScheduler.Default);
        }
    }

    public void Abort()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        _outbox.Writer.TryComplete();
        _cancellation.Cancel();
        _client.Close();
    }

    private async Task WriteLoopAsync()
    {
        var header = new byte[sizeof(int)];
        try
        {
            await foreach (var payload in _outbox.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await _stream.WriteAsync(header, _cancellation.Token).ConfigureAwait(false);
                await _stream.WriteAsync(payload, _cancellation.Token).ConfigureAwait(false);
            }

            // Outbox completed by CloseGracefully: everything is flushed.
            Abort();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            Abort();
        }
    }
}
