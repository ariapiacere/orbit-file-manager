namespace OrbitFileManager.Services.FileSystem;

/// <summary>
/// Notices drives coming and going (USB disks, card readers, mapped network drives) so the sidebar updates without
/// restarting Orbit.
/// </summary>
/// <remarks>
/// Two sources:
/// <list type="bullet">
/// <item>The drive letters (<see cref="Environment.GetLogicalDrives"/>: one bitmask, no disk access), checked every
/// <see cref="Interval"/>. This catches USB disks and network drives being mapped or unmapped.</item>
/// <item>Windows' device-change broadcast, which MainWindow forwards to <see cref="NotifyDeviceChange"/>. It is
/// instant and also covers media put into a drive that already has a letter (a card reader).</item>
/// </list>
/// <see cref="DrivesChanged"/> is raised on a thread-pool thread.
/// </remarks>
public sealed class DriveWatcher : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    // A device-change message comes before the new volume is usable; wait a moment before reading it.
    public static readonly TimeSpan DeviceSettleDelay = TimeSpan.FromMilliseconds(800);

    private readonly Func<string[]> _getDriveLetters;
    private readonly object _sync = new();
    private Timer? _timer;
    private string _lastLetters = string.Empty;
    private int _pendingDeviceChecks;
    private bool _disposed;

    public DriveWatcher(TimeSpan? interval = null, Func<string[]>? getDriveLetters = null)
    {
        Interval = interval ?? DefaultInterval;
        _getDriveLetters = getDriveLetters ?? Environment.GetLogicalDrives;
    }

    public TimeSpan Interval { get; }

    /// <summary>The set of drives changed, or Windows reported a device change. Thread-pool thread.</summary>
    public event Action? DrivesChanged;

    /// <summary>Remembers the current drives and starts checking. The drives present now raise nothing.</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _timer is not null)
                return;
            _lastLetters = ReadLetters();
            _timer = new Timer(_ => CheckNow(), null, Interval, Interval);
        }
    }

    /// <summary>Compares the drive letters with the last check; raises <see cref="DrivesChanged"/> when they differ.</summary>
    public bool CheckNow()
    {
        string now = ReadLetters();
        lock (_sync)
        {
            if (_disposed || now == _lastLetters)
                return false;
            _lastLetters = now;
        }

        DrivesChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Windows reported a device arriving or leaving. Raises <see cref="DrivesChanged"/> after
    /// <see cref="DeviceSettleDelay"/> even when the letters are unchanged (new media in a card reader).
    /// Several messages in a row produce one event.
    /// </summary>
    public void NotifyDeviceChange()
    {
        if (_disposed || Interlocked.Increment(ref _pendingDeviceChecks) > 1)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(DeviceSettleDelay).ConfigureAwait(false);
            Interlocked.Exchange(ref _pendingDeviceChecks, 0);
            if (_disposed)
                return;

            lock (_sync)
                _lastLetters = ReadLetters(); // the poll must not report the same change again
            DrivesChanged?.Invoke();
        });
    }

    private string ReadLetters()
    {
        try
        {
            return string.Join("|", _getDriveLetters().Select(d => d.ToUpperInvariant()).Order(StringComparer.Ordinal));
        }
        catch
        {
            return _lastLetters; // treat a failed read as "no change"
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
