using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using Ludo.Application;
using Ludo.Application.Session;
using Ludo.Presentation.Wpf.Shell;
using Ludo.Presentation.Wpf.Views.Board;
using Microsoft.Win32;

namespace Ludo.Presentation.Wpf.ViewModels;

/// <param name="Index">Index into the shared avatar palette.</param>
public sealed record ColorChoice(int Index, Brush Brush);

/// <summary>
/// The settings sheet: name and avatar, which go to everyone in the lobby, and local preferences.
/// It edits a draft; nothing changes until Save.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly LudoApplication _app;
    private readonly AsyncCommand _saveCommand;
    private UserSettings _saved;
    private bool _isOpen;
    private string _name = string.Empty;
    private int _color;
    private byte[]? _photo;
    private ImageSource? _photoPreview;
    private bool _chatPopups;
    private bool _nudgeSound;
    private bool _nudgeShake;
    private bool _startWithWindows;
    private bool _allowCloseByOthers;
    private string? _error;

    internal SettingsViewModel(LudoApplication app, UserSettings saved, Action<Exception> onError)
    {
        _app = app;
        _saved = saved;
        Colors = Enumerable.Range(0, SessionLimits.AvatarColors).Select(i => new ColorChoice(i, SeatPalette.Brush(i))).ToArray();
        _saveCommand = new AsyncCommand(_ => SaveAsync(), onError, _ => !string.IsNullOrWhiteSpace(Name));
        OpenCommand = new AsyncCommand(_ => { Open(); return Task.CompletedTask; }, onError);
        CancelCommand = new AsyncCommand(_ => { IsOpen = false; return Task.CompletedTask; }, onError);
        ChoosePhotoCommand = new AsyncCommand(_ => { ChoosePhoto(); return Task.CompletedTask; }, onError);
        RemovePhotoCommand = new AsyncCommand(_ => { SetPhoto(null); return Task.CompletedTask; }, onError);
        PickColorCommand = new AsyncCommand(choice => { Color = ((ColorChoice)choice!).Index; return Task.CompletedTask; }, onError);
    }

    /// <summary>The preferences as last saved; the rest of the app reads these.</summary>
    internal UserSettings Saved => _saved;

    public IReadOnlyList<ColorChoice> Colors { get; }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(Initials));
                _saveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Initials => PersonViewModel.InitialsOf(Name);

    public int MaxNameLength => SessionLimits.MaxNameLength;

    public int Color
    {
        get => _color;
        private set
        {
            if (SetProperty(ref _color, value))
            {
                OnPropertyChanged(nameof(ColorBrush));
            }
        }
    }

    public Brush ColorBrush => SeatPalette.Brush(Color);

    public ImageSource? PhotoPreview
    {
        get => _photoPreview;
        private set
        {
            if (SetProperty(ref _photoPreview, value))
            {
                OnPropertyChanged(nameof(HasPhoto));
            }
        }
    }

    public bool HasPhoto => PhotoPreview is not null;

    public bool ChatPopups
    {
        get => _chatPopups;
        set => SetProperty(ref _chatPopups, value);
    }

    public bool NudgeSound
    {
        get => _nudgeSound;
        set => SetProperty(ref _nudgeSound, value);
    }

    public bool NudgeShake
    {
        get => _nudgeShake;
        set => SetProperty(ref _nudgeShake, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool AllowCloseByOthers
    {
        get => _allowCloseByOthers;
        set => SetProperty(ref _allowCloseByOthers, value);
    }

    /// <summary>A problem with the last photo or save, shown inside the sheet.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public ICommand OpenCommand { get; }

    public ICommand SaveCommand => _saveCommand;

    public ICommand CancelCommand { get; }

    public ICommand ChoosePhotoCommand { get; }

    public ICommand RemovePhotoCommand { get; }

    public ICommand PickColorCommand { get; }

    /// <summary>Applies the saved name and avatar at startup, before the first lobby is found.</summary>
    internal async Task ApplySavedProfileAsync()
    {
        try
        {
            await _app.UpdateProfile.ExecuteAsync(_saved.AvatarColor, _saved.AvatarPhoto);
        }
        catch (Ludo.Application.Common.SessionException)
        {
            // A damaged settings file: carry on with the default avatar.
        }
    }

    /// <param name="currentName">The name in use, which is the Windows account name until one is saved.</param>
    internal void Open(string? currentName = null)
    {
        Name = _saved.Name ?? currentName ?? Environment.UserName;
        Color = _saved.AvatarColor;
        SetPhoto(_saved.AvatarPhoto);
        ChatPopups = _saved.ChatPopups;
        NudgeSound = _saved.NudgeSound;
        NudgeShake = _saved.NudgeShake;
        StartWithWindows = StartupRegistration.IsEnabled;
        AllowCloseByOthers = _saved.AllowCloseByOthers;
        Error = null;
        IsOpen = true;
    }

    private void ChoosePhoto()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an avatar photo",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            SetPhoto(AvatarImages.PrepareFromFile(dialog.FileName));
            Error = null;
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }
    }

    private void SetPhoto(byte[]? photo)
    {
        _photo = photo;
        PhotoPreview = AvatarImages.Decode(photo);
    }

    private async Task SaveAsync()
    {
        var settings = _saved with
        {
            Name = Name.Trim(),
            AvatarColor = Color,
            AvatarPhoto = _photo,
            ChatPopups = ChatPopups,
            NudgeSound = NudgeSound,
            NudgeShake = NudgeShake,
            AllowCloseByOthers = AllowCloseByOthers,
        };

        try
        {
            await _app.RenamePlayer.ExecuteAsync(settings.Name);
            await _app.UpdateProfile.ExecuteAsync(settings.AvatarColor, settings.AvatarPhoto);
            if (StartWithWindows != StartupRegistration.IsEnabled)
            {
                StartupRegistration.SetEnabled(StartWithWindows);
            }

            settings.Save();
        }
        catch (Exception ex) when (ex is Ludo.Application.Common.SessionException or IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            Error = ex.Message;
            return;
        }

        _saved = settings;
        IsOpen = false;
    }
}
