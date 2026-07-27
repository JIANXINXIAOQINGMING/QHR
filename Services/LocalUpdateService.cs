using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace QHR.Services;

/// <summary>
/// 校验 QHR 完整发布包，并交给独立 PowerShell 进程在主程序退出后完成覆盖。
/// </summary>
public sealed class LocalUpdateService
{
    public const string PackageFilePattern = "QHR.Overtime-v*.zip";
    private const int MaximumEntryCount = 10_000;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private readonly string _applicationDirectory;

    public LocalUpdateService() : this(AppContext.BaseDirectory)
    {
    }

    internal LocalUpdateService(string applicationDirectory)
    {
        _applicationDirectory = Path.GetFullPath(applicationDirectory);
    }

    public static Version CurrentVersion =>
        typeof(LocalUpdateService).Assembly.GetName().Version ?? new Version(1, 2, 1, 0);
    public static string CurrentDisplayVersion =>
        $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(0, CurrentVersion.Build)}";
    public string InstallDirectory => ResolveInstallDirectory(_applicationDirectory);

    internal static string ResolveInstallDirectory(string applicationDirectory)
    {
        var normalizedApplicationDirectory = Path.GetFullPath(applicationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentDirectory = Directory.GetParent(normalizedApplicationDirectory);
        return parentDirectory is not null &&
               string.Equals(Path.GetFileName(normalizedApplicationDirectory), "app", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(Path.Combine(parentDirectory.FullName, "QHR.exe"))
            ? parentDirectory.FullName
            : normalizedApplicationDirectory;
    }

    public IReadOnlyList<LocalUpdatePackage> FindPackages() => Directory
        .EnumerateFiles(InstallDirectory, PackageFilePattern, SearchOption.TopDirectoryOnly)
        .Select(path =>
        {
            var fileName = Path.GetFileName(path);
            return TryParsePackageFileName(fileName, out var version, out var versionText)
                ? new LocalUpdatePackage(path, version, versionText)
                : null;
        })
        .Where(package => package is not null)
        .Cast<LocalUpdatePackage>()
        .OrderByDescending(package => package.Version)
        .ThenByDescending(package => File.GetLastWriteTimeUtc(package.Path))
        .ToArray();

    public async Task LaunchUpdaterAsync(
        LocalUpdatePackage package,
        CancellationToken cancellationToken = default)
    {
        var packagePath = Path.GetFullPath(package.Path);
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("更新包不存在", packagePath);
        }
        if (!TryParsePackageFileName(Path.GetFileName(packagePath), out var fileNameVersion, out _) ||
            fileNameVersion != NormalizeVersion(package.Version))
        {
            throw new InvalidDataException(
                "更新包文件名不符合 QHR.Overtime-v版本-win-x64-Release.zip 命名规则");
        }
        if (package.Version <= NormalizeVersion(CurrentVersion))
        {
            throw new InvalidOperationException($"更新包版本 v{package.DisplayVersion} 不高于当前版本 v{CurrentDisplayVersion}");
        }

        var updateId = Guid.NewGuid().ToString("N");
        var stageRoot = Path.Combine(Path.GetTempPath(), "QHR.Overtime.Update", updateId);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"QHR.Overtime.Update-{updateId}.ps1");
        Directory.CreateDirectory(stageRoot);
        try
        {
            var payload = await ExtractAndValidateAsync(packagePath, stageRoot, cancellationToken);
            var payloadVersionText = FileVersionInfo.GetVersionInfo(payload.ApplicationExecutable).FileVersion;
            if (!Version.TryParse(payloadVersionText, out var payloadVersion))
            {
                throw new InvalidDataException("无法读取更新包内主程序的真实版本号");
            }
            payloadVersion = NormalizeVersion(payloadVersion);
            if (payloadVersion != package.Version)
            {
                throw new InvalidDataException(
                    $"更新包文件名版本 v{package.DisplayVersion} 与内部主程序版本 v{FormatVersion(payloadVersion)} 不一致");
            }
            if (payloadVersion <= NormalizeVersion(CurrentVersion))
            {
                throw new InvalidDataException($"更新包内部版本 v{FormatVersion(payloadVersion)} 不高于当前程序");
            }
            var launcherExecutable = Path.Combine(InstallDirectory, "QHR.exe");
            var legacyExecutable = Path.Combine(InstallDirectory, "QHR.Overtime.exe");
            var installedExecutable = File.Exists(launcherExecutable)
                ? launcherExecutable
                : legacyExecutable;
            if (!File.Exists(installedExecutable))
            {
                throw new InvalidOperationException("当前安装目录中没有 QHR.exe 或 QHR.Overtime.exe，无法执行自动覆盖更新");
            }

            await File.WriteAllTextAsync(scriptPath, UpdaterScript, new UTF8Encoding(true), cancellationToken);
            var powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (!File.Exists(powershellPath)) powershellPath = "powershell.exe";

            var startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = InstallDirectory
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-ParentProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("-StageRoot");
            startInfo.ArgumentList.Add(stageRoot);
            startInfo.ArgumentList.Add("-PayloadDirectory");
            startInfo.ArgumentList.Add(payload.RootDirectory);
            startInfo.ArgumentList.Add("-InstallDirectory");
            startInfo.ArgumentList.Add(InstallDirectory);
            startInfo.ArgumentList.Add("-PackagePath");
            startInfo.ArgumentList.Add(packagePath);
            startInfo.ArgumentList.Add("-ExecutablePath");
            startInfo.ArgumentList.Add(installedExecutable);

            if (Process.Start(startInfo) is null)
            {
                throw new InvalidOperationException("无法启动本地更新进程");
            }
        }
        catch
        {
            TryDeleteDirectory(stageRoot);
            TryDeleteFile(scriptPath);
            throw;
        }
    }

    internal static bool TryParsePackageFileName(
        string fileName,
        out Version version,
        out string versionText)
    {
        var match = Regex.Match(
            fileName ?? string.Empty,
            @"^QHR\.Overtime-v(?<version>\d+\.\d+\.\d+(?:\.\d+)?)-win-x64-Release\.zip$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        versionText = match.Success ? match.Groups["version"].Value : string.Empty;
        if (!match.Success || !Version.TryParse(versionText, out var parsed))
        {
            version = new Version(0, 0, 0, 0);
            return false;
        }

        version = NormalizeVersion(parsed);
        return true;
    }

    internal static Version NormalizeVersion(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    internal static string FormatVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    private static async Task<ExtractedUpdatePayload> ExtractAndValidateAsync(
        string packagePath,
        string stageRoot,
        CancellationToken cancellationToken)
    {
        var normalizedStageRoot = Path.GetFullPath(stageRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long expandedBytes = 0;
        using (var archive = ZipFile.OpenRead(packagePath))
        {
            if (archive.Entries.Count == 0) throw new InvalidDataException("本地更新 ZIP 是空包");
            if (archive.Entries.Count > MaximumEntryCount) throw new InvalidDataException("本地更新 ZIP 文件数量异常");
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > MaximumExpandedBytes)
                {
                    throw new InvalidDataException("本地更新 ZIP 解压后超过 2 GB，已拒绝安装");
                }

                var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destinationPath = Path.GetFullPath(Path.Combine(stageRoot, relativePath));
                if (!destinationPath.StartsWith(normalizedStageRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"本地更新 ZIP 包含非法路径：{entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await using var input = entry.Open();
                await using var output = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous);
                await input.CopyToAsync(output, cancellationToken);
            }
        }

        var executables = Directory
            .EnumerateFiles(stageRoot, "QHR.Overtime.exe", SearchOption.AllDirectories)
            .ToArray();
        if (executables.Length != 1)
        {
            throw new InvalidDataException("本地更新 ZIP 必须包含且只能包含一个 QHR.Overtime.exe");
        }

        var applicationExecutable = executables[0];
        var applicationDirectory = Path.GetDirectoryName(applicationExecutable)!;
        var candidateRoot = Directory.GetParent(applicationDirectory);
        var launcherExecutable = candidateRoot is null
            ? string.Empty
            : Path.Combine(candidateRoot.FullName, "QHR.exe");
        if (candidateRoot is not null &&
            string.Equals(Path.GetFileName(applicationDirectory), "app", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(launcherExecutable))
        {
            return new ExtractedUpdatePayload(candidateRoot.FullName, applicationExecutable);
        }

        return new ExtractedUpdatePayload(applicationDirectory, applicationExecutable);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // 临时目录清理失败不覆盖原始异常。
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时脚本清理失败不覆盖原始异常。
        }
    }

    private const string UpdaterScript = """
param(
    [Parameter(Mandatory = $true)][int]$ParentProcessId,
    [Parameter(Mandatory = $true)][string]$StageRoot,
    [Parameter(Mandatory = $true)][string]$PayloadDirectory,
    [Parameter(Mandatory = $true)][string]$InstallDirectory,
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$logPath = Join-Path $InstallDirectory 'update-error.log'

try {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue) {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw '等待主程序退出超时，更新未安装'
        }
        Start-Sleep -Milliseconds 250
    }

    $payloadAppDirectory = Join-Path $PayloadDirectory 'app'
    if (Test-Path -LiteralPath $payloadAppDirectory -PathType Container) {
        $installedAppDirectory = Join-Path $InstallDirectory 'app'
        if (Test-Path -LiteralPath $installedAppDirectory) {
            Remove-Item -LiteralPath $installedAppDirectory -Recurse -Force
        }
    }

    Get-ChildItem -LiteralPath $PayloadDirectory -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $InstallDirectory -Recurse -Force
    }

    $legacyManifest = Join-Path $InstallDirectory 'update.json'
    if (Test-Path -LiteralPath $legacyManifest) {
        Remove-Item -LiteralPath $legacyManifest -Force
    }
    Remove-Item -LiteralPath $PackagePath -Force
    if (Test-Path -LiteralPath $logPath) {
        Remove-Item -LiteralPath $logPath -Force
    }
    Start-Process -FilePath $ExecutablePath -WorkingDirectory $InstallDirectory
}
catch {
    ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f [DateTime]::Now, $_.Exception.ToString()) |
        Out-File -LiteralPath $logPath -Encoding UTF8 -Append
    if (Test-Path -LiteralPath $ExecutablePath) {
        Start-Process -FilePath $ExecutablePath -WorkingDirectory $InstallDirectory
    }
}
finally {
    Remove-Item -LiteralPath $StageRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
""";

    private sealed record ExtractedUpdatePayload(string RootDirectory, string ApplicationExecutable);
}

public sealed record LocalUpdatePackage(string Path, Version Version, string DisplayVersion);
