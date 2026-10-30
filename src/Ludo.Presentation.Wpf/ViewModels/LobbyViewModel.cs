using System.Collections.ObjectModel;
using System.Windows.Input;
using Ludo.Application;
using Ludo.Application.Session;

namespace Ludo.Presentation.Wpf.ViewModels;

/// <param name="Answer">"Host", "In", "Out" or "Invited".</param>
public sealed record InviteeItem(PersonViewModel Person, string Answer, bool IsIn);

/// <summary>A game from the catalog, as listed in the games panel.</summary>
public sealed record GameCardItem(GameInfo Info)
{
    public string Players => $"{Info.MinPlayers}–{Info.MaxPlayers} players";
}

/// <summary>Someone who can be ticked in the invite picker.</summary>
public sealed class InviteChoice(PersonViewModel person) : ObservableObject
{
    private bool _isChecked;

    public PersonViewModel Person { get; } = person;

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}

/// <summary>
/// The main screen: the people in the lobby, the group chat and private conversations with each of them,
/// and the games panel where anyone can invite some or all of the lobby to play.
/// </summary>
public sealed class LobbyViewModel : ObservableObject
{
    private readonly LudoApplication _app;
    private readonly Action<Exception> _onError;
    private readonly Dictionary<string, PersonViewModel> _people = [];
    private readonly Dictionary<string, ConversationViewModel> _direct = [];
    private readonly AsyncCommand _sendInviteCommand;
    private readonly AsyncCommand _beginCommand;
    private ConversationViewModel _selected;
    private PersonViewModel _me = new(string.Empty);
    private IReadOnlyList<PersonViewModel> _members = [];
    private IReadOnlyList<InviteeItem> _invitees = [];
    private string _status = string.Empty;
    private bool _canPropose;
    private bool _canBegin;
    private GameInfo? _picking;
    private bool _hasMatch;
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
    private int _totalUnread;

    internal LobbyViewModel(LudoApplication app, Action<Exception> onError, Action viewGame)
    {
        _app = app;
        _onError = onError;
        Notifications = new NotificationsViewModel(app, onError);
        LobbyChat = new ConversationViewModel(null, app, onError) { IsSelected = true };
        Conversations.Add(LobbyChat);
        _selected = LobbyChat;

        _sendInviteCommand = new AsyncCommand(
            async _ =>
            {
                var game = _picking!;
                var keys = InviteChoices.Where(c => c.IsChecked).Select(c => c.Person.Key).ToArray();
                await app.ProposeGame.ExecuteAsync(game.Id, keys.Length == InviteChoices.Count ? null : keys);
                Picking = null;
            },
            onError,
            _ => Picking is not null && InviteChoices.Any(c => c.IsChecked));
        _beginCommand = new AsyncCommand(_ => app.StartGame.ExecuteAsync(), onError, _ => CanBegin);

        SelectCommand = new AsyncCommand(item => { Select(item as ConversationViewModel); return Task.CompletedTask; }, onError);
        PickPlayersCommand = new AsyncCommand(game => { StartPicking((game as GameCardItem)?.Info); return Task.CompletedTask; }, onError);
        CancelPickCommand = new AsyncCommand(_ => { Picking = null; return Task.CompletedTask; }, onError);
        InvitePersonCommand = new AsyncCommand(
            key => app.ProposeGame.ExecuteAsync(GameCatalog.Ludo.Id, [(string)key!]),
            onError,
            _ => CanPropose);
        AcceptCommand = new AsyncCommand(_ => app.AcceptInvite.ExecuteAsync(), onError);
        DeclineCommand = new AsyncCommand(_ => app.DeclineInvite.ExecuteAsync(), onError);
        StopCommand = new AsyncCommand(_ => app.StopGame.ExecuteAsync(), onError);
        WatchCommand = new AsyncCommand(_ => { viewGame(); return Task.CompletedTask; }, onError);
        foreach (var game in GameCatalog.All)
        {
            Games.Add(new GameCardItem(game));
        }
    }

    public NotificationsViewModel Notifications { get; }

    /// <summary>The lobby's group chat, always first in the list.</summary>
    public ConversationViewModel LobbyChat { get; }

    /// <summary>The group chat, then a private conversation per person.</summary>
    public ObservableCollection<ConversationViewModel> Conversations { get; } = [];

    public ConversationViewModel SelectedConversation
    {
        get => _selected;
        private set => SetProperty(ref _selected, value);
    }

    public PersonViewModel Me
    {
        get => _me;
        private set => SetProperty(ref _me, value);
    }

    /// <summary>Everyone else in the lobby, in join order.</summary>
    public IReadOnlyList<PersonViewModel> Members
    {
        get => _members;
        private set => SetProperty(ref _members, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Unread lines across all conversations, for the tray.</summary>
    public int TotalUnread
    {
        get => _totalUnread;
        private set => SetProperty(ref _totalUnread, value);
    }

    public ObservableCollection<GameCardItem> Games { get; } = [];

    /// <summary>The game whose invite list is being picked; null when the picker is closed.</summary>
    public GameInfo? Picking
    {
        get => _picking;
        private set
        {
            if (SetProperty(ref _picking, value))
            {
                OnPropertyChanged(nameof(IsPicking));
                _sendInviteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPicking => Picking is not null;

    public ObservableCollection<InviteChoice> InviteChoices { get; } = [];

    public bool CanPropose
    {
        get => _canPropose;
        private set
        {
            if (SetProperty(ref _canPropose, value))
            {
                ((AsyncCommand)InvitePersonCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>An invite is open or a game exists, so the match card shows instead of the catalog.</summary>
    public bool HasMatch
    {
        get => _hasMatch;
        private set => SetProperty(ref _hasMatch, value);
    }

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

    public ICommand SelectCommand { get; }

    /// <summary>Opens the invite picker for a game from the catalog.</summary>
    public ICommand PickPlayersCommand { get; }

    public ICommand CancelPickCommand { get; }

    public ICommand SendInviteCommand => _sendInviteCommand;

    /// <summary>Invites one person, by key, to a game of Ludo.</summary>
    public ICommand InvitePersonCommand { get; }

    public ICommand BeginCommand => _beginCommand;

    public ICommand AcceptCommand { get; }

    public ICommand DeclineCommand { get; }

    /// <summary>Withdraws our invite or stops our game.</summary>
    public ICommand StopCommand { get; }

    public ICommand WatchCommand { get; }

    /// <summary>Opens a conversation by person key; the empty key is the lobby chat.</summary>
    internal void OpenConversation(string key) =>
        Select(key.Length == 0 ? LobbyChat : _direct.GetValueOrDefault(key));

    internal PersonViewModel? FindPerson(string key) => _people.GetValueOrDefault(key);

    /// <summary>Who can be written to from the tray: the lobby, then everyone present.</summary>
    internal IEnumerable<ConversationViewModel> WritableConversations() => Conversations.Where(c => c.CanWrite);

    internal void Update(SessionSnapshot snapshot)
    {
        foreach (var member in snapshot.Members)
        {
            PersonOf(member.Key, member.Name).Update(member);
        }

        var present = snapshot.Members.Select(m => m.Key).ToHashSet();
        foreach (var person in _people.Values.Where(p => !present.Contains(p.Key)))
        {
            person.IsPresent = false;
        }

        Me = snapshot.Members.FirstOrDefault(m => m.IsLocal) is { } local ? _people[local.Key] : Me;
        Members = snapshot.Members.Where(m => !m.IsLocal).Select(m => _people[m.Key]).ToArray();
        CanPropose = snapshot.CanProposeGame;
        Notifications.Update(snapshot.Polls, Members, PersonOf);

        LobbyChat.Sync(snapshot.Chat, SenderOf);
        foreach (var member in Members)
        {
            ConversationWith(member);
        }

        foreach (var chat in snapshot.DirectChats)
        {
            ConversationWith(PersonOf(chat.PeerKey, chat.PeerName)).Sync(chat.Lines, SenderOf);
        }

        OrderConversations();
        TotalUnread = Conversations.Sum(c => c.Unread);

        var connecting = snapshot.Members.Count(m => !m.IsReady);
        Status = snapshot.Members.Count < 2 ? "Waiting for others on your network…"
            : connecting > 0 ? $"Connecting ({connecting} pending)…"
            : $"{snapshot.Members.Count} online";

        if (Picking is not null)
        {
            SyncInviteChoices();
        }

        UpdateGameCard(snapshot);
    }

    internal void Clear()
    {
        foreach (var person in _people.Values)
        {
            person.IsPresent = false;
        }

        Notifications.Update([], [], PersonOf);
    }

    private PersonViewModel SenderOf(ChatLineView line) => PersonOf(line.SenderKey, line.Sender);

    private PersonViewModel PersonOf(string key, string name)
    {
        if (!_people.TryGetValue(key, out var person))
        {
            _people[key] = person = new PersonViewModel(key);
            person.UpdateName(name);
        }

        return person;
    }

    private ConversationViewModel ConversationWith(PersonViewModel person)
    {
        if (!_direct.TryGetValue(person.Key, out var conversation))
        {
            _direct[person.Key] = conversation = new ConversationViewModel(person, _app, _onError);
            conversation.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConversationViewModel.Unread))
                {
                    TotalUnread = Conversations.Sum(c => c.Unread);
                }
            };
        }

        return conversation;
    }

    /// <summary>The lobby, then people present in join order, then people who left but whose messages we keep.</summary>
    private void OrderConversations()
    {
        var order = Members.Select(m => _direct[m.Key])
            .Concat(_direct.Values.Where(c => !c.Person!.IsPresent && c.Messages.Count > 0))
            .Prepend(LobbyChat)
            .ToList();
        if (!order.Contains(SelectedConversation))
        {
            Select(LobbyChat);
        }

        for (var i = 0; i < order.Count; i++)
        {
            var at = Conversations.IndexOf(order[i]);
            if (at < 0)
            {
                Conversations.Insert(i, order[i]);
            }
            else if (at != i)
            {
                Conversations.Move(at, i);
            }
        }

        while (Conversations.Count > order.Count)
        {
            Conversations.RemoveAt(Conversations.Count - 1);
        }
    }

    private void Select(ConversationViewModel? conversation)
    {
        if (conversation is null || conversation == SelectedConversation)
        {
            return;
        }

        SelectedConversation.IsSelected = false;
        conversation.IsSelected = true;
        SelectedConversation = conversation;
    }

    private void StartPicking(GameInfo? game)
    {
        if (game is null)
        {
            return;
        }

        InviteChoices.Clear();
        Picking = game;
        SyncInviteChoices();
        foreach (var choice in InviteChoices)
        {
            choice.IsChecked = true;
        }
    }

    /// <summary>Keeps the picker's list in line with who is in the lobby, keeping the ticks already made.</summary>
    private void SyncInviteChoices()
    {
        var ready = Members.Where(m => m.IsReady).ToArray();
        foreach (var gone in InviteChoices.Where(c => !ready.Contains(c.Person)).ToArray())
        {
            InviteChoices.Remove(gone);
        }

        foreach (var person in ready.Where(p => InviteChoices.All(c => c.Person != p)))
        {
            var choice = new InviteChoice(person);
            choice.PropertyChanged += (_, _) => _sendInviteCommand.RaiseCanExecuteChanged();
            InviteChoices.Add(choice);
        }

        _sendInviteCommand.RaiseCanExecuteChanged();
    }

    private void UpdateGameCard(SessionSnapshot snapshot)
    {
        var match = snapshot.Match;
        var inviting = match is { Phase: MatchPhaseView.Inviting };
        var running = match is { Phase: MatchPhaseView.Playing };
        var mine = match?.IsInitiator == true;
        var answer = match?.LocalAnswer ?? InviteAnswer.Pending;
        var invited = match?.IsInvited == true;

        HasMatch = inviting || running || match?.Game is not null;
        if (inviting || running)
        {
            Picking = null;
        }

        ShowBegin = inviting && mine;
        ShowCancel = inviting && mine;
        ShowAccept = inviting && !mine && invited && answer != InviteAnswer.Accepted;
        ShowDecline = inviting && !mine && invited && answer == InviteAnswer.Accepted;
        ShowStop = running && mine;
        ShowWatch = match?.Game is not null;
        WatchLabel = running ? (match!.IsPlaying ? "Return to game" : "Watch") : "View last game";
        IsInvitedNow = inviting && !mine && invited && answer == InviteAnswer.Pending;
        CanBegin = match?.CanStart == true;

        Invitees = inviting
            ? match!.Invitees
                .Select(i => new InviteeItem(
                    PersonOf(i.Key, i.Name),
                    i.IsInitiator ? "Host" : i.Answer switch { InviteAnswer.Accepted => "In", InviteAnswer.Declined => "Out", _ => "Invited" },
                    i.IsInitiator || i.Answer == InviteAnswer.Accepted))
                .ToArray()
            : [];
        var players = match?.Invitees.Count(i => i.Answer == InviteAnswer.Accepted) ?? 0;
        BeginLabel = $"Begin game ({players} players)";

        var game = match?.GameType.Name ?? GameCatalog.Ludo.Name;
        (GameTitle, GameDetail) = match switch
        {
            { Phase: MatchPhaseView.Inviting, IsInitiator: true } => (
                $"Waiting for players",
                players > 1 ? "Begin whenever everyone who wants to play has said yes." : "Nobody has said yes yet."),
            { Phase: MatchPhaseView.Inviting, IsInvited: false } => (
                $"{match.InitiatorName} is setting up {game}",
                "You weren't invited this time, but you can watch once it begins."),
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
            { } => ($"Last game: {game}", match.EndReason ?? "The game is over."),
            _ => (string.Empty, string.Empty),
        };
    }
}
