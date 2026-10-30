using Ludo.Application.FairDice;
using Ludo.Application.Protocol;
using Ludo.Domain.Gameplay;
using Ludo.Domain.Players;

namespace Ludo.Application.Session;

internal sealed class PeerInfo(PlayerId id, string host, int listenPort)
{
    public PlayerId Id { get; } = id;

    public string Host { get; } = host;

    public int ListenPort { get; } = listenPort;

    public string? Name { get; set; }

    public PlayerProfile? Profile { get; set; }

    /// <summary>When this peer last nudged us; later nudges inside the cooldown are dropped.</summary>
    public DateTimeOffset? LastNudgeIn { get; set; }

    /// <summary>When we last nudged this peer.</summary>
    public DateTimeOffset? LastNudgeOut { get; set; }

    /// <summary>True once the peer's Hello arrived, i.e. we know its name.</summary>
    public bool IsGreeted => Name is not null;
}

internal sealed class RosterSeat(PlayerId id, string name)
{
    public PlayerId Id { get; } = id;

    public string Name { get; set; } = name;

    /// <summary>Host side only: the peers this member reported being connected to.</summary>
    public HashSet<PlayerId> ConnectedTo { get; } = [];

    public bool Ready { get; set; }
}

/// <summary>A notification we sent or received, with the answers heard so far.</summary>
internal sealed class PollState(Guid id, PlayerId sender, string question, IReadOnlyList<string> options, IReadOnlyList<PlayerId> recipients)
{
    public Guid Id { get; } = id;

    public PlayerId Sender { get; } = sender;

    public string Question { get; } = question;

    public IReadOnlyList<string> Options { get; } = options;

    public IReadOnlyList<PlayerId> Recipients { get; } = recipients;

    /// <summary>Latest answer per recipient, as an index into <see cref="Options"/>.</summary>
    public Dictionary<PlayerId, int> Votes { get; } = [];

    /// <summary>Names of those involved who have left; the lobby no longer knows them.</summary>
    public Dictionary<PlayerId, string> GoneNames { get; } = [];
}

internal sealed class DiceRoundState
{
    /// <summary>Latest verified chain value per player; starts at their anchor.</summary>
    public Dictionary<PlayerId, byte[]> Heads { get; } = [];

    /// <summary>Reveals received so far, by roll index. Early reveals for the next roll are kept here.</summary>
    public Dictionary<int, Dictionary<PlayerId, byte[]>> Reveals { get; } = [];

    public int? OpenRoll { get; private set; }

    public PlayerId? Roller { get; private set; }

    public void Open(int rollIndex, PlayerId roller)
    {
        OpenRoll = rollIndex;
        Roller = roller;
    }

    public void Close(int rollIndex)
    {
        Reveals.Remove(rollIndex);
        OpenRoll = null;
        Roller = null;
    }

    public void Reset()
    {
        Heads.Clear();
        Reveals.Clear();
        OpenRoll = null;
        Roller = null;
    }
}

internal enum MatchPhase
{
    Inviting,
    Playing,
    Finished,
    Stopped,
}

/// <param name="Anchor">The dice commitment the player sent us directly; null when it declined.</param>
internal sealed record MatchReply(bool Accepted, byte[]? Anchor);

/// <summary>
/// One game inside the lobby: proposed by any member (its initiator), joined by those who accept, and
/// watched by everyone else. Lobby members outlive it and can start the next one.
/// </summary>
internal sealed class MatchState(Guid id, PlayerId initiator, string gameId, IReadOnlySet<PlayerId>? invitees)
{
    private const int MaxLogEntries = 200;

    private readonly List<string> _log = [];

    public Guid Id { get; } = id;

    public PlayerId Initiator { get; } = initiator;

    public string GameId { get; } = gameId;

    /// <summary>Who may join besides the initiator; null when everyone in the lobby was invited.</summary>
    public IReadOnlySet<PlayerId>? Invitees { get; } = invitees;

    public MatchPhase Phase { get; set; } = MatchPhase.Inviting;

    public bool IsActive => Phase is MatchPhase.Inviting or MatchPhase.Playing;

    /// <summary>Answers to the invite. The initiator is in from the start.</summary>
    public Dictionary<PlayerId, MatchReply> Replies { get; } = [];

    /// <summary>Our dice commitment for this match, while we have said yes.</summary>
    public HashChain? Chain { get; set; }

    public GameRules Rules { get; set; } = GameRules.Classic;

    public IReadOnlyList<SeatAssignment> Seats { get; set; } = [];

    public LudoGame? Game { get; set; }

    /// <summary>
    /// True while we hold a seat and take part in the dice protocol. Spectators, and players who left,
    /// follow the initiator's record of the match instead.
    /// </summary>
    public bool IsPlaying { get; set; }

    public DiceRoundState Dice { get; } = new();

    /// <summary>Every change applied to the board, in order; spectators are sent this.</summary>
    public List<MatchStep> History { get; } = [];

    /// <summary>What happened in this game, oldest first, so a board never shows another game's moves.</summary>
    public IReadOnlyList<string> Log => _log;

    public string? EndReason { get; set; }

    /// <summary>Spectator side: we asked the initiator for a fresh replay and are waiting for it.</summary>
    public bool AwaitingReplay { get; set; }

    public void AddLog(string entry)
    {
        _log.Add(entry);
        if (_log.Count > MaxLogEntries)
        {
            _log.RemoveRange(0, _log.Count - MaxLogEntries);
        }
    }

    public bool IsInvited(PlayerId id) => id == Initiator || Invitees is null || Invitees.Contains(id);

    public bool IsSeated(PlayerId id) => Seats.Any(s => s.Id == id);

    /// <summary>A seated player that has not left: it must reveal a share for every roll.</summary>
    public bool IsActivePlayer(PlayerId id) => Game?.FindPlayer(id) is { Status: not PlayerStatus.Left };
}
