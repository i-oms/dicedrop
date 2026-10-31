using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ludo.Presentation.Wpf.Shell;

/// <summary>
/// The local player's preferences. Kept in one small JSON file under %APPDATA%\DiceDrop, the only thing
/// DiceDrop writes besides the "start with Windows" entry. Chat is never saved.
/// </summary>
internal sealed record UserSettings
{
    /// <summary>Null until the player picks a name: the Windows account name is used until then.</summary>
    public string? Name { get; init; }

    public int AvatarColor { get; init; }

    /// <summary>The avatar photo as a small JPEG, already cropped and shrunk to send.</summary>
    public byte[]? AvatarPhoto { get; init; }

    public bool ChatPopups { get; init; } = true;

    public bool NudgeSound { get; init; } = true;

    public bool NudgeShake { get; init; } = true;

    /// <summary>Whether another lobby member's "Close for everyone" hides this copy's window too.</summary>
    public bool AllowCloseByOthers { get; init; } = true;

    /// <summary>A random id, shown to the lobby, that keeps us the same person when we reconnect. Null until first run.</summary>
    public Guid? PlayerId { get; init; }

    /// <summary>The file exists but could not be read, so these are stand-ins that must not replace it.</summary>
    [JsonIgnore]
    public bool IsReadFailed { get; init; }

    /// <summary>True until the settings file has been written for the first time.</summary>
    public static bool IsFirstRun => !File.Exists(FilePath);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiceDrop", "settings.json");

    /// <summary>The saved settings, or defaults when there are none or the file is unreadable.</summary>
    public static UserSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllBytes(FilePath)) ?? new UserSettings()
                : new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UserSettings { IsReadFailed = true };
        }
        catch (JsonException)
        {
            return new UserSettings();
        }
    }

    /// <summary>These settings with a player id, saved if it is new. If saving fails the id only lasts this run.</summary>
    public UserSettings WithPlayerId()
    {
        if (PlayerId is not null && PlayerId != Guid.Empty)
        {
            return this;
        }

        var settings = this with { PlayerId = Guid.NewGuid() };
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return settings;
    }

    /// <exception cref="IOException">The file could not be written.</exception>
    public void Save()
    {
        if (IsReadFailed)
        {
            throw new IOException("Your saved settings could not be read, so they were not overwritten. Restart DiceDrop and try again.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }
}
