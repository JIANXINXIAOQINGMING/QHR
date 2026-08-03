namespace QHR.Models;

public sealed class SummaryRow
{
    public string Period { get; init; } = string.Empty;
    public int OvertimeDays { get; init; }
    public double WorkdayHours { get; init; }
    public double WeekendHours { get; init; }
    public double HolidayHours { get; init; }
    public double TotalHours => WorkdayHours + WeekendHours + HolidayHours;
    public double EquivalentDays => Math.Round(TotalHours / 8d, 2, MidpointRounding.AwayFromZero);
    public double DelayDeductedHours { get; init; }
    public double LeaveHours { get; init; }
    public double PersonalLeaveHours { get; init; }
    public double AnnualLeaveHours { get; init; }
    public double LeaveDeductedHours { get; init; }
    public double CapExcludedHours { get; init; }
    public double PaidHours => Math.Round(
        Math.Max(
            0,
            Math.Max(0, TotalHours) -
            Math.Max(0, LeaveDeductedHours) -
            Math.Max(0, CapExcludedHours)),
        6,
        MidpointRounding.AwayFromZero);
    public decimal GrossOvertimePay { get; init; }
    public decimal UncappedOvertimePay { get; init; }
    public decimal CapDeductedPay { get; init; }
    public decimal OvertimePay { get; init; }
    public int MealAllowanceCount { get; init; }
    public decimal MealAllowance { get; init; }
    public decimal TotalAmount { get; init; }
}
