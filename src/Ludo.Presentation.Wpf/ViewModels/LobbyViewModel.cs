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
    private readonly Action<Guid> _viewGame;
    private readonly AsyncCommand _sendInviteCommand;
    private ConversationViewModel _selected;
    private PersonViewModel _me = new(string.Empty);
    private IReadOnlyList<PersonViewModel> _members = [];
    private string _status = string.Empty;
    private bool _canPropose;
    private GameInfo? _picking;
    private int _totalUnread;
    private bool _isConfirmingClose;

    internal LobbyViewModel(LudoApplication app, Action<Exception> onError, Action<Guid> viewGame)
    {
        _app = app;
        _onError = onError;
        _viewGame = viewGame;
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

        SelectCommand = new AsyncCommand(item => { Select(item as ConversationViewModel); return Task.CompletedTask; }, onError);
        PickPlayersCommand = new AsyncCommand(game => { StartPicking((game as GameCardItem)?.Info); return Task.CompletedTask; }, onError);
        CancelPickCommand = new AsyncCommand(_ => { Picking = null; return Task.CompletedTask; }, onError);
        InvitePersonCommand = new AsyncCommand(
            key => app.ProposeGame.ExecuteAsync(GameCatalog.Ludo.Id, [(string)key!]),
            onError,
            _ => CanPropose);
        AskCloseEveryoneCommand = new AsyncCommand(_ => { IsConfirmingClose = true; return Task.CompletedTask; }, onError);
        CancelCloseEveryoneCommand = new AsyncCommand(_ => { IsConfirmingClose = false; return Task.CompletedTask; }, onError);
        CloseEveryoneCommand = new AsyncCommand(
            async _ =>
            {
                IsConfirmingClose = false;
                await app.CloseEveryApp.ExecuteAsync();
                ClosedForEveryone?.Invoke();
            },
            onError);
        foreach (var game in GameCatalog.All)
        {
            Games.Add(new GameCardItem(game));
        }
    }

    /// <summary>The request went out to the lobby; this copy should hide its window now.</summary>
    internal event Action? ClosedForEveryone;

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

    /// <summary>Open invites and running games, the one that concerns us first, then the last game that ended.</summary>
    public ObservableCollection<MatchCardViewModel> MatchCards { get; } = [];

    public bool IsConfirmingClose
    {
        get => _isConfirmingClose;
        private set => SetProperty(ref _isConfirmingClose, value);
    }

    public ICommand AskCloseEveryoneCommand { get; }

    public ICommand CancelCloseEveryoneCommand { get; }

    public ICommand CloseEveryoneCommand { get; }

    public ICommand SelectCommand { get; }

    /// <summary>Opens the invite picker for a game from the catalog.</summary>
    public ICommand PickPlayersCommand { get; }

    public ICommand CancelPickCommand { get; }

    public ICommand SendInviteCommand => _sendInviteCommand;

    /// <summary>Invites one person, by key, to a game of Ludo.</summary>
    public ICommand InvitePersonCommand { get; }

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

        if (!CanPropose)
        {
            Picking = null;
        }

        if (Picking is not null)
        {
            SyncInviteChoices();
        }

        UpdateMatchCards(snapshot);
    }

    internal void Clear()
    {
        foreach (var person in _people.Values)
        {
            person.IsPresent = false;
        }

        Notifications.Update([], [], PersonOf);
        MatchCards.Clear();
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

    /// <summary>Keeps the picker's list in line with who is free to play, keeping the ticks already made.</summary>
    private void SyncInviteChoices()
    {
        var ready = Members.Where(m => m.IsReady && !m.InGame).ToArray();
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

    private void UpdateMatchCards(SessionSnapshot snapshot)
    {
        var lastEnded = snapshot.Matches.LastOrDefault(m => m.Phase is MatchPhaseView.Finished or MatchPhaseView.Stopped && m.Game is not null);
        var shown = snapshot.Matches
            .Where(m => m.Phase is MatchPhaseView.Inviting or MatchPhaseView.Playing || m == lastEnded)
            .OrderByDescending(m => m.Id == snapshot.Match?.Id)
            .ToArray();

        foreach (var gone in MatchCards.Where(c => shown.All(m => m.Id != c.MatchId)).ToArray())
        {
            MatchCards.Remove(gone);
        }

        for (var i = 0; i < shown.Length; i++)
        {
            var match = shown[i];
            var card = MatchCards.FirstOrDefault(c => c.MatchId == match.Id);
            if (card is null)
            {
                card = new MatchCardViewModel(match.Id, _app, _onError, _viewGame);
                MatchCards.Insert(i, card);
            }
            else if (MatchCards.IndexOf(card) != i)
            {
                MatchCards.Move(MatchCards.IndexOf(card), i);
            }

            card.Update(match, PersonOf);
        }
    }
}
