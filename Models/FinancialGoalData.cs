using System.Text.Json.Serialization;

namespace QHR.Models;

public sealed class FinancialGoalData
{
    public int Version { get; set; } = 5;
    public string? ActiveGoalId { get; set; }
    public List<FinancialGoalProfile> Goals { get; set; } = [];

    // 下面字段继续作为当前目标的兼容镜像，保证旧备份和既有计算逻辑可以平滑迁移。
    public string GoalName { get; set; } = string.Empty;
    public decimal TargetAmount { get; set; }
    public DateOnly StartDate { get; set; } = new(DateTime.Today.Year, 1, 1);
    public bool IncludeMealAllowance { get; set; }
    public bool SuppressAutomaticCompletion { get; set; }
    public List<GoalExpense> Expenses { get; set; } = [];
    public List<CompletedFinancialGoal> CompletedGoals { get; set; } = [];

    [JsonIgnore]
    public FinancialGoalProfile? ActiveGoal =>
        Goals.FirstOrDefault(item => item.Id == ActiveGoalId);
}

public sealed class FinancialGoalProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GoalName { get; set; } = string.Empty;
    public decimal TargetAmount { get; set; }
    public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public bool IncludeMealAllowance { get; set; }
    public bool SuppressAutomaticCompletion { get; set; }
    public DateTimeOffset? ReachedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public List<GoalActivationPeriod> ActivationPeriods { get; set; } = [];

    [JsonIgnore]
    public bool IsActive { get; set; }

    [JsonIgnore]
    public string TargetAmountText => $"¥ {TargetAmount:N2}";

    [JsonIgnore]
    public string StartDateText => $"加班费起算日 {StartDate:yyyy-MM-dd}";

    [JsonIgnore]
    public string IncomeModeText => IncludeMealAllowance ? "加班费 + 餐补" : "仅加班费";

    [JsonIgnore]
    public string StatusText
    {
        get
        {
            if (ReachedAt is not null) return IsActive ? "已达成，待归档" : "已达成";
            if (IsActive) return "当前生效";
            var latest = ActivationPeriods.OrderByDescending(item => item.StartedAt).FirstOrDefault();
            if (latest is null) return "尚未生效";
            return string.IsNullOrWhiteSpace(latest.EndReason) ? "已暂停" : latest.EndReason;
        }
    }

    [JsonIgnore]
    public string ActivationSummaryText
    {
        get
        {
            var allocatedPeriods = ActivationPeriods.Where(item => item.CountsTowardGoal).ToArray();
            if (allocatedPeriods.Length == 0) return "暂无有效生效记录";
            var latest = allocatedPeriods.OrderByDescending(item => item.StartedAt).First();
            var suffix = allocatedPeriods.Length == 1 ? string.Empty : $" · 共 {allocatedPeriods.Length} 段";
            return $"最近生效 {latest.StartedAt:yyyy-MM-dd HH:mm}{suffix}";
        }
    }
}

public sealed class GoalActivationPeriod
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string EndReason { get; set; } = string.Empty;
    public string? ReplacedByGoalId { get; set; }
    public string? ReplacedByGoalName { get; set; }
    public bool CountsTowardGoal { get; set; } = true;

    [JsonIgnore]
    public string DateRangeText => EndedAt is DateTimeOffset endedAt
        ? $"{StartedAt:yyyy-MM-dd HH:mm} 至 {endedAt:yyyy-MM-dd HH:mm}"
        : $"{StartedAt:yyyy-MM-dd HH:mm} 至今";

    [JsonIgnore]
    public string ResultText => !CountsTowardGoal
        ? string.IsNullOrWhiteSpace(EndReason) ? "该段收入已转移" : EndReason
        : EndedAt is null
        ? "当前生效中"
        : !string.IsNullOrWhiteSpace(ReplacedByGoalName)
            ? $"被“{ReplacedByGoalName}”替换"
            : string.IsNullOrWhiteSpace(EndReason) ? "已结束" : EndReason;
}

public sealed class GoalExpense
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string? GoalId { get; init; }
    public DateOnly Date { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string DateText => Date.ToString("yyyy-MM-dd");
}

public sealed class CompletedFinancialGoal
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string GoalName { get; init; } = string.Empty;
    public decimal TargetAmount { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly CompletedDate { get; init; }
    public int DurationDays { get; init; }
    public bool IncludedMealAllowance { get; init; }
    public double OvertimeHours { get; init; }
    public int OvertimeDays { get; init; }
    public double WorkdayHours { get; init; }
    public double WeekendHours { get; init; }
    public double HolidayHours { get; init; }
    public decimal OvertimePay { get; init; }
    public decimal MealAllowance { get; init; }
    public decimal EarnedAmount { get; init; }
    public decimal ExpenseAmount { get; init; }
    public decimal EffectiveAmount { get; init; }
    public List<GoalExpense> Expenses { get; init; } = [];

    public string CompletedDateText => $"{CompletedDate:yyyy-MM-dd} 完成";
    public string DurationText => DurationDays <= 1 ? "1 天" : $"{DurationDays} 天";
    public string DateRangeText => $"{StartDate:yyyy-MM-dd} 至 {CompletedDate:yyyy-MM-dd}";
    public string TargetAmountText => $"¥ {TargetAmount:N2}";
    public string OvertimeHoursText => FormatDuration(OvertimeHours);
    public string OvertimeDaysText => $"{OvertimeDays} 天";
    public string OvertimePayText => $"¥ {OvertimePay:N2}";
    public string MealAllowanceText => $"¥ {MealAllowance:N2}";
    public string ExpenseAmountText => $"¥ {ExpenseAmount:N2}";
    public string EarnedAmountText => $"¥ {EarnedAmount:N2}";
    public string EffectiveAmountText => $"¥ {EffectiveAmount:N2}";
    public string CompletionCalculationText =>
        $"实际有效金额 {EffectiveAmountText} = 累计收入 {EarnedAmountText} - 期间消费 {ExpenseAmountText}";
    public string IncomeModeText => IncludedMealAllowance ? "加班费 + 餐补" : "仅加班费";
    public string IncomeDetailText => $"加班费 {OvertimePayText} · 餐补 {MealAllowanceText} · {IncomeModeText}";
    public string OvertimeCompositionText =>
        $"工作日 {FormatDuration(WorkdayHours)} · 周末 {FormatDuration(WeekendHours)} · 节假日 {FormatDuration(HolidayHours)}";

    private static string FormatDuration(double hours)
    {
        var minutes = (int)Math.Round(hours * 60d, MidpointRounding.AwayFromZero);
        var sign = minutes < 0 ? "-" : string.Empty;
        var absoluteMinutes = Math.Abs(minutes);
        return $"{sign}{absoluteMinutes / 60}h{absoluteMinutes % 60:00}m";
    }
}
