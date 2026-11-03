using OrbitFileManager.Models;
using System.IO;

namespace OrbitFileManager.Utilities;

public static class PathValidator
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    public static (bool IsValid, string? ErrorMessage) ValidateEntryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (false, "Name cannot be empty or whitespace.");

        if (name is "." or "..")
            return (false, "'.' and '..' are reserved directory names.");

        if (name.Length > 255)
            return (false, "Name cannot exceed 255 characters.");

        if (name.EndsWith(' ') || name.EndsWith('.'))
            return (false, "Windows file and folder names cannot end with a space or period.");

        if (name.IndexOfAny(InvalidNameChars) >= 0)
            return (false, "Name contains invalid characters (\\ / : * ? \" < > |).");

        // Windows forbids reserved device names even with extensions (e.g., CON.txt, NUL.log)
        int dotIndex = name.IndexOf('.');
        string stem = dotIndex >= 0 ? name[..dotIndex] : name;
        if (ReservedDeviceNames.Contains(stem))
            return (false, $"'{stem}' is a reserved Windows device name.");

        return (true, null);
    }

    public static string NormalizePath(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return string.Empty;

        string trimmed = rawInput.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        string expanded = Environment.ExpandEnvironmentVariables(trimmed)
            .Replace('/', Path.DirectorySeparatorChar);

        // Handle bare drive letter like "C:" -> "C:\"
        if (expanded.Length == 2 && char.IsLetter(expanded[0]) && expanded[1] == ':')
            return expanded + Path.DirectorySeparatorChar;

        try
        {
            string full = Path.GetFullPath(expanded);
            string? root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return root;

            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return expanded;
        }
    }

    public static bool PathsEqual(string? pathA, string? pathB)
    {
        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
            return false;

        return string.Equals(NormalizePath(pathA), NormalizePath(pathB), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if <paramref name="candidateTarget"/> is the same directory as <paramref name="sourceDirectory"/>
    /// or lies anywhere inside <paramref name="sourceDirectory"/>.
    /// Prevents recursive self-copy / self-move loops.
    /// </summary>
    public static bool IsSameOrSubPath(string sourceDirectory, string candidateTarget)
    {
        string normalizedSource = NormalizePath(sourceDirectory);
        string normalizedTarget = NormalizePath(candidateTarget);

        if (string.IsNullOrEmpty(normalizedSource) || string.IsNullOrEmpty(normalizedTarget))
            return false;

        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            return true;

        string sourceWithSep = normalizedSource.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedSource
            : normalizedSource + Path.DirectorySeparatorChar;

        return normalizedTarget.StartsWith(sourceWithSep, StringComparison.OrdinalIgnoreCase);
    }

    public static string? GetParentDirectoryPath(string? currentPath)
    {
        string normalized = NormalizePath(currentPath);
        if (string.IsNullOrEmpty(normalized))
            return null;

        string? root = Path.GetPathRoot(normalized);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(normalized.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Directory.GetParent(normalized)?.FullName;
    }

    public static IReadOnlyList<BreadcrumbSegment> BuildBreadcrumbs(string? fullPath)
    {
        string normalized = NormalizePath(fullPath);
        if (string.IsNullOrEmpty(normalized))
            return [];

        var segments = new List<BreadcrumbSegment>();
        string? root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root))
        {
            segments.Add(new BreadcrumbSegment { Label = normalized, FullPath = normalized, IsLast = true });
            return segments;
        }

        string rootLabel = root.StartsWith(@"\\", StringComparison.Ordinal)
            ? root.TrimEnd(Path.DirectorySeparatorChar)
            : root.TrimEnd(Path.DirectorySeparatorChar);

        string rootFullPath = root.StartsWith(@"\\", StringComparison.Ordinal)
            ? root.TrimEnd(Path.DirectorySeparatorChar)
            : (root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar);

        string remainder = normalized[root.Length..].Trim(Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(remainder))
        {
            segments.Add(new BreadcrumbSegment
            {
                Label = rootLabel,
                FullPath = rootFullPath,
                IsLast = true
            });
            return segments;
        }

        segments.Add(new BreadcrumbSegment
        {
            Label = rootLabel,
            FullPath = rootFullPath,
            IsLast = false
        });

        string[] parts = remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string running = rootFullPath.TrimEnd(Path.DirectorySeparatorChar);
        for (int i = 0; i < parts.Length; i++)
        {
            running = running + Path.DirectorySeparatorChar + parts[i];
            segments.Add(new BreadcrumbSegment
            {
                Label = parts[i],
                FullPath = running,
                IsLast = i == parts.Length - 1
            });
        }

        return segments;
    }

    public static string GenerateUniqueDestinationPath(
        string destinationDirectory,
        string itemName,
        bool isDirectory,
        bool useCopyLabel = false)
    {
        string candidate = Path.Combine(destinationDirectory, itemName);
        if (!EntryExists(candidate))
            return candidate;

        string baseName = isDirectory ? itemName : Path.GetFileNameWithoutExtension(itemName);
        string extension = isDirectory ? string.Empty : Path.GetExtension(itemName);

        for (int counter = 1; counter < 10_000; counter++)
        {
            string suffix = useCopyLabel
                ? (counter == 1 ? " - Copy" : $" - Copy ({counter})")
                : $" ({counter + 1})";

            string nextCandidate = Path.Combine(destinationDirectory, $"{baseName}{suffix}{extension}");
            if (!EntryExists(nextCandidate))
                return nextCandidate;
        }

        return Path.Combine(destinationDirectory, $"{baseName}_{Guid.NewGuid():N}{extension}");
    }

    public static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);
}
