namespace Ludo.Application.Session;

// Read models handed to the presenter. They carry plain values only, so the UI never touches the domain.

public enum SessionStage
{
    Idle,
    Lobby,
}

public enum MatchPhaseView
{
    Inviting,
    Playing,
    Finished,
    Stopped,
}

public enum InviteAnswer
{
    Pending,
    Accepted,
    Declined,
}

public enum GamePhaseView
{
    WaitingForRoll,
    Rolling,
    WaitingForMove,
    Over,
}

public enum TokenLocation
{
    Yard,
    Track,
    HomeColumn,
    Finished,
}

public enum SeatStanding
{
    Playing,
    Finished,
    Left,
}

/// <param name="Match">The one of <paramref name="Matches"/> that concerns us most: ours, then an invite to us, then any other.</param>
/// <param name="Matches">The lobby's invites and games, oldest first, including the last few that ended.</param>
public sealed record SessionSnapshot(
    SessionStage Stage,
    string LocalName,
    bool IsHost,
    IReadOnlyList<MemberView> Members,
    bool CanProposeGame,
    IReadOnlyList<string> JoinAddresses,
    MatchView? Match,
    IReadOnlyList<MatchView> Matches,
    IReadOnlyList<string> Log,
    IReadOnlyList<ChatLineView> Chat,
    IReadOnlyList<DirectChatView> DirectChats,
    string LocalKey,
    IReadOnlyList<PollView> Polls)
{
    public static SessionSnapshot Empty { get; } = new(SessionStage.Idle, string.Empty, false, [], false, [], null, [], [], [], [], string.Empty, []);
}

/// <param name="Key">Stable id for this member within the lobby; use it to message, nudge or invite them.</param>
/// <param name="InGame">Holds a seat in a running game and has not left it.</param>
public sealed record MemberView(string Key, string Name, bool IsHost, bool IsLocal, bool IsReady, bool InGame, ProfileView Profile);

/// <param name="Color">Index into the avatar palette, 0 to <see cref="SessionLimits.AvatarColors"/> - 1.</param>
/// <param name="Photo">PNG or JPEG bytes, unchecked beyond type and size: decode defensively.</param>
public sealed record ProfileView(int Color, byte[]? Photo);

public enum ChatLineKind
{
    Message,

    /// <summary>A nudge, shown in the private conversation it belongs to.</summary>
    Nudge,
}

/// <param name="SenderKey">The sender's <see cref="MemberView.Key"/>.</param>
/// <param name="Sender">The sender's name when the line was sent.</param>
/// <param name="Time">When this PC sent or received it; peers do not share clocks.</param>
/// <param name="Conversation">Null for the lobby chat, else the key of the other person in a private conversation.</param>
public sealed record ChatLineView(
    string SenderKey,
    string Sender,
    string Text,
    bool IsLocal,
    DateTimeOffset Time,
    ChatLineKind Kind,
    string? Conversation);

/// <param name="PeerKey">The other person's key.</param>
/// <param name="IsPresent">They are still in the lobby, so we can write to them.</param>
public sealed record DirectChatView(string PeerKey, string PeerName, bool IsPresent, IReadOnlyList<ChatLineView> Lines);

/// <summary>Someone nudged us.</summary>
public sealed record NudgeView(string FromKey, string From);

/// <summary>A notification with answer buttons that we sent or received.</summary>
/// <param name="FromKey">The sender's <see cref="MemberView.Key"/>.</param>
/// <param name="IsMine">We sent it, so we follow the answers rather than give one.</param>
public sealed record PollView(
    Guid Id,
    string FromKey,
    string From,
    bool IsMine,
    string Question,
    IReadOnlyList<string> Options,
    IReadOnlyList<PollMemberView> Recipients);

/// <param name="IsPresent">Still in the lobby, so an answer can still come.</param>
/// <param name="Choice">Index into <see cref="PollView.Options"/>; null until they answer.</param>
public sealed record PollMemberView(string Key, string Name, bool IsLocal, bool IsPresent, int? Choice);

/// <summary>Someone sent us a notification, answered one we are part of, or took one back.</summary>
/// <param name="Text">The question, or for an answer the chosen button and the question.</param>
/// <param name="IsRetracted">The sender took it back, so it is already gone from the list.</param>
public sealed record PollActivityView(Guid PollId, string FromKey, string From, string Text, bool IsNew, bool IsRetracted = false);

/// <summary>Someone in the lobby asked who wants to play.</summary>
public sealed record InviteView(Guid MatchId, string From, string GameName);

/// <param name="IsPlaying">We hold a seat and have not left; false for spectators.</param>
/// <param name="IsInvited">We may join: we were on the invite list, or everyone was invited.</param>
/// <param name="CanAnswer">The invite is open to us right now: we were invited and aren't playing another game.</param>
/// <param name="CanStart">We are the initiator and enough players have said yes.</param>
/// <param name="Log">What happened in this game, oldest first.</param>
public sealed record MatchView(
    Guid Id,
    GameInfo GameType,
    string InitiatorName,
    bool IsInitiator,
    MatchPhaseView Phase,
    InviteAnswer LocalAnswer,
    bool IsInvited,
    bool CanAnswer,
    bool IsPlaying,
    IReadOnlyList<MatchMemberView> Invitees,
    bool CanStart,
    string? EndReason,
    IReadOnlyList<string> Log,
    GameStateView? Game);

public sealed record MatchMemberView(string Key, string Name, bool IsInitiator, bool IsLocal, InviteAnswer Answer);

/// <param name="ArmCount">Arms drawn on the board: one per player, never fewer than four.</param>
public sealed record BoardLayoutView(
    int PlayerCount,
    int ArmCount,
    int MainTrackLength,
    int SquaresPerArm,
    int HomeColumnLength,
    int StarOffset,
    int TokensPerPlayer);

/// <param name="Progress">Steps along the owner's path: -1 in the yard, 0 on the start square, up to the finish.</param>
public sealed record TokenView(int Index, TokenLocation Location, int Progress, int TrackSquare, int HomeStep, bool CanMove);

/// <param name="Arm">Board arm this seat plays from.</param>
public sealed record SeatView(
    int Seat,
    int Arm,
    string Name,
    SeatStanding Standing,
    int? Rank,
    bool IsLocal,
    bool IsCurrent,
    IReadOnlyList<TokenView> Tokens);

/// <param name="RollCount">Rolls made so far, so the UI can tell a new roll from a repeat of the same face.</param>
public sealed record GameStateView(
    BoardLayoutView Board,
    IReadOnlyList<SeatView> Seats,
    int CurrentSeat,
    GamePhaseView Phase,
    bool IsLocalTurn,
    int? LastRoll,
    int RollCount,
    IReadOnlyList<string> WaitingFor);
