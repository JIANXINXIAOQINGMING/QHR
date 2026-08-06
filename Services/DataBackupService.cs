using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QHR.Models;

namespace QHR.Services;

public sealed class DataBackupService
{
    private const int FormatVersion = 1;
    private const int PasswordIterations = 240_000;
    private const int SaltLength = 16;
    private const int IvLength = 16;
    private const int AuthenticationTagLength = 32;
    private const int HeaderLength = 8 + sizeof(int) + sizeof(int) + SaltLength + IvLength;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("QHRBAK01");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly SettingsService _settingsService;
    private readonly AppSettings _activeSettings;
    private readonly string _username;
    private readonly EncryptedAttendanceCache _attendanceCache;
    private readonly FinancialGoalService _goalService;
    private readonly DailyEvidenceService _evidenceService;

    public DataBackupService(
        SettingsService settingsService,
        AppSettings activeSettings,
        string username)
    {
        _settingsService = settingsService;
        _activeSettings = activeSettings;
        _username = username.Trim();
        _attendanceCache = new EncryptedAttendanceCache(settingsService, username);
        _goalService = new FinancialGoalService(settingsService, username);
        _evidenceService = new DailyEvidenceService(settingsService, username);
    }

    public async Task<BackupExportResult> ExportAsync(
        string outputPath,
        string password,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(password, true);
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        var temporaryPath = fullOutputPath + $".tmp-{Guid.NewGuid():N}";

        progress?.Report("正在读取本地加密档案…");
        var attendance = await _attendanceCache.LoadAsync(cancellationToken);
        var goal = await _goalService.LoadAsync(cancellationToken);
        var evidenceDates = _evidenceService.GetStoredDates();
        var evidenceDays = new List<DailyEvidence>(evidenceDates.Count);
        foreach (var date in evidenceDates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            evidenceDays.Add(await _evidenceService.LoadAsync(date, cancellationToken));
        }

        var manifest = new BackupManifest
        {
            FormatVersion = FormatVersion,
            CreatedAt = DateTimeOffset.Now,
            AppVersion = LocalUpdateService.CurrentDisplayVersion,
            Account = _username,
            AttendanceCount = attendance.Count,
            ExpenseCount = goal.Expenses.Count + goal.CompletedGoals.Sum(item => item.Expenses.Count),
            EvidenceDayCount = evidenceDays.Count,
            EvidenceImageCount = evidenceDays.Sum(item => item.Images.Count)
        };
        var portableSettings = ClonePortableSettings(_activeSettings);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var derivedKey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            64);
        var encryptionKey = derivedKey[..32];
        var authenticationKey = derivedKey[32..];

        try
        {
            progress?.Report("正在创建密码加密备份…");
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var header = BuildHeader(salt, iv);
                await output.WriteAsync(header, cancellationToken);
                using var aes = Aes.Create();
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encryptionKey;
                aes.IV = iv;
                await using (var encryptedStream = new CryptoStream(
                                 output,
                                 aes.CreateEncryptor(),
                                 CryptoStreamMode.Write,
                                 leaveOpen: true))
                {
                    using (var archive = new ZipArchive(
                               encryptedStream,
                               ZipArchiveMode.Create,
                               leaveOpen: true,
                               Encoding.UTF8))
                    {
                        await WriteJsonEntryAsync(archive, "manifest.json", manifest, cancellationToken);
                        await WriteJsonEntryAsync(archive, "settings.json", portableSettings, cancellationToken);
                        await WriteJsonEntryAsync(archive, "attendance.json", attendance, cancellationToken);
                        await WriteJsonEntryAsync(archive, "goal.json", goal, cancellationToken);

                        for (var dayIndex = 0; dayIndex < evidenceDays.Count; dayIndex++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var evidence = evidenceDays[dayIndex];
                            progress?.Report($"正在备份证据资料 {dayIndex + 1}/{evidenceDays.Count}…");
                            var dayRoot = $"evidence/{evidence.Date:yyyy-MM-dd}";
                            await WriteJsonEntryAsync(archive, $"{dayRoot}/metadata.json", evidence, cancellationToken);
                            foreach (var attachment in evidence.Images)
                            {
                                byte[]? imageBytes = null;
                                try
                                {
                                    imageBytes = await _evidenceService.ReadImageBytesAsync(
                                        evidence.Date,
                                        attachment,
                                        cancellationToken);
                                    var entry = archive.CreateEntry(
                                        $"{dayRoot}/images/{attachment.Id}.bin",
                                        CompressionLevel.Optimal);
                                    await using var entryStream = entry.Open();
                                    await entryStream.WriteAsync(imageBytes, cancellationToken);
                                }
                                finally
                                {
                                    if (imageBytes is not null) CryptographicOperations.ZeroMemory(imageBytes);
                                }
                            }
                        }

                        if (Directory.Exists(_settingsService.HolidayCacheDirectory))
                        {
                            foreach (var path in Directory.EnumerateFiles(
                                         _settingsService.HolidayCacheDirectory,
                                         "*.json",
                                         SearchOption.TopDirectoryOnly))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var entry = archive.CreateEntry(
                                    $"holidays/{Path.GetFileName(path)}",
                                    CompressionLevel.Optimal);
                                await using var source = new FileStream(
                                    path,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.Read,
                                    81920,
                                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                                await using var target = entry.Open();
                                await source.CopyToAsync(target, cancellationToken);
                            }
                        }
                    }
                    encryptedStream.FlushFinalBlock();
                }
                await output.FlushAsync(cancellationToken);
            }

            progress?.Report("正在校验备份完整性…");
            var authenticationTag = await ComputeAuthenticationTagAsync(
                temporaryPath,
                authenticationKey,
                cancellationToken);
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.Append,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous))
            {
                await output.WriteAsync(authenticationTag, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, fullOutputPath, true);
            return new BackupExportResult
            {
                Manifest = manifest,
                FilePath = fullOutputPath,
                FileSize = new FileInfo(fullOutputPath).Length
            };
        }
        finally
        {
            File.Delete(temporaryPath);
            CryptographicOperations.ZeroMemory(derivedKey);
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(authenticationKey);
        }
    }

    public async Task<BackupImportPackage> OpenAsync(
        string inputPath,
        string password,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(password, false);
        progress?.Report("正在验证备份密码和文件完整性…");
        var source = new FileStream(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        FileStream? temporaryStream = null;
        ZipArchive? archive = null;
        byte[]? derivedKey = null;
        byte[]? encryptionKey = null;
        byte[]? authenticationKey = null;
        try
        {
            if (source.Length <= HeaderLength + AuthenticationTagLength)
                throw new InvalidDataException("备份文件不完整");
            var header = new byte[HeaderLength];
            await source.ReadExactlyAsync(header, cancellationToken);
            ValidateHeader(header, out var iterations, out var salt, out var iv);
            var ciphertextLength = source.Length - HeaderLength - AuthenticationTagLength;
            if (ciphertextLength <= 0 || ciphertextLength % 16 != 0)
                throw new InvalidDataException("备份文件长度不正确");

            derivedKey = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                64);
            encryptionKey = derivedKey[..32];
            authenticationKey = derivedKey[32..];
            var storedTag = new byte[AuthenticationTagLength];
            source.Position = source.Length - AuthenticationTagLength;
            await source.ReadExactlyAsync(storedTag, cancellationToken);
            source.Position = 0;
            var computedTag = await ComputeAuthenticationTagAsync(
                source,
                source.Length - AuthenticationTagLength,
                authenticationKey,
                cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(storedTag, computedTag))
                throw new InvalidDataException("备份密码错误，或备份文件已经损坏");

            progress?.Report("正在解密备份目录…");
            var temporaryPath = Path.Combine(Path.GetTempPath(), $"qhr-restore-{Guid.NewGuid():N}.tmp");
            temporaryStream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            source.Position = HeaderLength;
            using (var segment = new ReadOnlySegmentStream(source, ciphertextLength))
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encryptionKey;
                aes.IV = iv;
                await using var decryptedStream = new CryptoStream(
                    segment,
                    aes.CreateDecryptor(),
                    CryptoStreamMode.Read,
                    leaveOpen: true);
                await decryptedStream.CopyToAsync(temporaryStream, cancellationToken);
            }
            temporaryStream.Position = 0;
            archive = new ZipArchive(temporaryStream, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
            var manifest = await ReadJsonEntryAsync<BackupManifest>(archive, "manifest.json", 1024 * 1024, cancellationToken);
            if (manifest.FormatVersion != FormatVersion)
                throw new InvalidDataException($"暂不支持此备份格式版本：{manifest.FormatVersion}");
            if (manifest.AttendanceCount is < 0 or > 50_000 ||
                manifest.ExpenseCount is < 0 or > 500_000 ||
                manifest.EvidenceDayCount is < 0 or > 50_000 ||
                manifest.EvidenceImageCount is < 0 or > 1_000_000)
                throw new InvalidDataException("备份清单中的数据数量异常");

            var settings = await ReadJsonEntryAsync<AppSettings>(archive, "settings.json", 1024 * 1024, cancellationToken);
            var attendance = await ReadJsonEntryAsync<List<AttendanceRecord>>(
                archive,
                "attendance.json",
                64L * 1024 * 1024,
                cancellationToken);
            var goal = await ReadJsonEntryAsync<FinancialGoalData>(
                archive,
                "goal.json",
                64L * 1024 * 1024,
                cancellationToken);
            goal.Expenses ??= [];
            goal.CompletedGoals ??= [];
            FinancialGoalService.PrepareForUse(goal);
            var evidence = new Dictionary<DateOnly, DailyEvidence>();
            foreach (var entry in archive.Entries.Where(item =>
                         item.FullName.StartsWith("evidence/", StringComparison.Ordinal) &&
                         item.FullName.EndsWith("/metadata.json", StringComparison.Ordinal)))
            {
                var segments = entry.FullName.Split('/');
                if (segments.Length != 3 ||
                    !DateOnly.TryParseExact(segments[1], "yyyy-MM-dd", out var date))
                    throw new InvalidDataException("备份中的证据日期路径无效");
                var item = await ReadJsonEntryAsync<DailyEvidence>(entry, 4L * 1024 * 1024, cancellationToken);
                if (item.Date != date || item.Version != 1)
                    throw new InvalidDataException("备份中的证据元数据无效");
                item.Note ??= string.Empty;
                item.Images ??= [];
                evidence[date] = item;
            }

            return new BackupImportPackage(
                temporaryStream,
                archive,
                manifest,
                settings,
                attendance,
                goal,
                evidence);
        }
        catch
        {
            archive?.Dispose();
            if (temporaryStream is not null) await temporaryStream.DisposeAsync();
            throw;
        }
        finally
        {
            await source.DisposeAsync();
            if (derivedKey is not null) CryptographicOperations.ZeroMemory(derivedKey);
            if (encryptionKey is not null) CryptographicOperations.ZeroMemory(encryptionKey);
            if (authenticationKey is not null) CryptographicOperations.ZeroMemory(authenticationKey);
        }
    }

    public async Task<BackupConflictSummary> InspectConflictsAsync(
        BackupImportPackage package,
        CancellationToken cancellationToken = default)
    {
        var localSettings = ClonePortableSettings(_activeSettings);
        var localAttendance = await _attendanceCache.LoadAsync(cancellationToken);
        var localGoal = await _goalService.LoadAsync(cancellationToken);
        var localAttendanceByDate = localAttendance.ToDictionary(item => item.Date);
        var localExpensesById = localGoal.Expenses
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var localCompletedGoalsById = localGoal.CompletedGoals
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var attendanceConflicts = package.Attendance.Count(item =>
            localAttendanceByDate.TryGetValue(item.Date, out var local) && !AttendanceEquals(local, item));
        var expenseConflicts = package.Goal.Expenses.Count(item =>
            localExpensesById.TryGetValue(item.Id, out var local) && !ExpenseEquals(local, item));
        var completedGoalConflicts = package.Goal.CompletedGoals.Count(item =>
            localCompletedGoalsById.TryGetValue(item.Id, out var local) && !JsonEquals(local, item));
        var evidenceNoteConflicts = 0;
        var evidenceImageConflicts = 0;
        foreach (var pair in package.Evidence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = await _evidenceService.LoadAsync(pair.Key, cancellationToken);
            if (!string.IsNullOrWhiteSpace(local.Note) &&
                !string.IsNullOrWhiteSpace(pair.Value.Note) &&
                !string.Equals(local.Note, pair.Value.Note, StringComparison.Ordinal))
                evidenceNoteConflicts++;
            var localImages = local.Images.ToDictionary(item => item.Id, StringComparer.Ordinal);
            evidenceImageConflicts += pair.Value.Images.Count(item =>
                localImages.TryGetValue(item.Id, out var localImage) && !AttachmentEquals(localImage, item));
        }

        return new BackupConflictSummary
        {
            SettingsConflict = !JsonEquals(localSettings, package.Settings),
            GoalSettingsConflict = HasGoal(localGoal) &&
                                   HasGoal(package.Goal) &&
                                   !GoalSettingsEqual(localGoal, package.Goal),
            AttendanceConflicts = attendanceConflicts,
            ExpenseConflicts = expenseConflicts,
            CompletedGoalConflicts = completedGoalConflicts,
            EvidenceNoteConflicts = evidenceNoteConflicts,
            EvidenceImageConflicts = evidenceImageConflicts
        };
    }

    public async Task<BackupImportResult> ImportAsync(
        BackupImportPackage package,
        BackupConflictMode conflictMode,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var useBackup = conflictMode == BackupConflictMode.UseBackup;
        progress?.Report("正在合并设置和考勤档案…");
        var localSettings = ClonePortableSettings(_activeSettings);
        if (useBackup || JsonEquals(localSettings, package.Settings))
        {
            ApplyPortableSettings(package.Settings, _activeSettings);
            await _settingsService.SaveAsync(_activeSettings, cancellationToken);
        }

        var localAttendance = await _attendanceCache.LoadAsync(cancellationToken);
        var mergedAttendance = localAttendance.ToDictionary(item => item.Date);
        foreach (var item in package.Attendance)
        {
            if (!mergedAttendance.ContainsKey(item.Date) || useBackup) mergedAttendance[item.Date] = item;
        }
        await _attendanceCache.SaveAsync(mergedAttendance.Values.OrderBy(item => item.Date), cancellationToken);

        progress?.Report("正在合并目标与消费记录…");
        var localGoal = await _goalService.LoadAsync(cancellationToken);
        var useBackupGoalSettings = HasGoal(package.Goal) &&
                                    (!HasGoal(localGoal) || GoalSettingsEqual(localGoal, package.Goal) || useBackup);
        var mergedExpenses = localGoal.Expenses
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        foreach (var expense in package.Goal.Expenses)
        {
            if (!mergedExpenses.ContainsKey(expense.Id) || useBackup) mergedExpenses[expense.Id] = expense;
        }
        var mergedCompletedGoals = localGoal.CompletedGoals
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        foreach (var completedGoal in package.Goal.CompletedGoals)
        {
            if (!mergedCompletedGoals.ContainsKey(completedGoal.Id) || useBackup)
                mergedCompletedGoals[completedGoal.Id] = completedGoal;
        }
        var mergedGoalProfiles = localGoal.Goals
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        foreach (var goalProfile in package.Goal.Goals)
        {
            if (!mergedGoalProfiles.ContainsKey(goalProfile.Id) || useBackup)
                mergedGoalProfiles[goalProfile.Id] = goalProfile;
        }
        var mergedGoal = new FinancialGoalData
        {
            Version = 5,
            ActiveGoalId = useBackupGoalSettings ? package.Goal.ActiveGoalId : localGoal.ActiveGoalId,
            Goals = mergedGoalProfiles.Values.OrderByDescending(item => item.CreatedAt).ToList(),
            GoalName = useBackupGoalSettings ? package.Goal.GoalName : localGoal.GoalName,
            TargetAmount = useBackupGoalSettings ? package.Goal.TargetAmount : localGoal.TargetAmount,
            StartDate = useBackupGoalSettings ? package.Goal.StartDate : localGoal.StartDate,
            IncludeMealAllowance = useBackupGoalSettings
                ? package.Goal.IncludeMealAllowance
                : localGoal.IncludeMealAllowance,
            SuppressAutomaticCompletion = useBackupGoalSettings
                ? package.Goal.SuppressAutomaticCompletion
                : localGoal.SuppressAutomaticCompletion,
            Expenses = mergedExpenses.Values.OrderByDescending(item => item.Date).ToList(),
            CompletedGoals = mergedCompletedGoals.Values
                .OrderByDescending(item => item.CompletedDate)
                .ToList()
        };
        await _goalService.SaveAsync(mergedGoal, cancellationToken);

        var evidenceIndex = 0;
        foreach (var pair in package.Evidence.OrderBy(item => item.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            evidenceIndex++;
            progress?.Report($"正在合并备注与证据图片 {evidenceIndex}/{package.Evidence.Count}…");
            var backupEvidence = pair.Value;
            var localEvidence = await _evidenceService.LoadAsync(pair.Key, cancellationToken);
            if (!string.IsNullOrWhiteSpace(backupEvidence.Note) &&
                (string.IsNullOrWhiteSpace(localEvidence.Note) ||
                 string.Equals(localEvidence.Note, backupEvidence.Note, StringComparison.Ordinal) ||
                 useBackup))
            {
                await _evidenceService.SaveNoteAsync(pair.Key, backupEvidence.Note, cancellationToken);
            }

            var localImages = localEvidence.Images.ToDictionary(item => item.Id, StringComparer.Ordinal);
            foreach (var attachment in backupEvidence.Images)
            {
                var hasLocal = localImages.TryGetValue(attachment.Id, out var localAttachment);
                var isConflict = hasLocal && !AttachmentEquals(localAttachment!, attachment);
                if (hasLocal && (!isConflict || !useBackup)) continue;
                var imageBytes = await package.ReadImageBytesAsync(pair.Key, attachment, cancellationToken);
                try
                {
                    await _evidenceService.ImportImageAsync(
                        pair.Key,
                        attachment,
                        imageBytes,
                        overwrite: hasLocal && useBackup,
                        cancellationToken);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(imageBytes);
                }
            }
        }

        progress?.Report("正在恢复节假日缓存…");
        Directory.CreateDirectory(_settingsService.HolidayCacheDirectory);
        foreach (var entry in package.Archive.Entries.Where(item =>
                     item.FullName.StartsWith("holidays/", StringComparison.Ordinal) &&
                     item.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var fileName = Path.GetFileName(entry.FullName);
            if (string.IsNullOrWhiteSpace(fileName)) continue;
            var destination = Path.Combine(_settingsService.HolidayCacheDirectory, fileName);
            if (File.Exists(destination) && !useBackup) continue;
            var temporaryPath = destination + ".tmp";
            try
            {
                await using var source = entry.Open();
                await using (var target = new FileStream(
                                 temporaryPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 81920,
                                 FileOptions.Asynchronous))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }
                File.Move(temporaryPath, destination, true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        var finalEvidenceDates = _evidenceService.GetStoredDates();
        var finalImageCount = 0;
        foreach (var date in finalEvidenceDates)
        {
            finalImageCount += (await _evidenceService.LoadAsync(date, cancellationToken)).Images.Count;
        }
        return new BackupImportResult
        {
            AttendanceCount = mergedAttendance.Count,
            ExpenseCount = mergedGoal.Expenses.Count +
                           mergedGoal.CompletedGoals.Sum(item => item.Expenses.Count),
            EvidenceDayCount = finalEvidenceDates.Count,
            EvidenceImageCount = finalImageCount
        };
    }

    private static async Task WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private static async Task<T> ReadJsonEntryAsync<T>(
        ZipArchive archive,
        string entryName,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidDataException($"备份缺少 {entryName}");
        return await ReadJsonEntryAsync<T>(entry, maximumLength, cancellationToken);
    }

    private static async Task<T> ReadJsonEntryAsync<T>(
        ZipArchiveEntry entry,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        if (entry.Length > maximumLength) throw new InvalidDataException($"备份条目过大：{entry.FullName}");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidDataException($"备份条目无法解析：{entry.FullName}");
    }

    private static byte[] BuildHeader(byte[] salt, byte[] iv)
    {
        using var stream = new MemoryStream(HeaderLength);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(PasswordIterations);
        writer.Write(salt);
        writer.Write(iv);
        writer.Flush();
        return stream.ToArray();
    }

    private static void ValidateHeader(
        byte[] header,
        out int iterations,
        out byte[] salt,
        out byte[] iv)
    {
        using var stream = new MemoryStream(header, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("不是有效的 QHR 全量备份文件");
        var version = reader.ReadInt32();
        if (version != FormatVersion) throw new InvalidDataException($"暂不支持此备份加密版本：{version}");
        iterations = reader.ReadInt32();
        if (iterations is < 100_000 or > 2_000_000) throw new InvalidDataException("备份加密参数无效");
        salt = reader.ReadBytes(SaltLength);
        iv = reader.ReadBytes(IvLength);
        if (salt.Length != SaltLength || iv.Length != IvLength) throw new InvalidDataException("备份文件头不完整");
    }

    private static async Task<byte[]> ComputeAuthenticationTagAsync(
        string path,
        byte[] key,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeAuthenticationTagAsync(stream, stream.Length, key, cancellationToken);
    }

    private static async Task<byte[]> ComputeAuthenticationTagAsync(
        Stream stream,
        long length,
        byte[] key,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        var buffer = new byte[81920];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0) throw new EndOfStreamException("备份文件意外结束");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return hash.GetHashAndReset();
    }

    private static AppSettings ClonePortableSettings(AppSettings source)
    {
        var clone = JsonSerializer.Deserialize<AppSettings>(
                        JsonSerializer.Serialize(source, JsonOptions),
                        JsonOptions)
                    ?? new AppSettings();
        clone.AutoLoginEnabled = false;
        clone.LastUsername = string.Empty;
        clone.LastAuthenticatedUsername = string.Empty;
        return clone;
    }

    private static void ApplyPortableSettings(AppSettings source, AppSettings target)
    {
        var autoLogin = target.AutoLoginEnabled;
        var lastUsername = target.LastUsername;
        var lastAuthenticatedUsername = target.LastAuthenticatedUsername;
        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.CanRead && property.CanWrite &&
                                        property.Name is not nameof(AppSettings.AutoLoginEnabled) and
                                            not nameof(AppSettings.LastUsername) and
                                            not nameof(AppSettings.LastAuthenticatedUsername)))
        {
            property.SetValue(target, property.GetValue(source));
        }
        target.AutoLoginEnabled = autoLogin;
        target.LastUsername = lastUsername;
        target.LastAuthenticatedUsername = lastAuthenticatedUsername;
    }

    private static bool JsonEquals<T>(T left, T right) =>
        string.Equals(
            JsonSerializer.Serialize(left, JsonOptions),
            JsonSerializer.Serialize(right, JsonOptions),
            StringComparison.Ordinal);

    private static bool AttendanceEquals(AttendanceRecord left, AttendanceRecord right) =>
        left.Date == right.Date &&
        left.ClockIn == right.ClockIn &&
        left.ClockOut == right.ClockOut &&
        left.CardTimes.SequenceEqual(right.CardTimes) &&
        Math.Abs(left.LeaveHours - right.LeaveHours) < 0.0001 &&
        JsonEquals(left.LeaveEntries, right.LeaveEntries) &&
        Math.Abs(left.DelayedDeductionMinutes - right.DelayedDeductionMinutes) < 0.0001 &&
        Math.Abs(left.QhrMealAllowanceCount - right.QhrMealAllowanceCount) < 0.0001 &&
        string.Equals(left.ShiftName, right.ShiftName, StringComparison.Ordinal);

    private static bool ExpenseEquals(GoalExpense left, GoalExpense right) =>
        left.Id == right.Id && left.Date == right.Date && left.Amount == right.Amount &&
        string.Equals(left.Description, right.Description, StringComparison.Ordinal);

    private static bool AttachmentEquals(EvidenceAttachment left, EvidenceAttachment right) =>
        left.Id == right.Id && left.Length == right.Length && left.AddedAt == right.AddedAt &&
        string.Equals(left.OriginalFileName, right.OriginalFileName, StringComparison.Ordinal) &&
        string.Equals(left.Extension, right.Extension, StringComparison.OrdinalIgnoreCase);

    private static bool GoalSettingsEqual(FinancialGoalData left, FinancialGoalData right) =>
        left.GoalName == right.GoalName && left.TargetAmount == right.TargetAmount &&
        left.StartDate == right.StartDate && left.IncludeMealAllowance == right.IncludeMealAllowance &&
        left.SuppressAutomaticCompletion == right.SuppressAutomaticCompletion &&
        left.ActiveGoalId == right.ActiveGoalId && JsonEquals(left.Goals, right.Goals);

    private static bool HasGoal(FinancialGoalData goal) =>
        goal.Goals.Count > 0 || !string.IsNullOrWhiteSpace(goal.GoalName) || goal.TargetAmount > 0;

    private static void ValidatePassword(string password, bool creating)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("请输入备份密码");
        if (creating && password.Length < 8) throw new ArgumentException("备份密码至少需要 8 个字符");
    }

    private sealed class ReadOnlySegmentStream : Stream
    {
        private readonly Stream _source;
        private long _remaining;

        public ReadOnlySegmentStream(Stream source, long length)
        {
            _source = source;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _remaining;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = _source.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0) return 0;
            var read = await _source.ReadAsync(
                buffer[..(int)Math.Min(buffer.Length, _remaining)],
                cancellationToken);
            _remaining -= read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class BackupImportPackage : IDisposable
{
    private readonly FileStream _temporaryStream;

    internal BackupImportPackage(
        FileStream temporaryStream,
        ZipArchive archive,
        BackupManifest manifest,
        AppSettings settings,
        IReadOnlyList<AttendanceRecord> attendance,
        FinancialGoalData goal,
        IReadOnlyDictionary<DateOnly, DailyEvidence> evidence)
    {
        _temporaryStream = temporaryStream;
        Archive = archive;
        Manifest = manifest;
        Settings = settings;
        Attendance = attendance;
        Goal = goal;
        Evidence = evidence;
    }

    internal ZipArchive Archive { get; }
    public BackupManifest Manifest { get; }
    public AppSettings Settings { get; }
    public IReadOnlyList<AttendanceRecord> Attendance { get; }
    public FinancialGoalData Goal { get; }
    public IReadOnlyDictionary<DateOnly, DailyEvidence> Evidence { get; }

    internal async Task<byte[]> ReadImageBytesAsync(
        DateOnly date,
        EvidenceAttachment attachment,
        CancellationToken cancellationToken)
    {
        var entryName = $"evidence/{date:yyyy-MM-dd}/images/{attachment.Id}.bin";
        var entry = Archive.GetEntry(entryName) ?? throw new InvalidDataException($"备份缺少证据图片：{entryName}");
        if (entry.Length > DailyEvidenceService.MaximumImageBytes)
            throw new InvalidDataException($"备份中的证据图片超过 20 MB：{attachment.OriginalFileName}");
        await using var stream = entry.Open();
        using var memory = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    public void Dispose()
    {
        Archive.Dispose();
        _temporaryStream.Dispose();
    }
}
