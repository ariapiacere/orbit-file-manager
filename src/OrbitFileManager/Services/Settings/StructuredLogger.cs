using System.IO;
using System.Text.Json;

namespace OrbitFileManager.Services.Settings;

public sealed class StructuredLogger
{
    private readonly AppPathsService _paths;
    private readonly object _sync = new();

    public StructuredLogger(AppPathsService paths)
    {
        _paths = paths;
    }

    public void Info(string category, string message, object? context = null) =>
        WriteEntry("INFO", category, message, null, context);

    public void Warn(string category, string message, object? context = null) =>
        WriteEntry("WARN", category, message, null, context);

    public void Error(string category, string message, Exception? exception = null, object? context = null) =>
        WriteEntry("ERROR", category, message, exception, context);

    private void WriteEntry(string level, string category, string message, Exception? exception, object? context)
    {
        try
        {
            var payload = new
            {
                timestamp = DateTimeOffset.Now.ToString("O"),
                level,
                category,
                message,
                exceptionType = exception?.GetType().Name,
                exceptionMessage = exception?.Message,
                stackTrace = exception?.ToString(),
                context
            };

            string line = JsonSerializer.Serialize(payload) + Environment.NewLine;
            string logFile = Path.Combine(_paths.LogsDirectory, $"orbit-{DateTime.Now:yyyyMMdd}.jsonl");

            lock (_sync)
            {
                Directory.CreateDirectory(_paths.LogsDirectory);
                File.AppendAllText(logFile, line);
            }
        }
        catch
        {
            // Logging must never crash the application or fail user operations.
        }
    }
}
