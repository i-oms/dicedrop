using Ludo.Application.Session;
using Ludo.Application.Sync;

namespace Ludo.Application.UseCases;

/// <summary>Says yes or no to one of the open invites, which can be several at once.</summary>
public sealed class AnswerInviteUseCase
{
    private readonly SessionContext _context;
    private readonly SessionExecutor _executor;
    private readonly PeerMessageRouter _router;
    private readonly MatchProtocol _matches;
    private readonly bool _accept;

    internal AnswerInviteUseCase(SessionContext context, SessionExecutor executor, PeerMessageRouter router, MatchProtocol matches, bool accept)
    {
        _context = context;
        _executor = executor;
        _router = router;
        _matches = matches;
        _accept = accept;
    }

    public Task ExecuteAsync(Guid matchId) => _executor.RunAsync(async () =>
    {
        _matches.AnswerLocal(matchId, _accept);
        await _router.DrainDeferredAsync().ConfigureAwait(false);
        _context.Publish();
    });
}
