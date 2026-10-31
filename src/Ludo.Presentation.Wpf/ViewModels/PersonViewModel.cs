using System.Windows.Media;
using Ludo.Application.Session;
using Ludo.Presentation.Wpf.Shell;
using Ludo.Presentation.Wpf.Views.Board;

namespace Ludo.Presentation.Wpf.ViewModels;

/// <summary>
/// Someone in the lobby (or who was in it), kept as one object per member so avatars, names and
/// presence update everywhere they are shown, including on chat lines sent earlier.
/// </summary>
public sealed class PersonViewModel(string key, Action<string>? fetchPhoto = null) : ObservableObject
{
    private bool _isOnScreen;
    private string? _missingPhoto;
    private string? _fetchedPhoto;
    private string _name = string.Empty;
    private string _initials = "?";
    private Brush _color = SeatPalette.Brush(0);
    private ImageSource? _photo;
    private bool _isLocal;
    private bool _isPresent;
    private bool _isReady;
    private bool _inGame;
    private bool _isHost;

    public string Key { get; } = key;

    public string Name
    {
        get => _name;
        private set
        {
            if (SetProperty(ref _name, value))
            {
                Initials = InitialsOf(value);
            }
        }
    }

    public string Initials
    {
        get => _initials;
        private set => SetProperty(ref _initials, value);
    }

    /// <summary>Background behind the initials; also the accent of this person's chat avatar.</summary>
    public Brush Color
    {
        get => _color;
        private set => SetProperty(ref _color, value);
    }

    public ImageSource? Photo
    {
        get => _photo;
        private set => SetProperty(ref _photo, value);
    }

    public bool IsLocal
    {
        get => _isLocal;
        private set => SetProperty(ref _isLocal, value);
    }

    /// <summary>Still in the lobby. People who left keep their messages but can no longer be written to.</summary>
    public bool IsPresent
    {
        get => _isPresent;
        internal set
        {
            if (SetProperty(ref _isPresent, value))
            {
                OnPropertyChanged(nameof(Status));

                // A new link starts from scratch, so anything still on its way won't come.
                _fetchedPhoto = null;
            }
        }
    }

    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
    }

    public bool InGame
    {
        get => _inGame;
        private set => SetProperty(ref _inGame, value);
    }

    public bool IsHost
    {
        get => _isHost;
        private set => SetProperty(ref _isHost, value);
    }

    public string Status =>
        !IsPresent ? "Left the lobby"
        : !IsReady ? "Connecting…"
        : InGame ? "Playing Ludo"
        : IsHost ? "Online · host"
        : "Online";

    public static string InitialsOf(string name)
    {
        var words = name.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[^1][0])}",
        };
    }

    internal void Update(MemberView member)
    {
        Name = member.Name;
        IsLocal = member.IsLocal;
        IsReady = member.IsReady;
        InGame = member.InGame;
        IsHost = member.IsHost;
        IsPresent = true;
        ApplyProfile(member.Profile.Color, member.Profile.Photo);
        _missingPhoto = member.Profile.Photo is null ? member.Profile.PhotoId : null;
        FetchPhotoIfNeeded();
        OnPropertyChanged(nameof(Status));
    }

    /// <summary>Called when an avatar starts drawing this person: only then is their photo worth fetching.</summary>
    internal void MarkOnScreen()
    {
        _isOnScreen = true;
        FetchPhotoIfNeeded();
    }

    private void FetchPhotoIfNeeded()
    {
        if (_isOnScreen && _missingPhoto is { } photo && photo != _fetchedPhoto)
        {
            _fetchedPhoto = photo;
            fetchPhoto?.Invoke(Key);
        }
    }

    /// <summary>For people we only know from old chat lines.</summary>
    internal void UpdateName(string name) => Name = name;

    internal void ApplyProfile(int color, byte[]? photo)
    {
        Color = SeatPalette.Brush(color);
        Photo = AvatarImages.Decode(photo);
    }
}
