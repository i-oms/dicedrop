using Ludo.Application.Common;
using Ludo.Application.FairDice;
using Ludo.Application.Protocol;
using Ludo.Application.Session;
using Ludo.Domain.Common;
using Ludo.Domain.Gameplay;
using Ludo.Domain.Players;

namespace Ludo.Application.Sync;

/// <summary>
/// Games inside the lobby. Any member who is free can ask who wants to play; those who say yes commit to
/// a fresh dice chain. The initiator starts the game with them, can stop it at any time, and keeps everyone
/// who is not playing (spectators, and players who left) up to date with its record of the match. Several
/// games can run side by side, and invites never compete: each member is in at most one game, so saying yes
/// to one invite takes back a yes to any other. A start that seats someone who has since moved on gets a
/// leave back, so that game carries on without them.
/// </summary>
internal sealed class MatchProtocol(SessionContext context, GameplayProtocol gameplay)
{
    /// <param name="inviteeKeys">Members who may join; null or empty invites everyone who is free.</param>
    public void ProposeLocal(string gameId, IReadOnlyCollection<string>? inviteeKeys)
    {
        RequireLobby();
        var game = GameCatalog.Find(gameId) ?? throw new SessionException("That game is not available.");
        if (context.SeatedMatch is not null)
        {
            throw new SessionException("You are already in a game.");
        }

        if (context.OwnMatch is not null)
        {
            throw new SessionException("You already have an invite open.");
        }

        if (context.Roster.Count < 2)
        {
            throw new SessionException("Wait for someone else to join the lobby first.");
        }

        var busy = context.Roster.Where(r => r.Id != context.LocalId && context.IsInRunningGame(r.Id)).Select(r => r.Id).ToHashSet();
        if (busy.Count == context.Roster.Count(r => r.Id != context.LocalId))
        {
            throw new SessionException("Everyone else is in a game right now.");
        }

        HashSet<PlayerId>? invitees = null;
        if (inviteeKeys is { Count: > 0 })
        {
            invitees = inviteeKeys
                .Select(SessionContext.FindByKey)
                .OfType<PlayerId>()
                .Where(id => id != context.LocalId && context.Roster.Exists(r => r.Id == id))
                .ToHashSet();
            if (invitees.Count == 0)
            {
                throw new SessionException("Those players are no longer in the lobby.");
            }

            invitees.ExceptWith(busy);
            if (invitees.Count == 0)
            {
                throw new SessionException("Those players are in a game right now.");
            }

            if (invitees.Count + 1 > game.MaxPlayers)
            {
                throw new SessionException($"{game.Name} takes at most {game.MaxPlayers} players.");
            }
        }
        else if (busy.Count > 0)
        {
            invitees = context.Roster.Where(r => r.Id != context.LocalId && !busy.Contains(r.Id)).Select(r => r.Id).ToHashSet();
        }

        ReleaseOtherInvites(keep: null, "You started another invite.");
        var match = new MatchState(Guid.NewGuid(), context.LocalId, game.Id, invitees) { Chain = HashChain.Create(context.Crypto) };
        match.Replies[context.LocalId] = new MatchReply(true, match.Chain.Anchor);
        context.AddMatch(match);

        context.Network.Broadcast(ProposalOf(match));
        context.AddLog(invitees is null ? $"You asked who wants to play {game.Name}." : $"You invited {string.Join(", ", invitees.Select(context.NameOf))} to {game.Name}.");
    }

    public void AnswerLocal(Guid matchId, bool accept)
    {
        if (context.FindMatch(matchId) is not { Phase: MatchPhase.Inviting } match)
        {
            throw new SessionException("That invite is no longer open.");
        }

        if (match.Initiator == context.LocalId)
        {
            throw new SessionException("You started this invite.");
        }

        if (!match.IsInvited(context.LocalId))
        {
            throw new SessionException("You were not invited to this game, but you can watch it.");
        }

        if (accept)
        {
            if (context.SeatedMatch is not null)
            {
                throw new SessionException("Finish or leave your game before joining another.");
            }

            // An open invite can reach more people than the game seats; the first to say yes get in.
            if (match.Replies.Values.Count(r => r.Accepted) >= (GameCatalog.Find(match.GameId) ?? GameCatalog.Ludo).MaxPlayers)
            {
                throw new SessionException("That game is full.");
            }

            ReleaseOtherInvites(match, $"You joined {context.NameOf(match.Initiator)}'s game, so your invite was withdrawn.");
        }

        Answer(match, accept);
    }

    public void StartLocal()
    {
        if (context.OwnMatch is not { Phase: MatchPhase.Inviting } match)
        {
            throw new SessionException("Only whoever sent the invite can start the game.");
        }

        var players = context.AcceptedPlayers(match);
        if (players.Count == 0)
        {
            throw new SessionException("Nobody has accepted yet.");
        }

        if (players.Any(p => !p.Ready))
        {
            throw new SessionException("Some players are still connecting to each other.");
        }

        var seats = players
            .Prepend(context.Roster.Find(r => r.Id == context.LocalId) ?? new RosterSeat(context.LocalId, context.LocalName))
            .Take(SessionContext.MaxGamePlayers)
            .Select(r => new SeatAssignment(r.Id, r.Name, match.Replies[r.Id].Anchor!))
            .ToArray();
        var start = new GameStartMessage(match.Id, GameRules.Classic, seats);
        context.Network.Broadcast(start);
        ApplyStart(match, start);
    }

    public void LeaveLocal()
    {
        if (context.SeatedMatch is not { } match)
        {
            throw new SessionException("You are not playing in a game.");
        }

        if (match.Initiator == context.LocalId)
        {
            throw new SessionException("You started this game, so stop it instead.");
        }

        context.Network.Broadcast(new MatchLeaveMessage(match.Id));
        gameplay.DropPlayer(match, context.LocalId);
        match.IsPlaying = false;
        match.Chain = null;
        match.AddLog("You left the game. You can keep watching it.");
    }

    public void StopLocal()
    {
        if (context.OwnMatch is not { } match)
        {
            throw new SessionException("Only whoever started the game can stop it.");
        }

        context.Network.Broadcast(new MatchEndMessage(match.Id));
        context.EndMatch(match, match.Phase == MatchPhase.Inviting ? "You withdrew the invite." : "You stopped the game.");
    }

    public void ChatLocal(Guid matchId, string text)
    {
        RequireLobby();
        if (context.FindMatch(matchId) is not { Game: not null } match)
        {
            throw new SessionException("That game is no longer available.");
        }

        var line = SessionContext.NormalizeChat(text)
            ?? throw new SessionException($"Messages must be 1 to {SessionLimits.MaxChatLength} characters.");
        context.Network.Broadcast(new MatchChatMessage(match.Id, line));
        context.AddMatchChat(match, context.LocalId, line);
    }

    /// <summary>Tells the lobby which board we have open now; null when it's none.</summary>
    public void WatchLocal(Guid? matchId)
    {
        if (context.Stage != SessionStage.Lobby)
        {
            return;
        }

        foreach (var old in context.Matches.Where(m => m.Id != matchId && m.Viewers.Remove(context.LocalId)))
        {
            context.Network.Broadcast(new MatchWatchMessage(old.Id, false));
        }

        if (matchId is { } id && context.FindMatch(id) is { } match && match.Viewers.Add(context.LocalId))
        {
            context.Network.Broadcast(new MatchWatchMessage(match.Id, true));
        }
    }

    public DispatchResult OnChat(PlayerId from, MatchChatMessage chat)
    {
        if (!IsGreeted(from) || SessionContext.NormalizeChat(chat.Text) is not { } line)
        {
            return DispatchResult.Ignored;
        }

        if (Find(chat.MatchId) is not { } match)
        {
            return Unknown(chat.MatchId);
        }

        context.AddMatchChat(match, from, line);
        return DispatchResult.Handled;
    }

    public DispatchResult OnWatch(PlayerId from, MatchWatchMessage watch)
    {
        if (!IsGreeted(from))
        {
            return DispatchResult.Ignored;
        }

        if (Find(watch.MatchId) is not { } match)
        {
            return Unknown(watch.MatchId);
        }

        if (watch.IsWatching)
        {
            match.Viewers.Add(from);
        }
        else
        {
            match.Viewers.Remove(from);
        }

        return DispatchResult.Handled;
    }

    public DispatchResult OnProposal(PlayerId from, MatchProposalMessage proposal)
    {
        if (!IsGreeted(from) || proposal.ChainAnchor is not { Length: HashChain.ValueLength }
            || context.PastMatches.Contains(proposal.MatchId)
            || context.FindMatch(proposal.MatchId) is not null
            || GameCatalog.Find(proposal.GameId) is not { } game
            || proposal.Invitees is { Count: 0 or > SessionContext.MaxLobbyMembers })
        {
            return DispatchResult.Ignored;
        }

        // Each member runs one game at a time, so its last one must have ended; the news may still be on its way.
        if (RunsAMatch(from))
        {
            return DispatchResult.Deferred;
        }

        var match = new MatchState(proposal.MatchId, from, game.Id, proposal.Invitees?.ToHashSet());
        match.Replies[from] = new MatchReply(true, proposal.ChainAnchor);
        context.AddMatch(match);
        var name = context.NameOf(from);
        if (match.IsInvited(context.LocalId) && context.SeatedMatch is null)
        {
            context.AddLog($"{name} wants to play {game.Name}.");
            context.Presenter.PresentInvite(new InviteView(match.Id, name, game.Name));
        }
        else
        {
            context.AddLog($"{name} is setting up a game of {game.Name}.");
        }

        return DispatchResult.Handled;
    }

    public DispatchResult OnReply(PlayerId from, MatchReplyMessage reply)
    {
        if (Find(reply.MatchId) is not { } match)
        {
            return Unknown(reply.MatchId);
        }

        if (match.Phase != MatchPhase.Inviting || from == match.Initiator || !IsGreeted(from) || !match.IsInvited(from)
            || (reply.Accepted && reply.ChainAnchor is not { Length: HashChain.ValueLength }))
        {
            return DispatchResult.Ignored;
        }

        match.Replies[from] = new MatchReply(reply.Accepted, reply.Accepted ? reply.ChainAnchor : null);
        return DispatchResult.Handled;
    }

    public DispatchResult OnGameStart(PlayerId from, GameStartMessage start)
    {
        if (Find(start.MatchId) is not { } match)
        {
            var result = Unknown(start.MatchId);
            if (result == DispatchResult.Ignored && start.Seats.Any(s => s.Id == context.LocalId))
            {
                // A game we gave up on seated us anyway; say we are out, or it would wait for our dice shares forever.
                context.Network.Broadcast(new MatchLeaveMessage(start.MatchId));
            }

            return result;
        }

        if (from != match.Initiator || match.Phase != MatchPhase.Inviting)
        {
            return DispatchResult.Ignored;
        }

        // Each player's reply travels on its own link, so it may still be on the way to us.
        if (start.Seats.Any(s => s.Id == context.LocalId)
            && start.Seats.Any(s => s.Id != context.LocalId && !match.Replies.ContainsKey(s.Id) && context.Peers.ContainsKey(s.Id)))
        {
            return DispatchResult.Deferred;
        }

        ApplyStart(match, start);
        return DispatchResult.Handled;
    }

    public DispatchResult OnLeave(PlayerId from, MatchLeaveMessage leave)
    {
        if (Find(leave.MatchId) is not { } match)
        {
            return Unknown(leave.MatchId);
        }

        if (match.Phase != MatchPhase.Playing || match.Game is null)
        {
            return DispatchResult.Ignored;
        }

        if (match.IsPlaying)
        {
            gameplay.DropPlayer(match, from);
        }

        if (match.Initiator == context.LocalId)
        {
            // From now on the leaver follows the game as a spectator.
            SendReplay(from, match);
        }

        return DispatchResult.Handled;
    }

    public DispatchResult OnEnd(PlayerId from, MatchEndMessage end)
    {
        if (Find(end.MatchId) is not { IsActive: true } match || from != match.Initiator)
        {
            return DispatchResult.Ignored;
        }

        var name = context.NameOf(from);
        context.EndMatch(match, match.Phase == MatchPhase.Inviting ? $"{name} withdrew the invite." : $"{name} stopped the game.");
        return DispatchResult.Handled;
    }

    public DispatchResult OnReplay(PlayerId from, MatchReplayMessage replay)
    {
        if (context.PastMatches.Contains(replay.MatchId))
        {
            return DispatchResult.Ignored;
        }

        var match = Find(replay.MatchId);
        if (match is null)
        {
            // A game that was already running when we arrived. Only its initiator, always seat 0, sends these.
            if (!IsGreeted(from) || replay.Seats.Count == 0 || replay.Seats[0].Id != from)
            {
                return DispatchResult.Ignored;
            }

            if (RunsAMatch(from))
            {
                return DispatchResult.Deferred;
            }

            match = new MatchState(replay.MatchId, from, GameCatalog.Find(replay.GameId)?.Id ?? GameCatalog.Ludo.Id, invitees: null);
        }
        else if (from != match.Initiator || match.IsPlaying || !match.IsActive)
        {
            return DispatchResult.Ignored;
        }

        LudoGame game;
        try
        {
            game = LudoGame.Start(match.Id, replay.Seats.Select(s => (s.Id, s.Name)).ToArray(), replay.Rules);
            foreach (var step in replay.Steps)
            {
                Apply(game, step);
            }
        }
        catch (DomainException ex)
        {
            context.AddLog($"Could not follow the game: {ex.Message}");
            return DispatchResult.Ignored;
        }

        // History is replayed silently; only what happens from now on is narrated.
        game.DequeueEvents();
        if (Find(match.Id) is null)
        {
            context.AddMatch(match);
            context.AddLog($"{context.NameOf(from)}'s game is in progress. You can watch it.");
        }

        match.Rules = replay.Rules;
        match.Seats = replay.Seats;
        match.Game = game;
        match.IsPlaying = false;
        match.AwaitingReplay = false;
        match.Dice.Reset();
        match.History.Clear();
        match.History.AddRange(replay.Steps);
        match.Phase = MatchPhase.Playing;
        if (game.Phase == TurnPhase.GameOver)
        {
            match.Phase = MatchPhase.Finished;
            context.PastMatches.Add(match.Id);
        }

        return DispatchResult.Handled;
    }

    public DispatchResult OnStep(PlayerId from, MatchStepMessage message)
    {
        if (Find(message.MatchId) is not { Phase: MatchPhase.Playing, IsPlaying: false, Game: { } game } match
            || from != match.Initiator || message.Index < match.History.Count)
        {
            return DispatchResult.Ignored;
        }

        if (message.Index > match.History.Count)
        {
            RequestReplay(match);
            return DispatchResult.Ignored;
        }

        try
        {
            Apply(game, message.Step);
        }
        catch (DomainException)
        {
            RequestReplay(match);
            return DispatchResult.Ignored;
        }

        match.History.Add(message.Step);
        context.FlushGameEvents(match);
        return DispatchResult.Handled;
    }

    public DispatchResult OnSyncRequest(PlayerId from, MatchSyncRequestMessage request)
    {
        if (Find(request.MatchId) is { Phase: MatchPhase.Playing or MatchPhase.Finished } match
            && match.Initiator == context.LocalId && IsGreeted(from))
        {
            SendReplay(from, match);
        }

        return DispatchResult.Ignored;
    }

    /// <summary>Brings a newly greeted lobby member into the open invites and running games.</summary>
    public void OnMemberGreeted(PlayerId member)
    {
        foreach (var match in context.Matches.Where(m => m.IsActive))
        {
            if (match.Phase == MatchPhase.Playing)
            {
                if (match.Initiator == context.LocalId)
                {
                    SendReplay(member, match);
                }

                continue;
            }

            // Every player sends its own commitment, so the newcomer can check the start message against it.
            if (match.Initiator == context.LocalId)
            {
                context.Network.Send(member, ProposalOf(match));
            }
            else if (match.Replies.TryGetValue(context.LocalId, out var reply) && reply.Accepted)
            {
                context.Network.Send(member, new MatchReplyMessage(match.Id, true, reply.Anchor));
            }
        }

        foreach (var match in context.Matches)
        {
            // Someone back on a new link starts with no board open, whatever their old link last said.
            match.Viewers.Remove(member);
            if (match.IsActive && match.Viewers.Contains(context.LocalId))
            {
                context.Network.Send(member, new MatchWatchMessage(match.Id, true));
            }
        }
    }

    /// <summary>The initiator leaving ends its match; anyone else just drops out of the invite.</summary>
    public void OnPeerGone(PlayerId peer)
    {
        foreach (var match in context.Matches)
        {
            match.Viewers.Remove(peer);
        }

        foreach (var match in context.Matches.Where(m => m.IsActive))
        {
            if (peer == match.Initiator)
            {
                context.EndMatch(match, $"{context.NameOf(peer)} left the lobby, so their game ended.");
            }
            else if (match.Phase == MatchPhase.Inviting)
            {
                match.Replies.Remove(peer);
            }
        }
    }

    private void Answer(MatchState match, bool accept)
    {
        match.Chain = accept ? HashChain.Create(context.Crypto) : null;
        var anchor = match.Chain?.Anchor;
        match.Replies[context.LocalId] = new MatchReply(accept, anchor);
        context.Network.Broadcast(new MatchReplyMessage(match.Id, accept, anchor));
    }

    /// <summary>We are about to commit to one game: withdraw our own invite and take back a yes to anyone else's.</summary>
    private void ReleaseOtherInvites(MatchState? keep, string ownInviteNotice)
    {
        foreach (var other in context.Matches.Where(m => m.Phase == MatchPhase.Inviting && m != keep))
        {
            if (other.Initiator == context.LocalId)
            {
                context.Network.Broadcast(new MatchEndMessage(other.Id));
                context.EndMatch(other, ownInviteNotice);
            }
            else if (other.Replies.TryGetValue(context.LocalId, out var reply) && reply.Accepted)
            {
                Answer(other, accept: false);
            }
        }
    }

    private void ApplyStart(MatchState match, GameStartMessage start)
    {
        LudoGame game;
        try
        {
            if (start.Seats.Count is 0 or > SessionContext.MaxGamePlayers || start.Seats[0].Id != match.Initiator)
            {
                throw new DomainException("the seating is invalid.");
            }

            game = LudoGame.Start(match.Id, start.Seats.Select(s => (s.Id, s.Name)).ToArray(), start.Rules);
        }
        catch (DomainException ex)
        {
            context.EndMatch(match, $"The game could not start: {ex.Message}");
            return;
        }

        match.Rules = start.Rules;
        match.Seats = start.Seats;
        match.Game = game;
        match.Phase = MatchPhase.Playing;
        match.AddLog($"{context.NameOf(match.Initiator)} started a game with {start.Seats.Count} players.");

        if (match.IsSeated(context.LocalId))
        {
            if (ValidateCommitments(match, start) is { } problem)
            {
                // Sitting out silently would stall everyone waiting for our dice shares.
                match.AddLog($"You could not join the game: {problem}");
                context.Network.Broadcast(new MatchLeaveMessage(match.Id));
                gameplay.DropPlayer(match, context.LocalId);
                match.Chain = null;
            }
            else
            {
                match.IsPlaying = true;
                foreach (var seat in start.Seats)
                {
                    match.Dice.Heads[seat.Id] = seat.ChainAnchor;
                }
            }
        }

        context.FlushGameEvents(match);
    }

    /// <summary>
    /// Every seat's commitment must match the one its owner sent us directly, otherwise a peer could give
    /// different players different commitments and steer its dice share.
    /// </summary>
    private string? ValidateCommitments(MatchState match, GameStartMessage start)
    {
        if (match.Chain is null)
        {
            return "you did not accept the invite.";
        }

        foreach (var seat in start.Seats)
        {
            var expected = seat.Id == context.LocalId
                ? match.Chain.Anchor
                : match.Replies.TryGetValue(seat.Id, out var reply) ? reply.Anchor : null;
            if (expected is null)
            {
                return $"not connected to {seat.Name}.";
            }

            if (!expected.AsSpan().SequenceEqual(seat.ChainAnchor))
            {
                return $"dice commitment mismatch for {seat.Name}.";
            }
        }

        return null;
    }

    private void SendReplay(PlayerId peer, MatchState match) =>
        context.Network.Send(peer, new MatchReplayMessage(match.Id, match.GameId, match.Rules, match.Seats, match.History.ToArray()));

    private void RequestReplay(MatchState match)
    {
        if (!match.AwaitingReplay)
        {
            match.AwaitingReplay = true;
            context.Network.Send(match.Initiator, new MatchSyncRequestMessage(match.Id));
        }
    }

    private static void Apply(LudoGame game, MatchStep step)
    {
        switch (step.Kind)
        {
            case MatchStepKind.Roll:
                game.Roll(step.Player, new DiceValue(step.Value));
                break;
            case MatchStepKind.Move:
                game.MoveToken(step.Player, step.Value);
                break;
            case MatchStepKind.Remove:
                game.RemovePlayer(step.Player);
                break;
            default:
                throw new DomainException("Unknown step.");
        }
    }

    private static MatchProposalMessage ProposalOf(MatchState match) =>
        new(match.Id, match.Chain!.Anchor, match.GameId, match.Invitees?.ToArray());

    private MatchState? Find(Guid matchId) => context.FindMatch(matchId);

    private bool RunsAMatch(PlayerId member) => context.Matches.Exists(m => m.IsActive && m.Initiator == member);

    /// <summary>A message for a match we have not heard of may have overtaken the invite; one for an old match is stale.</summary>
    private DispatchResult Unknown(Guid matchId) =>
        context.PastMatches.Contains(matchId) ? DispatchResult.Ignored : DispatchResult.Deferred;

    private bool IsGreeted(PlayerId peer) => context.Peers.TryGetValue(peer, out var info) && info.IsGreeted;

    private void RequireLobby()
    {
        if (context.Stage != SessionStage.Lobby)
        {
            throw new SessionException("Join a lobby first.");
        }
    }
}
