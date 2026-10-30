using Ludo.Application.Common;
using Ludo.Application.Protocol;
using Ludo.Application.Session;
using Ludo.Domain.Players;

namespace Ludo.Application.Sync;

/// <summary>
/// Notifications with answer buttons, for making a decision together. A notification goes only to the members
/// its sender picked, and each answer goes to the sender and the other recipients over the mesh, so nobody else
/// ever hears about it. Answers can be changed; links keep their order, so every peer ends up with the latest one.
/// </summary>
internal sealed class PollProtocol(SessionContext context)
{
    public void SendLocal(string question, IReadOnlyCollection<string> options, IReadOnlyCollection<string> recipientKeys)
    {
        RequireLobby();
        var text = SessionContext.NormalizeChat(question)
            ?? throw new SessionException($"Group decisions must be 1 to {SessionLimits.MaxChatLength} characters.");
        var labels = NormalizeOptions(options)
            ?? throw new SessionException(
                $"Add {SessionLimits.MinPollOptions} to {SessionLimits.MaxPollOptions} different answers of up to {SessionLimits.MaxPollOptionLength} characters.");

        var recipients = recipientKeys.Distinct().Select(RequirePeer).ToArray();
        if (recipients.Length == 0)
        {
            throw new SessionException("Pick at least one person.");
        }

        var poll = new PollState(Guid.NewGuid(), context.LocalId, text, labels, recipients);
        var message = new PollMessage(poll.Id, text, labels, recipients);
        foreach (var recipient in recipients)
        {
            context.Network.Send(recipient, message);
        }

        context.AddPoll(poll);
    }

    public void VoteLocal(Guid pollId, int option)
    {
        RequireLobby();
        var poll = context.Polls.Find(p => p.Id == pollId)
            ?? throw new SessionException("That group decision is gone.");
        if (!poll.Recipients.Contains(context.LocalId))
        {
            throw new SessionException("Only the people it was sent to can vote.");
        }

        if (option < 0 || option >= poll.Options.Count)
        {
            throw new SessionException("That answer is not on the list.");
        }

        if (poll.Votes.TryGetValue(context.LocalId, out var current) && current == option)
        {
            return;
        }

        poll.Votes[context.LocalId] = option;
        var message = new PollVoteMessage(poll.Id, option);
        foreach (var member in poll.Recipients.Prepend(poll.Sender).Where(IsConnected))
        {
            context.Network.Send(member, message);
        }
    }

    public DispatchResult OnPoll(PlayerId from, PollMessage message)
    {
        if (context.Stage != SessionStage.Lobby || !IsConnected(from)
            || context.PastPolls.Contains(message.PollId) || context.Polls.Exists(p => p.Id == message.PollId)
            || !message.Recipients.Contains(context.LocalId) || message.Recipients.Contains(from)
            || SessionContext.NormalizeChat(message.Question) is not { } text
            || NormalizeOptions(message.Options) is not { } labels)
        {
            return DispatchResult.Ignored;
        }

        var recipients = message.Recipients.Distinct().Take(SessionContext.MaxPlayers).ToArray();
        context.AddPoll(new PollState(message.PollId, from, text, labels, recipients));
        context.Presenter.PresentPollActivity(new PollActivityView(
            message.PollId,
            SessionContext.KeyOf(from),
            context.NameOf(from),
            text,
            IsNew: true));
        return DispatchResult.Handled;
    }

    public DispatchResult OnVote(PlayerId from, PollVoteMessage vote)
    {
        if (context.Stage != SessionStage.Lobby || !IsConnected(from) || context.PastPolls.Contains(vote.PollId))
        {
            return DispatchResult.Ignored;
        }

        // Answers come straight from each recipient, so one can overtake the notification itself.
        if (context.Polls.Find(p => p.Id == vote.PollId) is not { } poll)
        {
            return DispatchResult.Deferred;
        }

        if (!poll.Recipients.Contains(from) || vote.Option < 0 || vote.Option >= poll.Options.Count
            || (poll.Votes.TryGetValue(from, out var current) && current == vote.Option))
        {
            return DispatchResult.Ignored;
        }

        poll.Votes[from] = vote.Option;
        context.Presenter.PresentPollActivity(new PollActivityView(
            poll.Id,
            SessionContext.KeyOf(from),
            context.NameOf(from),
            $"{poll.Options[vote.Option]}: {poll.Question}",
            IsNew: false));
        return DispatchResult.Handled;
    }

    /// <summary>Remembers the name of someone leaving, which the lobby forgets, so their answer stays readable.</summary>
    public void OnPeerGone(PlayerId peer)
    {
        foreach (var poll in context.Polls.Where(p => p.Sender == peer || p.Recipients.Contains(peer)))
        {
            poll.GoneNames.TryAdd(peer, context.NameOf(peer));
        }
    }

    /// <summary>Trimmed, distinct button labels, or null if they break the limits.</summary>
    private static string[]? NormalizeOptions(IEnumerable<string> options)
    {
        var labels = options.Select(o => SessionContext.NormalizeChat(o) ?? string.Empty).ToArray();
        return labels.Length is >= SessionLimits.MinPollOptions and <= SessionLimits.MaxPollOptions
            && labels.All(l => l.Length is > 0 and <= SessionLimits.MaxPollOptionLength)
            && labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() == labels.Length
                ? labels
                : null;
    }

    private bool IsConnected(PlayerId id) => context.Peers.TryGetValue(id, out var peer) && peer.IsGreeted;

    private PlayerId RequirePeer(string key) =>
        SessionContext.FindByKey(key) is { } id && id != context.LocalId && IsConnected(id)
            ? id
            : throw new SessionException("That person is no longer in the lobby.");

    private void RequireLobby()
    {
        if (context.Stage != SessionStage.Lobby)
        {
            throw new SessionException("That is only possible in the lobby.");
        }
    }
}
