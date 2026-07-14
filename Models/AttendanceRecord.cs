namespace QHR.Models;

public sealed class AttendanceRecord
{
    public DateOnly Date { get; init; }
    public DateTime? ClockIn { get; init; }
    public DateTime? ClockOut { get; init; }
    public IReadOnlyList<DateTime> CardTimes { get; init; } = Array.Empty<DateTime>();
    public double LeaveHours { get; init; }
    public double DelayedDeductionMinutes { get; init; }
    public double QhrMealAllowanceCount { get; init; }
    public string ShiftName { get; init; } = string.Empty;
}
