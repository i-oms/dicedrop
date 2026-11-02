using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Ludo.Presentation.Wpf.ViewModels;

namespace Ludo.Presentation.Wpf.Views;

/// <summary>
/// Round avatar. Bind <see cref="Person"/> to follow someone's photo, colour and presence, or set
/// <see cref="Initials"/>, <see cref="Fill"/> and <see cref="Photo"/> directly.
/// </summary>
public partial class AvatarView : UserControl
{
    public static readonly DependencyProperty PersonProperty = DependencyProperty.Register(
        nameof(Person), typeof(PersonViewModel), typeof(AvatarView), new PropertyMetadata(null, OnPersonChanged));

    public static readonly DependencyProperty InitialsProperty = DependencyProperty.Register(
        nameof(Initials), typeof(string), typeof(AvatarView), new PropertyMetadata("?"));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(AvatarView), new PropertyMetadata(Brushes.SlateGray));

    public static readonly DependencyProperty PhotoProperty = DependencyProperty.Register(
        nameof(Photo), typeof(ImageSource), typeof(AvatarView));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(AvatarView), new PropertyMetadata(32.0, (d, _) => ((AvatarView)d).Resize()));

    /// <summary>Shows the presence dot: green when connected, amber while connecting, grey once gone.</summary>
    public static readonly DependencyProperty ShowPresenceProperty = DependencyProperty.Register(
        nameof(ShowPresence), typeof(bool), typeof(AvatarView), new PropertyMetadata(false, (d, _) => ((AvatarView)d).UpdatePresence()));

    public static readonly DependencyProperty PresenceProperty = DependencyProperty.Register(
        nameof(Presence), typeof(Brush), typeof(AvatarView));

    private static readonly Brush Online = Frozen(new SolidColorBrush(Color.FromRgb(0x7B, 0xD8, 0x8F)));
    private static readonly Brush Connecting = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0x4A)));
    private static readonly Brush Away = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x6E, 0x80)));

    public AvatarView()
    {
        InitializeComponent();
        Resize();
    }

    public PersonViewModel? Person
    {
        get => (PersonViewModel?)GetValue(PersonProperty);
        set => SetValue(PersonProperty, value);
    }

    public string Initials
    {
        get => (string)GetValue(InitialsProperty);
        set => SetValue(InitialsProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public ImageSource? Photo
    {
        get => (ImageSource?)GetValue(PhotoProperty);
        set => SetValue(PhotoProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public bool ShowPresence
    {
        get => (bool)GetValue(ShowPresenceProperty);
        set => SetValue(ShowPresenceProperty, value);
    }

    public Brush? Presence
    {
        get => (Brush?)GetValue(PresenceProperty);
        set => SetValue(PresenceProperty, value);
    }

    private static void OnPersonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (AvatarView)d;
        if (e.OldValue is PersonViewModel old)
        {
            PropertyChangedEventManager.RemoveHandler(old, view.OnPersonPropertyChanged, string.Empty);
        }

        if (e.NewValue is not PersonViewModel person)
        {
            return;
        }

        view.SetBinding(InitialsProperty, new Binding(nameof(PersonViewModel.Initials)) { Source = person });
        view.SetBinding(FillProperty, new Binding(nameof(PersonViewModel.Color)) { Source = person });
        view.SetBinding(PhotoProperty, new Binding(nameof(PersonViewModel.Photo)) { Source = person });
        // Weak: people outlive the many chat lines and lists that draw them.
        PropertyChangedEventManager.AddHandler(person, view.OnPersonPropertyChanged, string.Empty);
        view.UpdatePresence();
        person.MarkOnScreen();
    }

    private void OnPersonPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdatePresence();

    private void UpdatePresence()
    {
        if (Person is { } person)
        {
            Presence = !person.IsPresent ? Away : person.IsReady ? Online : Connecting;
        }

        Dot.Visibility = ShowPresence && Presence is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Resize()
    {
        Letters.FontSize = Math.Max(8, Size * 0.36);
        Dot.Width = Dot.Height = Math.Max(9, Size * 0.3);
        Dot.Margin = new Thickness(0, 0, -1, -1);
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
