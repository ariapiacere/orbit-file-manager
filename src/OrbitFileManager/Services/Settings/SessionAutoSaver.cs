using OrbitFileManager.Models;
using System.Windows.Threading;

namespace OrbitFileManager.Services.Settings;

/// <summary>
/// Keeps sessions.json current while Orbit runs, so a crash, a forced kill or a Windows restart loses at most the
/// last second of tab changes instead of everything since the window was last closed normally.
/// <list type="bullet">
/// <item><see cref="RequestSave"/> (any tab or layout change) saves once the changes settle for <see cref="Debounce"/>.</item>
/// <item>A timer also saves every <see cref="Interval"/>, which catches changes nobody reported (selection, history).</item>
/// </list>
/// The state is captured on the UI thread; serialising, comparing and writing happen on a worker thread, and nothing
/// is written when only the timestamp would change.
/// </summary>
public sealed class SessionAutoSaver : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(750);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly SessionService _sessionService;
    private readonly Func<AppSessionState> _captureState;
    private readonly StructuredLogger _logger;
    private readonly DispatcherTimer _debounceTimer;
    private readonly DispatcherTimer _intervalTimer;
    private bool _running;

    public TimeSpan Debounce => _debounceTimer.Interval;
    public TimeSpan Interval => _intervalTimer.Interval;

    public SessionAutoSaver(
        SessionService sessionService,
        Func<AppSessionState> captureState,
        StructuredLogger logger,
        Dispatcher dispatcher,
        TimeSpan? debounce = null,
        TimeSpan? interval = null)
    {
        _sessionService = sessionService;
        _captureState = captureState;
        _logger = logger;

        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = debounce ?? DefaultDebounce };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            SaveIfChanged();
        };

        _intervalTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = interval ?? DefaultInterval };
        _intervalTimer.Tick += (_, _) => SaveIfChanged();
    }

    public void Start()
    {
        _running = true;
        _intervalTimer.Start();
        RequestSave();
    }

    /// <summary>Something in the session changed. Ignored before <see cref="Start"/> (while tabs are being restored).</summary>
    public void RequestSave()
    {
        if (!_running)
            return;

        _debounceTimer.Stop(); // restart: wait until a burst of changes (a splitter drag, a tab drag) is over
        _debounceTimer.Start();
    }

    /// <summary>Captures the session on the calling (UI) thread and writes it in the background if it changed.</summary>
    public Task SaveIfChanged()
    {
        if (!_running)
            return Task.CompletedTask;

        AppSessionState state;
        try
        {
            state = _captureState();
        }
        catch (Exception ex)
        {
            _logger.Error("Session", "Could not capture the session for autosave.", ex);
            return Task.CompletedTask;
        }

        long sequence = _sessionService.NextSequence();
        return Task.Run(() => _sessionService.WriteSession(state, sequence, onlyIfChanged: true));
    }

    public void Stop()
    {
        _running = false;
        _debounceTimer.Stop();
        _intervalTimer.Stop();
    }

    public void Dispose() => Stop();
}
