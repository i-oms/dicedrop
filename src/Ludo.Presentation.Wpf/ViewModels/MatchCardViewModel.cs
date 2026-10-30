using System.Windows.Input;
using Ludo.Application;
using Ludo.Application.Session;

namespace Ludo.Presentation.Wpf.ViewModels;

/// <summary>One invite or game in the games panel, with what we can do about it.</summary>
public sealed class MatchCardViewModel : ObservableObject
{
    private readonly AsyncCommand _beginCommand;
    private IReadOnlyList<InviteeItem> _invitees = [];
    private bool _canBegin;
    private string _gameTitle = string.Empty;
    private string _gameDetail = string.Empty;
    private string _beginLabel = "Begin game";
    private string _watchLabel = "Watch";
    private bool _isInvitedNow;
    private bool _showBegin;
    private bool _showCancel;
    private bool _showAccept;
    private bool _showDecline;
    private bool _showWatch;
    private bool _showStop;

    internal MatchCardViewModel(Guid matchId, LudoApplication app, Action<Exception> onError, Action<Guid> viewGame)
    {
        MatchId = matchId;
        _beginCommand = new AsyncCommand(_ => app.StartGame.ExecuteAsync(), onError, _ => CanBegin);
        AcceptCommand = new AsyncCommand(_ => app.AcceptInvite.ExecuteAsync(matchId), onError);
        DeclineCommand = new AsyncCommand(_ => app.DeclineInvite.ExecuteAsync(matchId), onError);
        StopCommand = new AsyncCommand(_ => app.StopGame.ExecuteAsync(), onError);
        WatchCommand = new AsyncCommand(_ => { viewGame(matchId); return Task.CompletedTask; }, onError);
    }

    public Guid MatchId { get; }

    public bool CanBegin
    {
        get => _canBegin;
        private set
        {
            if (SetProperty(ref _canBegin, value))
            {
                _beginCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string GameTitle
    {
        get => _gameTitle;
        private set => SetProperty(ref _gameTitle, value);
    }

    public string GameDetail
    {
        get => _gameDetail;
        private set => SetProperty(ref _gameDetail, value);
    }

    public IReadOnlyList<InviteeItem> Invitees
    {
        get => _invitees;
        private set => SetProperty(ref _invitees, value);
    }

    /// <summary>An invite is waiting for our answer; the card is highlighted.</summary>
    public bool IsInvitedNow
    {
        get => _isInvitedNow;
        private set => SetProperty(ref _isInvitedNow, value);
    }

    public string BeginLabel
    {
        get => _beginLabel;
        private set => SetProperty(ref _beginLabel, value);
    }

    public string WatchLabel
    {
        get => _watchLabel;
        private set => SetProperty(ref _watchLabel, value);
    }

    public bool ShowBegin
    {
        get => _showBegin;
        private set => SetProperty(ref _showBegin, value);
    }

    public bool ShowCancel
    {
        get => _showCancel;
        private set => SetProperty(ref _showCancel, value);
    }

    public bool ShowAccept
    {
        get => _showAccept;
        private set => SetProperty(ref _showAccept, value);
    }

    public bool ShowDecline
    {
        get => _showDecline;
        private set => SetProperty(ref _showDecline, value);
    }

    public bool ShowWatch
    {
        get => _showWatch;
        private set => SetProperty(ref _showWatch, value);
    }

    public bool ShowStop
    {
        get => _showStop;
        private set => SetProperty(ref _showStop, value);
    }

    public ICommand BeginCommand => _beginCommand;

    public ICommand AcceptCommand { get; }

    public ICommand DeclineCommand { get; }

    /// <summary>Withdraws our invite or stops our game.</summary>
    public ICommand StopCommand { get; }

    public ICommand WatchCommand { get; }

    internal void Update(MatchView match, Func<string, string, PersonViewModel> personOf)
    {
        var inviting = match.Phase == MatchPhaseView.Inviting;
        var running = match.Phase == MatchPhaseView.Playing;
        var mine = match.IsInitiator;
        var answer = match.LocalAnswer;

        ShowBegin = inviting && mine;
        ShowCancel = inviting && mine;
        ShowAccept = match.CanAnswer && answer != InviteAnswer.Accepted;
        ShowDecline = match.CanAnswer && answer == InviteAnswer.Accepted;
        ShowStop = running && mine;
        ShowWatch = match.Game is not null;
        WatchLabel = running ? (match.IsPlaying ? "Return to game" : "Watch") : "View last game";
        IsInvitedNow = match.CanAnswer && answer == InviteAnswer.Pending;
        CanBegin = match.CanStart;

        Invitees = inviting
            ? match.Invitees
                .Select(i => new InviteeItem(
                    personOf(i.Key, i.Name),
                    i.IsInitiator ? "Host" : i.Answer switch { InviteAnswer.Accepted => "In", InviteAnswer.Declined => "Out", _ => "Invited" },
                    i.IsInitiator || i.Answer == InviteAnswer.Accepted))
                .ToArray()
            : [];
        var players = match.Invitees.Count(i => i.Answer == InviteAnswer.Accepted);
        BeginLabel = $"Begin game ({players} players)";

        var game = match.GameType.Name;
        (GameTitle, GameDetail) = match switch
        {
            { Phase: MatchPhaseView.Inviting, IsInitiator: true } => (
                $"Waiting for players",
                players > 1 ? "Begin whenever everyone who wants to play has said yes." : "Nobody has said yes yet."),
            { Phase: MatchPhaseView.Inviting, IsInvited: false } => (
                $"{match.InitiatorName} is setting up {game}",
                "You weren't invited this time, but you can watch once it begins."),
            { Phase: MatchPhaseView.Inviting, CanAnswer: false } => (
                $"{match.InitiatorName} is setting up {game}",
                "You can join if your game ends before this one begins."),
            { Phase: MatchPhaseView.Inviting } when answer == InviteAnswer.Accepted => (
                "You're in!",
                $"Waiting for {match.InitiatorName} to begin the game…"),
            { Phase: MatchPhaseView.Inviting } when answer == InviteAnswer.Declined => (
                $"{match.InitiatorName} is setting up {game}",
                "You said not now. You can still join until it begins."),
            { Phase: MatchPhaseView.Inviting } => (
                $"{match.InitiatorName} invited you to {game}",
                "Join in, or skip this one and watch."),
            { Phase: MatchPhaseView.Playing } => (
                match.IsInitiator ? $"Your {game} game is on" : $"{match.InitiatorName}'s {game} game is on",
                $"{string.Join(", ", match.Game?.Seats.Where(s => s.Standing != SeatStanding.Left).Select(s => s.Name) ?? [])} playing."),
            _ => ($"Last game: {game}", match.EndReason ?? "The game is over."),
        };
    }
}
