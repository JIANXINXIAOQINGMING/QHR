using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QHR.Models;

namespace QHR.Services;

public sealed class FinancialGoalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public FinancialGoalService(SettingsService settingsService, string username)
    {
        var normalizedUsername = username.Trim().ToLowerInvariant();
        var accountHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUsername)))[..24].ToLowerInvariant();
        var directory = Path.Combine(settingsService.DataDirectory, "secure");
        Directory.CreateDirectory(directory);
        StoragePath = Path.Combine(directory, $"goal-{accountHash}.qhrgoal");
    }

    public string StoragePath { get; }

    public async Task<FinancialGoalData> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(StoragePath)) return new FinancialGoalData();

            byte[]? plainBytes = null;
            try
            {
                var encryptedBytes = await File.ReadAllBytesAsync(StoragePath, cancellationToken);
                plainBytes = EncryptedAttendanceCache.UnprotectForCurrentUser(encryptedBytes);
                var data = JsonSerializer.Deserialize<FinancialGoalData>(plainBytes, JsonOptions);
                if (data is null || data.Version is < 1 or > 4)
                    throw new InvalidDataException("不支持的目标档案版本");
                if (data.Version == 1)
                {
                    // 旧版曾把用户给出的“电钢琴 22000 元”示例当作默认目标，升级时清除该示例。
                    if (data.GoalName == "电钢琴" && data.TargetAmount == 22000m)
                    {
                        data.GoalName = string.Empty;
                        data.TargetAmount = 0;
                    }
                }
                data.Expenses ??= [];
                data.CompletedGoals ??= [];
                data.Version = 4;
                return data;
            }
            catch (Exception ex) when (ex is Win32Exception or JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                try
                {
                    PreserveUnreadableArchive();
                }
                catch
                {
                    // 只读或受管目录中无法改名时，仍允许程序回退到空目标。
                }
                return new FinancialGoalData();
            }
            finally
            {
                if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(FinancialGoalData data, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var plainBytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
            try
            {
                var encryptedBytes = EncryptedAttendanceCache.ProtectForCurrentUser(plainBytes);
                var temporaryPath = StoragePath + ".tmp";
                await File.WriteAllBytesAsync(temporaryPath, encryptedBytes, cancellationToken);
                File.Move(temporaryPath, StoragePath, true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PreserveUnreadableArchive()
    {
        if (!File.Exists(StoragePath)) return;
        File.Move(StoragePath, StoragePath + $".unreadable-{DateTime.Now:yyyyMMddHHmmss}.bak", true);
    }
}
