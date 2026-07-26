namespace QHR.Models;

public sealed class AttendanceRecord
{
    public DateOnly Date { get; init; }
    public DateTime? ClockIn { get; init; }
    public DateTime? ClockOut { get; init; }
    public IReadOnlyList<DateTime> CardTimes { get; init; } = Array.Empty<DateTime>();
    public double LeaveHours { get; init; }
    public IReadOnlyList<LeaveEntry> LeaveEntries { get; init; } = Array.Empty<LeaveEntry>();
    public double DelayedDeductionMinutes { get; init; }
    public double QhrMealAllowanceCount { get; init; }
    public string ShiftName { get; init; } = string.Empty;
}

public enum LeaveKind
{
    Unknown,
    Personal,
    Annual
}

public sealed class LeaveEntry
{
    public LeaveKind Kind { get; init; }
    public double Hours { get; init; }
    public string SourceTypeName { get; init; } = string.Empty;

    public string TypeText => Kind switch
    {
        LeaveKind.Personal => "事假",
        LeaveKind.Annual => "年假",
        _ => string.IsNullOrWhiteSpace(SourceTypeName) ? "请假" : SourceTypeName
    };
}
