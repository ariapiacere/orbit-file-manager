using OrbitFileManager.Models;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrbitFileManager.Services.Settings;

/// <summary>Where <see cref="SessionService.LoadSessionWithRecovery"/> found the session it returned.</summary>
public enum SessionLoadSource
{
    /// <summary>No usable session: first start, or both files missing or damaged.</summary>
    None,
    /// <summary>sessions.json.</summary>
    Primary,
    /// <summary>sessions.json.bak, the copy kept from the previous save, because sessions.json was missing or damaged.</summary>
    Backup
}

public sealed record SessionLoadResult(AppSessionState? Session, SessionLoadSource Source, string? Problem);

/// <summary>
/// Persists open tabs and the pane layout (sessions.json) and saved workspaces (workspaces.json) in the Data folder.
/// The session is rewritten while Orbit runs (see <see cref="SessionAutoSaver"/>), so every write is atomic:
/// the JSON goes to sessions.json.tmp and is flushed to disk, then swapped in, and the file it replaces is kept as
/// sessions.json.bak. A crash, forced kill or power cut therefore leaves either the new or the previous session,
/// never a half-written one. Writes are thread-safe and numbered, so a slower, older write never overwrites a newer one.
/// </summary>
public sealed class SessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AppPathsService _paths;
    private readonly StructuredLogger _logger;

    private readonly object _writeLock = new();
    private long _lastSequence;          // Interlocked
    private long _writtenSequence;       // _writeLock: newest snapshot that is on disk (or was identical to it)
    private string? _writtenFingerprint; // _writeLock: JSON of that snapshot without SavedAt
    private bool _primaryIsDamaged;      // _writeLock: never rotate a damaged sessions.json into the backup
    private bool _lastWriteFailed;       // _writeLock: log a failing disk once, not on every autosave

    public string WorkspacesFilePath => Path.Combine(_paths.DataDirectory, "workspaces.json");
    public string SessionFilePath => _paths.SessionsFilePath;
    public string BackupFilePath => SessionFilePath + ".bak";
    private string TempFilePath => SessionFilePath + ".tmp";

    public SessionService(AppPathsService paths, StructuredLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    // ─── Loading ──────────────────────────────────────────────────────────────

    public AppSessionState? LoadSession() => LoadSessionWithRecovery().Session;

    /// <summary>
    /// Reads sessions.json, falling back to sessions.json.bak when it is missing or damaged. A damaged file is
    /// renamed to sessions.json.corrupt-{time} so it can be inspected and is never rotated into the backup.
    /// Never throws.
    /// </summary>
    public SessionLoadResult LoadSessionWithRecovery()
    {
        string? problem = null;

        switch (TryRead(SessionFilePath, out AppSessionState? primary, out string? primaryError))
        {
            case ReadOutcome.Loaded:
                return new SessionLoadResult(primary!.Sanitize(), SessionLoadSource.Primary, null);
            case ReadOutcome.Corrupt:
                problem = $"sessions.json was damaged ({primaryError})";
                _logger.Warn("Session", "sessions.json is damaged; trying the backup.", new { error = primaryError });
                Quarantine(SessionFilePath);
                break;
            case ReadOutcome.Unreadable:
                problem = $"sessions.json could not be read ({primaryError})";
                _logger.Warn("Session", "sessions.json could not be read; trying the backup.", new { error = primaryError });
                break;
        }

        switch (TryRead(BackupFilePath, out AppSessionState? backup, out string? backupError))
        {
            case ReadOutcome.Loaded:
                _logger.Warn("Session", "Restored tabs from sessions.json.bak.", new { problem });
                return new SessionLoadResult(backup!.Sanitize(), SessionLoadSource.Backup, problem ?? "sessions.json was missing");
            case ReadOutcome.Corrupt:
            case ReadOutcome.Unreadable:
                _logger.Warn("Session", "sessions.json.bak is not usable either.", new { error = backupError });
                break;
        }

        return new SessionLoadResult(null, SessionLoadSource.None, problem);
    }

    private enum ReadOutcome { Missing, Loaded, Corrupt, Unreadable }

    private static ReadOutcome TryRead(string path, out AppSessionState? state, out string? error)
    {
        state = null;
        error = null;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return ReadOutcome.Missing;

                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    error = "the file is empty";
                    return ReadOutcome.Corrupt;
                }

                state = JsonSerializer.Deserialize<AppSessionState>(json, JsonOptions);
                if (state is null)
                {
                    error = "the file holds no session";
                    return ReadOutcome.Corrupt;
                }

                return ReadOutcome.Loaded;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return ReadOutcome.Corrupt;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Usually brief (a virus scanner or backup tool holding the file): try again before giving up.
                error = ex.Message;
                if (attempt >= 3)
                    return ReadOutcome.Unreadable;
                Thread.Sleep(100);
            }
        }
    }

    private void Quarantine(string path)
    {
        lock (_writeLock)
        {
            try
            {
                File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            }
            catch (Exception ex)
            {
                _primaryIsDamaged = true; // could not move it aside: the next save overwrites it without backing it up
                _logger.Warn("Session", "Could not set the damaged sessions.json aside.", new { ex.Message });
            }
        }
    }

    // ─── Saving ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Numbers a session snapshot. Call it on the thread that builds the state (the UI thread), at the moment the
    /// state is captured, so numbers follow the order in which the tabs actually changed.
    /// </summary>
    public long NextSequence() => Interlocked.Increment(ref _lastSequence);

    /// <summary>Saves now (used when the window closes and when Windows ends the session).</summary>
    public bool SaveSession(AppSessionState state) => WriteSession(state, NextSequence(), onlyIfChanged: false);

    /// <summary>
    /// Writes <paramref name="state"/> atomically unless a newer snapshot is already on disk or, with
    /// <paramref name="onlyIfChanged"/>, nothing but the timestamp differs from what is on disk. Safe to call from
    /// any thread; <paramref name="state"/> must not be modified meanwhile. Returns false when the disk write failed.
    /// </summary>
    public bool WriteSession(AppSessionState state, long sequence, bool onlyIfChanged)
    {
        DateTimeOffset savedAt = DateTimeOffset.Now;
        state.SavedAt = default;
        string fingerprint = JsonSerializer.Serialize(state, JsonOptions);
        state.SavedAt = savedAt;

        lock (_writeLock)
        {
            if (sequence <= _writtenSequence)
                return true; // superseded: a newer state was written meanwhile

            if (onlyIfChanged && string.Equals(fingerprint, _writtenFingerprint, StringComparison.Ordinal))
            {
                _writtenSequence = sequence;
                return true;
            }

            try
            {
                WriteAtomically(JsonSerializer.Serialize(state, JsonOptions));
                _writtenSequence = sequence;
                _writtenFingerprint = fingerprint;
                if (_lastWriteFailed)
                    _logger.Info("Session", "Saving sessions.json works again.");
                _lastWriteFailed = false;
                return true;
            }
            catch (Exception ex)
            {
                if (!_lastWriteFailed)
                    _logger.Error("Session", "Unable to save sessions.json.", ex);
                _lastWriteFailed = true;
                TryDelete(TempFilePath);
                return false;
            }
        }
    }

    private void WriteAtomically(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionFilePath)!);

        byte[] bytes = Utf8NoBom.GetBytes(json);
        using (var stream = new FileStream(TempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true); // on disk before it replaces anything, so a power cut cannot leave it empty
        }

        if (_primaryIsDamaged || !File.Exists(SessionFilePath))
        {
            File.Move(TempFilePath, SessionFilePath, overwrite: true);
            _primaryIsDamaged = false;
            return;
        }

        try
        {
            // One step: sessions.json becomes the backup and the new file takes its name.
            File.Replace(TempFilePath, SessionFilePath, BackupFilePath, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // ReplaceFile is not available on every volume (some network shares, FAT): same result in two steps.
            File.Copy(SessionFilePath, BackupFilePath, overwrite: true);
            File.Move(TempFilePath, SessionFilePath, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    // ─── Workspaces ───────────────────────────────────────────────────────────

    public List<WorkspacePreset> LoadWorkspaces()
    {
        try
        {
            if (File.Exists(WorkspacesFilePath))
            {
                string json = File.ReadAllText(WorkspacesFilePath);
                return (JsonSerializer.Deserialize<List<WorkspacePreset>>(json, JsonOptions) ?? [])
                    .Where(w => w?.Session is not null)
                    .Select(w => { w.Session.Sanitize(); return w; })
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Workspaces", "Unable to load workspaces.json.", ex);
        }

        return [];
    }

    public bool SaveWorkspaces(IEnumerable<WorkspacePreset> presets)
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            string temp = WorkspacesFilePath + ".tmp";
            string json = JsonSerializer.Serialize(presets.ToList(), JsonOptions);
            File.WriteAllText(temp, json);
            File.Move(temp, WorkspacesFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Workspaces", "Unable to save workspaces.json.", ex);
            return false;
        }
    }
}
