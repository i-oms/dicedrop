using Ludo.Domain.Board;
using Ludo.Domain.Common;
using Ludo.Domain.Gameplay;
using Ludo.Domain.Players;

namespace Ludo.Domain.Tests;

public sealed class LudoGameTests
{
    private readonly PlayerId _alice = PlayerId.New();
    private readonly PlayerId _bob = PlayerId.New();

    [Theory]
    [InlineData(2, 52)]
    [InlineData(3, 52)]
    [InlineData(4, 52)]
    [InlineData(7, 91)]
    public void Board_has_one_arm_per_player_but_at_least_four(int players, int trackLength)
    {
        var board = new BoardTopology(players);

        Assert.Equal(trackLength, board.MainTrackLength);
        Assert.Equal(trackLength - 2 + BoardTopology.HomeColumnLength + 1, board.FinishProgress);
    }

    [Theory]
    [InlineData(2, new[] { 0, 2 })]
    [InlineData(3, new[] { 0, 1, 2 })]
    [InlineData(6, new[] { 0, 1, 2, 3, 4, 5 })]
    public void Seats_spread_over_the_arms(int players, int[] arms)
    {
        var board = new BoardTopology(players);

        Assert.Equal(arms, Enumerable.Range(0, players).Select(board.ArmOf));
        Assert.Equal(26, new BoardTopology(2).StartSquareOf(1));
    }

    [Fact]
    public void Classic_four_player_board_matches_traditional_path_length()
    {
        Assert.Equal(56, new BoardTopology(4).FinishProgress);
    }

    [Fact]
    public void Tokens_need_the_entry_roll_to_leave_the_yard()
    {
        var game = TwoPlayerGame();

        game.Roll(_alice, new DiceValue(4));

        Assert.Equal(TurnPhase.AwaitingRoll, game.Phase);
        Assert.Equal(_bob, game.CurrentPlayer.Id);
    }

    [Fact]
    public void Six_brings_a_token_out_and_grants_another_roll()
    {
        var game = TwoPlayerGame();

        Play(game, _alice, 6, token: 0);

        Assert.Equal(0, game.TokensOf(0)[0].Progress);
        Assert.Equal(_alice, game.CurrentPlayer.Id);
        Assert.Equal(TurnPhase.AwaitingRoll, game.Phase);
    }

    [Fact]
    public void Landing_on_an_opponent_captures_it_and_grants_another_roll()
    {
        var game = FourPlayerGame();
        Play(game, _alice, 6, token: 0);
        Play(game, _alice, 6, token: 0);
        Play(game, _alice, 3, token: 0);  // Alice on square 9.
        Play(game, _bob, 6, token: 0);    // Bob enters on square 13.
        Play(game, _bob, 1, token: 0);    // Bob on square 14.
        PassIdleSeats(game);

        Play(game, _alice, 5, token: 0);  // Alice 9 -> 14.

        Assert.True(game.TokensOf(1)[0].IsInYard);
        Assert.Equal(_alice, game.CurrentPlayer.Id);
        Assert.Contains(game.DequeueEvents(), e => e is TokenCaptured);
    }

    [Fact]
    public void Tokens_on_safe_squares_cannot_be_captured()
    {
        var game = FourPlayerGame();
        Play(game, _alice, 6, token: 0);
        Play(game, _alice, 6, token: 0);
        Play(game, _alice, 3, token: 0);  // Alice on square 9.
        Play(game, _bob, 6, token: 0);    // Bob on his start square 13, which is safe.
        Play(game, _bob, 6, token: 1);
        Play(game, _bob, 2, token: 1);    // Bob's second token leaves 13 so the turn ends.
        PassIdleSeats(game);

        Play(game, _alice, 4, token: 0);  // Alice 9 -> 13.

        Assert.Equal(0, game.TokensOf(1)[0].Progress);
        Assert.Equal(_bob, game.CurrentPlayer.Id);
    }

    [Fact]
    public void Three_sixes_in_a_row_forfeit_the_turn()
    {
        var game = TwoPlayerGame();
        Play(game, _alice, 6, token: 0);
        Play(game, _alice, 6, token: 1);

        game.Roll(_alice, new DiceValue(6));

        Assert.Equal(_bob, game.CurrentPlayer.Id);
        Assert.Contains(game.DequeueEvents(), e => e is TurnForfeited);
    }

    [Fact]
    public void Overshooting_home_is_not_a_legal_move()
    {
        var game = LudoGame.Start(Guid.NewGuid(), [(_alice, "Alice"), (_bob, "Bob")], GameRules.Classic with { TokensPerPlayer = 1 });
        Play(game, _alice, 6, token: 0);
        while (game.Board.FinishProgress - game.TokensOf(0)[0].Progress > 5)
        {
            Play(game, _alice, 5, token: 0);
            game.Roll(_bob, new DiceValue(1)); // Bob has nothing out, so the turn comes straight back.
        }

        var remaining = game.Board.FinishProgress - game.TokensOf(0)[0].Progress;
        game.Roll(_alice, new DiceValue(remaining + 1));

        Assert.Empty(game.LegalMoves);
        Assert.Equal(remaining, game.Board.FinishProgress - game.TokensOf(0)[0].Progress);
    }

    [Fact]
    public void Entering_from_a_full_yard_is_forced()
    {
        var game = TwoPlayerGame();

        game.Roll(_alice, new DiceValue(6));

        Assert.Equal(4, game.LegalMoves.Count);
        Assert.Equal(0, game.ForcedMove?.TokenIndex);
    }

    [Fact]
    public void A_lone_token_is_forced_on_a_roll_that_cannot_enter()
    {
        var game = TwoPlayerGame();
        Play(game, _alice, 6, token: 2);

        game.Roll(_alice, new DiceValue(3));

        Assert.Equal(2, game.ForcedMove?.TokenIndex);
    }

    [Fact]
    public void A_six_with_a_token_out_and_tokens_in_the_yard_is_a_choice()
    {
        var game = TwoPlayerGame();
        Play(game, _alice, 6, token: 0);

        game.Roll(_alice, new DiceValue(6));

        Assert.Null(game.ForcedMove);
    }

    [Fact]
    public void Tokens_on_different_squares_are_a_choice()
    {
        var game = TwoPlayerGame();
        Play(game, _alice, 6, token: 0);
        Play(game, _alice, 6, token: 1);
        Play(game, _alice, 2, token: 1);
        game.Roll(_bob, new DiceValue(1));

        game.Roll(_alice, new DiceValue(3));

        Assert.Null(game.ForcedMove);
    }

    [Fact]
    public void Tokens_stacked_on_one_square_are_forced()
    {
        var game = TwoPlayerGame();
        Play(game, _alice, 6, token: 0);
        Play(game, _alice, 6, token: 1);
        Play(game, _alice, 1, token: 0);
        game.Roll(_bob, new DiceValue(1));
        Play(game, _alice, 6, token: 1); // Token 1 passes token 0, then token 0 catches it up.
        Play(game, _alice, 5, token: 0);
        game.Roll(_bob, new DiceValue(1));

        game.Roll(_alice, new DiceValue(3));

        Assert.Equal(2, game.LegalMoves.Count);
        Assert.Equal(0, game.ForcedMove?.TokenIndex);
    }

    [Fact]
    public void No_move_is_forced_before_the_roll()
    {
        Assert.Null(TwoPlayerGame().ForcedMove);
    }

    [Fact]
    public void Moving_out_of_turn_is_rejected()
    {
        var game = TwoPlayerGame();

        Assert.Throws<DomainException>(() => game.Roll(_bob, new DiceValue(3)));
    }

    [Fact]
    public void Leaving_player_ends_a_two_player_game_with_the_other_as_winner()
    {
        var game = TwoPlayerGame();

        game.RemovePlayer(_alice);

        Assert.Equal(TurnPhase.GameOver, game.Phase);
        Assert.Equal([_bob], game.Ranking);
    }

    [Fact]
    public void Leaving_on_your_turn_passes_the_turn_on()
    {
        var carol = PlayerId.New();
        var game = LudoGame.Start(Guid.NewGuid(), [(_alice, "Alice"), (_bob, "Bob"), (carol, "Carol")], GameRules.Classic);

        game.RemovePlayer(_alice);

        Assert.Equal(_bob, game.CurrentPlayer.Id);
        Assert.Equal(TurnPhase.AwaitingRoll, game.Phase);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(6, 3)]
    [InlineData(8, 4)]
    public void Random_games_always_finish_with_a_full_ranking(int players, int seed)
    {
        var random = new Random(seed);
        var ids = Enumerable.Range(0, players).Select(_ => PlayerId.New()).ToArray();
        var game = LudoGame.Start(Guid.NewGuid(), ids.Select((id, i) => (id, $"P{i}")).ToArray(), GameRules.Classic);

        for (var guard = 0; game.Phase != TurnPhase.GameOver; guard++)
        {
            Assert.True(guard < 200_000, "game did not finish");
            var current = game.CurrentPlayer.Id;
            game.Roll(current, new DiceValue(random.Next(1, 7)));
            if (game.Phase == TurnPhase.AwaitingMove)
            {
                var moves = game.LegalMoves;
                game.MoveToken(current, moves[random.Next(moves.Count)].TokenIndex);
            }
        }

        Assert.Equal(players, game.Ranking.Distinct().Count());
        Assert.All(game.Players, p => Assert.NotNull(p.Rank));
    }

    private LudoGame TwoPlayerGame() =>
        LudoGame.Start(Guid.NewGuid(), [(_alice, "Alice"), (_bob, "Bob")], GameRules.Classic);

    private LudoGame FourPlayerGame() =>
        LudoGame.Start(
            Guid.NewGuid(),
            [(_alice, "Alice"), (_bob, "Bob"), (PlayerId.New(), "Carol"), (PlayerId.New(), "Dave")],
            GameRules.Classic);

    /// <summary>Seats 2 and 3 have nothing out, so a roll of 1 passes their turns straight on.</summary>
    private static void PassIdleSeats(LudoGame game)
    {
        game.Roll(game.Players[2].Id, new DiceValue(1));
        game.Roll(game.Players[3].Id, new DiceValue(1));
    }

    private static void Play(LudoGame game, PlayerId player, int roll, int token)
    {
        game.Roll(player, new DiceValue(roll));
        game.MoveToken(player, token);
    }
}
