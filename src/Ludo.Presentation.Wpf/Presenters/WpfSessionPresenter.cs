using System.Windows.Threading;
using Ludo.Application.Abstractions;
using Ludo.Application.Session;

namespace Ludo.Presentation.Wpf.Presenters;

/// <summary>
/// Implements the application's output port. Snapshots arrive on background threads; only the newest
/// one is marshalled to the UI thread, so bursts of network traffic never flood the dispatcher.
/// </summary>
internal sealed class WpfSessionPresenter(Dispatcher dispatcher) : ISessionPresenter
{
    private SessionSnapshot? _pending;

    public event Action<SessionSnapshot>? SessionChanged;

    public event Action<string>? NoticeRaised;

    public void PresentSession(SessionSnapshot snapshot)
    {
        if (Interlocked.Exchange(ref _pending, snapshot) is null)
        {
            dispatcher.BeginInvoke(() =>
            {
                if (Interlocked.Exchange(ref _pending, null) is { } latest)
                {
                    SessionChanged?.Invoke(latest);
                }
            });
        }
    }

    public event Action<ChatLineView>? ChatReceived;

    public void PresentNotice(string message) => dispatcher.BeginInvoke(() => NoticeRaised?.Invoke(message));

    public void PresentChat(ChatLineView line) => dispatcher.BeginInvoke(() => ChatReceived?.Invoke(line));

    public event Action<InviteView>? InviteReceived;

    public void PresentInvite(InviteView invite) => dispatcher.BeginInvoke(() => InviteReceived?.Invoke(invite));

    public event Action<NudgeView>? NudgeReceived;

    public void PresentNudge(NudgeView nudge) => dispatcher.BeginInvoke(() => NudgeReceived?.Invoke(nudge));

    public event Action<PollActivityView>? PollActivity;

    public void PresentPollActivity(PollActivityView activity) => dispatcher.BeginInvoke(() => PollActivity?.Invoke(activity));

    public event Action<string>? CloseRequested;

    public void PresentCloseRequest(string from) => dispatcher.BeginInvoke(() => CloseRequested?.Invoke(from));
}
