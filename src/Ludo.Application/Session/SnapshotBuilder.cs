using Ludo.Domain.Board;
using Ludo.Domain.Gameplay;
using Ludo.Domain.Players;

namespace Ludo.Application.Session;

internal static class SnapshotBuilder
{
    public static SessionSnapshot Build(SessionContext context)
    {
        if (context.Stage == SessionStage.Idle)
        {
            return SessionSnapshot.Empty;
        }

        var members = context.Roster
            .Select(r => new MemberView(
                SessionContext.KeyOf(r.Id),
                r.Name,
                r.Id == context.HostId,
                r.Id == context.LocalId,
                r.Ready,
                context.IsInRunningGame(r.Id),
                ToView(context.ProfileOf(r.Id))))
            .ToArray();
        var canPropose = context.SeatedMatch is null && context.OwnMatch is null
            && context.Roster.Count >= BoardTopology.MinPlayers
            && context.Roster.Exists(r => r.Id != context.LocalId && !context.IsInRunningGame(r.Id));
        var matches = context.Matches.Select(m => BuildMatch(context, m)).ToArray();
        var focus = context.FocusMatch;
        var joinAddresses = context.IsHost
            ? context.Network.GetLocalAddresses().Select(a => $"{a}:{context.Network.ListenPort}").ToArray()
            : [];

        return new SessionSnapshot(
            context.Stage,
            context.LocalName,
            context.IsHost,
            members,
            canPropose,
            joinAddresses,
            focus is null ? null : Array.Find(matches, m => m.Id == focus.Id),
            matches,
            context.Log.ToArray(),
            context.Chat.ToArray(),
            context.DirectChats
                .Select(d => new DirectChatView(
                    SessionContext.KeyOf(d.Key),
                    context.NameOf(d.Key),
                    context.Peers.TryGetValue(d.Key, out var peer) && peer.IsGreeted,
                    d.Value.ToArray()))
                .ToArray(),
            SessionContext.KeyOf(context.LocalId),
            context.Polls.Select(p => BuildPoll(context, p)).ToArray());
    }

    private static PollView BuildPoll(SessionContext context, PollState poll)
    {
        string NameIn(PlayerId id) => poll.GoneNames.TryGetValue(id, out var name) ? name : context.NameOf(id);

        return new PollView(
            poll.Id,
            SessionContext.KeyOf(poll.Sender),
            NameIn(poll.Sender),
            poll.Sender == context.LocalId,
            poll.Question,
            poll.Options,
            poll.Recipients
                .Select(id => new PollMemberView(
                    SessionContext.KeyOf(id),
                    NameIn(id),
                    id == context.LocalId,
                    id == context.LocalId || (context.Peers.TryGetValue(id, out var peer) && peer.IsGreeted),
                    poll.Votes.TryGetValue(id, out var choice) ? choice : null))
                .ToArray());
    }

    private static MatchView BuildMatch(SessionContext context, MatchState match)
    {
        var isInitiator = match.Initiator == context.LocalId;
        var invitees = context.Roster
            .Where(r => match.IsInvited(r.Id))
            .Select(r => new MatchMemberView(SessionContext.KeyOf(r.Id), r.Name, r.Id == match.Initiator, r.Id == context.LocalId, AnswerOf(match, r.Id)))
            .ToArray();
        var accepted = context.AcceptedPlayers(match);
        var canStart = isInitiator && match.Phase == MatchPhase.Inviting && accepted.Count > 0 && accepted.TrueForAll(p => p.Ready);

        return new MatchView(
            match.Id,
            GameCatalog.Find(match.GameId) ?? GameCatalog.Ludo,
            context.NameOf(match.Initiator),
            isInitiator,
            match.Phase switch
            {
                MatchPhase.Inviting => MatchPhaseView.Inviting,
                MatchPhase.Playing => MatchPhaseView.Playing,
                MatchPhase.Finished => MatchPhaseView.Finished,
                _ => MatchPhaseView.Stopped,
            },
            AnswerOf(match, context.LocalId),
            match.IsInvited(context.LocalId),
            match.Phase == MatchPhase.Inviting && !isInitiator && match.IsInvited(context.LocalId) && context.SeatedMatch is null,
            match.IsPlaying,
            invitees,
            canStart,
            match.EndReason,
            match.Log.ToArray(),
            match.Game is { } game ? BuildGame(context, match, game) : null,
            context.Roster
                .Where(r => match.Viewers.Contains(r.Id) && !match.IsActivePlayer(r.Id))
                .Select(r => new WatcherView(SessionContext.KeyOf(r.Id), r.Name, r.Id == context.LocalId))
                .ToArray(),
            match.Chat.ToArray());
    }

    private static ProfileView ToView(Protocol.PlayerProfile profile) => new(profile.Color, profile.Photo);

    private static InviteAnswer AnswerOf(MatchState match, PlayerId id) =>
        match.Replies.TryGetValue(id, out var reply)
            ? reply.Accepted ? InviteAnswer.Accepted : InviteAnswer.Declined
            : InviteAnswer.Pending;

    private static GameStateView BuildGame(SessionContext context, MatchState match, LudoGame game)
    {
        var board = game.Board;
        var isOver = game.Phase == TurnPhase.GameOver || match.Phase == MatchPhase.Stopped;
        var isLocalTurn = !isOver && match.IsPlaying && game.CurrentPlayer.Id == context.LocalId;
        var movable = isLocalTurn && game.Phase == TurnPhase.AwaitingMove
            ? game.LegalMoves.Select(m => m.TokenIndex).ToHashSet()
            : [];

        var seats = game.Players.Select(p => new SeatView(
            p.Seat,
            board.ArmOf(p.Seat),
            p.Name,
            p.Status switch
            {
                PlayerStatus.Finished => SeatStanding.Finished,
                PlayerStatus.Left => SeatStanding.Left,
                _ => SeatStanding.Playing,
            },
            p.Rank,
            p.Id == context.LocalId,
            !isOver && p.Seat == game.CurrentSeat,
            game.TokensOf(p.Seat)
                .Select((t, i) => ToView(board, p.Seat, t, i, p.Id == context.LocalId && movable.Contains(i)))
                .ToArray())).ToArray();

        var rolling = !isOver && match.Dice.OpenRoll is not null;
        var phase = isOver ? GamePhaseView.Over
            : rolling ? GamePhaseView.Rolling
            : game.Phase == TurnPhase.AwaitingMove ? GamePhaseView.WaitingForMove
            : GamePhaseView.WaitingForRoll;

        IReadOnlyList<string> waitingFor = [];
        if (rolling && match.Dice.OpenRoll is { } open)
        {
            match.Dice.Reveals.TryGetValue(open, out var reveals);
            waitingFor = game.Players
                .Where(p => p.Status != PlayerStatus.Left && reveals?.ContainsKey(p.Id) != true)
                .Select(p => p.Name)
                .ToArray();
        }

        return new GameStateView(
            new BoardLayoutView(
                board.PlayerCount,
                board.ArmCount,
                board.MainTrackLength,
                BoardTopology.SquaresPerArm,
                BoardTopology.HomeColumnLength,
                BoardTopology.StarOffset,
                game.Rules.TokensPerPlayer),
            seats,
            game.CurrentSeat,
            phase,
            isLocalTurn,
            game.LastRoll?.Value,
            game.RollCount,
            isLocalTurn ? game.ForcedMove?.TokenIndex : null,
            waitingFor);
    }

    private static TokenView ToView(BoardTopology board, int seat, TokenPosition position, int index, bool canMove) =>
        board.ZoneOf(position) switch
        {
            TokenZone.Yard => new TokenView(index, TokenLocation.Yard, -1, -1, -1, canMove),
            TokenZone.Track => new TokenView(index, TokenLocation.Track, position.Progress, board.TrackSquareOf(seat, position)!.Value, -1, canMove),
            TokenZone.HomeColumn => new TokenView(index, TokenLocation.HomeColumn, position.Progress, -1, board.HomeStepOf(position), canMove),
            _ => new TokenView(index, TokenLocation.Finished, position.Progress, -1, -1, canMove),
        };
}
