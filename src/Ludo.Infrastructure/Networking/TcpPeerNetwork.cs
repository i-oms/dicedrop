using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Ludo.Application.Abstractions;
using Ludo.Application.Protocol;
using Ludo.Domain.Players;
using Ludo.Infrastructure.Serialization;

namespace Ludo.Infrastructure.Networking;

/// <summary>
/// Full-mesh TCP transport. Every peer listens; links open in either direction and start with a
/// fixed-size handshake that exchanges peer ids and listen ports.
/// </summary>
public sealed class TcpPeerNetwork(IDiagnosticLog? diagnostics = null) : IPeerNetwork
{
    private const string Source = "tcp";
    private const int Magic = 0x4F44554C; // "LUDO"
    private const int HandshakeBytes = sizeof(int) + 16 + sizeof(int);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Listing the adapters takes tens of milliseconds, and the host asks on every state change, so a long
    /// game would leave it minutes behind. Addresses rarely change, so a few seconds' staleness is fine.
    /// </summary>
    private static readonly TimeSpan AddressCacheLifetime = TimeSpan.FromSeconds(5);

    private readonly PeerMessageSerializer _serializer = new();
    private readonly IDiagnosticLog _log = diagnostics ?? NullDiagnosticLog.Instance;
    private readonly ConcurrentDictionary<PlayerId, PeerLink> _links = new();

    /// <summary>A peer keeps its id when it comes back, so a link's events must not interleave with its successor's.</summary>
    private readonly Lock _linkEvents = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private PlayerId _localId;
    private (IReadOnlyList<string> Addresses, long ReadAt)? _localAddresses;

    public event EventHandler<PeerConnection>? PeerConnected;

    public event EventHandler<PlayerId>? PeerDisconnected;

    public event EventHandler<IncomingMessage>? MessageReceived;

    public int ListenPort { get; private set; }

    public Task StartAsync(PlayerId localId, int port, CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The network is already running.");
        }

        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;

        // Nobody else may bind this port alongside us and intercept our peers.
        listener.ExclusiveAddressUse = true;
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            _log.Write(Source, $"Can't listen on TCP port {port}: {Describe(ex)}");
            listener.Server.Dispose();
            throw;
        }

        _localId = localId;
        _listener = listener;
        _lifetime = new CancellationTokenSource();
        ListenPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _log.Write(Source, $"Listening for players on TCP port {ListenPort}.");
        _ = AcceptLoopAsync(listener, _lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task<PlayerId> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var lifetime = _lifetime ?? throw new InvalidOperationException("Start the network first.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(HandshakeTimeout);

        var client = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        client.Client.DualMode = true;
        var connected = false;
        _log.Write(Source, $"Connecting to {host}:{port}...");
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            connected = true;
            var link = await HandshakeAsync(client, timeout.Token).ConfigureAwait(false);
            Attach(link, replaceExisting: false);
            _log.Write(Source, $"Connected to {host}:{port} (peer {Short(link.RemoteId)}).");
            return link.RemoteId;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            _log.Write(Source, connected
                ? $"{host}:{port} accepted the connection but never completed the handshake; it may not be DiceDrop, or is a different version."
                : $"No answer from {host}:{port} within {HandshakeTimeout.TotalSeconds:0} s. Usually that PC's firewall silently drops incoming TCP for DiceDrop.");
            throw new TimeoutException("No answer from the host.");
        }
        catch (Exception ex)
        {
            client.Dispose();
            _log.Write(Source, $"Connecting to {host}:{port} failed: {Describe(ex)}");
            throw;
        }
    }

    public void Send(PlayerId peer, PeerMessage message)
    {
        if (_links.TryGetValue(peer, out var link))
        {
            link.Enqueue(_serializer.Serialize(message));
        }
    }

    public void Broadcast(PeerMessage message)
    {
        var payload = _serializer.Serialize(message);
        foreach (var link in _links.Values)
        {
            link.Enqueue(payload);
        }
    }

    public void Disconnect(PlayerId peer)
    {
        if (_links.TryGetValue(peer, out var link))
        {
            link.CloseGracefully();
        }
    }

    public Task StopAsync()
    {
        _lifetime?.Cancel();
        _listener?.Stop();
        _listener = null;
        _lifetime = null;
        foreach (var link in _links.Values)
        {
            link.CloseGracefully();
        }

        _links.Clear();
        ListenPort = 0;
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetLocalAddresses()
    {
        var now = Environment.TickCount64;
        if (_localAddresses is { } cached && now - cached.ReadAt < (long)AddressCacheLifetime.TotalMilliseconds)
        {
            return cached.Addresses;
        }

        var addresses = ReadLocalAddresses();
        _localAddresses = (addresses, now);
        return addresses;
    }

    private static string[] ReadLocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .Distinct()
            .ToArray();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _log.Write(Source, $"Incoming connection from {client.Client.RemoteEndPoint}.");
            _ = AcceptAsync(client, cancellation);
        }
    }

    private async Task AcceptAsync(TcpClient client, CancellationToken cancellation)
    {
        client.NoDelay = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(HandshakeTimeout);
        var from = client.Client.RemoteEndPoint;
        try
        {
            var link = await HandshakeAsync(client, timeout.Token).ConfigureAwait(false);
            Attach(link, replaceExisting: true);
            _log.Write(Source, $"Accepted peer {Short(link.RemoteId)} from {from}.");
        }
        catch (Exception ex)
        {
            // Not a peer of ours, a duplicate link, or it went silent.
            _log.Write(Source, $"Dropped incoming connection from {from}: {Describe(ex)}");
            client.Dispose();
        }
    }

    private async Task<PeerLink> HandshakeAsync(TcpClient client, CancellationToken cancellation)
    {
        var stream = client.GetStream();
        var outgoing = new byte[HandshakeBytes];
        BinaryPrimitives.WriteInt32LittleEndian(outgoing, Magic);
        _localId.Value.TryWriteBytes(outgoing.AsSpan(sizeof(int), 16));
        BinaryPrimitives.WriteInt32LittleEndian(outgoing.AsSpan(sizeof(int) + 16), ListenPort);
        await stream.WriteAsync(outgoing, cancellation).ConfigureAwait(false);

        var incoming = new byte[HandshakeBytes];
        await stream.ReadExactlyAsync(incoming, cancellation).ConfigureAwait(false);
        if (BinaryPrimitives.ReadInt32LittleEndian(incoming) != Magic)
        {
            throw new InvalidDataException("Not a Ludo peer.");
        }

        var remoteId = new PlayerId(new Guid(incoming.AsSpan(sizeof(int), 16)));
        var remotePort = BinaryPrimitives.ReadInt32LittleEndian(incoming.AsSpan(sizeof(int) + 16));
        if (remoteId == _localId)
        {
            throw new InvalidOperationException("Connected to ourselves.");
        }

        var endpoint = (IPEndPoint)client.Client.RemoteEndPoint!;
        var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        return new PeerLink(client, remoteId, address.ToString(), remotePort);
    }

    /// <param name="replaceExisting">
    /// A peer dialling in while we still hold a link to it has restarted its session, and the old link just
    /// hasn't timed out. It counts as leaving and rejoining, so its old seats and invites are cleared up.
    /// </param>
    private void Attach(PeerLink link, bool replaceExisting)
    {
        lock (_linkEvents)
        {
            if (_links.TryGetValue(link.RemoteId, out var existing))
            {
                if (!replaceExisting)
                {
                    link.Abort();
                    throw new InvalidOperationException("Already connected to this peer.");
                }

                _links.TryRemove(new KeyValuePair<PlayerId, PeerLink>(link.RemoteId, existing));
                existing.Abort();
                _log.Write(Source, $"Peer {Short(link.RemoteId)} reconnected from {link.RemoteHost}; replacing its old link.");
                PeerDisconnected?.Invoke(this, link.RemoteId);
            }

            _links[link.RemoteId] = link;

            // Raised before the read loop starts, so it always precedes this peer's first message.
            PeerConnected?.Invoke(this, new PeerConnection(link.RemoteId, link.RemoteHost, link.RemoteListenPort));
        }

        _ = RunLinkAsync(link);
    }

    private async Task RunLinkAsync(PeerLink link)
    {
        await link.RunAsync(payload =>
        {
            PeerMessage message;
            try
            {
                message = _serializer.Deserialize(payload);
            }
            catch (JsonException)
            {
                link.Abort();
                return;
            }

            // A replaced link's last frames must not reach the peer's new session.
            lock (_linkEvents)
            {
                if (_links.TryGetValue(link.RemoteId, out var current) && current == link)
                {
                    MessageReceived?.Invoke(this, new IncomingMessage(link.RemoteId, message));
                }
            }
        }).ConfigureAwait(false);

        // Only report links that are still registered; a stopped network reports nothing.
        lock (_linkEvents)
        {
            if (_links.TryRemove(new KeyValuePair<PlayerId, PeerLink>(link.RemoteId, link)))
            {
                _log.Write(Source, $"Link to peer {Short(link.RemoteId)} at {link.RemoteHost} closed.");
                PeerDisconnected?.Invoke(this, link.RemoteId);
            }
        }
    }

    private static string Short(PlayerId id) => id.Value.ToString()[..8];

    private static string Describe(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            "connection refused: that PC is reachable but nothing listens on the port (DiceDrop closed or the lobby moved).",
        SocketException { SocketErrorCode: SocketError.TimedOut } =>
            "timed out: usually that PC's firewall drops incoming TCP for DiceDrop.",
        SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable } socket =>
            $"{socket.SocketErrorCode}: no route to that PC; are both on the same network?",
        SocketException { SocketErrorCode: SocketError.AccessDenied or SocketError.AddressAlreadyInUse } socket =>
            $"{socket.SocketErrorCode}: another program or a Windows port reservation holds the port.",
        SocketException socket => $"{socket.SocketErrorCode} ({socket.Message})",
        OperationCanceledException => "it went silent during the handshake.",
        EndOfStreamException => "it closed the connection during the handshake.",
        _ => ex.Message,
    };
}
