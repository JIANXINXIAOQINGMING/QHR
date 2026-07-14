namespace QHR.Models;

public sealed class MonthlyAnalyticsPoint
{
    public string Label { get; init; } = string.Empty;
    public string PayText { get; init; } = string.Empty;
    public string MealText { get; init; } = string.Empty;
    public double PayHeight { get; init; }
    public double MealHeight { get; init; }
    public string HoursText { get; init; } = string.Empty;
    public double WorkdayHoursHeight { get; init; }
    public double WeekendHoursHeight { get; init; }
    public double HolidayHoursHeight { get; init; }
}
