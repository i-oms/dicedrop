using System.Text;
using Ludo.Application.Abstractions;
using Ludo.Application.Common;
using Ludo.Application.Protocol;
using Ludo.Domain.Gameplay;
using Ludo.Domain.Players;

namespace Ludo.Application.Session;

/// <summary>
/// All mutable state of one local session plus the ports it talks to.
/// Only ever touched from the <see cref="SessionExecutor"/>, so it needs no locking.
/// </summary>
internal sealed class SessionContext(
    IPeerNetwork network,
    ILobbyDiscovery discovery,
    ICryptoProvider crypto,
    ISessionPresenter presenter,
    TimeProvider time,
    PlayerId localId)
{
    public const int MaxPlayers = 8;
    private const int MaxLogEntries = 200;
    private const int MaxChatEntries = 200;
    private const int MaxDirectEntries = 200;
    private const int MaxPolls = 50;
    private const int MaxEndedMatches = 4;

    private readonly List<string> _log = [];
    private readonly List<ChatLineView> _chat = [];
    private readonly Dictionary<PlayerId, List<ChatLineView>> _direct = [];

    public IPeerNetwork Network { get; } = network;

    public ILobbyDiscovery Discovery { get; } = discovery;

    public ICryptoProvider Crypto { get; } = crypto;

    public ISessionPresenter Presenter { get; } = presenter;

    public TimeProvider Time { get; } = time;

    public SessionStage Stage { get; set; } = SessionStage.Idle;

    /// <summary>Who we are. Stays the same across sessions, so coming back doesn't make us a new person.</summary>
    public PlayerId LocalId { get; } = localId;

    /// <summary>Names the lobby we would advertise. New each session, so a reopened lobby is not taken for the old one.</summary>
    public Guid LobbyId { get; private set; }

    public string LocalName { get; private set; } = string.Empty;

    /// <summary>Our avatar. Unlike the rest of the session it is kept when we move to another lobby.</summary>
    public PlayerProfile LocalProfile { get; set; } = new(0, null);

    public PlayerId? HostId { get; set; }

    public bool IsHost => Stage != SessionStage.Idle && HostId == LocalId;

    public Dictionary<PlayerId, PeerInfo> Peers { get; } = [];

    public List<RosterSeat> Roster { get; } = [];

    /// <summary>The lobby's invites and games, oldest first, including the last few that ended.</summary>
    public List<MatchState> Matches { get; } = [];

    /// <summary>The running game we hold a seat in. A member plays one game at a time.</summary>
    public MatchState? SeatedMatch => Matches.Find(m => m is { Phase: MatchPhase.Playing, IsPlaying: true });

    /// <summary>The invite or running game we started. A member runs one at a time.</summary>
    public MatchState? OwnMatch => Matches.Find(m => m.IsActive && m.Initiator == LocalId);

    /// <summary>The match that concerns us most: ours, then one we said yes to or were asked to, then any other.</summary>
    public MatchState? FocusMatch =>
        SeatedMatch
        ?? OwnMatch
        ?? Matches.Find(m => m.Phase == MatchPhase.Inviting && m.Replies.TryGetValue(LocalId, out var reply) && reply.Accepted)
        ?? Matches.Find(m => m.Phase == MatchPhase.Inviting && m.IsInvited(LocalId) && !m.Replies.ContainsKey(LocalId))
        ?? Matches.FindLast(m => m.IsActive)
        ?? Matches.LastOrDefault();

    /// <summary>Matches that ended in this session; late messages for them are dropped, not parked.</summary>
    public HashSet<Guid> PastMatches { get; } = [];

    /// <summary>Notifications of the current lobby, oldest first. Held in memory only, like the chat.</summary>
    public List<PollState> Polls { get; } = [];

    /// <summary>Notifications trimmed from <see cref="Polls"/>; late answers to them are dropped, not parked.</summary>
    public HashSet<Guid> PastPolls { get; } = [];

    public List<IncomingMessage> Deferred { get; } = [];

    public IReadOnlyList<string> Log => _log;

    /// <summary>Group chat of the current lobby. Held in memory only and dropped with the session.</summary>
    public IReadOnlyList<ChatLineView> Chat => _chat;

    /// <summary>Private conversations, by the other person. Held in memory only, like the group chat.</summary>
    public IReadOnlyDictionary<PlayerId, List<ChatLineView>> DirectChats => _direct;

    public static string ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > Player.MaxNameLength)
        {
            throw new SessionException($"Enter a name of 1 to {Player.MaxNameLength} characters.");
        }

        return trimmed;
    }

    /// <summary>Trims a chat line and flattens line breaks; null if nothing sendable is left.</summary>
    public static string? NormalizeChat(string? text)
    {
        var flat = new string((text ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return flat.Length is 0 or > SessionLimits.MaxChatLength ? null : flat;
    }

    public void EnsureIdle()
    {
        if (Stage != SessionStage.Idle)
        {
            throw new SessionException("Leave the current lobby first.");
        }
    }

    public void Begin(string name)
    {
        Reset();
        LobbyId = Guid.NewGuid();
        LocalName = name;
    }

    public void Rename(string name) => LocalName = name;

    public void Reset()
    {
        Stage = SessionStage.Idle;
        HostId = null;
        Matches.Clear();
        PastMatches.Clear();
        Polls.Clear();
        PastPolls.Clear();
        Peers.Clear();
        Roster.Clear();
        Deferred.Clear();
        _log.Clear();
        _chat.Clear();
        _direct.Clear();
    }

    public async Task TeardownAsync()
    {
        Discovery.StopAdvertising();
        await Network.StopAsync().ConfigureAwait(false);
        Reset();
    }

    public void AddLog(string entry)
    {
        _log.Add(entry);
        if (_log.Count > MaxLogEntries)
        {
            _log.RemoveRange(0, _log.Count - MaxLogEntries);
        }
    }

    /// <summary>Stable key the UI uses to tell people apart, whatever their names.</summary>
    public static string KeyOf(PlayerId id) => id.Value.ToString("N");

    public static PlayerId? FindByKey(string key) =>
        Guid.TryParseExact(key, "N", out var value) ? new PlayerId(value) : null;

    /// <summary>A profile from the network, or null if it breaks the limits. Only the size and image type are checked here.</summary>
    public static PlayerProfile? ValidateProfile(PlayerProfile? profile)
    {
        if (profile is null || profile.Color is < 0 or >= SessionLimits.AvatarColors)
        {
            return null;
        }

        if (profile.Photo is { } photo && (photo.Length > SessionLimits.MaxPhotoBytes || !IsPngOrJpeg(photo)))
        {
            return profile with { Photo = null };
        }

        return profile;
    }

    public void AddChat(PlayerId sender, string text, bool isLocal)
    {
        var line = new ChatLineView(KeyOf(sender), NameOf(sender), text, isLocal, Time.GetLocalNow(), ChatLineKind.Message, Conversation: null);
        _chat.Add(line);
        if (_chat.Count > MaxChatEntries)
        {
            _chat.RemoveRange(0, _chat.Count - MaxChatEntries);
        }

        Presenter.PresentChat(line);
    }

    /// <summary>Adds a line to our private conversation with <paramref name="peer"/>.</summary>
    public void AddDirect(PlayerId peer, PlayerId sender, string text, ChatLineKind kind)
    {
        var line = new ChatLineView(KeyOf(sender), NameOf(sender), text, sender == LocalId, Time.GetLocalNow(), kind, KeyOf(peer));
        if (!_direct.TryGetValue(peer, out var lines))
        {
            _direct[peer] = lines = [];
        }

        lines.Add(line);
        if (lines.Count > MaxDirectEntries)
        {
            lines.RemoveRange(0, lines.Count - MaxDirectEntries);
        }

        Presenter.PresentChat(line);
    }

    public void AddPoll(PollState poll)
    {
        Polls.Add(poll);
        if (Polls.Count > MaxPolls)
        {
            PastPolls.Add(Polls[0].Id);
            Polls.RemoveAt(0);
        }
    }

    /// <summary>Our avatar, or the one a peer sent us; peers we have no profile for get a colour from their id.</summary>
    public PlayerProfile ProfileOf(PlayerId id) =>
        id == LocalId ? LocalProfile
        : Peers.TryGetValue(id, out var peer) && peer.Profile is { } profile ? profile
        : new PlayerProfile((int)((uint)id.Value.GetHashCode() % SessionLimits.AvatarColors), null);

    private static bool IsPngOrJpeg(byte[] data) =>
        data is [0x89, 0x50, 0x4E, 0x47, ..] or [0xFF, 0xD8, 0xFF, ..];

    public void Notify(string message) => Presenter.PresentNotice(message);

    public void Publish() => Presenter.PresentSession(SnapshotBuilder.Build(this));

    public string NameOf(PlayerId id) =>
        (id == LocalId ? LocalName : null)
        ?? Roster.Find(r => r.Id == id)?.Name
        ?? Matches.Select(m => m.Game?.FindPlayer(id)?.Name).FirstOrDefault(n => n is not null)
        ?? (Peers.TryGetValue(id, out var peer) ? peer.Name : null)
        ?? id.ToString();

    public MatchState? FindMatch(Guid id) => Matches.Find(m => m.Id == id);

    /// <summary>Adds an invite or game. Each member runs one at a time, so only ended ones need trimming.</summary>
    public void AddMatch(MatchState match)
    {
        Matches.Add(match);
        var ended = Matches.Where(m => !m.IsActive).ToArray();
        foreach (var old in ended.Take(ended.Length - MaxEndedMatches))
        {
            Matches.Remove(old);
        }
    }

    /// <summary>The member holds a seat in a running game and has not left it, so it can't be invited to another.</summary>
    public bool IsInRunningGame(PlayerId id) => Matches.Exists(m => m.Phase == MatchPhase.Playing && m.IsActivePlayer(id));

    /// <summary>Turns pending domain events into log lines and marks the match finished when the game ends.</summary>
    public void FlushGameEvents(MatchState match)
    {
        if (match.Game is not { } game)
        {
            return;
        }

        foreach (var domainEvent in game.DequeueEvents())
        {
            if (EventNarrator.Describe(domainEvent, game) is { } line)
            {
                match.AddLog(line);
            }

            if (domainEvent is GameEnded && match.Phase == MatchPhase.Playing)
            {
                match.Phase = MatchPhase.Finished;
                match.Chain = null;
                PastMatches.Add(match.Id);
            }
        }
    }

    /// <summary>
    /// Notes a change just applied to the board. The initiator forwards it to everyone not playing,
    /// which is how spectators follow the game without taking part in the dice protocol.
    /// </summary>
    public void RecordStep(MatchState match, MatchStep step)
    {
        match.History.Add(step);
        if (match.Initiator != LocalId)
        {
            return;
        }

        var message = new MatchStepMessage(match.Id, match.History.Count - 1, step);
        foreach (var watcher in Peers.Values.Where(p => p.IsGreeted && !match.IsActivePlayer(p.Id)))
        {
            Network.Send(watcher.Id, message);
        }
    }

    /// <summary>Lobby members, other than the initiator, who said yes to the open invite and are connected to us.</summary>
    public List<RosterSeat> AcceptedPlayers(MatchState match) =>
        match.Phase == MatchPhase.Inviting
            ? Roster
                .Where(r => r.Id != match.Initiator
                    && match.Replies.TryGetValue(r.Id, out var reply) && reply.Accepted
                    && Peers.TryGetValue(r.Id, out var peer) && peer.IsGreeted)
                .ToList()
            : [];

    /// <summary>Sends to every other player still in the match.</summary>
    public void SendToPlayers(MatchState match, PeerMessage message)
    {
        foreach (var peer in Peers.Keys.Where(id => match.IsActivePlayer(id)))
        {
            Network.Send(peer, message);
        }
    }

    /// <summary>Closes an invite or game for good; its board stays visible for a while.</summary>
    public void EndMatch(MatchState match, string reason)
    {
        if (!match.IsActive)
        {
            return;
        }

        match.Phase = MatchPhase.Stopped;
        match.EndReason = reason;
        match.IsPlaying = false;
        match.Chain = null;
        match.Dice.Reset();
        PastMatches.Add(match.Id);
        match.AddLog(reason);
        AddLog(reason);
    }

    /// <summary>Short fingerprint of the replicated game state, compared between peers to spot desyncs.</summary>
    public string ComputeDigest(MatchState match)
    {
        if (match.Game is not { } game)
        {
            return string.Empty;
        }

        var text = new StringBuilder()
            .Append(game.RollCount).Append('|')
            .Append(game.CurrentSeat).Append('|')
            .Append((int)game.Phase).Append('|')
            .Append(game.ConsecutiveSixes);
        foreach (var player in game.Players)
        {
            text.Append('|').Append((int)player.Status);
            foreach (var token in game.TokensOf(player.Seat))
            {
                text.Append(',').Append(token.Progress);
            }
        }

        return Convert.ToHexString(Crypto.Hash(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }
}
