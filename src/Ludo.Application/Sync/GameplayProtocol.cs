using Ludo.Application.Common;
using Ludo.Application.FairDice;
using Ludo.Application.Protocol;
using Ludo.Application.Session;
using Ludo.Domain.Common;
using Ludo.Domain.Gameplay;
using Ludo.Domain.Players;

namespace Ludo.Application.Sync;

/// <summary>
/// Replicates a match between its players by lockstep: only the current player may roll or move, every
/// player checks each action against its own copy of the rules, and each die is derived from a share
/// revealed by every player. Spectators stay out of this and follow the initiator's record instead.
/// </summary>
internal sealed class GameplayProtocol(SessionContext context)
{
    /// <summary>Reveals for rolls further ahead than this are dropped, which bounds memory.</summary>
    private const int MaxRevealLookahead = 8;

    public void RollLocal()
    {
        var (match, game) = RequirePlaying();
        if (game.Phase != TurnPhase.AwaitingRoll || game.CurrentPlayer.Id != context.LocalId)
        {
            throw new SessionException("It is not your turn to roll.");
        }

        if (match.Dice.OpenRoll is not null)
        {
            throw new SessionException("The dice are already rolling.");
        }

        var rollIndex = game.RollCount + 1;
        context.SendToPlayers(match, new RollRequestMessage(match.Id, rollIndex, context.ComputeDigest(match)));
        OpenRound(match, rollIndex, context.LocalId);
    }

    public void MoveLocal(int tokenIndex)
    {
        var (match, game) = RequirePlaying();
        if (game.Phase != TurnPhase.AwaitingMove || game.CurrentPlayer.Id != context.LocalId)
        {
            throw new SessionException("It is not your move.");
        }

        try
        {
            game.MoveToken(context.LocalId, tokenIndex);
        }
        catch (DomainException ex)
        {
            throw new SessionException(ex.Message);
        }

        context.SendToPlayers(match, new MoveMessage(match.Id, game.RollCount, tokenIndex));
        context.RecordStep(match, new MatchStep(MatchStepKind.Move, context.LocalId, tokenIndex));
        context.FlushGameEvents(match);
    }

    public DispatchResult OnRollRequest(PlayerId from, RollRequestMessage request)
    {
        if (Gate(request.MatchId) is not (var match, var game))
        {
            return Hold(request.MatchId);
        }

        var expected = game.RollCount + 1;
        if (request.RollIndex > expected || (request.RollIndex == expected && game.Phase == TurnPhase.AwaitingMove))
        {
            // The sender is ahead of us: an earlier roll or move has not reached us yet.
            return DispatchResult.Deferred;
        }

        if (request.RollIndex < expected || game.Phase != TurnPhase.AwaitingRoll || match.Dice.OpenRoll == request.RollIndex)
        {
            return DispatchResult.Ignored;
        }

        if (game.CurrentPlayer.Id != from)
        {
            match.AddLog($"Ignored an out-of-turn roll from {context.NameOf(from)}.");
            return DispatchResult.Ignored;
        }

        if (request.StateDigest != context.ComputeDigest(match))
        {
            match.AddLog($"Warning: board state differs from {context.NameOf(from)}'s.");
        }

        OpenRound(match, request.RollIndex, from);
        return DispatchResult.Handled;
    }

    public DispatchResult OnReveal(PlayerId from, RevealMessage reveal)
    {
        if (Gate(reveal.MatchId) is not (var match, var game))
        {
            return Hold(reveal.MatchId);
        }

        if (!match.IsActivePlayer(from)
            || reveal.RollIndex <= game.RollCount
            || reveal.RollIndex > game.RollCount + MaxRevealLookahead)
        {
            return DispatchResult.Ignored;
        }

        StoreReveal(match, from, reveal.RollIndex, reveal.Preimage);
        TryCompleteRound(match);
        return DispatchResult.Handled;
    }

    public DispatchResult OnMove(PlayerId from, MoveMessage move)
    {
        if (Gate(move.MatchId) is not (var match, var game))
        {
            return Hold(move.MatchId);
        }

        if (move.RollIndex > game.RollCount)
        {
            // We are still collecting reveals for the roll this move belongs to.
            return DispatchResult.Deferred;
        }

        if (move.RollIndex < game.RollCount || game.Phase != TurnPhase.AwaitingMove || game.CurrentPlayer.Id != from)
        {
            match.AddLog($"Ignored an out-of-turn move from {context.NameOf(from)}.");
            return DispatchResult.Ignored;
        }

        try
        {
            game.MoveToken(from, move.TokenIndex);
        }
        catch (DomainException)
        {
            match.AddLog($"Rejected an illegal move from {context.NameOf(from)}.");
            return DispatchResult.Ignored;
        }

        context.RecordStep(match, new MatchStep(MatchStepKind.Move, from, move.TokenIndex));
        context.FlushGameEvents(match);
        return DispatchResult.Handled;
    }

    public void OnPeerGone(PlayerId peer)
    {
        if (context.SeatedMatch is { } match)
        {
            DropPlayer(match, peer);
        }
    }

    /// <summary>
    /// Takes a player out of the game (they left or disconnected). If a roll was only waiting for their
    /// share, it completes now instead of stalling.
    /// </summary>
    public void DropPlayer(MatchState match, PlayerId player)
    {
        RemovePlayer(match, player);
        TryCompleteRound(match);
    }

    /// <summary>Takes a player out of the game; their tokens leave the board.</summary>
    private void RemovePlayer(MatchState match, PlayerId player)
    {
        if (match.Game is not { } game || !match.IsActivePlayer(player))
        {
            return;
        }

        game.RemovePlayer(player);
        context.RecordStep(match, new MatchStep(MatchStepKind.Remove, player, 0));
        if (match.Dice.OpenRoll is { } open && (match.Dice.Roller == player || game.Phase == TurnPhase.GameOver))
        {
            // The roller is gone (so the turn has moved on) or the game ended: the round is void.
            match.Dice.Close(open);
        }

        context.FlushGameEvents(match);
    }

    private void OpenRound(MatchState match, int rollIndex, PlayerId roller)
    {
        match.Dice.Open(rollIndex, roller);
        var share = match.Chain!.Reveal(rollIndex);
        StoreReveal(match, context.LocalId, rollIndex, share);
        context.SendToPlayers(match, new RevealMessage(match.Id, rollIndex, share));
        TryCompleteRound(match);
    }

    private static void StoreReveal(MatchState match, PlayerId from, int rollIndex, byte[] share)
    {
        if (!match.Dice.Reveals.TryGetValue(rollIndex, out var shares))
        {
            match.Dice.Reveals[rollIndex] = shares = [];
        }

        shares.TryAdd(from, share);
    }

    private void TryCompleteRound(MatchState match)
    {
        while (match is { IsPlaying: true, Game: { } game } && match.Dice.OpenRoll is { } rollIndex)
        {
            if (!match.Dice.Reveals.TryGetValue(rollIndex, out var shares))
            {
                return;
            }

            var participants = game.Players.Where(p => p.Status != PlayerStatus.Left).ToArray();
            if (!participants.All(p => shares.ContainsKey(p.Id)))
            {
                return;
            }

            var forgers = participants
                .Where(p => !HashChain.Verifies(context.Crypto, match.Dice.Heads[p.Id], shares[p.Id]))
                .ToArray();
            if (forgers.Length > 0)
            {
                // Every player sees the same invalid share, so every player removes the same cheat.
                foreach (var forger in forgers)
                {
                    match.AddLog($"{forger.Name} sent a forged dice share and was removed from the game.");
                    RemovePlayer(match, forger.Id);
                }

                continue;
            }

            var die = DiceDerivation.Derive(context.Crypto, rollIndex, participants.Select(p => shares[p.Id]));
            foreach (var participant in participants)
            {
                match.Dice.Heads[participant.Id] = shares[participant.Id];
            }

            match.Dice.Close(rollIndex);
            var roller = game.CurrentPlayer.Id;
            game.Roll(roller, die);
            context.RecordStep(match, new MatchStep(MatchStepKind.Roll, roller, die.Value));
            context.FlushGameEvents(match);
            return;
        }
    }

    /// <summary>The match and its board, if we are one of its players and it is under way.</summary>
    private (MatchState Match, LudoGame Game)? Gate(Guid matchId) =>
        context.FindMatch(matchId) is { Phase: MatchPhase.Playing, IsPlaying: true, Game: { } game } match
            ? (match, game)
            : null;

    /// <summary>Messages for a match that has not started for us yet wait; anything else is stale or not for us.</summary>
    private DispatchResult Hold(Guid matchId) =>
        context.FindMatch(matchId) is { } match
            ? match.Phase == MatchPhase.Inviting ? DispatchResult.Deferred : DispatchResult.Ignored
            : context.PastMatches.Contains(matchId) ? DispatchResult.Ignored : DispatchResult.Deferred;

    private (MatchState Match, LudoGame Game) RequirePlaying() =>
        context.SeatedMatch is { Game: { } game } match
            ? (match, game)
            : throw new SessionException("You are not playing in a game.");
}
