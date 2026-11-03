namespace OrbitFileManager.Utilities;

/// <summary>
/// Lifecycle rules for "latest operation wins" CancellationTokenSource fields.
/// A superseded source is only cancelled, never disposed, because the operation that owns it may still
/// be reading its token. The owning operation disposes its own source when it finishes via <see cref="Release"/>.
/// </summary>
public static class CancellationHelper
{
    /// <summary>Cancels whatever source is in <paramref name="field"/> and installs a fresh one.</summary>
    public static CancellationTokenSource Replace(ref CancellationTokenSource? field)
    {
        var next = new CancellationTokenSource();
        SafeCancel(Interlocked.Exchange(ref field, next));
        return next;
    }

    /// <summary>Cancels and detaches the current source; its owner still disposes it.</summary>
    public static void CancelAndClear(ref CancellationTokenSource? field) =>
        SafeCancel(Interlocked.Exchange(ref field, null));

    /// <summary>
    /// Called by the owning operation when it is done. Clears the field if it still holds
    /// <paramref name="owned"/> and disposes it. Returns true when the operation was still the latest one.
    /// </summary>
    public static bool Release(ref CancellationTokenSource? field, CancellationTokenSource owned)
    {
        bool wasCurrent = ReferenceEquals(Interlocked.CompareExchange(ref field, null, owned), owned);
        owned.Dispose();
        return wasCurrent;
    }

    public static void SafeCancel(CancellationTokenSource? cts)
    {
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The owning operation already finished and released it.
        }
    }
}
