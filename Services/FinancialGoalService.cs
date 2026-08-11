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
                if (data is null || data.Version is < 1 or > 5)
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
                PrepareForUse(data);
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
            CaptureActiveGoalFromLegacy(data);
            data.Version = 5;
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

    public static void ApplyActiveGoalToLegacy(FinancialGoalData data)
    {
        var active = data.ActiveGoal;
        if (active is null)
        {
            data.GoalName = string.Empty;
            data.TargetAmount = 0;
            data.StartDate = new DateOnly(DateTime.Today.Year, 1, 1);
            data.IncludeMealAllowance = false;
            data.SuppressAutomaticCompletion = false;
            return;
        }

        data.GoalName = active.GoalName;
        data.TargetAmount = active.TargetAmount;
        data.StartDate = active.StartDate;
        data.IncludeMealAllowance = active.IncludeMealAllowance;
        data.SuppressAutomaticCompletion = active.SuppressAutomaticCompletion;
    }

    public static void CaptureActiveGoalFromLegacy(FinancialGoalData data)
    {
        data.Goals ??= [];
        var active = data.ActiveGoal;
        if (active is null) return;
        active.GoalName = data.GoalName;
        active.TargetAmount = data.TargetAmount;
        active.StartDate = data.StartDate;
        active.IncludeMealAllowance = data.IncludeMealAllowance;
        active.SuppressAutomaticCompletion = data.SuppressAutomaticCompletion;
        active.ActivationPeriods ??= [];
    }

    public static void PrepareForUse(FinancialGoalData data) => MigrateGoalProfiles(data);

    private static void MigrateGoalProfiles(FinancialGoalData data)
    {
        data.Goals ??= [];
        data.Expenses ??= [];
        data.CompletedGoals ??= [];
        foreach (var goal in data.Goals)
        {
            goal.ActivationPeriods ??= [];
        }

        if (data.Version <= 4 && data.Goals.Count == 0 &&
            (!string.IsNullOrWhiteSpace(data.GoalName) || data.TargetAmount > 0))
        {
            var start = data.StartDate.ToDateTime(TimeOnly.MinValue);
            var migrated = new FinancialGoalProfile
            {
                GoalName = data.GoalName,
                TargetAmount = data.TargetAmount,
                StartDate = data.StartDate,
                IncludeMealAllowance = data.IncludeMealAllowance,
                SuppressAutomaticCompletion = data.SuppressAutomaticCompletion,
                CreatedAt = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)),
                ActivationPeriods =
                [
                    new GoalActivationPeriod
                    {
                        StartedAt = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start))
                    }
                ]
            };
            data.Goals.Add(migrated);
            data.ActiveGoalId = migrated.Id;
        }

        if (data.ActiveGoalId is not null && data.ActiveGoal is null) data.ActiveGoalId = null;
        MigrateExpenseOwnership(data);
        ApplyActiveGoalToLegacy(data);
        data.Version = 5;
    }

    private static void MigrateExpenseOwnership(FinancialGoalData data)
    {
        if (data.Expenses.All(item => !string.IsNullOrWhiteSpace(item.GoalId))) return;

        data.Expenses = data.Expenses
            .Select(expense => string.IsNullOrWhiteSpace(expense.GoalId)
                ? CloneExpense(expense, ResolveLegacyExpenseGoalId(data, expense))
                : expense)
            .ToList();
    }

    private static string? ResolveLegacyExpenseGoalId(FinancialGoalData data, GoalExpense expense)
    {
        if (data.Goals.Count == 0) return null;

        var dayStart = ToLocalTimestamp(expense.Date);
        var nextDate = expense.Date == DateOnly.MaxValue ? expense.Date : expense.Date.AddDays(1);
        var dayEnd = expense.Date == DateOnly.MaxValue
            ? DateTimeOffset.MaxValue
            : ToLocalTimestamp(nextDate);
        var candidates = new List<(FinancialGoalProfile Goal, GoalActivationPeriod Period, int Priority)>();

        foreach (var goal in data.Goals)
        {
            foreach (var period in goal.ActivationPeriods)
            {
                var overlapsExpenseDate = period.StartedAt < dayEnd &&
                                          (period.EndedAt is null || period.EndedAt > dayStart);
                if (overlapsExpenseDate)
                {
                    // 追溯只转移收入；被转移区间仍代表当时消费原本所属的目标。
                    candidates.Add((goal, period, period.CountsTowardGoal ? 10 : 30));
                    continue;
                }

                // 兼容首次加入消费归属前已经保存过的追溯数据：旧版本会截断原区间，
                // 但 ReplacedByGoalId 和替换目标的创建时间仍能还原消费的原始归属。
                if (period.CountsTowardGoal &&
                    period.EndedAt is DateTimeOffset endedAt &&
                    !string.IsNullOrWhiteSpace(period.ReplacedByGoalId) &&
                    period.EndReason.Contains("追溯", StringComparison.Ordinal) &&
                    data.Goals.FirstOrDefault(item => item.Id == period.ReplacedByGoalId) is { } replacement &&
                    dayEnd > endedAt && dayStart < replacement.CreatedAt)
                {
                    candidates.Add((goal, period, 25));
                }
            }
        }

        var match = candidates
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.Period.StartedAt)
            .FirstOrDefault();
        if (match.Goal is not null) return match.Goal.Id;

        // 单目标旧档案中的消费都属于唯一目标；没有目标时则保留为独立账本记录。
        return data.Goals.Count == 1 ? data.Goals[0].Id : null;
    }

    private static DateTimeOffset ToLocalTimestamp(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static GoalExpense CloneExpense(GoalExpense source, string? goalId) => new()
    {
        Id = source.Id,
        GoalId = goalId,
        Date = source.Date,
        Description = source.Description,
        Amount = source.Amount
    };
}
