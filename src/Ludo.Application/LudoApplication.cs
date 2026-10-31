using Ludo.Application.Abstractions;
using Ludo.Application.Session;
using Ludo.Application.Sync;
using Ludo.Application.UseCases;
using Ludo.Domain.Players;

namespace Ludo.Application;

/// <summary>
/// The application layer's public surface: one object per use case, wired to the ports supplied by
/// the composition root. Everything behind it stays internal.
/// </summary>
public sealed class LudoApplication : IAsyncDisposable
{
    private readonly SessionExecutor _executor;

    public LudoApplication(
        IPeerNetwork network,
        ILobbyDiscovery discovery,
        ICryptoProvider crypto,
        ISessionPresenter presenter,
        TimeProvider time,
        IDiagnosticLog? diagnostics = null,
        PlayerId? localId = null)
    {
        _executor = new SessionExecutor(ex => presenter.PresentNotice($"Unexpected error: {ex.Message}"));
        var context = new SessionContext(network, discovery, crypto, presenter, time, localId ?? PlayerId.New());
        var gameplay = new GameplayProtocol(context);
        var matches = new MatchProtocol(context, gameplay);
        var lobby = new LobbyProtocol(context, matches, _executor);
        var polls = new PollProtocol(context);
        var router = new PeerMessageRouter(context, _executor, lobby, matches, gameplay, polls);

        HostLobby = new HostLobbyUseCase(context, _executor, lobby);
        JoinLobby = new JoinLobbyUseCase(context, _executor);
        ProposeGame = new ProposeGameUseCase(context, _executor, router, matches);
        AcceptInvite = new AnswerInviteUseCase(context, _executor, router, matches, accept: true);
        DeclineInvite = new AnswerInviteUseCase(context, _executor, router, matches, accept: false);
        StartGame = new MatchCommandUseCase(context, _executor, router, matches.StartLocal);
        LeaveGame = new MatchCommandUseCase(context, _executor, router, matches.LeaveLocal);
        StopGame = new MatchCommandUseCase(context, _executor, router, matches.StopLocal);
        RollDice = new RollDiceUseCase(context, _executor, gameplay, router);
        MoveToken = new MoveTokenUseCase(context, _executor, gameplay, router);
        LeaveSession = new LeaveSessionUseCase(context, _executor);
        AutoLobby = new AutoLobbyUseCase(discovery, context, _executor, HostLobby, JoinLobby, time, diagnostics ?? NullDiagnosticLog.Instance);
        RenamePlayer = new RenamePlayerUseCase(context, _executor, lobby, AutoLobby);
        SendChat = new SendChatUseCase(context, _executor, lobby);
        SendMatchChat = new SendMatchChatUseCase(context, _executor, matches);
        WatchGame = new WatchGameUseCase(context, _executor, matches);
        SendDirectChat = new SendDirectChatUseCase(context, _executor, lobby);
        Nudge = new NudgeUseCase(context, _executor, lobby);
        UpdateProfile = new UpdateProfileUseCase(context, _executor, lobby);
        SendPoll = new SendPollUseCase(context, _executor, polls);
        VotePoll = new VotePollUseCase(context, _executor, polls);
        RetractPoll = new RetractPollUseCase(context, _executor, polls);
        CloseEveryApp = new CloseEveryAppUseCase(context, _executor, lobby);
    }

    public HostLobbyUseCase HostLobby { get; }

    public JoinLobbyUseCase JoinLobby { get; }

    /// <summary>Any lobby member: invite some or all of the lobby to a game, which the caller then runs.</summary>
    public ProposeGameUseCase ProposeGame { get; }

    /// <summary>An invitee: join a game, which takes back a yes to any other invite.</summary>
    public AnswerInviteUseCase AcceptInvite { get; }

    public AnswerInviteUseCase DeclineInvite { get; }

    /// <summary>The initiator: begin the game with everyone who accepted.</summary>
    public MatchCommandUseCase StartGame { get; }

    /// <summary>A player: drop out of the game but stay in the lobby, watching.</summary>
    public MatchCommandUseCase LeaveGame { get; }

    /// <summary>The initiator: withdraw the invite or end the game for everyone.</summary>
    public MatchCommandUseCase StopGame { get; }

    public RollDiceUseCase RollDice { get; }

    public MoveTokenUseCase MoveToken { get; }

    public LeaveSessionUseCase LeaveSession { get; }

    public AutoLobbyUseCase AutoLobby { get; }

    public RenamePlayerUseCase RenamePlayer { get; }

    public SendChatUseCase SendChat { get; }

    /// <summary>Anyone with a board open: write to that game's players and watchers.</summary>
    public SendMatchChatUseCase SendMatchChat { get; }

    /// <summary>Says which game's board is on screen, if any.</summary>
    public WatchGameUseCase WatchGame { get; }

    public SendDirectChatUseCase SendDirectChat { get; }

    public NudgeUseCase Nudge { get; }

    public UpdateProfileUseCase UpdateProfile { get; }

    /// <summary>Any lobby member: send chosen members a notification with answer buttons.</summary>
    public SendPollUseCase SendPoll { get; }

    /// <summary>A recipient: answer a notification, or change the answer.</summary>
    public VotePollUseCase VotePoll { get; }

    /// <summary>The sender: take a notification back from everyone it went to.</summary>
    public RetractPollUseCase RetractPoll { get; }

    /// <summary>Any lobby member: ask every DiceDrop in the lobby to close its window.</summary>
    public CloseEveryAppUseCase CloseEveryApp { get; }

    public async ValueTask DisposeAsync()
    {
        AutoLobby.Dispose();
        await LeaveSession.ExecuteAsync().ConfigureAwait(false);
        await _executor.DisposeAsync().ConfigureAwait(false);
    }
}
