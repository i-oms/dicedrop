using System.Reflection;
using System.Windows;
using Ludo.Application;
using Ludo.Application.UseCases;
using Ludo.Infrastructure.Crypto;
using Ludo.Infrastructure.Discovery;
using Ludo.Infrastructure.Networking;
using Ludo.Infrastructure.Updates;
using Ludo.Presentation.Wpf.Presenters;
using Ludo.Presentation.Wpf.Security;
using Ludo.Presentation.Wpf.Shell;
using Ludo.Presentation.Wpf.ViewModels;
using Ludo.Presentation.Wpf.Views;

namespace Ludo.Presentation.Wpf;

/// <summary>Composition root: the only place that knows every layer and wires adapters to ports.</summary>
public partial class App : System.Windows.Application
{
    private ProcessWindowGuard? _windowGuard;
    private UdpLobbyDiscovery? _discovery;
    private LudoApplication? _app;
    private ChatToastController? _chatToasts;
    private NudgeController? _nudges;
    private QuickMessageWindow? _quickMessage;
    private SingleInstance? _instance;
    private TrayShell? _tray;
    private GitHubReleaseFeed? _releases;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var startInTray = e.Args.Contains(StartupRegistration.TrayArgument, StringComparer.OrdinalIgnoreCase);
        _instance = SingleInstance.TryClaim(quiet: startInTray);
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        // Protection goes up before any window exists.
        PopupSuppression.Register();
        _windowGuard = ProcessWindowGuard.Install();

        var presenter = new WpfSessionPresenter(Dispatcher);
        var diagnostics = new WpfDiagnosticLog(Dispatcher);
        _discovery = new UdpLobbyDiscovery(diagnostics);
        _app = new LudoApplication(
            new TcpPeerNetwork(diagnostics),
            _discovery,
            new Sha256CryptoProvider(),
            presenter,
            TimeProvider.System,
            diagnostics);

        // The release workflow stamps the tag's version into the exe; local builds stay 0.0.0 and skip the check.
        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        _releases = new GitHubReleaseFeed(ProjectLinks.Owner, ProjectLinks.Repository, $"DiceDrop/{version.ToString(3)}");
        var checkForUpdate = new CheckForUpdateUseCase(_releases, version, diagnostics);

        var settings = UserSettings.Load();
        _viewModel = new MainViewModel(_app, presenter, diagnostics, checkForUpdate, settings);
        var viewModel = _viewModel;
        var window = new MainWindow { DataContext = _viewModel };
        window.ProtectionFailed += (_, _) =>
        {
            MessageBox.Show(
                "DiceDrop could not enable screen-capture protection on this PC, so it will not run.\n\n" +
                "Windows 10 or 11 with desktop composition is required.",
                "DiceDrop",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _tray?.Exit(2);
        };

        _tray = new TrayShell(window, presenter, Shutdown, OpenQuickMessage);
        _chatToasts = new ChatToastController(
            window,
            presenter,
            () => viewModel.Settings.Saved,
            viewModel.Lobby.FindPerson,
            viewModel.OpenConversation);
        _nudges = new NudgeController(window, presenter, () => viewModel.Settings.Saved);
        _instance.Listen(
            () => Dispatcher.BeginInvoke(window.BringToFront),
            () => Dispatcher.BeginInvoke(() => _tray.Exit()));
        StartupRegistration.RefreshPath();
        MainWindow = window;

        // Started with Windows: stay in the tray. The lobby still forms and chat still shows as toasts.
        if (!startInTray)
        {
            window.Show();
        }

        _viewModel.Update.Start();
    }

    /// <summary>The tray's "Send message" window; one at a time.</summary>
    private void OpenQuickMessage()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_quickMessage is { IsVisible: true })
        {
            _quickMessage.Activate();
            return;
        }

        var window = new QuickMessageWindow();
        window.DataContext = new QuickMessageViewModel(_app!, _viewModel.Lobby, _viewModel.ShowError, window.CloseOnce);
        window.Closed += (_, _) => _quickMessage = null;
        _quickMessage = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Tell peers we are leaving; don't hang the exit if the network is wedged.
        _app?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _viewModel?.Update.Dispose();
        _releases?.Dispose();
        _discovery?.Dispose();
        _windowGuard?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
