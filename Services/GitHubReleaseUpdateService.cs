using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QHR.Services;

/// <summary>
/// 从 QHR GitHub Releases 查询并下载与当前平台匹配的完整发布包。
/// 包的结构和内部程序版本仍由 <see cref="LocalUpdateService"/> 在安装前校验。
/// </summary>
public sealed class GitHubReleaseUpdateService
{
    private const string Repository = "JIANXINXIAOQINGMING/QHR";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const string LatestReleasePage = $"https://github.com/{Repository}/releases/latest";
    private const long MaximumDownloadBytes = 1024L * 1024 * 1024;

    public async Task<GitHubReleaseInfo> CheckLatestAsync(
        CancellationToken cancellationToken = default)
    {
        GitHubReleaseInfo release;
        try
        {
            release = await CheckLatestFromApiAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            release = await CheckLatestFromRedirectAsync(cancellationToken);
        }

        if (release.AssetSizeBytes <= 0 && !string.IsNullOrWhiteSpace(release.AssetDownloadUrl))
        {
            var size = await TryGetAssetSizeAsync(release.AssetDownloadUrl, cancellationToken);
            if (size > 0) release = release with { AssetSizeBytes = size };
        }

        return release;
    }

    public async Task<LocalUpdatePackage> DownloadAsync(
        GitHubReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(release.AssetDownloadUrl) ||
            string.IsNullOrWhiteSpace(release.AssetName))
        {
            throw new InvalidOperationException("GitHub Release 中没有可下载的 QHR Windows x64 ZIP");
        }

        var safeFileName = Path.GetFileName(release.AssetName);
        if (!LocalUpdateService.TryParsePackageFileName(
                safeFileName,
                out var packageVersion,
                out var versionText) ||
            packageVersion != LocalUpdateService.NormalizeVersion(release.Version))
        {
            throw new InvalidDataException(
                "在线更新包必须按 QHR.Overtime-v版本-win-x64-Release.zip 命名，且版本应与 Release 一致");
        }

        var downloadDirectory = Path.Combine(
            Path.GetTempPath(),
            "QHR.Overtime.Update",
            "downloads");
        Directory.CreateDirectory(downloadDirectory);
        var packagePath = Path.Combine(downloadDirectory, safeFileName);
        var partialPath = packagePath + ".download";
        TryDeleteFile(partialPath);

        try
        {
            using var client = CreateHttpClient(TimeSpan.FromMinutes(10));
            using var response = await client.GetAsync(
                release.AssetDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? release.AssetSizeBytes;
            if (totalBytes > MaximumDownloadBytes)
            {
                throw new InvalidDataException("在线更新包超过 1 GB，已拒绝下载");
            }

            progress?.Report(new UpdateDownloadProgress(0, totalBytes));
            long downloadedBytes = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                             partialPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var bytesRead = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (bytesRead == 0) break;

                    downloadedBytes = checked(downloadedBytes + bytesRead);
                    if (downloadedBytes > MaximumDownloadBytes)
                    {
                        throw new InvalidDataException("在线更新包超过 1 GB，已停止下载");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    progress?.Report(new UpdateDownloadProgress(downloadedBytes, totalBytes));
                }

                await output.FlushAsync(cancellationToken);
            }

            if (totalBytes > 0 && downloadedBytes != totalBytes)
            {
                throw new InvalidDataException(
                    $"更新包下载不完整：应为 {totalBytes} 字节，实际收到 {downloadedBytes} 字节");
            }

            File.Move(partialPath, packagePath, true);
            return new LocalUpdatePackage(packagePath, packageVersion, versionText);
        }
        catch
        {
            TryDeleteFile(partialPath);
            throw;
        }
    }

    private static async Task<GitHubReleaseInfo> CheckLatestFromApiAsync(
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(TimeSpan.FromSeconds(20));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.GetAsync(LatestReleaseApi, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = ReadString(root, "tag_name");
        var name = ReadString(root, "name");
        var version = ParseVersion(tag) ?? ParseVersion(name) ??
                      throw new InvalidDataException("无法从 GitHub latest release 读取版本号");
        var asset = root.TryGetProperty("assets", out var assets)
            ? SelectReleaseAsset(assets, version)
            : null;

        return new GitHubReleaseInfo(
            LocalUpdateService.NormalizeVersion(version),
            LocalUpdateService.FormatVersion(version),
            tag,
            ReadString(root, "html_url") is { Length: > 0 } releaseUrl
                ? releaseUrl
                : $"https://github.com/{Repository}/releases",
            asset?.Name,
            asset?.DownloadUrl,
            asset?.SizeBytes ?? 0);
    }

    private static async Task<GitHubReleaseInfo> CheckLatestFromRedirectAsync(
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync(LatestReleasePage, cancellationToken);
        response.EnsureSuccessStatusCode();
        var releaseUrl = response.RequestMessage?.RequestUri?.ToString() ?? LatestReleasePage;
        var tag = GetTagFromReleaseUrl(releaseUrl);
        var version = ParseVersion(tag) ??
                      throw new InvalidDataException("无法从 GitHub latest release 页面读取版本号");
        var expandedAssetsUrl =
            $"https://github.com/{Repository}/releases/expanded_assets/{Uri.EscapeDataString(tag)}";
        var html = await client.GetStringAsync(expandedAssetsUrl, cancellationToken);
        var asset = SelectReleaseAssetFromHtml(html, version);

        return new GitHubReleaseInfo(
            LocalUpdateService.NormalizeVersion(version),
            LocalUpdateService.FormatVersion(version),
            tag,
            releaseUrl,
            asset?.Name,
            asset?.DownloadUrl,
            asset?.SizeBytes ?? 0);
    }

    private static GitHubReleaseAsset? SelectReleaseAsset(
        JsonElement assets,
        Version releaseVersion)
    {
        if (assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in assets.EnumerateArray())
        {
            var name = ReadString(item, "name");
            var downloadUrl = ReadString(item, "browser_download_url");
            if (string.IsNullOrWhiteSpace(downloadUrl) ||
                !LocalUpdateService.TryParsePackageFileName(name, out var version, out _) ||
                version != LocalUpdateService.NormalizeVersion(releaseVersion))
            {
                continue;
            }

            var size = item.TryGetProperty("size", out var sizeElement) &&
                       sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : 0;
            return new GitHubReleaseAsset(name, downloadUrl, size);
        }

        return null;
    }

    private static GitHubReleaseAsset? SelectReleaseAssetFromHtml(
        string html,
        Version releaseVersion)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        foreach (Match match in Regex.Matches(
                     html,
                     "href=\"(?<url>[^\"]+/releases/download/[^\"]+?\\.zip(?:\\?[^\"]*)?)\"",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (string.IsNullOrWhiteSpace(url)) continue;
            var cleanUrl = url.Split('?')[0];
            if (cleanUrl.StartsWith('/')) cleanUrl = "https://github.com" + cleanUrl;
            var fileName = Uri.UnescapeDataString(
                cleanUrl[(cleanUrl.LastIndexOf('/') + 1)..]);
            if (!LocalUpdateService.TryParsePackageFileName(
                    fileName,
                    out var version,
                    out _) ||
                version != LocalUpdateService.NormalizeVersion(releaseVersion))
            {
                continue;
            }

            return new GitHubReleaseAsset(fileName, cleanUrl, 0);
        }

        return null;
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QHR-Overtime-Updater");
        return client;
    }

    private static async Task<long> TryGetAssetSizeAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateHttpClient(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return response.IsSuccessStatusCode
                ? response.Content.Headers.ContentLength ?? 0
                : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    private static Version? ParseVersion(string value)
    {
        var match = Regex.Match(
            value ?? string.Empty,
            @"(?<version>\d+\.\d+\.\d+(?:\.\d+)?)",
            RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version)
            ? version
            : null;
    }

    private static string GetTagFromReleaseUrl(string releaseUrl)
    {
        var match = Regex.Match(
            releaseUrl ?? string.Empty,
            @"/releases/tag/(?<tag>[^/?#]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? Uri.UnescapeDataString(match.Groups["tag"].Value) : string.Empty;
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理下载临时文件失败不覆盖原始异常。
        }
    }

    private sealed record GitHubReleaseAsset(
        string Name,
        string DownloadUrl,
        long SizeBytes);
}

public sealed record GitHubReleaseInfo(
    Version Version,
    string DisplayVersion,
    string TagName,
    string ReleaseUrl,
    string? AssetName,
    string? AssetDownloadUrl,
    long AssetSizeBytes)
{
    public bool HasUpdate =>
        Version > LocalUpdateService.NormalizeVersion(LocalUpdateService.CurrentVersion);
    public bool HasDownloadableAsset =>
        !string.IsNullOrWhiteSpace(AssetName) &&
        !string.IsNullOrWhiteSpace(AssetDownloadUrl);
}

public readonly record struct UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percent => TotalBytes <= 0
        ? 0
        : Math.Min(100, BytesReceived * 100d / TotalBytes);
}
