namespace Ludo.Application.Session;

/// <param name="Id">Travels in invites, so it never changes once released.</param>
public sealed record GameInfo(string Id, string Name, string Description, int MinPlayers, int MaxPlayers);

/// <summary>The games a lobby can play. Every peer runs the same version, so all of them know the same list.</summary>
public static class GameCatalog
{
    public static GameInfo Ludo { get; } = new(
        "ludo",
        "Ludo",
        "The classic race home. Roll a six to come out, capture to send rivals back.",
        2,
        SessionContext.MaxGamePlayers);

    public static IReadOnlyList<GameInfo> All { get; } = [Ludo];

    public static GameInfo? Find(string? id) => All.FirstOrDefault(g => g.Id == id);
}
