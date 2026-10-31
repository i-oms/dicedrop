namespace Ludo.Application.Session;

/// <summary>
/// Runs work at most once per interval: at once if it has been quiet, otherwise once more when the interval
/// is up, however many requests came in meanwhile. Requests must come from the <see cref="SessionExecutor"/>,
/// and the work always runs on it.
/// </summary>
internal sealed class ExecutorThrottle(SessionExecutor executor, TimeProvider time, TimeSpan interval, Action work)
{
    private DateTimeOffset? _lastRun;
    private bool _scheduled;

    public void Request()
    {
        if (_scheduled)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (_lastRun is not { } last || now - last >= interval)
        {
            Run(now);
            return;
        }

        _scheduled = true;
        _ = Task.Delay(interval - (now - last), time).ContinueWith(
            _ => executor.Post(() =>
            {
                _scheduled = false;
                Run(time.GetUtcNow());
            }),
            TaskScheduler.Default);
    }

    private void Run(DateTimeOffset now)
    {
        _lastRun = now;
        work();
    }
}
