using Ludo.Application.Session;
using Ludo.Application.Sync;

namespace Ludo.Application.UseCases;

/// <summary>Takes back a notification we sent; it disappears for everyone it went to, along with the answers.</summary>
public sealed class RetractPollUseCase
{
    private readonly SessionContext _context;
    private readonly SessionExecutor _executor;
    private readonly PollProtocol _polls;

    internal RetractPollUseCase(SessionContext context, SessionExecutor executor, PollProtocol polls)
    {
        _context = context;
        _executor = executor;
        _polls = polls;
    }

    public Task ExecuteAsync(Guid pollId) => _executor.RunAsync(() =>
    {
        _polls.RetractLocal(pollId);
        _context.Publish();
    });
}
