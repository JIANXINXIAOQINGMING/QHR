namespace QHR.Models;

public sealed class OvertimeRecord
{
    public DateOnly Date { get; init; }
    public string DateText => Date.ToString("yyyy-MM-dd");
    public string WeekText => Date.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };
    public string ClockInText { get; init; } = "--:--";
    public string ClockOutText { get; init; } = "--:--";
    public DayKind Kind { get; init; }
    public string KindText => Kind switch
    {
        DayKind.Holiday => "节假日",
        DayKind.Weekend => "周末",
        _ => "工作日"
    };
    public string HolidayName { get; init; } = string.Empty;
    public string DateTypeText => string.IsNullOrWhiteSpace(HolidayName)
        ? KindText
        : $"{KindText} · {HolidayName}";
    public decimal HourlyRate { get; init; }
    public double GrossHours { get; init; }
    public string GrossDurationText => FormatDuration(GrossHours);
    public decimal GrossOvertimePay { get; set; }
    public double DelayedHours { get; init; }
    public double DelayDeductedHours { get; init; }
    public string DelayDeductedDurationText => FormatDuration(DelayDeductedHours);
    public double ActualHours { get; init; }
    public string ActualDurationText => FormatDuration(ActualHours);
    public double LeaveHours { get; init; }
    public double PersonalLeaveHours { get; init; }
    public double AnnualLeaveHours { get; init; }
    public string LeaveSummaryText { get; init; } = string.Empty;
    public double LeaveDeductedHours { get; set; }
    public string LeaveDeductedDurationText => FormatDuration(LeaveDeductedHours);
    public double Hours { get; set; }
    public string HoursDurationText => FormatDuration(Hours);
    public decimal UncappedOvertimePay { get; set; }
    public decimal OvertimePay { get; set; }
    public decimal CapDeductedPay { get; set; }
    public double CapExcludedHours { get; set; }
    public string CapExcludedDurationText => FormatDuration(CapExcludedHours);
    public double PaidHours => Math.Round(
        Math.Max(0, Hours) - Math.Max(0, CapExcludedHours),
        6,
        MidpointRounding.AwayFromZero);
    public string PaidHoursDurationText => FormatDuration(PaidHours);
    public decimal MealAllowance { get; set; }
    public int MealAllowanceCount { get; init; }
    public decimal GrossAmount => GrossOvertimePay + MealAllowance;
    public decimal UncappedAmount => UncappedOvertimePay + MealAllowance;
    public decimal Amount { get; set; }
    public string SourceDescription { get; set; } = string.Empty;

    private static string FormatDuration(double hours)
    {
        var totalMinutes = (int)Math.Round(hours * 60d, MidpointRounding.AwayFromZero);
        var sign = totalMinutes < 0 ? "-" : string.Empty;
        var absoluteMinutes = Math.Abs(totalMinutes);
        return $"{sign}{absoluteMinutes / 60}h{absoluteMinutes % 60:00}m";
    }
}
