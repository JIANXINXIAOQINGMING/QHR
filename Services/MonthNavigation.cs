namespace QHR.Services;

public readonly record struct MonthDateRange(DateOnly Start, DateOnly End);

public static class MonthNavigation
{
    public static DateOnly GetMonthStart(DateTime date) => new(date.Year, date.Month, 1);

    public static bool CanNavigateNext(DateOnly selectedMonth, DateOnly today) =>
        Normalize(selectedMonth) < Normalize(today);

    public static MonthDateRange GetRange(DateOnly selectedMonth, DateOnly today)
    {
        var currentMonth = Normalize(today);
        var month = Normalize(selectedMonth);
        if (month > currentMonth) month = currentMonth;
        var end = month == currentMonth ? today : month.AddMonths(1).AddDays(-1);
        return new MonthDateRange(month, end);
    }

    private static DateOnly Normalize(DateOnly date) => new(date.Year, date.Month, 1);
}
