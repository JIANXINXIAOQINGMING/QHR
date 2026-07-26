namespace QHR.Models;

public sealed class FinancialGoalData
{
    public int Version { get; set; } = 4;
    public string GoalName { get; set; } = string.Empty;
    public decimal TargetAmount { get; set; }
    public DateOnly StartDate { get; set; } = new(DateTime.Today.Year, 1, 1);
    public bool IncludeMealAllowance { get; set; }
    public bool SuppressAutomaticCompletion { get; set; }
    public List<GoalExpense> Expenses { get; set; } = [];
    public List<CompletedFinancialGoal> CompletedGoals { get; set; } = [];
}

public sealed class GoalExpense
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
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
