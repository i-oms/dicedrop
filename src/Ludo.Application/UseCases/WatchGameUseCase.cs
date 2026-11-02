using Ludo.Application.Session;
using Ludo.Application.Sync;

namespace Ludo.Application.UseCases;

/// <summary>Tells the lobby which game's board is on our screen, so others can see who is watching.</summary>
public sealed class WatchGameUseCase
{
    private readonly SessionContext _context;
    private readonly SessionExecutor _executor;
    private readonly MatchProtocol _matches;

    internal WatchGameUseCase(SessionContext context, SessionExecutor executor, MatchProtocol matches)
    {
        _context = context;
        _executor = executor;
        _matches = matches;
    }

    /// <param name="matchId">The game on screen; null when no board is open.</param>
    public Task ExecuteAsync(Guid? matchId) => _executor.RunAsync(() =>
    {
        _matches.WatchLocal(matchId);
        _context.Publish();
    });
}
