using Ludo.Application.Session;
using Ludo.Application.Sync;

namespace Ludo.Application.UseCases;

/// <summary>Sends a notification with answer buttons to the chosen lobby members. It lives in memory only, like chat.</summary>
public sealed class SendPollUseCase
{
    private readonly SessionContext _context;
    private readonly SessionExecutor _executor;
    private readonly PollProtocol _polls;

    internal SendPollUseCase(SessionContext context, SessionExecutor executor, PollProtocol polls)
    {
        _context = context;
        _executor = executor;
        _polls = polls;
    }

    public Task ExecuteAsync(string question, IReadOnlyCollection<string> options, IReadOnlyCollection<string> recipientKeys) => _executor.RunAsync(() =>
    {
        _polls.SendLocal(question, options, recipientKeys);
        _context.Publish();
    });
}
