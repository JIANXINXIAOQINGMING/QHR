using System.Globalization;
using QHR.Models;

namespace QHR.Services;

public sealed class OvertimeCalculator
{
    private const double Epsilon = 0.0001;

    public IReadOnlyList<OvertimeRecord> Calculate(
        IEnumerable<AttendanceRecord> attendance,
        IReadOnlyDictionary<DateOnly, HolidayInfo> calendar,
        AppSettings settings)
    {
        var records = attendance
            .OrderBy(item => item.Date)
            .Select(day => BuildDailyRecord(day, calendar, settings))
            .ToList();

        foreach (var record in records)
        {
            record.GrossOvertimePay = decimal.Round(
                (decimal)Math.Max(0, record.GrossHours) * record.HourlyRate,
                2,
                MidpointRounding.AwayFromZero);
        }
        ReconcileMonthlyGrossOvertimePay(records);

        ApplyMonthlyPersonalLeaveDeduction(records, settings);
        foreach (var record in records)
        {
            record.OvertimePay = decimal.Round(
                (decimal)Math.Max(0, record.Hours) * record.HourlyRate,
                2,
                MidpointRounding.AwayFromZero);
        }
        ReconcileMonthlyOvertimePay(records);
        foreach (var record in records)
        {
            record.Amount = record.OvertimePay + record.MealAllowance;
        }
        return records;
    }

    private static void ReconcileMonthlyGrossOvertimePay(IReadOnlyList<OvertimeRecord> records)
    {
        // 每日展示的是抵扣前金额。按月、日期类型和费率统一处理舍入差，
        // 让日历逐日金额相加后与“总加班费”严格一致。
        foreach (var group in records
                     .GroupBy(item => new
                     {
                         item.Date.Year,
                         item.Date.Month,
                         item.Kind,
                         item.HourlyRate
                     }))
        {
            var target = decimal.Round(
                (decimal)Math.Max(0, group.Sum(item => item.GrossHours)) * group.Key.HourlyRate,
                2,
                MidpointRounding.AwayFromZero);
            var current = group.Sum(item => item.GrossOvertimePay);
            var difference = target - current;
            if (difference == 0) continue;

            var adjustmentRecord = group
                .Where(item => item.GrossHours > Epsilon)
                .OrderBy(item => item.Date)
                .LastOrDefault();
            if (adjustmentRecord is not null)
            {
                adjustmentRecord.GrossOvertimePay += difference;
            }
        }
    }

    private static void ReconcileMonthlyOvertimePay(IReadOnlyList<OvertimeRecord> records)
    {
        // 工资侧按“月份 + 日期类型 + 费率”的总小时计算金额，再统一保留到分。
        // 日历仍展示逐日金额，最后一个有效日期承接最多几分钱的舍入差，确保月合计一致。
        foreach (var group in records
                     .GroupBy(item => new
                     {
                         item.Date.Year,
                         item.Date.Month,
                         item.Kind,
                         item.HourlyRate
                     }))
        {
            var target = decimal.Round(
                (decimal)Math.Max(0, group.Sum(item => item.Hours)) * group.Key.HourlyRate,
                2,
                MidpointRounding.AwayFromZero);
            var current = group.Sum(item => item.OvertimePay);
            var difference = target - current;
            if (difference == 0) continue;

            var adjustmentRecord = group
                .Where(item => item.Hours > Epsilon)
                .OrderBy(item => item.Date)
                .LastOrDefault();
            if (adjustmentRecord is not null)
            {
                adjustmentRecord.OvertimePay += difference;
            }
        }
    }

    public static IReadOnlyList<SummaryRow> BuildMonthlySummary(IEnumerable<OvertimeRecord> records) =>
        records.GroupBy(item => new { item.Date.Year, item.Date.Month })
            .OrderByDescending(group => group.Key.Year)
            .ThenByDescending(group => group.Key.Month)
            .Select(group => BuildSummary(group, $"{group.Key.Year} 年 {group.Key.Month:00} 月"))
            .ToArray();

    public static IReadOnlyList<SummaryRow> BuildYearlySummary(IEnumerable<OvertimeRecord> records) =>
        records.GroupBy(item => item.Date.Year)
            .OrderByDescending(group => group.Key)
            .Select(group => BuildSummary(group, $"{group.Key} 年"))
            .ToArray();

    public static decimal CalculateGrossOvertimePay(IEnumerable<OvertimeRecord> records) =>
        records
            .GroupBy(item => new
            {
                item.Date.Year,
                item.Date.Month,
                item.Kind,
                item.HourlyRate
            })
            .Sum(group => decimal.Round(
                (decimal)Math.Max(0, group.Sum(item => item.GrossHours)) * group.Key.HourlyRate,
                2,
                MidpointRounding.AwayFromZero));

    private static OvertimeRecord BuildDailyRecord(
        AttendanceRecord attendance,
        IReadOnlyDictionary<DateOnly, HolidayInfo> calendar,
        AppSettings settings)
    {
        var (kind, holidayName) = GetDayKind(attendance.Date, calendar);
        var rate = kind switch
        {
            DayKind.Holiday => settings.HolidayRate,
            DayKind.Weekend => settings.WeekendRate,
            _ => settings.WorkdayRate
        };
        var grossHours = CalculateGrossHours(
            attendance,
            kind,
            settings,
            out var description);
        var delayedHours = Math.Round(
            Math.Max(0, attendance.DelayedDeductionMinutes) / 60d,
            6,
            MidpointRounding.AwayFromZero);
        var delayDeductedHours = Math.Round(
            Math.Min(grossHours, delayedHours),
            6,
            MidpointRounding.AwayFromZero);
        var hoursAfterDelay = Math.Round(
            Math.Max(0, grossHours - delayDeductedHours),
            6,
            MidpointRounding.AwayFromZero);
        var personalLeaveHours = Math.Round(attendance.LeaveEntries
            .Where(item => item.Kind == LeaveKind.Personal)
            .Sum(item => Math.Max(0, item.Hours)), 6, MidpointRounding.AwayFromZero);
        var annualLeaveHours = Math.Round(attendance.LeaveEntries
            .Where(item => item.Kind == LeaveKind.Annual)
            .Sum(item => Math.Max(0, item.Hours)), 6, MidpointRounding.AwayFromZero);
        if (delayedHours > 0)
        {
            description += $"；延时工时申请 {delayedHours:0.##}h，抵扣 {delayDeductedHours:0.##}h";
        }
        if (annualLeaveHours > 0)
        {
            description += "；年假不抵扣加班";
        }
        var mealThreshold = kind == DayKind.Workday
            ? Math.Max(0, settings.WorkdayMealAllowanceMinimumHours)
            : Math.Max(0, settings.NonWorkdayMealAllowanceMinimumHours);
        var qualifiesForMealAllowance = hoursAfterDelay > 0 &&
                                        hoursAfterDelay + Epsilon >= mealThreshold;
        var mealAllowance = qualifiesForMealAllowance ? settings.MealAllowanceAmount : 0m;
        if (mealAllowance > 0)
        {
            description += $"；餐补 ¥{mealAllowance:0.##}";
        }

        return new OvertimeRecord
        {
            Date = attendance.Date,
            ClockInText = attendance.ClockIn?.ToString("HH:mm") ?? "--:--",
            ClockOutText = attendance.ClockOut?.ToString("HH:mm") ?? "--:--",
            Kind = kind,
            HolidayName = holidayName,
            HourlyRate = rate,
            GrossHours = grossHours,
            DelayedHours = delayedHours,
            DelayDeductedHours = delayDeductedHours,
            LeaveHours = Math.Round(Math.Max(0, attendance.LeaveHours), 6, MidpointRounding.AwayFromZero),
            PersonalLeaveHours = personalLeaveHours,
            AnnualLeaveHours = annualLeaveHours,
            LeaveSummaryText = BuildLeaveSummary(attendance),
            Hours = hoursAfterDelay,
            MealAllowance = mealAllowance,
            MealAllowanceCount = qualifiesForMealAllowance ? 1 : 0,
            SourceDescription = description
        };
    }

    private static void ApplyMonthlyPersonalLeaveDeduction(
        IReadOnlyList<OvertimeRecord> records,
        AppSettings settings)
    {
        if (!settings.DeductLeaveFromOvertime) return;

        foreach (var month in records.GroupBy(item => new { item.Date.Year, item.Date.Month }))
        {
            var remainingLeave = Math.Round(
                month.Sum(item => item.PersonalLeaveHours),
                6,
                MidpointRounding.AwayFromZero);
            if (remainingLeave <= Epsilon) continue;

            // 月度事假先抵低费率加班，避免把普通工作日事假放大成高倍费率扣款。
            foreach (var record in month
                         .Where(item => item.Hours > Epsilon)
                         .OrderBy(item => GetDeductionPriority(item.Kind))
                         .ThenBy(item => item.HourlyRate)
                         .ThenBy(item => item.Date))
            {
                if (remainingLeave <= Epsilon) break;
                var deducted = Math.Round(
                    Math.Min(record.Hours, remainingLeave),
                    6,
                    MidpointRounding.AwayFromZero);
                record.LeaveDeductedHours = Math.Round(
                    record.LeaveDeductedHours + deducted,
                    6,
                    MidpointRounding.AwayFromZero);
                record.Hours = Math.Round(
                    record.Hours - deducted,
                    6,
                    MidpointRounding.AwayFromZero);
                record.SourceDescription += $"；月度事假抵扣 {FormatDuration(deducted)}";
                remainingLeave = Math.Round(
                    Math.Max(0, remainingLeave - deducted),
                    6,
                    MidpointRounding.AwayFromZero);
            }

            if (remainingLeave <= Epsilon) continue;

            // 事假多于整月加班时，剩余部分保留为负净加班，费用仍以 0 元为下限。
            var adjustmentRecord = month
                .Where(item => item.PersonalLeaveHours > Epsilon)
                .OrderBy(item => item.Date)
                .Last();
            adjustmentRecord.LeaveDeductedHours = Math.Round(
                adjustmentRecord.LeaveDeductedHours + remainingLeave,
                6,
                MidpointRounding.AwayFromZero);
            adjustmentRecord.Hours = Math.Round(
                adjustmentRecord.Hours - remainingLeave,
                6,
                MidpointRounding.AwayFromZero);
            adjustmentRecord.SourceDescription +=
                $"；月度事假超出加班 {FormatDuration(remainingLeave)}，净加班记为负数且加班费为 0";
        }
    }

    private static int GetDeductionPriority(DayKind kind) => kind switch
    {
        DayKind.Workday => 0,
        DayKind.Weekend => 1,
        _ => 2
    };

    private static SummaryRow BuildSummary(IEnumerable<OvertimeRecord> source, string period)
    {
        var records = source.ToArray();
        return new SummaryRow
        {
            Period = period,
            OvertimeDays = records.Count(item => item.Hours > Epsilon),
            WorkdayHours = records.Where(item => item.Kind == DayKind.Workday).Sum(item => item.Hours),
            WeekendHours = records.Where(item => item.Kind == DayKind.Weekend).Sum(item => item.Hours),
            HolidayHours = records.Where(item => item.Kind == DayKind.Holiday).Sum(item => item.Hours),
            DelayDeductedHours = records.Sum(item => item.DelayDeductedHours),
            LeaveHours = records.Sum(item => item.LeaveHours),
            PersonalLeaveHours = records.Sum(item => item.PersonalLeaveHours),
            AnnualLeaveHours = records.Sum(item => item.AnnualLeaveHours),
            LeaveDeductedHours = records.Sum(item => item.LeaveDeductedHours),
            OvertimePay = records.Sum(item => item.OvertimePay),
            MealAllowanceCount = records.Sum(item => item.MealAllowanceCount),
            MealAllowance = records.Sum(item => item.MealAllowance),
            TotalAmount = records.Sum(item => item.Amount)
        };
    }

    private static double CalculateGrossHours(
        AttendanceRecord attendance,
        DayKind kind,
        AppSettings settings,
        out string description)
    {
        if (attendance.ClockIn is null || attendance.ClockOut is null ||
            attendance.ClockOut <= attendance.ClockIn)
        {
            var incompleteDayLeaveSummary = BuildLeaveSummary(attendance);
            description = attendance.LeaveHours > 0
                ? $"{incompleteDayLeaveSummary}；打卡不完整"
                : "打卡不完整，未计加班";
            return 0;
        }

        DateTime intervalStart;
        var workdayRuleText = string.Empty;
        if (kind == DayKind.Workday)
        {
            var earliestStart = ParseTime(settings.FlexibleWorkStartEarliest, new TimeOnly(8, 30));
            var latestStart = ParseTime(settings.FlexibleWorkStartLatest, new TimeOnly(9, 30));
            if (latestStart < earliestStart) (earliestStart, latestStart) = (latestStart, earliestStart);
            intervalStart = attendance.Date.ToDateTime(
                ParseTime(settings.WorkdayOvertimeStart, new TimeOnly(19, 0)));
            workdayRuleText = $"弹性上班 {earliestStart:HH:mm}-{latestStart:HH:mm}，{intervalStart:HH:mm} 后计时";
        }
        else
        {
            intervalStart = attendance.ClockIn.Value;
        }

        var intervalEnd = attendance.ClockOut.Value;
        if (intervalEnd <= intervalStart)
        {
            description = kind == DayKind.Workday
                ? $"末卡未超过工作日起算时间 {intervalStart:HH:mm}"
                : "首末卡时间无有效跨度";
            return 0;
        }

        var minutes = (intervalEnd - intervalStart).TotalMinutes;
        var rounding = Math.Clamp(settings.RoundingMinutes, 1, 240);
        minutes = Math.Floor(minutes / rounding) * rounding;
        var hours = minutes / 60d;
        if (hours + Epsilon < Math.Max(0, settings.MinimumOvertimeHours)) hours = 0;
        // 小时数保留足够精度用于按分钟计费，金额只在最终结果处四舍五入到分。
        hours = Math.Round(hours, 6, MidpointRounding.AwayFromZero);

        description = kind == DayKind.Workday
            ? workdayRuleText
            : "首卡至末卡直算，不扣休息";
        if (hours > 0 && rounding > 1)
        {
            description += $"，按 {rounding:0.##} 分钟向下取整";
        }
        var leaveSummary = BuildLeaveSummary(attendance);
        if (!string.IsNullOrWhiteSpace(leaveSummary))
        {
            description += $"；{leaveSummary}";
        }
        return hours;
    }

    private static string BuildLeaveSummary(AttendanceRecord attendance)
    {
        var parts = attendance.LeaveEntries
            .Where(item => item.Hours > Epsilon)
            .GroupBy(item => item.TypeText)
            .Select(group => $"{group.Key} {FormatDuration(group.Sum(item => item.Hours))}")
            .ToList();
        var typedHours = attendance.LeaveEntries.Sum(item => Math.Max(0, item.Hours));
        var unknownHours = Math.Max(0, attendance.LeaveHours - typedHours);
        if (unknownHours > Epsilon) parts.Add($"请假/缺勤 {FormatDuration(unknownHours)}");
        return parts.Count == 0 ? string.Empty : string.Join("；", parts);
    }

    private static string FormatDuration(double hours)
    {
        var totalMinutes = (int)Math.Round(hours * 60d, MidpointRounding.AwayFromZero);
        var sign = totalMinutes < 0 ? "-" : string.Empty;
        var absoluteMinutes = Math.Abs(totalMinutes);
        return $"{sign}{absoluteMinutes / 60}h{absoluteMinutes % 60:00}m";
    }

    private static (DayKind Kind, string HolidayName) GetDayKind(
        DateOnly date,
        IReadOnlyDictionary<DateOnly, HolidayInfo> calendar)
    {
        if (calendar.TryGetValue(date, out var holiday))
        {
            if (!holiday.IsOffDay) return (DayKind.Workday, $"{holiday.Name}调休");

            return ChineseStatutoryHoliday.IsStatutory(holiday)
                ? (DayKind.Holiday, holiday.Name)
                : (DayKind.Weekend, $"{holiday.Name}假期");
        }
        return date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? (DayKind.Weekend, string.Empty)
            : (DayKind.Workday, string.Empty);
    }

    private static TimeOnly ParseTime(string value, TimeOnly fallback) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result
            : fallback;
}
