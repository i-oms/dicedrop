using Ludo.Application.Session;

namespace Ludo.Application.Abstractions;

/// <summary>
/// Output port. Called from a background thread; implementations marshal to their UI thread.
/// </summary>
public interface ISessionPresenter
{
    void PresentSession(SessionSnapshot snapshot);

    void PresentNotice(string message);

    /// <summary>A chat line was just sent or received; the next snapshot carries it too.</summary>
    void PresentChat(ChatLineView line);

    /// <summary>Someone asked who wants to play; the next snapshot carries the invite too.</summary>
    void PresentInvite(InviteView invite);

    /// <summary>Someone nudged us; the next snapshot carries the nudge line in their private conversation.</summary>
    void PresentNudge(NudgeView nudge);

    /// <summary>Someone sent us a notification or answered one; the next snapshot carries it too.</summary>
    void PresentPollActivity(PollActivityView activity);
}
