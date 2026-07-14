namespace QHR.Models;

public sealed class CalendarDayCell
{
    public DateOnly? Date { get; init; }
    public string DayText { get; init; } = string.Empty;
    public string KindText { get; init; } = string.Empty;
    public string HoursText { get; init; } = string.Empty;
    public string AmountText { get; init; } = string.Empty;
    public bool HasOvertime { get; init; }
    public bool IsToday { get; init; }
    public bool IsHoliday { get; init; }
    public bool IsBlank => Date is null;
}
