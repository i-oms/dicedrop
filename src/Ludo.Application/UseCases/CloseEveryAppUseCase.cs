using Ludo.Application.Session;
using Ludo.Application.Sync;

namespace Ludo.Application.UseCases;

/// <summary>Tells every DiceDrop in the lobby to close its window. Closing this one is left to the caller.</summary>
public sealed class CloseEveryAppUseCase
{
    private readonly SessionContext _context;
    private readonly SessionExecutor _executor;
    private readonly LobbyProtocol _lobby;

    internal CloseEveryAppUseCase(SessionContext context, SessionExecutor executor, LobbyProtocol lobby)
    {
        _context = context;
        _executor = executor;
        _lobby = lobby;
    }

    public Task ExecuteAsync() => _executor.RunAsync(() =>
    {
        _lobby.CloseEveryoneLocal();
        _context.Publish();
    });
}
