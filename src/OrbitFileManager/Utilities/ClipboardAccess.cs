using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OrbitFileManager.Utilities;

/// <summary>
/// Clipboard calls that survive another program holding the clipboard.
/// </summary>
/// <remarks>
/// Only one program can have the clipboard open at a time. Clipboard history, remote-desktop clipboard sync and
/// clipboard managers open it right after every change to read it. WPF gives up quickly: the flush after
/// <c>Clipboard.SetDataObject(data, copy: true)</c> retries for only about 0.1 s. It then throws
/// <c>COMException 0x800401D0 CLIPBRD_E_CANT_OPEN</c>, which used to reach the "unexpected error" dialog.
/// Every call here is retried for about 2.5 s without blocking the UI thread, then reported as busy instead.
/// </remarks>
public static class ClipboardAccess
{
    public const string BusyMessage = "The clipboard is in use by another program. Try again in a moment.";

    // CLIPBRD_E_CANT_OPEN, CLIPBRD_E_CANT_EMPTY, CLIPBRD_E_CANT_SET, CLIPBRD_E_BAD_DATA, CLIPBRD_E_CANT_CLOSE
    private static readonly int[] BusyCodes =
    [
        unchecked((int)0x800401D0), unchecked((int)0x800401D1), unchecked((int)0x800401D2),
        unchecked((int)0x800401D3), unchecked((int)0x800401D4)
    ];

    /// <summary>Waits between attempts; about 2.5 s in all.</summary>
    public static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(600),
        TimeSpan.FromMilliseconds(900)
    ];

    public static bool IsBusy(Exception ex) => ex is ExternalException && BusyCodes.Contains(ex.HResult);

    private static HwndSource? _probeWindow;

    /// <summary>
    /// True when no other program has the clipboard open right now. WPF's own clipboard calls retry for about a
    /// second by putting the UI thread to sleep, so a busy clipboard is waited out here first, without blocking.
    /// Opening and closing without changing anything sends no clipboard-change notice. UI thread only.
    /// </summary>
    /// <remarks>
    /// The check opens the clipboard with a real (hidden, message-only) window. Opened without a window, Windows
    /// can let the open through while another program holds the clipboard the same way, and the CloseClipboard
    /// that follows then ends that program's open. Measured on Windows 11.
    /// </remarks>
    public static bool IsFreeNow()
    {
        IntPtr window = ProbeWindow();
        if (window == IntPtr.Zero)
            return true; // cannot check safely; WPF's own call still retries briefly

        if (!OpenClipboard(window))
            return false;
        CloseClipboard();
        return true;
    }

    private static IntPtr ProbeWindow()
    {
        try
        {
            _probeWindow ??= new HwndSource(new HwndSourceParameters("Orbit clipboard check")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE: never shown, receives no broadcasts
                WindowStyle = 0,
                Width = 0,
                Height = 0
            });
            return _probeWindow.Handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> (a clipboard call, on the UI thread). Returns false when the clipboard stayed
    /// busy; other errors are thrown as before. Completes synchronously when the clipboard is free.
    /// </summary>
    public static async Task<bool> TryRunAsync(Action action, Func<TimeSpan, Task>? delay = null, Func<bool>? isFree = null)
    {
        var (ok, _) = await TryGetAsync(() => { action(); return true; }, delay, isFree);
        return ok;
    }

    /// <summary>Like <see cref="TryRunAsync"/> for a read; <c>Ok</c> is false when the clipboard stayed busy.</summary>
    public static async Task<(bool Ok, T? Value)> TryGetAsync<T>(Func<T> read, Func<TimeSpan, Task>? delay = null, Func<bool>? isFree = null)
    {
        delay ??= Task.Delay;
        isFree ??= IsFreeNow;
        for (int attempt = 0; ; attempt++)
        {
            if (isFree())
            {
                try
                {
                    return (true, read());
                }
                catch (Exception ex) when (IsBusy(ex))
                {
                    // Taken again between the check and the call (e.g. a clipboard listener reacting to a change).
                }
            }

            if (attempt >= RetryDelays.Length)
                return (false, default);
            await delay(RetryDelays[attempt]);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
}
