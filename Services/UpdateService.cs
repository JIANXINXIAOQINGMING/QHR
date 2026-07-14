using System.Reflection;
using System.Text.Json;

namespace QHR.Services;

public sealed class UpdateService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 2, 0, 0);

    public static string CurrentDisplayVersion => $"{CurrentVersion.Major}.{CurrentVersion.Minor}";

    public async Task<UpdateCheckResult> CheckAsync(
        string manifestUrl,
        CancellationToken cancellationToken = default)
    {
        string json;
        string source;
        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            var uri = new Uri(manifestUrl.Trim(), UriKind.Absolute);
            json = await HttpClient.GetStringAsync(uri, cancellationToken);
            source = uri.ToString();
        }
        else
        {
            var localPath = Path.Combine(AppContext.BaseDirectory, "update.json");
            if (!File.Exists(localPath))
                throw new InvalidOperationException("未配置远程更新清单，程序目录中也没有 update.json");
            json = await File.ReadAllTextAsync(localPath, cancellationToken);
            source = localPath;
        }

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions)
                       ?? throw new InvalidDataException("更新清单内容为空");
        if (!Version.TryParse(manifest.Version.Trim().TrimStart('v', 'V'), out var latestVersion))
            throw new InvalidDataException("更新清单中的版本号无效");

        return new UpdateCheckResult(
            latestVersion > CurrentVersion,
            CurrentVersion,
            latestVersion,
            manifest.DownloadUrl?.Trim() ?? string.Empty,
            manifest.ReleaseNotes?.Trim() ?? string.Empty,
            source);
    }

    private sealed class UpdateManifest
    {
        public string Version { get; init; } = string.Empty;
        public string? DownloadUrl { get; init; }
        public string? ReleaseNotes { get; init; }
    }
}

public sealed record UpdateCheckResult(
    bool HasUpdate,
    Version CurrentVersion,
    Version LatestVersion,
    string DownloadUrl,
    string ReleaseNotes,
    string Source);
