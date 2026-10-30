using Ludo.Domain.Board;
using Ludo.Domain.Common;
using Ludo.Domain.Players;

namespace Ludo.Domain.Gameplay;

/// <summary>
/// Aggregate root for one match. It is fully deterministic: dice values are supplied from outside,
/// so every peer that applies the same rolls and moves in the same order reaches the same state.
/// </summary>
public sealed class LudoGame
{
    private readonly Player[] _players;
    private readonly TokenPosition[][] _tokens;
    private readonly List<PlayerId> _ranking = [];
    private readonly List<IDomainEvent> _pendingEvents = [];
    private List<Move> _legalMoves = [];

    private LudoGame(Guid id, Player[] players, GameRules rules)
    {
        Id = id;
        Rules = rules;
        Board = new BoardTopology(players.Length);
        _players = players;
        _tokens = players
            .Select(_ => Enumerable.Repeat(TokenPosition.Yard, rules.TokensPerPlayer).ToArray())
            .ToArray();
        Phase = TurnPhase.AwaitingRoll;
    }

    public Guid Id { get; }

    public GameRules Rules { get; }

    public BoardTopology Board { get; }

    public IReadOnlyList<Player> Players => _players;

    public int CurrentSeat { get; private set; }

    public Player CurrentPlayer => _players[CurrentSeat];

    public TurnPhase Phase { get; private set; }

    public DiceValue? LastRoll { get; private set; }

    public int ConsecutiveSixes { get; private set; }

    /// <summary>Number of rolls applied so far; also the sequence number of the latest roll.</summary>
    public int RollCount { get; private set; }

    public IReadOnlyList<Move> LegalMoves => _legalMoves;

    /// <summary>The move to play when the roll leaves no real choice: all legal moves start from the same spot, so they are alike.</summary>
    public Move? ForcedMove =>
        _legalMoves.Count > 0 && _legalMoves.TrueForAll(m => m.From == _legalMoves[0].From) ? _legalMoves[0] : null;

    public IReadOnlyList<PlayerId> Ranking => _ranking;

    public static LudoGame Start(Guid id, IReadOnlyList<(PlayerId Id, string Name)> seats, GameRules rules)
    {
        ArgumentNullException.ThrowIfNull(seats);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        if (seats.Count < BoardTopology.MinPlayers)
        {
            throw new DomainException($"A game needs at least {BoardTopology.MinPlayers} players.");
        }

        if (seats.Select(s => s.Id).Distinct().Count() != seats.Count)
        {
            throw new DomainException("A player cannot take two seats.");
        }

        var players = seats.Select((s, seat) => new Player(s.Id, s.Name, seat)).ToArray();
        var game = new LudoGame(id, players, rules);
        game.Raise(new TurnChanged(players[0].Id, 0));
        return game;
    }

    public IReadOnlyList<TokenPosition> TokensOf(int seat) => _tokens[seat];

    public Player? FindPlayer(PlayerId id) => Array.Find(_players, p => p.Id == id);

    public IReadOnlyList<IDomainEvent> DequeueEvents()
    {
        var events = _pendingEvents.ToArray();
        _pendingEvents.Clear();
        return events;
    }

    public void Roll(PlayerId playerId, DiceValue value)
    {
        EnsurePhase(TurnPhase.AwaitingRoll);
        EnsureCurrent(playerId);

        RollCount++;
        LastRoll = value;
        ConsecutiveSixes = value.IsSix ? ConsecutiveSixes + 1 : 0;
        Raise(new DiceRolled(playerId, value.Value));

        if (value.IsSix && Rules.MaxConsecutiveSixes > 0 && ConsecutiveSixes >= Rules.MaxConsecutiveSixes)
        {
            Raise(new TurnForfeited(playerId, $"rolled {ConsecutiveSixes} sixes in a row"));
            AdvanceTurn();
            return;
        }

        _legalMoves = ComputeLegalMoves(CurrentSeat, value);
        if (_legalMoves.Count == 0)
        {
            Raise(new NoMovesAvailable(playerId));
            EndTurn(extraTurn: value.IsSix && Rules.ExtraTurnOnSix);
            return;
        }

        Phase = TurnPhase.AwaitingMove;
    }

    public Move MoveToken(PlayerId playerId, int tokenIndex)
    {
        EnsurePhase(TurnPhase.AwaitingMove);
        EnsureCurrent(playerId);

        var move = _legalMoves.Find(m => m.TokenIndex == tokenIndex)
            ?? throw new DomainException("That token cannot move with the current roll.");

        var seat = CurrentSeat;
        _tokens[seat][tokenIndex] = move.To;
        Raise(new TokenMoved(playerId, tokenIndex, move.From, move.To));

        foreach (var capture in move.Captures)
        {
            _tokens[capture.Seat][capture.TokenIndex] = TokenPosition.Yard;
            Raise(new TokenCaptured(playerId, _players[capture.Seat].Id, capture.TokenIndex));
        }

        var reachedHome = Board.ZoneOf(move.To) == TokenZone.Finished;
        if (reachedHome)
        {
            Raise(new TokenReachedHome(playerId, tokenIndex));
        }

        _legalMoves = [];

        if (_tokens[seat].All(t => Board.ZoneOf(t) == TokenZone.Finished))
        {
            MarkFinished(_players[seat]);
            if (!TryEndGame())
            {
                AdvanceTurn();
            }

            return move;
        }

        var extraTurn = (LastRoll is { IsSix: true } && Rules.ExtraTurnOnSix)
            || (move.Captures.Count > 0 && Rules.ExtraTurnOnCapture)
            || (reachedHome && Rules.ExtraTurnOnFinish);
        EndTurn(extraTurn);
        return move;
    }

    /// <summary>Removes a player who disconnected or forfeited; their tokens leave the board.</summary>
    public void RemovePlayer(PlayerId playerId)
    {
        var player = FindPlayer(playerId) ?? throw new DomainException("Unknown player.");
        if (player.Status == PlayerStatus.Left || Phase == TurnPhase.GameOver)
        {
            return;
        }

        var wasRacing = player.Status == PlayerStatus.Playing;
        player.MarkLeft();
        Raise(new PlayerLeft(playerId));

        if (!wasRacing || TryEndGame())
        {
            return;
        }

        if (CurrentSeat == player.Seat)
        {
            AdvanceTurn();
        }
    }

    private List<Move> ComputeLegalMoves(int seat, DiceValue roll)
    {
        var moves = new List<Move>();
        for (var i = 0; i < _tokens[seat].Length; i++)
        {
            var from = _tokens[seat][i];
            TokenPosition to;
            switch (Board.ZoneOf(from))
            {
                case TokenZone.Yard when roll.Value == Rules.EntryRoll:
                    to = TokenPosition.AtProgress(0);
                    break;
                case TokenZone.Track or TokenZone.HomeColumn when from.Progress + roll.Value <= Board.FinishProgress:
                    to = TokenPosition.AtProgress(from.Progress + roll.Value);
                    break;
                default:
                    continue;
            }

            moves.Add(new Move(i, from, to, FindCaptures(seat, to)));
        }

        return moves;
    }

    private List<TokenRef> FindCaptures(int seat, TokenPosition to)
    {
        var square = Board.TrackSquareOf(seat, to);
        if (square is null || Board.IsSafeSquare(square.Value))
        {
            return [];
        }

        var captures = new List<TokenRef>();
        foreach (var other in _players)
        {
            if (other.Seat == seat || other.Status != PlayerStatus.Playing)
            {
                continue;
            }

            for (var t = 0; t < _tokens[other.Seat].Length; t++)
            {
                if (Board.TrackSquareOf(other.Seat, _tokens[other.Seat][t]) == square)
                {
                    captures.Add(new TokenRef(other.Seat, t));
                }
            }
        }

        return captures;
    }

    private void EndTurn(bool extraTurn)
    {
        _legalMoves = [];
        if (extraTurn)
        {
            Phase = TurnPhase.AwaitingRoll;
            Raise(new ExtraTurnGranted(CurrentPlayer.Id));
            return;
        }

        AdvanceTurn();
    }

    private void AdvanceTurn()
    {
        ConsecutiveSixes = 0;
        _legalMoves = [];
        for (var step = 1; step <= _players.Length; step++)
        {
            var seat = (CurrentSeat + step) % _players.Length;
            if (_players[seat].Status == PlayerStatus.Playing)
            {
                CurrentSeat = seat;
                Phase = TurnPhase.AwaitingRoll;
                Raise(new TurnChanged(_players[seat].Id, seat));
                return;
            }
        }
    }

    private bool TryEndGame()
    {
        var racing = _players.Where(p => p.Status == PlayerStatus.Playing).ToList();
        if (racing.Count > 1)
        {
            return false;
        }

        if (racing.Count == 1)
        {
            MarkFinished(racing[0]);
        }

        Phase = TurnPhase.GameOver;
        _legalMoves = [];
        Raise(new GameEnded(_ranking.ToArray()));
        return true;
    }

    private void MarkFinished(Player player)
    {
        _ranking.Add(player.Id);
        player.MarkFinished(_ranking.Count);
        Raise(new PlayerFinished(player.Id, _ranking.Count));
    }

    private void EnsurePhase(TurnPhase expected)
    {
        if (Phase != expected)
        {
            throw new DomainException($"Not allowed now: the game is in phase {Phase}.");
        }
    }

    private void EnsureCurrent(PlayerId playerId)
    {
        if (CurrentPlayer.Id != playerId)
        {
            throw new DomainException("It is not this player's turn.");
        }
    }

    private void Raise(IDomainEvent domainEvent) => _pendingEvents.Add(domainEvent);
}
