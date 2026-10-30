using Ludo.Application.Abstractions;
using Ludo.Application.Common;
using Ludo.Application.Protocol;
using Ludo.Application.Session;
using Ludo.Domain.Players;

namespace Ludo.Application.UseCases;

/// <summary>
/// Puts every player on the LAN into one lobby with no manual steps. While idle it joins the best lobby
/// it hears about, or opens one itself if none appears. A host whose lobby is outranked by another one
/// (more players, or equal players and a lower id) closes it and moves there, so lobbies opened at the
/// same moment merge into one, unless a game is under way in it. The ranking is a strict total order,
/// so this always settles. Hearing can be one-way (a firewall on one PC, lost broadcasts), so a lobby we
/// outrank that still hasn't come to us after <see cref="WaitForMerge"/> is joined by us instead.
/// </summary>
public sealed class AutoLobbyUseCase : IDisposable
{
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a lobby we joined or tried to join is skipped. Long enough to stop two lobbies bouncing
    /// players back and forth, short enough that one failed attempt never keeps two PCs apart for good.
    /// </summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A host that hears us moves within about a second. One still there after this most likely can't hear
    /// our beacons, so waiting longer would keep both PCs apart for good.
    /// </summary>
    private static readonly TimeSpan WaitForMerge = TimeSpan.FromSeconds(6);

    private readonly ILobbyDiscovery _discovery;
    private readonly SessionContext _context;
    private readonly SessionExecutor _executor;
    private readonly HostLobbyUseCase _host;
    private readonly JoinLobbyUseCase _join;
    private readonly TimeProvider _time;
    private readonly IDiagnosticLog _diagnostics;
    private readonly Lock _gate = new();

    // Executor-only state.
    private readonly Dictionary<Guid, (DiscoveredLobby Lobby, DateTimeOffset SeenAt)> _seen = [];
    private readonly HashSet<Guid> _warnedIncompatible = [];
    private readonly Dictionary<Guid, DateTimeOffset> _triedAt = [];
    private readonly Dictionary<Guid, DateTimeOffset> _outrankedSince = [];
    private DateTimeOffset? _hostAt;

    private string? _playerName;
    private ITimer? _timer;
    private int _tickQueued;

    internal AutoLobbyUseCase(
        ILobbyDiscovery discovery,
        SessionContext context,
        SessionExecutor executor,
        HostLobbyUseCase host,
        JoinLobbyUseCase join,
        TimeProvider time,
        IDiagnosticLog diagnostics)
    {
        _discovery = discovery;
        _context = context;
        _executor = executor;
        _host = host;
        _join = join;
        _time = time;
        _diagnostics = diagnostics;
        _discovery.LobbyDiscovered += OnLobbyDiscovered;
    }

    /// <summary>
    /// Starts finding or hosting a lobby, now and whenever we are idle again. The name is trimmed and
    /// shortened to fit; the name actually used is returned.
    /// </summary>
    public string Start(string playerName)
    {
        var name = playerName.Trim();
        name = name.Length == 0 ? "Player" : name[..Math.Min(name.Length, Player.MaxNameLength)];
        lock (_gate)
        {
            _playerName = name;
            if (_timer is not null)
            {
                return name;
            }

            _timer = _time.CreateTimer(_ => QueueTick(), null, TickInterval, TickInterval);
        }

        _diagnostics.Write("lobby", $"Looking for lobbies as \"{name}\" (protocol v{ProtocolInfo.Version}).");
        _discovery.StartBrowsing();
        return name;
    }

    /// <summary>Lobbies found or hosted from now on use this (already validated) name.</summary>
    internal void Rename(string playerName)
    {
        lock (_gate)
        {
            _playerName = playerName;
        }
    }

    public void Dispose()
    {
        _discovery.LobbyDiscovered -= OnLobbyDiscovered;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        _discovery.StopBrowsing();
    }

    private void OnLobbyDiscovered(object? sender, DiscoveredLobby lobby) => _executor.Post(() =>
    {
        if (lobby.ProtocolVersion == ProtocolInfo.Version)
        {
            _seen[lobby.LobbyId] = (lobby, _time.GetUtcNow());
            return;
        }

        // Joining would only be refused at the handshake, leaving both PCs alone in silence. Say why instead.
        if (_warnedIncompatible.Add(lobby.LobbyId))
        {
            var which = lobby.ProtocolVersion < ProtocolInfo.Version ? "an older" : "a newer";
            _diagnostics.Write(
                "lobby",
                $"Ignoring {lobby.HostName}'s lobby at {lobby.Address}:{lobby.Port}: protocol v{lobby.ProtocolVersion}, we speak v{ProtocolInfo.Version}.");
            _context.Notify(
                $"{lobby.HostName}'s DiceDrop at {lobby.Address} is {which} version and can't play with this one. " +
                "Copy the same DiceDrop.exe to every PC.");
        }
    });

    /// <summary>At most one tick waits in the queue, so a slow connect doesn't pile them up.</summary>
    private void QueueTick()
    {
        if (Interlocked.Exchange(ref _tickQueued, 1) == 0)
        {
            _executor.Post(async () =>
            {
                Volatile.Write(ref _tickQueued, 0);
                await TickAsync().ConfigureAwait(false);
            });
        }
    }

    private async Task TickAsync()
    {
        string? name;
        lock (_gate)
        {
            name = _timer is null ? null : _playerName;
        }

        if (name is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var stale in _seen.Where(e => now - e.Value.SeenAt > Expiry).Select(e => e.Key).ToArray())
        {
            _seen.Remove(stale);
        }

        foreach (var cooled in _triedAt.Where(e => now - e.Value > RetryAfter).Select(e => e.Key).ToArray())
        {
            _triedAt.Remove(cooled);
        }

        switch (_context.Stage)
        {
            case SessionStage.Idle:
                await WhileIdleAsync(name, now).ConfigureAwait(false);
                break;
            case SessionStage.Lobby when _context.IsHost:
                _hostAt = null;
                await MergeIfOutrankedAsync(name, now).ConfigureAwait(false);
                break;
            default:
                _hostAt = null;
                _outrankedSince.Clear();
                break;
        }
    }

    private async Task WhileIdleAsync(string name, DateTimeOffset now)
    {
        if (BestLobby(seats: 1) is { } lobby)
        {
            await TryJoinAsync(name, lobby).ConfigureAwait(false);
            return;
        }

        // Give beacons time to arrive first; the jitter keeps players who start together from all hosting.
        _hostAt ??= now + TimeSpan.FromMilliseconds(Random.Shared.Next(1500, 3000));
        if (now < _hostAt)
        {
            return;
        }

        _hostAt = null;
        _diagnostics.Write("lobby", _seen.Count == 0
            ? "No lobby heard on the network, so opening our own. Others should now find it."
            : $"Heard {_seen.Count} lobby(s) but none we can join right now (full, ours, or tried in the last {RetryAfter.TotalSeconds:0} s); opening our own.");
        try
        {
            await _host.HostOnExecutorAsync(name, ProtocolInfo.PreferredPort).ConfigureAwait(false);
        }
        catch (SessionException ex)
        {
            _diagnostics.Write("lobby", $"Could not open a lobby: {ex.Message}");
            _context.Notify(ex.Message);
        }
    }

    private async Task MergeIfOutrankedAsync(string name, DateTimeOffset now)
    {
        var ours = _context.Roster.Count;
        var ourId = _context.LobbyId;
        foreach (var gone in _outrankedSince.Keys.Where(id => !_seen.ContainsKey(id)).ToArray())
        {
            _outrankedSince.Remove(gone);
        }

        foreach (var lobby in _seen.Values.Select(e => e.Lobby).Where(l => l.LobbyId != ourId && !Outranks(l, ours, ourId)))
        {
            _outrankedSince.TryAdd(lobby.LobbyId, now);
        }

        // Never break up a lobby while a game is being set up or played in it.
        if (_context.Matches.Exists(m => m.IsActive))
        {
            return;
        }

        DiscoveredLobby other;
        if (BestLobby(seats: ours) is { } better && Outranks(better, ours, ourId))
        {
            other = better;
            _diagnostics.Write(
                "lobby",
                $"{other.HostName}'s lobby at {other.Address}:{other.Port} ({other.Players} players) outranks ours ({ours}); merging into it.");
        }
        else if (BestLobby(seats: ours, l => _outrankedSince.TryGetValue(l.LobbyId, out var since) && now - since >= WaitForMerge) is { } stuck)
        {
            // Only the outranking side ever does this, so two lobbies never cross over.
            other = stuck;
            _diagnostics.Write(
                "lobby",
                $"{other.HostName}'s lobby at {other.Address}:{other.Port} should have moved to ours but hasn't for {WaitForMerge.TotalSeconds:0} s, " +
                "so it probably can't hear us (firewall on that PC, or lost broadcasts). Moving there instead.");
        }
        else
        {
            return;
        }

        // Our guests go idle when the lobby closes, which brings them to the same lobby.
        _context.Network.Broadcast(new LobbyClosedMessage());
        await _context.TeardownAsync().ConfigureAwait(false);
        await TryJoinAsync(name, other).ConfigureAwait(false);
    }

    private async Task TryJoinAsync(string name, DiscoveredLobby lobby)
    {
        _triedAt[lobby.LobbyId] = _time.GetUtcNow();
        _diagnostics.Write("lobby", $"Joining {lobby.HostName}'s lobby at {lobby.Address}:{lobby.Port} ({lobby.Players}/{lobby.MaxPlayers} players).");
        try
        {
            await _join.JoinOnExecutorAsync(name, lobby.Address, lobby.Port).ConfigureAwait(false);
            _diagnostics.Write("lobby", $"Joined {lobby.HostName}'s lobby.");
        }
        catch (SessionException ex)
        {
            _diagnostics.Write(
                "lobby",
                $"Join failed: {ex.Message} Will retry in {RetryAfter.TotalSeconds:0} s. If this repeats, the host's firewall is likely blocking TCP port {lobby.Port}.");
            _context.Notify(ex.Message);
        }
    }

    /// <summary>
    /// The highest-ranked lobby, other than our own and any we tried in the last <see cref="RetryAfter"/>,
    /// that still has room for <paramref name="seats"/> more players.
    /// </summary>
    private DiscoveredLobby? BestLobby(int seats, Func<DiscoveredLobby, bool>? filter = null) => _seen.Values
        .Select(e => e.Lobby)
        .Where(l => l.LobbyId != _context.LobbyId && !_triedAt.ContainsKey(l.LobbyId) && l.Players + seats <= l.MaxPlayers)
        .Where(l => filter?.Invoke(l) ?? true)
        .OrderByDescending(l => l.Players)
        .ThenBy(l => l.LobbyId)
        .FirstOrDefault();

    private static bool Outranks(DiscoveredLobby lobby, int players, Guid lobbyId) =>
        lobby.Players > players || (lobby.Players == players && lobby.LobbyId.CompareTo(lobbyId) < 0);
}
