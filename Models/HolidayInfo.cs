namespace QHR.Models;

public sealed class HolidayInfo
{
    public DateOnly Date { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsOffDay { get; init; }
}

public enum DayKind
{
    Workday,
    Weekend,
    Holiday
}
