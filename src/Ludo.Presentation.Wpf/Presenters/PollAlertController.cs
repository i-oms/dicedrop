using System.Windows;
using Ludo.Presentation.Wpf.Shell;
using Ludo.Presentation.Wpf.ViewModels;
using Ludo.Presentation.Wpf.Views;

namespace Ludo.Presentation.Wpf.Presenters;

/// <summary>
/// Puts notifications that need our answer in an alert that stays on screen while the main window is minimised,
/// hidden in the tray or behind other windows. A card goes when it is answered, here or in the lobby.
/// </summary>
internal sealed class PollAlertController
{
    private readonly Window _mainWindow;
    private readonly NotificationsViewModel _notifications;
    private readonly Func<UserSettings> _settings;
    private readonly Action _openLobby;
    private PollAlertWindow? _alert;

    public PollAlertController(
        Window mainWindow,
        NotificationsViewModel notifications,
        Func<UserSettings> settings,
        Action openLobby)
    {
        _mainWindow = mainWindow;
        _notifications = notifications;
        _settings = settings;
        _openLobby = openLobby;
        notifications.AnswerNeeded += OnAnswerNeeded;
        notifications.CardsChanged += () => _alert?.Keep(c => c.NeedsAnswer && notifications.Cards.Contains(c));
    }

    private void OnAnswerNeeded(PollCardViewModel card)
    {
        if (_mainWindow.IsActive || !_settings().ChatPopups)
        {
            return;
        }

        if (_alert is null)
        {
            _alert = new PollAlertWindow();
            _alert.CardClicked += OpenLobby;
        }

        _alert.Add(card);
    }

    private void OpenLobby(PollCardViewModel card)
    {
        _alert?.Remove(card);
        _openLobby();
        _mainWindow.BringToFront();
    }
}
