using System.ComponentModel;
using System.Diagnostics;

namespace Ludo.Presentation.Wpf.Shell;

/// <summary>
/// One game per Windows session: with the app able to sit in the tray, a second copy would join the
/// lobby as a second player. A second launch of the same exe asks the running copy to show itself; a launch
/// of a different exe replaces it, so updating is just opening the new download.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string SignalName = @"Local\DiceDrop.Activate.7E3C1B52";
    private const string QuitSignalName = @"Local\DiceDrop.Quit.7E3C1B52";
    private const string ProductName = "DiceDrop";

    // Long enough for a clean exit, which waits up to 2 seconds on telling peers we are leaving.
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(3);

    private readonly EventWaitHandle _signal;
    private readonly EventWaitHandle _quitSignal = new(false, EventResetMode.AutoReset, QuitSignalName);
    private RegisteredWaitHandle? _listener;
    private RegisteredWaitHandle? _quitListener;

    private SingleInstance(EventWaitHandle signal) => _signal = signal;

    /// <summary>
    /// Returns null when another copy stays running; it has been asked to show itself unless <paramref name="quiet"/>.
    /// A quiet launch is Windows starting us, never the user opening an update, so it replaces nothing.
    /// </summary>
    public static SingleInstance? TryClaim(bool quiet)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName, out var createdNew);
        if (createdNew)
        {
            return new SingleInstance(signal);
        }

        if (quiet)
        {
            signal.Dispose();
            return null;
        }

        using var running = FindOtherCopyFromAnotherExe();
        if (running is not null)
        {
            // Our handle would keep the signal alive, and with it the running copy's claim.
            signal.Dispose();
            StopCopy(running);
            signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName, out createdNew);
            if (createdNew)
            {
                return new SingleInstance(signal);
            }
        }

        signal.Set();
        signal.Dispose();
        return null;
    }

    /// <summary>The callbacks run on a thread-pool thread.</summary>
    public void Listen(Action onActivate, Action onQuit)
    {
        _listener = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
        _quitListener = ThreadPool.RegisterWaitForSingleObject(_quitSignal, (_, _) => onQuit(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public void Dispose()
    {
        _listener?.Unregister(null);
        _quitListener?.Unregister(null);
        _signal.Dispose();
        _quitSignal.Dispose();
    }

    /// <summary>Matches on the product name, not the process name: a re-download is often "DiceDrop (1).exe".</summary>
    private static Process? FindOtherCopyFromAnotherExe()
    {
        using var self = Process.GetCurrentProcess();
        Process? found = null;
        foreach (var process in Process.GetProcesses())
        {
            if (found is null && process.Id != self.Id && process.SessionId == self.SessionId && IsCopyFromAnotherExe(process))
            {
                found = process;
            }
            else
            {
                process.Dispose();
            }
        }

        return found;
    }

    private static bool IsCopyFromAnotherExe(Process process)
    {
        try
        {
            return process.MainModule is { } module
                && module.FileVersionInfo.ProductName == ProductName
                && !string.Equals(module.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Protected, or gone already: not one we can replace.
            return false;
        }
    }

    /// <summary>Asks for a clean exit so peers hear we left, then kills: builds before this one don't listen.</summary>
    private static void StopCopy(Process process)
    {
        if (EventWaitHandle.TryOpenExisting(QuitSignalName, out var quit))
        {
            quit.Set();
            quit.Dispose();
            if (process.WaitForExit(QuitTimeout))
            {
                return;
            }
        }

        try
        {
            process.Kill();
            process.WaitForExit(KillTimeout);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // It exited by itself, or can't be stopped; the caller then finds the claim still taken.
        }
    }
}
