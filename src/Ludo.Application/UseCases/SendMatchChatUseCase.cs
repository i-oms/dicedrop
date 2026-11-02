using Ludo.Application.Session;
using Ludo.Application.Sync;

namespace Ludo.Application.UseCases;

/// <summary>Sends a line to a game's group chat. Chat lives in memory only and is gone when the lobby ends.</summary>
public sealed class SendMatchChatUseCase
{
    private readonly SessionContext _context;
    private readonly SessionExecutor _executor;
    private readonly MatchProtocol _matches;

    internal SendMatchChatUseCase(SessionContext context, SessionExecutor executor, MatchProtocol matches)
    {
        _context = context;
        _executor = executor;
        _matches = matches;
    }

    public Task ExecuteAsync(Guid matchId, string text) => _executor.RunAsync(() =>
    {
        _matches.ChatLocal(matchId, text);
        _context.Publish();
    });
}
