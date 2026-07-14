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

        ApplyMonthlyLeaveDeduction(records, settings);
        foreach (var record in records)
        {
            record.OvertimePay = decimal.Round(
                (decimal)record.Hours * record.HourlyRate,
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
                (decimal)group.Sum(item => item.Hours) * group.Key.HourlyRate,
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
            out var scheduledClockOutText,
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
        if (hoursAfterDelay + Epsilon < Math.Max(0, settings.MinimumOvertimeHours))
        {
            hoursAfterDelay = 0;
        }
        if (delayedHours > 0)
        {
            description += $"；延时工时申请 {delayedHours:0.##}h，抵扣 {delayDeductedHours:0.##}h";
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
            ScheduledClockOutText = scheduledClockOutText,
            Kind = kind,
            HolidayName = holidayName,
            HourlyRate = rate,
            GrossHours = grossHours,
            DelayedHours = delayedHours,
            DelayDeductedHours = delayDeductedHours,
            LeaveHours = Math.Round(Math.Max(0, attendance.LeaveHours), 6, MidpointRounding.AwayFromZero),
            Hours = hoursAfterDelay,
            MealAllowance = mealAllowance,
            MealAllowanceCount = qualifiesForMealAllowance ? 1 : 0,
            SourceDescription = description
        };
    }

    private static void ApplyMonthlyLeaveDeduction(
        IReadOnlyList<OvertimeRecord> records,
        AppSettings settings)
    {
        if (!settings.DeductLeaveFromOvertime) return;

        foreach (var month in records.GroupBy(item => new { item.Date.Year, item.Date.Month }))
        {
            var remainingLeave = month.Sum(item => item.LeaveHours);
            if (remainingLeave <= Epsilon) continue;

            // 先抵扣低费率工作日，再抵扣周末和节假日，避免请假被放大成高倍扣款。
            foreach (var record in month
                         .Where(item => item.Hours > Epsilon)
                         .OrderBy(item => GetDeductionPriority(item.Kind))
                         .ThenBy(item => item.HourlyRate)
                         .ThenBy(item => item.Date))
            {
                if (remainingLeave <= Epsilon) break;
                var deducted = Math.Min(record.Hours, remainingLeave);
                deducted = Math.Round(deducted, 6, MidpointRounding.AwayFromZero);
                record.LeaveDeductedHours = deducted;
                record.Hours = Math.Round(
                    Math.Max(0, record.Hours - deducted),
                    6,
                    MidpointRounding.AwayFromZero);
                record.SourceDescription += $"；月度请假抵扣 {deducted:0.##}h";
                remainingLeave = Math.Max(0, remainingLeave - deducted);
            }
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
        out string scheduledClockOutText,
        out string description)
    {
        scheduledClockOutText = "--:--";
        if (attendance.ClockIn is null || attendance.ClockOut is null ||
            attendance.ClockOut <= attendance.ClockIn)
        {
            description = attendance.LeaveHours > 0
                ? $"QHR 请假/缺勤 {attendance.LeaveHours:0.##}h；打卡不完整"
                : "打卡不完整，未计加班";
            return 0;
        }

        DateTime intervalStart;
        if (kind == DayKind.Workday)
        {
            var earliestStart = ParseTime(settings.FlexibleWorkStartEarliest, new TimeOnly(8, 30));
            var latestStart = ParseTime(settings.FlexibleWorkStartLatest, new TimeOnly(9, 30));
            if (latestStart < earliestStart) (earliestStart, latestStart) = (latestStart, earliestStart);
            var cardTime = TimeOnly.FromDateTime(attendance.ClockIn.Value);
            var effectiveStart = cardTime < earliestStart
                ? earliestStart
                : cardTime > latestStart
                    ? latestStart
                    : cardTime;
            var scheduledClockOut = attendance.Date.ToDateTime(effectiveStart)
                .AddHours(Math.Max(0, settings.StandardWorkSpanHours));
            scheduledClockOutText = scheduledClockOut.ToString("HH:mm");
            var fixedOvertimeStart = attendance.Date.ToDateTime(
                ParseTime(settings.WorkdayOvertimeStart, new TimeOnly(19, 0)));
            intervalStart = scheduledClockOut > fixedOvertimeStart
                ? scheduledClockOut
                : fixedOvertimeStart;
        }
        else
        {
            intervalStart = attendance.ClockIn.Value;
        }

        var intervalEnd = attendance.ClockOut.Value;
        if (intervalEnd <= intervalStart)
        {
            description = kind == DayKind.Workday
                ? $"弹性下班 {scheduledClockOutText}，末卡未超过 {intervalStart:HH:mm}"
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
            ? $"弹性下班 {scheduledClockOutText}，{intervalStart:HH:mm} 后计时"
            : "首卡至末卡直算，不扣休息";
        if (hours > 0 && rounding > 1)
        {
            description += $"，按 {rounding:0.##} 分钟向下取整";
        }
        if (attendance.LeaveHours > 0)
        {
            description += $"；当日 QHR 请假/缺勤 {attendance.LeaveHours:0.##}h";
        }
        return hours;
    }

    private static (DayKind Kind, string HolidayName) GetDayKind(
        DateOnly date,
        IReadOnlyDictionary<DateOnly, HolidayInfo> calendar)
    {
        if (calendar.TryGetValue(date, out var holiday))
        {
            return holiday.IsOffDay
                ? (DayKind.Holiday, holiday.Name)
                : (DayKind.Workday, $"{holiday.Name}调休");
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
