using System.Windows.Input;
using Ludo.Application;
using Ludo.Application.Common;
using Ludo.Application.Session;
using Ludo.Application.UseCases;
using Ludo.Presentation.Wpf.Presenters;
using Ludo.Presentation.Wpf.Shell;

namespace Ludo.Presentation.Wpf.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private object _currentScreen;
    private string? _notice;
    private SessionSnapshot _snapshot = SessionSnapshot.Empty;

    // Which board is on screen is a view concern: the session only says which games exist.
    private Guid? _viewing;
    private Guid? _autoOpened;
    private Guid? _announcedViewing;

    internal MainViewModel(
        LudoApplication app,
        WpfSessionPresenter presenter,
        WpfDiagnosticLog diagnostics,
        CheckForUpdateUseCase checkForUpdate,
        UserSettings settings)
    {
        App = app;
        Diagnostics = new DiagnosticsViewModel(diagnostics, ShowError);
        Update = new UpdateViewModel(checkForUpdate, ShowError);

        // No setup screen: play under the saved name, or the Windows account name until one is saved.
        Settings = new SettingsViewModel(app, settings, ShowError);
        _ = Settings.ApplySavedProfileAsync();
        Searching = new SearchingViewModel(app.AutoLobby.Start(settings.Name ?? Environment.UserName));
        Lobby = new LobbyViewModel(app, ShowError, ViewGame);
        Game = new GameViewModel(app, ShowError, Lobby, BackToLobby);
        OpenSettingsCommand = new AsyncCommand(_ => { Settings.Open(Lobby.Me.Name.Length > 0 ? Lobby.Me.Name : Searching.PlayerName); return Task.CompletedTask; }, ShowError);
        DismissNoticeCommand = new AsyncCommand(_ => { Notice = null; return Task.CompletedTask; }, ShowError);
        OpenSourceCodeCommand = new AsyncCommand(_ => { ProjectLinks.OpenInBrowser(ProjectLinks.SourceCode); return Task.CompletedTask; }, ShowError);

        presenter.SessionChanged += Apply;
        presenter.NoticeRaised += message => Notice = message;

        // The list is on the lobby screen, so someone at the board needs telling.
        presenter.PollActivity += activity =>
        {
            if (activity.IsNew && CurrentScreen == Game)
            {
                Notice = $"{activity.From} started a group decision. Go back to the lobby to vote.";
            }
        };

        _currentScreen = Searching;
    }

    internal LudoApplication App { get; }

    public SearchingViewModel Searching { get; }

    public SettingsViewModel Settings { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public UpdateViewModel Update { get; }

    public LobbyViewModel Lobby { get; }

    public GameViewModel Game { get; }

    public object CurrentScreen
    {
        get => _currentScreen;
        private set
        {
            if (SetProperty(ref _currentScreen, value))
            {
                OnPropertyChanged(nameof(CanCloseEveryone));
            }
        }
    }

    /// <summary>There is nobody to close for until a lobby has been found.</summary>
    public bool CanCloseEveryone => CurrentScreen != Searching;

    public string? Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public ICommand DismissNoticeCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    /// <summary>Opens the project's GitHub repository in the browser.</summary>
    public ICommand OpenSourceCodeCommand { get; }

    private void Apply(SessionSnapshot snapshot)
    {
        _snapshot = snapshot;
        Refresh();
    }

    private void Refresh()
    {
        var snapshot = _snapshot;
        if (snapshot.Stage == SessionStage.Idle)
        {
            _viewing = null;
            AnnounceViewing();
            Game.Clear();
            Lobby.Clear();
            Searching.PlayerName = Settings.Saved.Name ?? Searching.PlayerName;
            CurrentScreen = Searching;
            return;
        }

        // Players are taken to the board when their game begins, once; after that they come and go freely.
        var match = snapshot.Match;
        if (match is { IsPlaying: true, Phase: MatchPhaseView.Playing } && _autoOpened != match.Id)
        {
            _autoOpened = match.Id;
            _viewing = match.Id;
        }

        var viewed = snapshot.Matches.FirstOrDefault(m => m.Id == _viewing && m.Game is not null);
        _viewing = viewed?.Id;
        AnnounceViewing();

        Lobby.Update(snapshot);
        Searching.PlayerName = snapshot.LocalName;
        if (viewed is null)
        {
            Game.Clear();
            CurrentScreen = Lobby;
            return;
        }

        Game.Update(viewed);
        CurrentScreen = Game;
    }

    /// <summary>The session only says which games exist, so others learn who is watching from here.</summary>
    private void AnnounceViewing()
    {
        if (_announcedViewing != _viewing)
        {
            _announcedViewing = _viewing;
            _ = App.WatchGame.ExecuteAsync(_viewing);
        }
    }

    /// <summary>Shows the lobby screen with a conversation open, e.g. after clicking a chat pop-up.</summary>
    internal void OpenConversation(string key)
    {
        BackToLobby();
        Lobby.OpenConversation(key);
    }

    private void ViewGame(Guid matchId)
    {
        _viewing = matchId;
        Refresh();
    }

    internal void BackToLobby()
    {
        _viewing = null;
        Refresh();
    }

    internal void ShowError(Exception error) =>
        Notice = error is SessionException ? error.Message : $"Something went wrong: {error.Message}";
}
