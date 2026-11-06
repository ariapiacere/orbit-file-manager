using System.IO;

namespace OrbitFileManager.Utilities;

internal static class FileTransferCommit
{
    private const int MaxAttempts = 16;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    public static void MoveFile(
        string source,
        string destination,
        bool overwrite,
        CancellationToken cancellationToken,
        Action? waitIfPaused = null)
    {
        if (Directory.Exists(destination))
            throw new IOException("Cannot overwrite a directory with a file.");

        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            waitIfPaused?.Invoke();
            try
            {
                if (overwrite && File.Exists(destination))
                {
                    FileAttributes attrs = File.GetAttributes(destination);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(destination, attrs & ~FileAttributes.ReadOnly);
                }

                File.Move(source, destination, overwrite);
                return;
            }
            catch (Exception ex) when (overwrite && IsReplacementBlocked(ex))
            {
                if (attempt >= MaxAttempts)
                    throw new IOException(
                        $"Could not replace '{destination}'. The file is in use or write access was denied. " +
                        "Close any program using it and check destination permissions, then retry. " +
                        "The existing destination file was kept.", ex);

                // SMB readers and shell previews can deny rename/delete briefly. Retain the staged file
                // and the old destination while waiting; never delete the destination to force a replace.
                cancellationToken.WaitHandle.WaitOne(RetryDelay);
            }
        }
    }

    private static bool IsReplacementBlocked(Exception exception)
    {
        // MoveFileEx can report ACCESS_DENIED (5), as well as the two sharing/locking errors,
        // when an existing destination was opened without FILE_SHARE_DELETE.
        return (exception is IOException or UnauthorizedAccessException) &&
               (exception.HResult & 0xFFFF) is 5 or 32 or 33;
    }
}
