namespace QHR.Models;

public sealed class ExpenseCalendarDayCell
{
    public DateOnly? Date { get; init; }
    public string DayText { get; init; } = string.Empty;
    public string AmountText { get; init; } = string.Empty;
    public string CountText { get; init; } = string.Empty;
    public bool HasExpense { get; init; }
    public bool IsToday { get; init; }
    public bool IsFuture { get; init; }
    public bool IsBlank => Date is null;
}
