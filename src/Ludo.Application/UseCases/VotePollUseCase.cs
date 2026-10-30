using Ludo.Application.Session;
using Ludo.Application.Sync;

namespace Ludo.Application.UseCases;

/// <summary>Answers a notification, or changes the answer; the sender and the other recipients all see it.</summary>
public sealed class VotePollUseCase
{
    private readonly SessionContext _context;
    private readonly SessionExecutor _executor;
    private readonly PollProtocol _polls;

    internal VotePollUseCase(SessionContext context, SessionExecutor executor, PollProtocol polls)
    {
        _context = context;
        _executor = executor;
        _polls = polls;
    }

    public Task ExecuteAsync(Guid pollId, int option) => _executor.RunAsync(() =>
    {
        _polls.VoteLocal(pollId, option);
        _context.Publish();
    });
}
