using OrbitFileManager.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrbitFileManager.Services.Settings;

public sealed class SessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppPathsService _paths;
    private readonly StructuredLogger _logger;

    public string WorkspacesFilePath => Path.Combine(_paths.DataDirectory, "workspaces.json");

    public SessionService(AppPathsService paths, StructuredLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public AppSessionState? LoadSession()
    {
        try
        {
            if (File.Exists(_paths.SessionsFilePath))
            {
                string json = File.ReadAllText(_paths.SessionsFilePath);
                return JsonSerializer.Deserialize<AppSessionState>(json, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Session", "Unable to load sessions.json.", ex);
        }

        return null;
    }

    public bool SaveSession(AppSessionState state)
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            state.SavedAt = DateTimeOffset.Now;
            string temp = _paths.SessionsFilePath + ".tmp";
            string json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(temp, json);
            File.Move(temp, _paths.SessionsFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Session", "Unable to save sessions.json.", ex);
            return false;
        }
    }

    public List<WorkspacePreset> LoadWorkspaces()
    {
        try
        {
            if (File.Exists(WorkspacesFilePath))
            {
                string json = File.ReadAllText(WorkspacesFilePath);
                return JsonSerializer.Deserialize<List<WorkspacePreset>>(json, JsonOptions) ?? [];
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
