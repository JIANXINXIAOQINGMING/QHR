namespace QHR.Models;

public sealed class AppSettings
{
    public int SettingsVersion { get; set; } = 6;
    public decimal WorkdayRate { get; set; } = 20m;
    public decimal WeekendRate { get; set; } = 30m;
    public decimal HolidayRate { get; set; } = 60m;
    public bool EnableOvertimePayCap { get; set; }
    public decimal MonthlyOvertimePayCap { get; set; } = 2000m;
    public bool ExcludeHolidayPayFromCap { get; set; } = true;
    public DateOnly? OvertimePayCapEffectiveDate { get; set; }
    public decimal MealAllowanceAmount { get; set; } = 20m;
    public string FlexibleWorkStartEarliest { get; set; } = "08:30";
    public string FlexibleWorkStartLatest { get; set; } = "09:30";
    public string WorkdayOvertimeStart { get; set; } = "19:00";
    public string LunchBreakStart { get; set; } = "12:00";
    public string LunchBreakEnd { get; set; } = "13:00";
    public string DinnerBreakStart { get; set; } = "18:00";
    public string DinnerBreakEnd { get; set; } = "18:30";
    public double MinimumOvertimeHours { get; set; } = 0;
    public double RoundingMinutes { get; set; } = 1;
    public bool DeductLunchBreak { get; set; }
    public bool DeductDinnerBreak { get; set; }
    public bool DeductLeaveFromOvertime { get; set; } = true;
    public double WorkdayMealAllowanceMinimumHours { get; set; } = 1;
    public double NonWorkdayMealAllowanceMinimumHours { get; set; } = 4;
    public bool AutoSyncHolidays { get; set; } = true;
    public bool AutoLoginEnabled { get; set; } = true;
    public string LastUsername { get; set; } = string.Empty;
    public string QhrBaseUrl { get; set; } = "https://hr.quectel.com";
    public string HolidaySourceUrl { get; set; } = "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{year}.json";
}
