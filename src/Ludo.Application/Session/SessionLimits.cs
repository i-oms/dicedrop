using Ludo.Domain.Players;

namespace Ludo.Application.Session;

/// <summary>Input limits the UI can enforce up front; the use cases check them again.</summary>
public static class SessionLimits
{
    public const int MaxNameLength = Player.MaxNameLength;

    public const int MaxChatLength = 500;

    public const int MinPollOptions = 2;

    public const int MaxPollOptions = 6;

    public const int MaxPollOptionLength = 30;

    /// <summary>Avatar photos travel to every peer in the Hello, so they stay small.</summary>
    public const int MaxPhotoBytes = 64 * 1024;

    /// <summary>Colours in the shared avatar palette.</summary>
    public const int AvatarColors = 8;

    /// <summary>How often one person may nudge the same person.</summary>
    public static readonly TimeSpan NudgeCooldown = TimeSpan.FromSeconds(10);

    /// <summary>How often one person may close the window for everyone.</summary>
    public static readonly TimeSpan CloseCooldown = TimeSpan.FromSeconds(10);
}
