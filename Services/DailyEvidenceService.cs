using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using QHR.Models;

namespace QHR.Services;

/// <summary>
/// 按日期、按文件保存加班证据。元数据和图片均使用 Windows 当前用户 DPAPI 加密，
/// 不并入考勤主缓存，避免一张图片导致整个历史档案反复读写。
/// </summary>
public sealed class DailyEvidenceService
{
    public const int MaximumImagesPerDay = 30;
    public const long MaximumImageBytes = 20L * 1024 * 1024;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string _rootDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DailyEvidenceService(SettingsService settingsService, string username)
    {
        var normalizedUsername = username.Trim().ToLowerInvariant();
        var usernameBytes = Encoding.UTF8.GetBytes(normalizedUsername);
        var accountHash = Convert.ToHexString(SHA256.HashData(usernameBytes))[..24].ToLowerInvariant();
        _rootDirectory = Path.Combine(settingsService.DataDirectory, "secure", $"evidence-{accountHash}");
        Directory.CreateDirectory(_rootDirectory);
    }

    public async Task<DailyEvidence> LoadAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(date, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveNoteAsync(DateOnly date, string note, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var evidence = await LoadCoreAsync(date, cancellationToken);
            evidence.Note = note.Trim();
            await SaveMetadataCoreAsync(evidence, cancellationToken);
            RemoveDayDirectoryWhenEmpty(evidence);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddImageAsync(DateOnly date, string sourcePath, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new InvalidDataException("仅支持 JPG、PNG、BMP、GIF 或 WEBP 图片");
        }

        var fileInfo = new FileInfo(sourcePath);
        if (!fileInfo.Exists) throw new FileNotFoundException("图片文件不存在", sourcePath);
        if (fileInfo.Length > MaximumImageBytes) throw new InvalidDataException("单张图片不能超过 20 MB");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var evidence = await LoadCoreAsync(date, cancellationToken);
            if (evidence.Images.Count >= MaximumImagesPerDay)
            {
                throw new InvalidDataException($"每天最多保存 {MaximumImagesPerDay} 张证据图片");
            }

            var attachment = new EvidenceAttachment
            {
                Id = Guid.NewGuid().ToString("N"),
                OriginalFileName = Path.GetFileName(sourcePath),
                Extension = extension.ToLowerInvariant(),
                Length = fileInfo.Length,
                AddedAt = DateTimeOffset.Now
            };
            var directory = GetDayDirectory(date);
            Directory.CreateDirectory(directory);
            var destinationPath = GetImagePath(directory, attachment.Id);

            byte[]? plainBytes = null;
            try
            {
                plainBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
                var encryptedBytes = EncryptedAttendanceCache.ProtectForCurrentUser(plainBytes);
                await WriteAtomicallyAsync(destinationPath, encryptedBytes, cancellationToken);
            }
            finally
            {
                if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
            }

            evidence.Images.Add(attachment);
            try
            {
                await SaveMetadataCoreAsync(evidence, cancellationToken);
            }
            catch
            {
                File.Delete(destinationPath);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BitmapSource> LoadPreviewAsync(
        DateOnly date,
        EvidenceAttachment attachment,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        byte[]? plainBytes = null;
        try
        {
            var encryptedPath = GetImagePath(GetDayDirectory(date), attachment.Id);
            var encryptedBytes = await File.ReadAllBytesAsync(encryptedPath, cancellationToken);
            plainBytes = EncryptedAttendanceCache.UnprotectForCurrentUser(encryptedBytes);
            using var stream = new MemoryStream(plainBytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 240;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
            _gate.Release();
        }
    }

    public async Task DeleteImageAsync(
        DateOnly date,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var evidence = await LoadCoreAsync(date, cancellationToken);
            var attachment = evidence.Images.FirstOrDefault(item => item.Id == attachmentId);
            if (attachment is null) return;
            evidence.Images.Remove(attachment);
            await SaveMetadataCoreAsync(evidence, cancellationToken);
            File.Delete(GetImagePath(GetDayDirectory(date), attachment.Id));
            RemoveDayDirectoryWhenEmpty(evidence);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExportDayAsync(
        DateOnly date,
        string outputPath,
        string calculationDetails,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var evidence = await LoadCoreAsync(date, cancellationToken);
            var temporaryPath = outputPath + ".tmp";
            File.Delete(temporaryPath);
            try
            {
                await using (var fileStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
                {
                    var detailsEntry = archive.CreateEntry("当天详情.txt", CompressionLevel.Optimal);
                    await using (var entryStream = detailsEntry.Open())
                    await using (var writer = new StreamWriter(entryStream, new UTF8Encoding(true)))
                    {
                        await writer.WriteLineAsync(calculationDetails);
                        await writer.WriteLineAsync();
                        await writer.WriteLineAsync("备注：");
                        await writer.WriteLineAsync(string.IsNullOrWhiteSpace(evidence.Note) ? "（无）" : evidence.Note);
                    }

                    for (var index = 0; index < evidence.Images.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var attachment = evidence.Images[index];
                        var encryptedPath = GetImagePath(GetDayDirectory(date), attachment.Id);
                        if (!File.Exists(encryptedPath)) continue;
                        byte[]? plainBytes = null;
                        try
                        {
                            var encryptedBytes = await File.ReadAllBytesAsync(encryptedPath, cancellationToken);
                            plainBytes = EncryptedAttendanceCache.UnprotectForCurrentUser(encryptedBytes);
                            var safeName = MakeSafeFileName(attachment.OriginalFileName, attachment.Extension);
                            var entry = archive.CreateEntry($"加班证据/{index + 1:00}-{safeName}", CompressionLevel.Optimal);
                            await using var entryStream = entry.Open();
                            await entryStream.WriteAsync(plainBytes, cancellationToken);
                        }
                        finally
                        {
                            if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
                        }
                    }
                }

                File.Move(temporaryPath, outputPath, true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExportMonthAsync(
        DateOnly month,
        string outputPath,
        string monthlyCsv,
        IReadOnlyDictionary<DateOnly, string> calculationDetails,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var temporaryPath = outputPath + ".tmp";
            File.Delete(temporaryPath);
            try
            {
                await using (var fileStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
                {
                    var summaryEntry = archive.CreateEntry("月度加班明细.csv", CompressionLevel.Optimal);
                    await using (var entryStream = summaryEntry.Open())
                    await using (var writer = new StreamWriter(entryStream, new UTF8Encoding(true)))
                    {
                        await writer.WriteAsync(monthlyCsv);
                    }

                    var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
                    for (var day = 1; day <= daysInMonth; day++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var date = new DateOnly(month.Year, month.Month, day);
                        var evidence = await LoadCoreAsync(date, cancellationToken);
                        if (string.IsNullOrWhiteSpace(evidence.Note) && evidence.Images.Count == 0) continue;

                        var folder = $"每日资料/{date:yyyy-MM-dd}/";
                        var detailsEntry = archive.CreateEntry(folder + "当天详情.txt", CompressionLevel.Optimal);
                        await using (var entryStream = detailsEntry.Open())
                        await using (var writer = new StreamWriter(entryStream, new UTF8Encoding(true)))
                        {
                            await writer.WriteLineAsync(calculationDetails.GetValueOrDefault(date, $"日期：{date:yyyy-MM-dd}\n当日无加班计算记录"));
                            await writer.WriteLineAsync();
                            await writer.WriteLineAsync("备注：");
                            await writer.WriteLineAsync(string.IsNullOrWhiteSpace(evidence.Note) ? "（无）" : evidence.Note);
                        }

                        for (var index = 0; index < evidence.Images.Count; index++)
                        {
                            var attachment = evidence.Images[index];
                            var encryptedPath = GetImagePath(GetDayDirectory(date), attachment.Id);
                            if (!File.Exists(encryptedPath)) continue;
                            byte[]? plainBytes = null;
                            try
                            {
                                var encryptedBytes = await File.ReadAllBytesAsync(encryptedPath, cancellationToken);
                                plainBytes = EncryptedAttendanceCache.UnprotectForCurrentUser(encryptedBytes);
                                var safeName = MakeSafeFileName(attachment.OriginalFileName, attachment.Extension);
                                var entry = archive.CreateEntry($"{folder}加班证据/{index + 1:00}-{safeName}", CompressionLevel.Optimal);
                                await using var entryStream = entry.Open();
                                await entryStream.WriteAsync(plainBytes, cancellationToken);
                            }
                            finally
                            {
                                if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
                            }
                        }
                    }
                }

                File.Move(temporaryPath, outputPath, true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DailyEvidence> LoadCoreAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var metadataPath = GetMetadataPath(date);
        if (!File.Exists(metadataPath)) return new DailyEvidence { Date = date };

        byte[]? plainBytes = null;
        try
        {
            var encryptedBytes = await File.ReadAllBytesAsync(metadataPath, cancellationToken);
            plainBytes = EncryptedAttendanceCache.UnprotectForCurrentUser(encryptedBytes);
            var evidence = JsonSerializer.Deserialize<DailyEvidence>(plainBytes, JsonOptions);
            if (evidence is null || evidence.Version != 1 || evidence.Date != date)
            {
                throw new InvalidDataException("证据档案格式不正确");
            }

            evidence.Note ??= string.Empty;
            evidence.Images ??= [];
            evidence.Images = evidence.Images
                .Where(item => IsValidAttachmentId(item.Id))
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToList();
            return evidence;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidDataException("当天证据档案无法读取，请确认仍在原 Windows 账户下使用", ex);
        }
        finally
        {
            if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private async Task SaveMetadataCoreAsync(DailyEvidence evidence, CancellationToken cancellationToken)
    {
        var directory = GetDayDirectory(evidence.Date);
        Directory.CreateDirectory(directory);
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions);
        try
        {
            var encryptedBytes = EncryptedAttendanceCache.ProtectForCurrentUser(plainBytes);
            await WriteAtomicallyAsync(Path.Combine(directory, "metadata.qhrnote"), encryptedBytes, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private string GetDayDirectory(DateOnly date) =>
        Path.Combine(_rootDirectory, date.Year.ToString("0000"), date.Month.ToString("00"), date.ToString("yyyy-MM-dd"));

    private string GetMetadataPath(DateOnly date) => Path.Combine(GetDayDirectory(date), "metadata.qhrnote");

    private static string GetImagePath(string directory, string attachmentId)
    {
        if (!IsValidAttachmentId(attachmentId)) throw new InvalidDataException("图片标识无效");
        return Path.Combine(directory, attachmentId + ".qhrimg");
    }

    private static bool IsValidAttachmentId(string value) =>
        value.Length == 32 && Guid.TryParseExact(value, "N", out _);

    private void RemoveDayDirectoryWhenEmpty(DailyEvidence evidence)
    {
        if (!string.IsNullOrWhiteSpace(evidence.Note) || evidence.Images.Count != 0) return;
        var directory = GetDayDirectory(evidence.Date);
        File.Delete(Path.Combine(directory, "metadata.qhrnote"));
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    private static string MakeSafeFileName(string originalFileName, string fallbackExtension)
    {
        var safeName = Path.GetFileName(originalFileName);
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(invalidCharacter, '_');
        return string.IsNullOrWhiteSpace(safeName) ? $"证据{fallbackExtension}" : safeName;
    }
}
