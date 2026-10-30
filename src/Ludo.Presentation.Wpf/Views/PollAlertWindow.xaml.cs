using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ludo.Presentation.Wpf.Security;
using Ludo.Presentation.Wpf.ViewModels;

namespace Ludo.Presentation.Wpf.Views;

/// <summary>
/// Notifications waiting for our answer, in the top-right corner with their buttons. Unlike the chat pop-up it has
/// no timer: a card stays until it is answered, clicked or dismissed. Protected from captures like the main window.
/// </summary>
public partial class PollAlertWindow : ProtectedWindow
{
    private const double ScreenMargin = 16;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly ObservableCollection<PollCardViewModel> _cards = [];
    private bool _protectionFailed;

    public PollAlertWindow()
    {
        InitializeComponent();
        Cards.ItemsSource = _cards;
        Scroller.MaxHeight = SystemParameters.WorkArea.Height * 0.8;
        SizeChanged += (_, _) => PlaceInCorner();
        ProtectionFailed += (_, _) => _protectionFailed = true;
    }

    public event Action<PollCardViewModel>? CardClicked;

    public void Add(PollCardViewModel card)
    {
        if (_protectionFailed || _cards.Contains(card))
        {
            return;
        }

        _cards.Insert(0, card);
        if (!IsVisible)
        {
            Show();
            SlideIn();
        }
    }

    /// <summary>Takes away the cards that no longer qualify, and the window with the last of them.</summary>
    public void Keep(Func<PollCardViewModel, bool> stillNeeded)
    {
        foreach (var card in _cards.Where(c => !stillNeeded(c)).ToArray())
        {
            Remove(card);
        }
    }

    public void Remove(PollCardViewModel card)
    {
        _cards.Remove(card);
        if (_cards.Count == 0)
        {
            Hide();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Keep it out of Alt+Tab and never let it take focus; the buttons still work without it.
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExToolWindow | WsExNoActivate);
    }

    private void OnCardClicked(object sender, MouseButtonEventArgs e) =>
        CardClicked?.Invoke((PollCardViewModel)((FrameworkElement)sender).DataContext);

    private void OnDismissClicked(object sender, RoutedEventArgs e) =>
        Remove((PollCardViewModel)((FrameworkElement)sender).DataContext);

    /// <summary>The content glides in from the screen edge when the alert appears.</summary>
    private void SlideIn()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(320);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = new TranslateTransform();
        Body.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, duration) { EasingFunction = ease });
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    private void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - ScreenMargin;
        Top = area.Top + ScreenMargin;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
