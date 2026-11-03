using OrbitFileManager.Services.Settings;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrbitFileManager.Services.Updates;

/// <summary>A newer release published on GitHub.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Notes,
    string ReleasePageUrl,
    string PackageUrl,
    long PackageSize,
    string ChecksumUrl);

/// <summary>
/// Checks the public GitHub repository for a newer portable build, downloads and verifies it, and hands it to a
/// small script that swaps the files once Orbit has exited. Release assets are produced by tools/release.ps1:
/// <c>OrbitFileManager-win-x64-{version}.zip</c> plus the same name with <c>.sha256</c>.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryOwner = "ariapiacere";
    public const string RepositoryName = "orbit-file-manager";
    public const string PackagePrefix = "OrbitFileManager-win-x64-";
    public const string ReleasesPageUrl = $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases";
    private const string LatestReleasePageUrl = $"{ReleasesPageUrl}/latest";
    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
    private const string NotesUnavailable = "GitHub is limiting requests from this network right now, so the release notes could not be loaded. Use Release page to see what's new.";

    /// <summary>Version of the running build (the Version in Directory.Build.props).</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateHttpClient(allowRedirects: true);
    private static readonly HttpClient NoRedirectHttp = CreateHttpClient(allowRedirects: false);

    private readonly AppPathsService _paths;
    private readonly StructuredLogger _logger;

    public UpdateService(AppPathsService paths, StructuredLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>A portable install updates in place; a read-only folder (e.g. Program Files) cannot.</summary>
    public bool CanInstallInPlace => _paths.IsPortableWritable;

    public static string UpdateRoot => Path.Combine(Path.GetTempPath(), "OrbitFileManager-update");
    private static string ResultFilePath => Path.Combine(UpdateRoot, "last-update-result.txt");
    public static string InstallLogPath => Path.Combine(UpdateRoot, "apply-update.log");

    public static string Format(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    /// <summary>
    /// GitHub allows 60 API calls an hour per IP without a token, and every PC behind the office IP shares them.
    /// The web redirect /releases/latest → /releases/tag/{tag} is not counted, so it is asked first and the API
    /// (which has the release notes) is only called when a newer version exists. If the API is refused, the update
    /// is built from the tag and the release's fixed download URLs instead.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        string? latestTag = await TryGetLatestTagAsync(cancellationToken);
        if (latestTag is null)
            return await CheckWithApiAsync(cancellationToken); // redirect unavailable: the API is the only source

        if (!TryParseVersion(latestTag, out Version? latest) || latest <= CurrentVersion)
            return null;

        try
        {
            UpdateInfo? fromApi = await CheckWithApiAsync(cancellationToken);
            if (fromApi is not null)
                return fromApi;
        }
        catch (HttpRequestException ex)
        {
            _logger.Warn("Update", "GitHub API refused the update check; using the release page instead.", new { ex.StatusCode, ex.Message });
        }

        UpdateInfo? update = FromReleaseTag(latestTag, CurrentVersion);
        if (update is null)
            return null;

        // tools/release.ps1 uploads both files together; a missing checksum means the release is not complete yet.
        using HttpResponseMessage checksum = await Http.GetAsync(update.ChecksumUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return checksum.IsSuccessStatusCode ? update : null;
    }

    private static async Task<string?> TryGetLatestTagAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await NoRedirectHttp.GetAsync(LatestReleasePageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return TryParseReleaseTag(response.Headers.Location, out string? tag) ? tag : null;
        }
        catch (HttpRequestException)
        {
            return null; // fall back to the API alone
        }
    }

    /// <summary>Reads the tag from the /releases/tag/{tag} address that /releases/latest redirects to.</summary>
    public static bool TryParseReleaseTag(Uri? location, [NotNullWhen(true)] out string? tag)
    {
        tag = null;
        if (location is null)
            return false;

        const string marker = "/releases/tag/";
        string path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        int index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return false; // no release yet: GitHub redirects to /releases

        string value = Uri.UnescapeDataString(path[(index + marker.Length)..].TrimEnd('/'));
        if (value.Length == 0)
            return false;

        tag = value;
        return true;
    }

    /// <summary>
    /// Builds the update from a release tag alone, using GitHub's fixed asset download addresses. Used when the API
    /// is unavailable, so there are no release notes and the package size is unknown.
    /// </summary>
    public static UpdateInfo? FromReleaseTag(string tag, Version current)
    {
        if (!TryParseVersion(tag, out Version? version) || version <= Normalize(current))
            return null;

        string escapedTag = Uri.EscapeDataString(tag);
        string packageUrl = $"{ReleasesPageUrl}/download/{escapedTag}/{PackagePrefix}{Format(version)}.zip";
        return new UpdateInfo(version, NotesUnavailable, $"{ReleasesPageUrl}/tag/{escapedTag}", packageUrl, 0, packageUrl + ".sha256");
    }

    private static async Task<UpdateInfo?> CheckWithApiAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null; // nothing published yet

        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return ParseLatestRelease(document.RootElement, CurrentVersion);
    }

    /// <summary>
    /// Returns the release when it is a published (non-draft, non-prerelease) version newer than
    /// <paramref name="current"/> that carries both the package and its checksum; otherwise null.
    /// </summary>
    public static UpdateInfo? ParseLatestRelease(JsonElement release, Version current)
    {
        if (IsTrue(release, "draft") || IsTrue(release, "prerelease"))
            return null;

        string tag = release.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
        if (!TryParseVersion(tag, out Version? version) || version <= Normalize(current))
            return null;

        string packageName = $"{PackagePrefix}{Format(version)}.zip";
        string? packageUrl = null;
        string? checksumUrl = null;
        long packageSize = 0;

        if (release.TryGetProperty("assets", out JsonElement assets))
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.GetProperty("name").GetString();
                string? url = asset.GetProperty("browser_download_url").GetString();
                if (string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    packageUrl = url;
                    packageSize = asset.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : 0;
                }
                else if (string.Equals(name, packageName + ".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = url;
                }
            }
        }

        if (packageUrl is null || checksumUrl is null)
            return null;

        string notes = release.TryGetProperty("body", out JsonElement body) ? body.GetString() ?? string.Empty : string.Empty;
        string pageUrl = release.TryGetProperty("html_url", out JsonElement html) ? html.GetString() ?? ReleasesPageUrl : ReleasesPageUrl;
        return new UpdateInfo(version, notes.Trim(), pageUrl, packageUrl, packageSize, checksumUrl);
    }

    public static bool TryParseVersion(string tag, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out Version? parsed))
            return false;

        version = Normalize(parsed);
        return true;
    }

    /// <summary>Downloads the package, verifies its SHA-256 and extracts it. Returns the folder holding the new exe.</summary>
    public async Task<string> DownloadAndStageAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string root = Path.Combine(UpdateRoot, Format(update.Version));
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);

        string zipPath = Path.Combine(root, $"{PackagePrefix}{Format(update.Version)}.zip");
        using (HttpResponseMessage response = await Http.GetAsync(update.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.PackageSize;
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream target = File.Create(zipPath);

            byte[] buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (total > 0)
                    progress?.Report((double)received / total);
            }
        }

        string checksumText = await Http.GetStringAsync(update.ChecksumUrl, cancellationToken);
        string expected = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        string actual;
        await using (FileStream zip = File.OpenRead(zipPath))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(zip, cancellationToken));

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded update failed its checksum check, so it was not installed. Try again later.");

        string extractDir = Path.Combine(root, "files");
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir), cancellationToken);

        string? appDir = Directory
            .EnumerateFiles(extractDir, "OrbitFileManager.exe", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault();

        return appDir ?? throw new InvalidDataException("The update package does not contain OrbitFileManager.exe.");
    }

    /// <summary>
    /// Starts a hidden script that waits for this process to exit, copies the staged build over the install folder
    /// (keeping Data, Logs and Cache) and starts Orbit again. The caller must close Orbit right after this.
    /// </summary>
    public void StartInstallAfterExit(string stagedAppDir, Version version)
    {
        string installDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string exePath = Path.Combine(installDir, "OrbitFileManager.exe");
        string scriptPath = Path.Combine(UpdateRoot, "apply-update.cmd");
        string v = Format(version);

        // cmd expands %; double it in paths. chcp 65001 lets the UTF-8 script carry non-ASCII paths.
        static string Q(string path) => path.Replace("%", "%%");
        string script = $"""
            @echo off
            chcp 65001 >NUL
            rem Orbit File Manager updater: wait for Orbit to exit, copy the new build over it, start it again.
            set tries=0
            :wait
            tasklist /FI "PID eq {Environment.ProcessId}" 2>NUL | find /I "OrbitFileManager.exe" >NUL || goto copy
            set /a tries+=1
            if %tries% GEQ 60 goto copy
            ping -n 2 127.0.0.1 >NUL
            goto wait
            :copy
            robocopy "{Q(stagedAppDir)}" "{Q(installDir)}" /E /R:5 /W:1 /XD Data Logs Cache /NFL /NDL /NP > "{Q(InstallLogPath)}"
            if %ERRORLEVEL% GEQ 8 (echo failed %ERRORLEVEL% {v}> "{Q(ResultFilePath)}") else (echo ok {v}> "{Q(ResultFilePath)}")
            start "" "{Q(exePath)}"
            """;

        Directory.CreateDirectory(UpdateRoot);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/c \"\"{scriptPath}\"\"",
            WorkingDirectory = UpdateRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        _logger.Info("Update", "Started update install.", new { version = v, installDir });
    }

    /// <summary>
    /// Returns (and clears) what the last update script reported: (true, version) after a successful copy,
    /// (false, version) when robocopy failed, or null when no update ran since the last start.
    /// </summary>
    public (bool Succeeded, string Version)? TakeLastInstallResult()
    {
        try
        {
            if (!File.Exists(ResultFilePath))
                return null;

            string[] parts = File.ReadAllText(ResultFilePath).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            File.Delete(ResultFilePath);
            if (parts.Length < 2)
                return null;

            bool succeeded = parts[0] == "ok";
            string version = parts[^1];
            if (succeeded)
                TryDeleteDirectory(Path.Combine(UpdateRoot, version)); // staged copy is no longer needed

            _logger.Info("Update", succeeded ? "Update installed." : "Update install failed.", new { result = string.Join(' ', parts) });
            return (succeeded, version);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static HttpClient CreateHttpClient(bool allowRedirects)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = allowRedirects }) { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OrbitFileManager", Format(CurrentVersion)));
        return client;
    }
}
