using Ludo.Domain.Gameplay;
using Ludo.Domain.Players;

namespace Ludo.Application.Protocol;

public static class ProtocolInfo
{
    public const int Version = 4;

    /// <summary>
    /// Port a host listens on when it can. Peers never depend on it: if it is taken, the host falls back
    /// to a port the OS picks and announces that one over discovery.
    /// </summary>
    public const int PreferredPort = 47777;
}

/// <summary>Base of every message exchanged between peers.</summary>
public abstract record PeerMessage;

/// <summary>First message on every link: who I am.</summary>
public sealed record HelloMessage(int ProtocolVersion, string Name, PlayerProfile? Profile) : PeerMessage;

public sealed record JoinRejectedMessage(string Reason) : PeerMessage;

/// <summary>Host to newcomer: the other lobby members it should connect to.</summary>
public sealed record PeerDirectoryMessage(IReadOnlyList<PeerAddress> Peers) : PeerMessage;

/// <summary>Member to host: which peers I have a greeted link with, so the host knows when the mesh is complete.</summary>
public sealed record PeerStatusMessage(IReadOnlyList<PlayerId> ConnectedPeers) : PeerMessage;

/// <summary>Host to everyone: lobby members in join order.</summary>
public sealed record LobbyStateMessage(IReadOnlyList<RosterEntry> Roster) : PeerMessage;

/// <summary>Host to everyone: this lobby is closing because it merges into a bigger one; go find it.</summary>
public sealed record LobbyClosedMessage : PeerMessage;

/// <summary>Lobby member to everyone: the name I go by from now on.</summary>
public sealed record RenameMessage(string Name) : PeerMessage;

/// <summary>Lobby member to everyone: one line of group chat. Peers keep it in memory only.</summary>
public sealed record ChatMessage(string Text) : PeerMessage;

/// <summary>Lobby member to one other member: a private line. Only the two of them ever see it.</summary>
public sealed record DirectChatMessage(string Text) : PeerMessage;

/// <summary>Lobby member to one other member: "hey, look here". Receivers ignore nudges that come too fast.</summary>
public sealed record NudgeMessage : PeerMessage;

/// <summary>Lobby member to everyone: my avatar changed. The first one travels in the Hello.</summary>
public sealed record ProfileMessage(PlayerProfile Profile) : PeerMessage;

/// <summary>
/// Any lobby member to everyone: "who wants to play?". The sender becomes the match's initiator and
/// commits to its dice chain up front. Only <see cref="Invitees"/> may join; null invites everyone.
/// Everyone else hears about it too, so they can watch.
/// </summary>
public sealed record MatchProposalMessage(Guid MatchId, byte[] ChainAnchor, string GameId, IReadOnlyList<PlayerId>? Invitees) : PeerMessage;

/// <summary>
/// Any member to the initiator: this invite is now the one I hold. The initiator starts only once every
/// member has sent it, so no crossing invite can still be winning on some peer when the game begins.
/// </summary>
public sealed record MatchSeenMessage(Guid MatchId) : PeerMessage;

/// <summary>
/// Any member to an initiator: I hold a game that beats your invite (a running game, or a crossing invite
/// with a lower id), so it can never start. Reaches initiators that cannot hear the winning one themselves.
/// </summary>
public sealed record MatchOutrankedMessage(Guid MatchId) : PeerMessage;

/// <summary>Invited member to everyone: in or out. Players who accept commit to a fresh dice chain.</summary>
public sealed record MatchReplyMessage(Guid MatchId, bool Accepted, byte[]? ChainAnchor) : PeerMessage;

/// <summary>Initiator to everyone: seats, rules and every seat's dice commitment, frozen for the whole match.</summary>
public sealed record GameStartMessage(Guid MatchId, GameRules Rules, IReadOnlyList<SeatAssignment> Seats) : PeerMessage;

/// <summary>Player to everyone: I am out of this match but stay in the lobby.</summary>
public sealed record MatchLeaveMessage(Guid MatchId) : PeerMessage;

/// <summary>Initiator to everyone: the invite is withdrawn or the running game is stopped.</summary>
public sealed record MatchEndMessage(Guid MatchId) : PeerMessage;

/// <summary>
/// Initiator to a spectator: the whole match so far, which the spectator replays on a fresh board.
/// Spectators do not take part in the dice protocol, so they follow the initiator's record instead.
/// </summary>
public sealed record MatchReplayMessage(
    Guid MatchId,
    string GameId,
    GameRules Rules,
    IReadOnlyList<SeatAssignment> Seats,
    IReadOnlyList<MatchStep> Steps) : PeerMessage;

/// <summary>Initiator to spectators: the next step of the match, numbered so gaps are noticed.</summary>
public sealed record MatchStepMessage(Guid MatchId, int Index, MatchStep Step) : PeerMessage;

/// <summary>Spectator to initiator: my copy fell behind, send me the whole match again.</summary>
public sealed record MatchSyncRequestMessage(Guid MatchId) : PeerMessage;

/// <summary>Current player asks every other player to reveal its share of roll <see cref="RollIndex"/>.</summary>
public sealed record RollRequestMessage(Guid MatchId, int RollIndex, string StateDigest) : PeerMessage;

public sealed record RevealMessage(Guid MatchId, int RollIndex, byte[] Preimage) : PeerMessage;

public sealed record MoveMessage(Guid MatchId, int RollIndex, int TokenIndex) : PeerMessage;

/// <summary>Member to everyone: I am leaving the lobby.</summary>
public sealed record LeaveMessage : PeerMessage;

/// <param name="Color">Index into the shared avatar palette, used behind initials when there is no photo.</param>
/// <param name="Photo">A small square PNG or JPEG, or null.</param>
public sealed record PlayerProfile(int Color, byte[]? Photo);

public sealed record PeerAddress(PlayerId Id, string Host, int Port);

public sealed record RosterEntry(PlayerId Id, string Name, bool Ready);

public sealed record SeatAssignment(PlayerId Id, string Name, byte[] ChainAnchor);

public enum MatchStepKind
{
    Roll,
    Move,
    Remove,
}

/// <summary>One change to the board: a die result, a token move, or a player dropping out.</summary>
public sealed record MatchStep(MatchStepKind Kind, PlayerId Player, int Value);
