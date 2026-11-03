using OrbitFileManager.Utilities;
using System.IO;
using System.Security.Cryptography;

namespace OrbitFileManager.Services.Productivity;

public sealed record FileChecksumResult
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public string FormattedSize => FileSizeFormatter.FormatBytes(SizeBytes);
    public required string Md5 { get; init; }
    public required string Sha1 { get; init; }
    public required string Sha256 { get; init; }
    public required string Sha512 { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public sealed class ChecksumService
{
    private const int BufferSize = 81920;

    public async Task<FileChecksumResult> ComputeChecksumsAsync(
        string filePath,
        IProgress<double>? progressPercentage = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Target file was not found.", filePath);

        var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

        return await Task.Run(async () =>
        {
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            long totalLength = stream.Length;
            long totalRead = 0;
            byte[] buffer = new byte[BufferSize];

            if (totalLength == 0)
            {
                progressPercentage?.Report(100.0);
            }
            else
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                {
                    md5.AppendData(buffer, 0, read);
                    sha1.AppendData(buffer, 0, read);
                    sha256.AppendData(buffer, 0, read);
                    sha512.AppendData(buffer, 0, read);

                    totalRead += read;
                    progressPercentage?.Report(Math.Clamp((double)totalRead / totalLength * 100.0, 0.0, 100.0));
                }
            }

            TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp);

            return new FileChecksumResult
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                SizeBytes = totalLength,
                Md5 = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
                Sha1 = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
                Sha256 = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(),
                Sha512 = Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant(),
                Elapsed = elapsed
            };
        }, cancellationToken);
    }

    public static (bool IsMatch, string? AlgorithmName) VerifyExpectedHash(FileChecksumResult result, string? expectedHashInput)
    {
        if (string.IsNullOrWhiteSpace(expectedHashInput))
            return (false, null);

        string cleaned = new string(expectedHashInput
            .Where(c => !char.IsWhiteSpace(c) && c != '-')
            .ToArray());

        if (cleaned.Length == 0)
            return (false, null);

        if (string.Equals(cleaned, result.Md5, StringComparison.OrdinalIgnoreCase))
            return (true, "MD5");
        if (string.Equals(cleaned, result.Sha1, StringComparison.OrdinalIgnoreCase))
            return (true, "SHA-1");
        if (string.Equals(cleaned, result.Sha256, StringComparison.OrdinalIgnoreCase))
            return (true, "SHA-256");
        if (string.Equals(cleaned, result.Sha512, StringComparison.OrdinalIgnoreCase))
            return (true, "SHA-512");

        return (false, null);
    }
}
