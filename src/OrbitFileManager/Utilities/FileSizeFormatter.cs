using System.IO;

namespace OrbitFileManager.Utilities;

public static class FileSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            return "0 B";
        if (bytes < 1024)
            return $"{bytes} B";

        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return value >= 100
            ? $"{value:0} {Units[unitIndex]}"
            : value >= 10
                ? $"{value:0.#} {Units[unitIndex]}"
                : $"{value:0.##} {Units[unitIndex]}";
    }

    public static string FormatAttributes(FileAttributes attributes)
    {
        Span<char> buffer = stackalloc char[4];
        int pos = 0;

        if ((attributes & FileAttributes.ReadOnly) != 0) buffer[pos++] = 'R';
        if ((attributes & FileAttributes.Hidden) != 0) buffer[pos++] = 'H';
        if ((attributes & FileAttributes.System) != 0) buffer[pos++] = 'S';
        if ((attributes & FileAttributes.ReparsePoint) != 0) buffer[pos++] = 'L';

        return pos == 0 ? "—" : new string(buffer[..pos]);
    }

    public static string GetFallbackTypeDescription(string extension, bool isDirectory)
    {
        if (isDirectory)
            return "File folder";

        if (string.IsNullOrWhiteSpace(extension))
            return "File";

        string clean = extension.TrimStart('.').ToUpperInvariant();
        return clean switch
        {
            "TXT" => "Text Document",
            "MD" => "Markdown Document",
            "JSON" => "JSON Source File",
            "XML" => "XML Document",
            "CSV" => "CSV Spreadsheet",
            "PDF" => "PDF Document",
            "PNG" or "JPG" or "JPEG" or "BMP" or "GIF" or "WEBP" or "TIFF" or "ICO" => $"{clean} Image",
            "MP4" or "MKV" or "MOV" or "AVI" or "WEBM" => $"{clean} Video",
            "MP3" or "WAV" or "FLAC" or "OGG" or "M4A" => $"{clean} Audio",
            "ZIP" or "7Z" or "RAR" or "TAR" or "GZ" => $"{clean} Archive",
            "EXE" => "Application",
            "DLL" => "Application Extension",
            "CS" => "C# Source File",
            "XAML" => "XAML Markup File",
            "SLN" => "Visual Studio Solution",
            "CSPROJ" => "C# Project File",
            _ => $"{clean} File"
        };
    }
}
