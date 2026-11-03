using OrbitFileManager.Models;
using OrbitFileManager.Utilities;
using System.IO;
using System.Text.Json;

namespace OrbitFileManager.Services.Settings;

public sealed class ItemUserMetadata
{
    public string? ColorTagHex { get; set; }
    public string? Note { get; set; }
}

public sealed class MetadataStoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppPathsService _paths;
    private readonly StructuredLogger _logger;
    private readonly Dictionary<string, ItemUserMetadata> _entries = new(StringComparer.OrdinalIgnoreCase);

    public string MetadataFilePath => Path.Combine(_paths.DataDirectory, "item_metadata.json");

    public MetadataStoreService(AppPathsService paths, StructuredLogger logger)
    {
        _paths = paths;
        _logger = logger;
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(MetadataFilePath))
            {
                string json = File.ReadAllText(MetadataFilePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, ItemUserMetadata>>(json, JsonOptions);
                _entries.Clear();
                if (data is not null)
                {
                    foreach (var kvp in data)
                        _entries[PathValidator.NormalizePath(kvp.Key)] = kvp.Value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Metadata", "Unable to load item_metadata.json.", ex);
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            string temp = MetadataFilePath + ".tmp";
            string json = JsonSerializer.Serialize(_entries, JsonOptions);
            File.WriteAllText(temp, json);
            File.Move(temp, MetadataFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Error("Metadata", "Unable to save item_metadata.json.", ex);
        }
    }

    public ItemUserMetadata? GetMetadata(string fullPath)
    {
        string key = PathValidator.NormalizePath(fullPath);
        return _entries.TryGetValue(key, out var meta) ? meta : null;
    }

    public void SetColorTag(string fullPath, string? colorHex)
    {
        string key = PathValidator.NormalizePath(fullPath);
        if (string.IsNullOrEmpty(key))
            return;

        if (!_entries.TryGetValue(key, out var meta))
        {
            if (string.IsNullOrWhiteSpace(colorHex))
                return;
            meta = new ItemUserMetadata();
            _entries[key] = meta;
        }

        meta.ColorTagHex = string.IsNullOrWhiteSpace(colorHex) ? null : colorHex;
        CleanupIfEmpty(key, meta);
        Save();
    }

    public void SetNote(string fullPath, string? note)
    {
        string key = PathValidator.NormalizePath(fullPath);
        if (string.IsNullOrEmpty(key))
            return;

        if (!_entries.TryGetValue(key, out var meta))
        {
            if (string.IsNullOrWhiteSpace(note))
                return;
            meta = new ItemUserMetadata();
            _entries[key] = meta;
        }

        meta.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        CleanupIfEmpty(key, meta);
        Save();
    }

    public void DecorateItems(IEnumerable<FileEntryItem> items)
    {
        if (_entries.Count == 0)
            return;

        foreach (var item in items)
        {
            if (_entries.TryGetValue(PathValidator.NormalizePath(item.FullPath), out var meta))
            {
                item.ColorTagHex = meta.ColorTagHex;
                item.Note = meta.Note;
            }
        }
    }

    private void CleanupIfEmpty(string key, ItemUserMetadata meta)
    {
        if (string.IsNullOrWhiteSpace(meta.ColorTagHex) && string.IsNullOrWhiteSpace(meta.Note))
            _entries.Remove(key);
    }
}
