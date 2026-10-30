using System.Collections.ObjectModel;
using System.Windows.Input;
using Ludo.Application;
using Ludo.Application.Session;

namespace Ludo.Presentation.Wpf.ViewModels;

/// <summary>One answer on a notification card, with who picked it.</summary>
/// <param name="Voters">Names of those who picked it, or empty.</param>
/// <param name="CanVote">We are a recipient; the sender only sees the tally.</param>
public sealed record PollOptionItem(int Index, string Label, int Count, string Voters, bool IsChosen, bool CanVote)
{
    public bool ShowChosen => CanVote && IsChosen;

    public bool ShowVote => CanVote && !IsChosen;

    public bool ShowLabel => !CanVote;

    public bool HasVoters => Voters.Length > 0;
}

/// <summary>A button being written in the composer.</summary>
public sealed class PollOptionField(string text, Action changed) : ObservableObject
{
    private string _text = text;

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                changed();
            }
        }
    }

    public int MaxLength => SessionLimits.MaxPollOptionLength;
}

/// <summary>A notification in the list. Stays the same object between snapshots, so its buttons survive updates.</summary>
public sealed class PollCardViewModel : ObservableObject
{
    private IReadOnlyList<PollOptionItem> _options = [];
    private string _recipients = string.Empty;
    private string _waiting = string.Empty;
    private bool _needsAnswer;

    internal PollCardViewModel(PollView poll, PersonViewModel sender, LudoApplication app, Action<Exception> onError)
    {
        Id = poll.Id;
        Sender = sender;
        Question = poll.Question;
        Title = poll.IsMine ? "You asked" : $"{poll.From} asks";
        IsMine = poll.IsMine;
        VoteCommand = new AsyncCommand(option => app.VotePoll.ExecuteAsync(Id, (int)option!), onError);
        TakeBackCommand = new AsyncCommand(_ => app.RetractPoll.ExecuteAsync(Id), onError);
    }

    public Guid Id { get; }

    public PersonViewModel Sender { get; }

    public string Title { get; }

    public string Question { get; }

    /// <summary>We sent it, so we can take it back from everyone.</summary>
    public bool IsMine { get; }

    public ICommand VoteCommand { get; }

    public ICommand TakeBackCommand { get; }

    public IReadOnlyList<PollOptionItem> Options
    {
        get => _options;
        private set => SetProperty(ref _options, value);
    }

    public string Recipients
    {
        get => _recipients;
        private set => SetProperty(ref _recipients, value);
    }

    /// <summary>Who has not answered yet, or empty when nobody is left to wait for.</summary>
    public string Waiting
    {
        get => _waiting;
        private set
        {
            if (SetProperty(ref _waiting, value))
            {
                OnPropertyChanged(nameof(HasWaiting));
            }
        }
    }

    public bool HasWaiting => Waiting.Length > 0;

    /// <summary>It was sent to us and we have not answered; the card is highlighted.</summary>
    public bool NeedsAnswer
    {
        get => _needsAnswer;
        private set => SetProperty(ref _needsAnswer, value);
    }

    internal void Update(PollView poll)
    {
        var mine = poll.Recipients.FirstOrDefault(r => r.IsLocal);
        var options = poll.Options
            .Select((label, i) =>
            {
                var voters = poll.Recipients.Where(r => r.Choice == i).ToArray();
                return new PollOptionItem(
                    i,
                    label,
                    voters.Length,
                    string.Join(", ", voters.Select(NameOf)),
                    mine?.Choice == i,
                    mine is not null);
            })
            .ToArray();
        if (!options.SequenceEqual(Options))
        {
            Options = options;
        }

        Recipients = $"To {string.Join(", ", poll.Recipients.Select(NameOf))}";
        var waiting = poll.Recipients.Where(r => r.IsPresent && r.Choice is null).Select(NameOf).ToArray();
        Waiting = waiting.Length > 0 ? $"Waiting for {string.Join(", ", waiting)}" : string.Empty;
        NeedsAnswer = mine is { Choice: null };
    }

    private static string NameOf(PollMemberView member) => member.IsLocal ? "you" : member.Name;
}

/// <summary>
/// Notifications with answer buttons: writing one for the people picked, and the list of those sent and
/// received, where everyone involved sees each answer. Clearing a card only hides it on this PC; its sender
/// can also take it back, which removes it for everyone.
/// </summary>
public sealed class NotificationsViewModel : ObservableObject
{
    private readonly LudoApplication _app;
    private readonly Action<Exception> _onError;
    private readonly HashSet<Guid> _cleared = [];
    private readonly AsyncCommand _composeCommand;
    private readonly AsyncCommand _sendCommand;
    private readonly AsyncCommand _addOptionCommand;
    private readonly AsyncCommand _removeOptionCommand;
    private IReadOnlyList<PersonViewModel> _ready = [];
    private bool _isComposing;
    private string _question = string.Empty;
    private int _pendingCount;

    internal NotificationsViewModel(LudoApplication app, Action<Exception> onError)
    {
        _app = app;
        _onError = onError;
        _composeCommand = new AsyncCommand(_ => { StartComposing(); return Task.CompletedTask; }, onError, _ => _ready.Count > 0);
        CancelCommand = new AsyncCommand(_ => { IsComposing = false; return Task.CompletedTask; }, onError);
        _sendCommand = new AsyncCommand(
            async _ =>
            {
                await app.SendPoll.ExecuteAsync(
                    Question,
                    OptionFields.Select(o => o.Text).ToArray(),
                    Choices.Where(c => c.IsChecked).Select(c => c.Person.Key).ToArray());
                IsComposing = false;
            },
            onError,
            _ => CanSend());
        _addOptionCommand = new AsyncCommand(
            _ => { AddOption(string.Empty); RefreshComposer(); return Task.CompletedTask; },
            onError,
            _ => OptionFields.Count < SessionLimits.MaxPollOptions);
        _removeOptionCommand = new AsyncCommand(
            field => { OptionFields.Remove((PollOptionField)field!); RefreshComposer(); return Task.CompletedTask; },
            onError,
            _ => OptionFields.Count > SessionLimits.MinPollOptions);
        ClearCommand = new AsyncCommand(
            card =>
            {
                var item = (PollCardViewModel)card!;
                _cleared.Add(item.Id);
                Cards.Remove(item);
                RefreshPending();
                return Task.CompletedTask;
            },
            onError);
    }

    /// <summary>A notification just arrived that waits for our answer.</summary>
    public event Action<PollCardViewModel>? AnswerNeeded;

    /// <summary>The list or an answer on it changed.</summary>
    public event Action? CardsChanged;

    /// <summary>Newest first.</summary>
    public ObservableCollection<PollCardViewModel> Cards { get; } = [];

    /// <summary>Notifications waiting for our answer.</summary>
    public int PendingCount
    {
        get => _pendingCount;
        private set => SetProperty(ref _pendingCount, value);
    }

    public bool IsComposing
    {
        get => _isComposing;
        private set => SetProperty(ref _isComposing, value);
    }

    public string Question
    {
        get => _question;
        set
        {
            if (SetProperty(ref _question, value))
            {
                _sendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int MaxQuestionLength => SessionLimits.MaxChatLength;

    public ObservableCollection<PollOptionField> OptionFields { get; } = [];

    public ObservableCollection<InviteChoice> Choices { get; } = [];

    public ICommand ComposeCommand => _composeCommand;

    public ICommand CancelCommand { get; }

    public ICommand SendCommand => _sendCommand;

    public ICommand AddOptionCommand => _addOptionCommand;

    public ICommand RemoveOptionCommand => _removeOptionCommand;

    public ICommand ClearCommand { get; }

    internal void Update(IReadOnlyList<PollView> polls, IReadOnlyList<PersonViewModel> members, Func<string, string, PersonViewModel> personOf)
    {
        _ready = members.Where(m => m.IsReady).ToArray();
        _composeCommand.RaiseCanExecuteChanged();
        if (IsComposing)
        {
            SyncChoices();
        }

        var current = polls.Where(p => !_cleared.Contains(p.Id)).ToDictionary(p => p.Id);
        foreach (var gone in Cards.Where(c => !current.ContainsKey(c.Id)).ToArray())
        {
            Cards.Remove(gone);
        }

        var added = new List<PollCardViewModel>();
        foreach (var poll in current.Values)
        {
            var card = Cards.FirstOrDefault(c => c.Id == poll.Id);
            if (card is null)
            {
                card = new PollCardViewModel(poll, personOf(poll.FromKey, poll.From), _app, _onError);
                Cards.Insert(0, card);
                added.Add(card);
            }

            card.Update(poll);
        }

        RefreshPending();
        foreach (var card in added.Where(c => c.NeedsAnswer))
        {
            AnswerNeeded?.Invoke(card);
        }
    }

    private void StartComposing()
    {
        Question = string.Empty;
        OptionFields.Clear();
        AddOption("Yes");
        AddOption("No");
        Choices.Clear();
        SyncChoices();
        IsComposing = true;
        RefreshComposer();
    }

    private void AddOption(string text) => OptionFields.Add(new PollOptionField(text, _sendCommand.RaiseCanExecuteChanged));

    /// <summary>Keeps the list in line with who is in the lobby, keeping the ticks already made.</summary>
    private void SyncChoices()
    {
        foreach (var gone in Choices.Where(c => !_ready.Contains(c.Person)).ToArray())
        {
            Choices.Remove(gone);
        }

        foreach (var person in _ready.Where(p => Choices.All(c => c.Person != p)))
        {
            var choice = new InviteChoice(person);
            choice.PropertyChanged += (_, _) => _sendCommand.RaiseCanExecuteChanged();
            Choices.Add(choice);
        }

        _sendCommand.RaiseCanExecuteChanged();
    }

    private void RefreshComposer()
    {
        _sendCommand.RaiseCanExecuteChanged();
        _addOptionCommand.RaiseCanExecuteChanged();
        _removeOptionCommand.RaiseCanExecuteChanged();
    }

    private void RefreshPending()
    {
        PendingCount = Cards.Count(c => c.NeedsAnswer);
        CardsChanged?.Invoke();
    }

    private bool CanSend() =>
        IsComposing
        && !string.IsNullOrWhiteSpace(Question)
        && Choices.Any(c => c.IsChecked)
        && OptionFields.All(o => !string.IsNullOrWhiteSpace(o.Text));
}
