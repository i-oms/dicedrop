using Ludo.Domain.Gameplay;
using Ludo.Domain.Players;

namespace Ludo.Application.Protocol;

public static class ProtocolInfo
{
    public const int Version = 12;

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

/// <summary>Lobby member to the members it picked: a notification with answer buttons. Nobody else receives it.</summary>
public sealed record PollMessage(Guid PollId, string Question, IReadOnlyList<string> Options, IReadOnlyList<PlayerId> Recipients) : PeerMessage;

/// <summary>Recipient to the sender and the other recipients: my answer, replacing any earlier one.</summary>
public sealed record PollVoteMessage(Guid PollId, int Option) : PeerMessage;

/// <summary>Sender to the recipients: I took this back, so drop it and every answer to it.</summary>
public sealed record PollRetractMessage(Guid PollId) : PeerMessage;

/// <summary>Lobby member to everyone: my avatar changed. The first one travels in the Hello.</summary>
public sealed record ProfileMessage(PlayerProfile Profile) : PeerMessage;

/// <summary>Lobby member to another: send me this photo, now that your avatar is on my screen.</summary>
public sealed record PhotoRequestMessage(string PhotoId) : PeerMessage;

/// <summary>Answer to <see cref="PhotoRequestMessage"/>.</summary>
public sealed record PhotoMessage(string PhotoId, byte[] Photo) : PeerMessage;

/// <summary>Initiator to members not watching: the little they need to know about a game without following its moves.</summary>
public sealed record MatchStatusMessage(Guid MatchId, PlayerId? Left, bool Finished) : PeerMessage;

/// <summary>
/// Any lobby member to everyone: "who wants to play?". The sender becomes the match's initiator and
/// commits to its dice chain up front. Only <see cref="Invitees"/> may join; null invites everyone.
/// Everyone else hears about it too, so they can watch.
/// </summary>
public sealed record MatchProposalMessage(Guid MatchId, byte[] ChainAnchor, string GameId, IReadOnlyList<PlayerId>? Invitees) : PeerMessage;

/// <summary>Invited member to everyone: in or out. Players who accept commit to a fresh dice chain.</summary>
public sealed record MatchReplyMessage(Guid MatchId, bool Accepted, byte[]? ChainAnchor) : PeerMessage;

/// <summary>Player who said yes to the initiator: whose yes I hold, so the game only starts once every player can reach every other.</summary>
public sealed record MatchLinksMessage(Guid MatchId, IReadOnlyList<PlayerId> Heard) : PeerMessage;

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

/// <summary>
/// Lobby member to everyone: one line of a game's group chat. Everyone keeps it with the match, so whoever
/// opens the board later can read back. Peers keep it in memory only.
/// </summary>
public sealed record MatchChatMessage(Guid MatchId, string Text) : PeerMessage;

/// <summary>Lobby member to everyone: I opened or closed this game's board.</summary>
public sealed record MatchWatchMessage(Guid MatchId, bool IsWatching) : PeerMessage;

/// <summary>Current player asks every other player to reveal its share of roll <see cref="RollIndex"/>.</summary>
public sealed record RollRequestMessage(Guid MatchId, int RollIndex, string StateDigest) : PeerMessage;

public sealed record RevealMessage(Guid MatchId, int RollIndex, byte[] Preimage) : PeerMessage;

public sealed record MoveMessage(Guid MatchId, int RollIndex, int TokenIndex) : PeerMessage;

/// <summary>Member to everyone: I am leaving the lobby.</summary>
public sealed record LeaveMessage : PeerMessage;

/// <summary>Member to everyone: close your window but keep running. Each receiver's own settings decide whether it obeys.</summary>
public sealed record CloseAppMessage : PeerMessage;

/// <param name="Color">Index into the shared avatar palette, used behind initials when there is no photo.</param>
/// <param name="Photo">A small square PNG or JPEG, or null.</param>
/// <param name="Photo">Never sent with the profile: it is fetched by <paramref name="PhotoId"/> when needed.</param>
/// <param name="PhotoId">Hash of the photo; null when there is none.</param>
public sealed record PlayerProfile(int Color, byte[]? Photo, string? PhotoId = null);

public sealed record PeerAddress(PlayerId Id, string Host, int Port);

public sealed record RosterEntry(PlayerId Id, string Name);

public sealed record SeatAssignment(PlayerId Id, string Name, byte[] ChainAnchor);

public enum MatchStepKind
{
    Roll,
    Move,
    Remove,
}

/// <summary>One change to the board: a die result, a token move, or a player dropping out.</summary>
public sealed record MatchStep(MatchStepKind Kind, PlayerId Player, int Value);
