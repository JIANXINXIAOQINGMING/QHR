namespace QHR.Models;

public sealed class FinancialGoalData
{
    public int Version { get; set; } = 2;
    public string GoalName { get; set; } = string.Empty;
    public decimal TargetAmount { get; set; }
    public DateOnly StartDate { get; set; } = new(DateTime.Today.Year, 1, 1);
    public bool IncludeMealAllowance { get; set; }
    public List<GoalExpense> Expenses { get; set; } = [];
}

public sealed class GoalExpense
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateOnly Date { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string DateText => Date.ToString("yyyy-MM-dd");
}
