using Ludo.Application.Abstractions;
using Ludo.Application.Protocol;
using Ludo.Application.Session;
using Ludo.Domain.Players;

namespace Ludo.Application.Sync;

/// <summary>
/// Entry point for everything the network delivers. Events are queued onto the session executor,
/// routed to the right protocol, and parked when they arrive ahead of a message they depend on.
/// </summary>
internal sealed class PeerMessageRouter
{
    private const int MaxDeferred = 256;

    private readonly SessionContext _context;
    private readonly SessionExecutor _executor;
    private readonly LobbyProtocol _lobby;
    private readonly MatchProtocol _matches;
    private readonly GameplayProtocol _gameplay;
    private readonly PollProtocol _polls;

    public PeerMessageRouter(
        SessionContext context,
        SessionExecutor executor,
        LobbyProtocol lobby,
        MatchProtocol matches,
        GameplayProtocol gameplay,
        PollProtocol polls)
    {
        _context = context;
        _executor = executor;
        _lobby = lobby;
        _matches = matches;
        _gameplay = gameplay;
        _polls = polls;

        context.Network.PeerConnected += (_, connection) => executor.Post(() => OnConnected(connection));
        context.Network.PeerDisconnected += (_, peer) => executor.Post(() => OnDisconnectedAsync(peer));
        context.Network.MessageReceived += (_, incoming) => executor.Post(() => OnMessageAsync(incoming));
    }

    /// <summary>Retries parked messages until none of them can make progress.</summary>
    public async Task DrainDeferredAsync()
    {
        bool progressed;
        do
        {
            progressed = false;
            foreach (var parked in _context.Deferred.ToArray())
            {
                if (await DispatchAsync(parked).ConfigureAwait(false) != DispatchResult.Deferred)
                {
                    _context.Deferred.Remove(parked);
                    progressed = true;
                }
            }
        }
        while (progressed && _context.Deferred.Count > 0);
    }

    private void OnConnected(PeerConnection connection)
    {
        if (_context.Stage == SessionStage.Idle)
        {
            _context.Network.Disconnect(connection.Peer);
            return;
        }

        _lobby.OnPeerConnected(connection);
        _context.Publish();
    }

    private async Task OnDisconnectedAsync(PlayerId peer)
    {
        if (_context.Stage == SessionStage.Idle)
        {
            return;
        }

        // Game first: it still needs the leaver's name, which the lobby forgets.
        _matches.OnPeerGone(peer);
        _gameplay.OnPeerGone(peer);
        _polls.OnPeerGone(peer);
        await _lobby.OnPeerGoneAsync(peer).ConfigureAwait(false);
        await DrainDeferredAsync().ConfigureAwait(false);
        _context.Publish();
    }

    private async Task OnMessageAsync(IncomingMessage incoming)
    {
        if (_context.Stage == SessionStage.Idle)
        {
            return;
        }

        var result = await DispatchAsync(incoming).ConfigureAwait(false);
        if (result == DispatchResult.Deferred)
        {
            if (_context.Deferred.Count < MaxDeferred)
            {
                _context.Deferred.Add(incoming);
            }
        }
        else if (result == DispatchResult.Handled)
        {
            await DrainDeferredAsync().ConfigureAwait(false);
        }

        _context.Publish();
    }

    private async Task<DispatchResult> DispatchAsync(IncomingMessage incoming)
    {
        var from = incoming.From;
        switch (incoming.Message)
        {
            case HelloMessage hello:
                return _lobby.OnHello(from, hello);
            case PeerDirectoryMessage directory:
                return _lobby.OnPeerDirectory(from, directory, ConnectInBackground);
            case PeerStatusMessage status:
                return _lobby.OnPeerStatus(from, status);
            case LobbyStateMessage state:
                return _lobby.OnLobbyState(from, state);
            case RenameMessage rename:
                return _lobby.OnRename(from, rename);
            case ChatMessage chat:
                return _lobby.OnChat(from, chat);
            case DirectChatMessage direct:
                return _lobby.OnDirectChat(from, direct);
            case NudgeMessage:
                return _lobby.OnNudge(from);
            case PollMessage poll:
                return _polls.OnPoll(from, poll);
            case PollVoteMessage vote:
                return _polls.OnVote(from, vote);
            case PollRetractMessage retract:
                return _polls.OnRetract(from, retract);
            case ProfileMessage profile:
                return _lobby.OnProfile(from, profile);
            case JoinRejectedMessage rejected:
                return await _lobby.OnJoinRejectedAsync(from, rejected).ConfigureAwait(false);
            case LobbyClosedMessage:
                return await _lobby.OnLobbyClosedAsync(from).ConfigureAwait(false);
            case MatchProposalMessage proposal:
                return _matches.OnProposal(from, proposal);
            case MatchReplyMessage reply:
                return _matches.OnReply(from, reply);
            case GameStartMessage start:
                return _matches.OnGameStart(from, start);
            case MatchLeaveMessage leave:
                return _matches.OnLeave(from, leave);
            case MatchEndMessage end:
                return _matches.OnEnd(from, end);
            case MatchReplayMessage replay:
                return _matches.OnReplay(from, replay);
            case MatchStepMessage step:
                return _matches.OnStep(from, step);
            case MatchSyncRequestMessage request:
                return _matches.OnSyncRequest(from, request);
            case RollRequestMessage request:
                return _gameplay.OnRollRequest(from, request);
            case RevealMessage reveal:
                return _gameplay.OnReveal(from, reveal);
            case MoveMessage move:
                return _gameplay.OnMove(from, move);
            case LeaveMessage:
                _context.Network.Disconnect(from);
                await OnDisconnectedAsync(from).ConfigureAwait(false);
                return DispatchResult.Ignored;
            default:
                return DispatchResult.Ignored;
        }
    }

    private void ConnectInBackground(PeerAddress address) => _ = Task.Run(async () =>
    {
        try
        {
            await _context.Network.ConnectAsync(address.Host, address.Port).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _executor.Post(() =>
            {
                _context.AddLog($"Could not reach a lobby member at {address.Host}:{address.Port} ({ex.Message}).");
                _context.Publish();
            });
        }
    });
}
