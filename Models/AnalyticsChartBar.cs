namespace QHR.Models;

public sealed class AnalyticsChartBar
{
    public string Label { get; init; } = string.Empty;
    public string DisplayValue { get; init; } = string.Empty;
    public double BarHeight { get; init; }
    public double BarLength { get; init; }
}
