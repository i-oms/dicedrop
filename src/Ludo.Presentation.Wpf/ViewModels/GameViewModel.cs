using System.Windows.Input;
using System.Windows.Media;
using Ludo.Application;
using Ludo.Application.Session;
using Ludo.Presentation.Wpf.Views.Board;

namespace Ludo.Presentation.Wpf.ViewModels;

public sealed record SeatItem(string Name, Brush Color, string Detail, bool IsCurrent, bool IsLocal, bool HasLeft);

public sealed class GameViewModel : ObservableObject
{
    private readonly AsyncCommand _rollCommand;
    private Guid? _matchId;
    private GameStateView? _game;
    private IReadOnlyList<SeatItem> _seats = [];
    private IReadOnlyList<string> _log = [];
    private string _title = string.Empty;
    private string _status = string.Empty;
    private bool _canRoll;
    private int? _diceValue;
    private int _rollCount;
    private bool _isRolling;
    private Brush _turnColor = Brushes.White;
    private bool _isOver;
    private string _outcome = string.Empty;
    private string _ranking = string.Empty;
    private bool _canLeave;
    private bool _canStop;

    internal GameViewModel(LudoApplication app, Action<Exception> onError, LobbyViewModel lobby, Action backToLobby)
    {
        Lobby = lobby;
        _rollCommand = new AsyncCommand(_ => app.RollDice.ExecuteAsync(), onError, _ => CanRoll);
        MoveCommand = new AsyncCommand(
            token => token is int index ? app.MoveToken.ExecuteAsync(index) : Task.CompletedTask,
            onError);
        LeaveCommand = new AsyncCommand(
            async _ =>
            {
                await app.LeaveGame.ExecuteAsync();
                backToLobby();
            },
            onError);
        StopCommand = new AsyncCommand(_ => app.StopGame.ExecuteAsync(), onError);
        BackCommand = new AsyncCommand(_ => { backToLobby(); return Task.CompletedTask; }, onError);
    }

    /// <summary>For the chat panel beside the board.</summary>
    public LobbyViewModel Lobby { get; }

    public GameStateView? Game
    {
        get => _game;
        private set => SetProperty(ref _game, value);
    }

    public IReadOnlyList<SeatItem> Seats
    {
        get => _seats;
        private set => SetProperty(ref _seats, value);
    }

    /// <summary>Newest entry first.</summary>
    public IReadOnlyList<string> Log
    {
        get => _log;
        private set => SetProperty(ref _log, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool CanRoll
    {
        get => _canRoll;
        private set
        {
            if (SetProperty(ref _canRoll, value))
            {
                _rollCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int? DiceValue
    {
        get => _diceValue;
        private set => SetProperty(ref _diceValue, value);
    }

    public int RollCount
    {
        get => _rollCount;
        private set => SetProperty(ref _rollCount, value);
    }

    public bool IsRolling
    {
        get => _isRolling;
        private set => SetProperty(ref _isRolling, value);
    }

    /// <summary>Colour of whoever's turn it is.</summary>
    public Brush TurnColor
    {
        get => _turnColor;
        private set => SetProperty(ref _turnColor, value);
    }

    public bool IsOver
    {
        get => _isOver;
        private set => SetProperty(ref _isOver, value);
    }

    public string Outcome
    {
        get => _outcome;
        private set => SetProperty(ref _outcome, value);
    }

    public string Ranking
    {
        get => _ranking;
        private set => SetProperty(ref _ranking, value);
    }

    public bool CanLeave
    {
        get => _canLeave;
        private set => SetProperty(ref _canLeave, value);
    }

    public bool CanStop
    {
        get => _canStop;
        private set => SetProperty(ref _canStop, value);
    }

    public ICommand RollCommand => _rollCommand;

    public ICommand MoveCommand { get; }

    /// <summary>Drop out of the game and go back to the lobby.</summary>
    public ICommand LeaveCommand { get; }

    /// <summary>The initiator ends the game for everyone.</summary>
    public ICommand StopCommand { get; }

    /// <summary>Back to the lobby without leaving; the game carries on.</summary>
    public ICommand BackCommand { get; }

    /// <summary>Forgets the board, so the next game (or the next visit) starts without replaying old moves.</summary>
    internal void Clear()
    {
        _matchId = null;
        Game = null;
    }

    /// <summary>Shows the game on screen, which need not be the one we play in.</summary>
    internal void Update(MatchView match)
    {
        if (match.Game is not { } game)
        {
            return;
        }

        if (_matchId != match.Id)
        {
            Game = null;
            _matchId = match.Id;
        }

        Game = game;
        Seats = game.Seats.Select(s => new SeatItem(
            s.IsLocal ? $"{s.Name} (you)" : s.Name,
            SeatPalette.Brush(s.Arm),
            s.Standing switch
            {
                SeatStanding.Finished => $"Finished #{s.Rank}",
                SeatStanding.Left => s.Rank is { } rank ? $"Finished #{rank}, left" : "Left",
                _ => $"{s.Tokens.Count(t => t.Location == TokenLocation.Finished)}/{s.Tokens.Count} home",
            },
            s.IsCurrent,
            s.IsLocal,
            s.Standing == SeatStanding.Left)).ToArray();
        Log = match.Log.Reverse().ToArray();
        DiceValue = game.LastRoll;
        RollCount = game.RollCount;
        IsRolling = game.Phase == GamePhaseView.Rolling;
        CanRoll = game.IsLocalTurn && game.Phase == GamePhaseView.WaitingForRoll;
        IsOver = match.Phase is MatchPhaseView.Finished or MatchPhaseView.Stopped;
        CanLeave = match.IsPlaying && !match.IsInitiator && !IsOver;
        CanStop = match.IsInitiator && match.Phase == MatchPhaseView.Playing;

        var current = game.Seats[game.CurrentSeat];
        TurnColor = SeatPalette.Brush(current.Arm);
        Title = match.IsInitiator ? "Your game"
            : match.IsPlaying ? $"{match.InitiatorName}'s game"
            : $"Watching {match.InitiatorName}'s game";
        Status = game.Phase switch
        {
            _ when match.Phase == MatchPhaseView.Stopped => "Game stopped",
            GamePhaseView.Over => "Game over",
            GamePhaseView.Rolling => game.IsLocalTurn ? "Rolling…" : $"{current.Name} is rolling…",
            GamePhaseView.WaitingForRoll => game.IsLocalTurn ? "Your turn: roll the dice" : $"{current.Name} is about to roll",
            _ => game.IsLocalTurn ? "Pick one of your bouncing pawns" : $"{current.Name} is choosing a move",
        };

        Outcome = match.Phase == MatchPhaseView.Stopped ? match.EndReason ?? "The game was stopped." : "Final standings";
        Ranking = string.Join(
            Environment.NewLine,
            game.Seats.Where(s => s.Rank is not null).OrderBy(s => s.Rank).Select(s => $"{s.Rank}. {s.Name}"));
    }
}
