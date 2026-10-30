using System.Windows;
using Ludo.Application.Session;
using Ludo.Presentation.Wpf.Shell;
using Ludo.Presentation.Wpf.ViewModels;
using Ludo.Presentation.Wpf.Views;

namespace Ludo.Presentation.Wpf.Presenters;

/// <summary>
/// Shows other players' chat lines, nudges, game invites and answers to notifications in a corner pop-up while the main window is
/// minimised, hidden in the tray or behind other windows. Bringing the game back to the front dismisses it;
/// clicking it opens the conversation the latest line belongs to.
/// </summary>
internal sealed class ChatToastController
{
    private readonly Window _mainWindow;
    private readonly Func<UserSettings> _settings;
    private readonly Func<string, PersonViewModel?> _personOf;
    private readonly Action<string> _openConversation;
    private ChatToastWindow? _toast;
    private string _latestConversation = string.Empty;

    public ChatToastController(
        Window mainWindow,
        WpfSessionPresenter presenter,
        Func<UserSettings> settings,
        Func<string, PersonViewModel?> personOf,
        Action<string> openConversation)
    {
        _mainWindow = mainWindow;
        _settings = settings;
        _personOf = personOf;
        _openConversation = openConversation;
        presenter.ChatReceived += OnChatReceived;
        presenter.InviteReceived += invite => Show(
            new ToastLine(PersonViewModel.InitialsOf(invite.From), null, null, $"{invite.From} · game invite", $"wants to play {invite.GameName}. Open DiceDrop to join in."),
            conversation: string.Empty);
        presenter.PollActivity += OnPollActivity;
        mainWindow.Activated += (_, _) => _toast?.Dismiss();
    }

    private void OnPollActivity(PollActivityView activity)
    {
        // A new notification gets its own alert, with the buttons; see PollAlertController.
        if (activity.IsNew)
        {
            return;
        }

        var person = _personOf(activity.FromKey);
        Show(
            new ToastLine(PersonViewModel.InitialsOf(activity.From), person?.Color, person?.Photo, $"{activity.From} · {(activity.IsRetracted ? "took back" : "voted")}", activity.Text),
            conversation: string.Empty);
    }

    private void OnChatReceived(ChatLineView line)
    {
        if (line.IsLocal)
        {
            return;
        }

        var person = _personOf(line.SenderKey);
        var where = line.Kind == ChatLineKind.Nudge ? "nudge" : line.Conversation is null ? "lobby" : "private";
        Show(
            new ToastLine(PersonViewModel.InitialsOf(line.Sender), person?.Color, person?.Photo, $"{line.Sender} · {where}", line.Text),
            line.Conversation ?? string.Empty);
    }

    private void Show(ToastLine line, string conversation)
    {
        if (_mainWindow.IsActive || !_settings().ChatPopups)
        {
            return;
        }

        if (_toast is null)
        {
            _toast = new ChatToastWindow();
            _toast.MouseLeftButtonUp += (_, _) => OpenLatest();
        }

        _latestConversation = conversation;
        _toast.Push(line);
    }

    private void OpenLatest()
    {
        _toast?.Dismiss();
        _openConversation(_latestConversation);
        _mainWindow.BringToFront();
    }
}
