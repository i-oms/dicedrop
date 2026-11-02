using System.Collections.ObjectModel;
using System.Windows.Input;
using Ludo.Application;
using Ludo.Application.Session;

namespace Ludo.Presentation.Wpf.ViewModels;

/// <summary>The lobby's group chat, a game's group chat, or a private conversation with one person.</summary>
public sealed class ConversationViewModel : ObservableObject
{
    private readonly AsyncCommand _sendCommand;
    private readonly AsyncCommand _nudgeCommand;
    private string _draft = string.Empty;
    private int _unread;
    private string _preview = string.Empty;
    private bool _isSelected;

    /// <param name="person">The other person, or null for the lobby chat.</param>
    /// <param name="matchId">The game whose group chat this is; null for the lobby and private chats.</param>
    internal ConversationViewModel(PersonViewModel? person, LudoApplication app, Action<Exception> onError, Guid? matchId = null)
    {
        Person = person;
        MatchId = matchId;
        _sendCommand = new AsyncCommand(
            async _ =>
            {
                var text = Draft;
                Draft = string.Empty;
                try
                {
                    await (matchId is { } match ? app.SendMatchChat.ExecuteAsync(match, text)
                        : person is null ? app.SendChat.ExecuteAsync(text)
                        : app.SendDirectChat.ExecuteAsync(person.Key, text));
                }
                catch
                {
                    // Give the text back so nothing typed is lost.
                    Draft = text;
                    throw;
                }
            },
            onError,
            _ => !string.IsNullOrWhiteSpace(Draft) && CanWrite);
        _nudgeCommand = new AsyncCommand(_ => app.Nudge.ExecuteAsync(person!.Key), onError, _ => person is { IsPresent: true });
        if (person is not null)
        {
            person.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PersonViewModel.Name))
                {
                    OnPropertyChanged(nameof(Placeholder));
                }

                if (e.PropertyName == nameof(PersonViewModel.IsPresent))
                {
                    OnPropertyChanged(nameof(CanWrite));
                    _sendCommand.RaiseCanExecuteChanged();
                    _nudgeCommand.RaiseCanExecuteChanged();
                }
            };
        }
    }

    /// <summary>Null for a group chat.</summary>
    public PersonViewModel? Person { get; }

    internal Guid? MatchId { get; }

    /// <summary>A group chat, drawn with the chat icon rather than an avatar: the lobby's or a game's.</summary>
    public bool IsLobby => Person is null;

    public bool IsGameChat => MatchId is not null;

    public string Key => Person?.Key ?? string.Empty;

    public ObservableCollection<ChatMessageItem> Messages { get; } = [];

    public string Draft
    {
        get => _draft;
        set
        {
            if (SetProperty(ref _draft, value))
            {
                _sendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int MaxChatLength => SessionLimits.MaxChatLength;

    public string Placeholder => IsGameChat ? "Message this game…" : Person is null ? "Message the lobby…" : $"Message {Person.Name}…";

    /// <summary>A group chat always; a private one while the other person is still in the lobby.</summary>
    public bool CanWrite => Person is not { IsPresent: false };

    /// <summary>New lines from others since the conversation was last open.</summary>
    public int Unread
    {
        get => _unread;
        private set => SetProperty(ref _unread, value);
    }

    /// <summary>The latest line, for the conversation list.</summary>
    public string Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (SetProperty(ref _isSelected, value) && value)
            {
                Unread = 0;
            }
        }
    }

    public ICommand SendCommand => _sendCommand;

    public ICommand NudgeCommand => _nudgeCommand;

    /// <summary>
    /// Keeps the list in place so it holds its scroll position and only new lines animate in: drops lines trimmed
    /// from the front of the history, appends new ones, and rebuilds only when the history was replaced.
    /// </summary>
    /// <returns>How many lines from other people were added.</returns>
    internal int Sync(IReadOnlyList<ChatLineView> lines, Func<ChatLineView, PersonViewModel> senderOf)
    {
        var trimmed = Enumerable.Range(0, Messages.Count + 1)
            .First(skip => skip == Messages.Count
                || (Messages.Count - skip <= lines.Count && Messages.Skip(skip).Select((item, i) => item.Line == lines[i]).All(same => same)));
        for (var i = 0; i < trimmed; i++)
        {
            Messages.RemoveAt(0);
        }

        if (Messages.Count > 0 && !Messages[0].StartsGroup)
        {
            Messages[0] = Messages[0] with { StartsGroup = true };
        }

        var incoming = 0;
        foreach (var line in lines.Skip(Messages.Count))
        {
            var previous = Messages.Count > 0 ? Messages[^1].Line : null;
            var startsGroup = previous is null || previous.SenderKey != line.SenderKey
                || previous.Kind != line.Kind || line.Kind == ChatLineKind.Nudge
                || line.Time - previous.Time > TimeSpan.FromMinutes(5);
            Messages.Add(new ChatMessageItem(line, startsGroup, senderOf(line)));
            incoming += line.IsLocal ? 0 : 1;
        }

        if (Messages.Count > 0)
        {
            var last = Messages[^1];
            Preview = last.IsNudge || Person is not null && !last.IsLocal ? last.Text
                : last.IsLocal ? $"You: {last.Text}"
                : $"{last.Sender.Name}: {last.Text}";
        }

        if (!IsSelected)
        {
            Unread += incoming;
        }

        return incoming;
    }
}
