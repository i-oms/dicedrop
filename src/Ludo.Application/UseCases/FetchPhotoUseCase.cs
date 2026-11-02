using Ludo.Application.Session;
using Ludo.Application.Sync;

namespace Ludo.Application.UseCases;

/// <summary>Gets someone's avatar photo now that it is on our screen.</summary>
public sealed class FetchPhotoUseCase
{
    private readonly SessionExecutor _executor;
    private readonly LobbyProtocol _lobby;

    internal FetchPhotoUseCase(SessionExecutor executor, LobbyProtocol lobby)
    {
        _executor = executor;
        _lobby = lobby;
    }

    public Task ExecuteAsync(string personKey) => _executor.RunAsync(() => _lobby.FetchPhotoLocal(personKey));
}
