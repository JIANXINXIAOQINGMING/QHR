using System.Globalization;

namespace QHR.Models;

/// <summary>
/// 区分法定节假日本身与为了形成连续假期而安排的普通休息日。
/// HolidayInfo.IsOffDay 只表示当天不上班，并不表示当天都适用法定节假日费率。
/// </summary>
public static class ChineseStatutoryHoliday
{
    private static readonly ChineseLunisolarCalendar LunarCalendar = new();

    public static bool IsStatutory(HolidayInfo holiday) =>
        holiday.IsOffDay && IsStatutory(holiday.Date, holiday.Name);

    public static bool IsStatutory(DateOnly date, string holidayName)
    {
        if (string.IsNullOrWhiteSpace(holidayName)) return false;

        // 合并假期的名称可能同时包含“中秋节、国庆节”，因此各规则需要独立判断。
        if (holidayName.Contains("元旦", StringComparison.Ordinal) &&
            date.Month == 1 && date.Day == 1)
        {
            return true;
        }

        if (holidayName.Contains("春节", StringComparison.Ordinal) && IsSpringFestival(date))
        {
            return true;
        }

        if (holidayName.Contains("清明", StringComparison.Ordinal) && IsQingmingFestival(date))
        {
            return true;
        }

        if (holidayName.Contains("劳动节", StringComparison.Ordinal) &&
            date.Month == 5 && date.Day >= 1 && date.Day <= (date.Year >= 2025 ? 2 : 1))
        {
            return true;
        }

        if (holidayName.Contains("端午", StringComparison.Ordinal) && IsLunarDate(date, 5, 5))
        {
            return true;
        }

        if (holidayName.Contains("中秋", StringComparison.Ordinal) && IsLunarDate(date, 8, 15))
        {
            return true;
        }

        return holidayName.Contains("国庆", StringComparison.Ordinal) &&
               date.Month == 10 && date.Day is >= 1 and <= 3;
    }

    private static bool IsSpringFestival(DateOnly date)
    {
        var solarDate = date.ToDateTime(TimeOnly.MinValue);
        if (solarDate < LunarCalendar.MinSupportedDateTime ||
            solarDate > LunarCalendar.MaxSupportedDateTime)
        {
            return false;
        }

        var lunarYear = LunarCalendar.GetYear(solarDate);
        var newYear = DateOnly.FromDateTime(
            LunarCalendar.ToDateTime(lunarYear, 1, 1, 0, 0, 0, 0));
        // 除夕仍属于上一农历年；此时要与即将到来的正月初一比较。
        if (date.DayNumber - newYear.DayNumber > 300 &&
            lunarYear < LunarCalendar.GetYear(LunarCalendar.MaxSupportedDateTime))
        {
            newYear = DateOnly.FromDateTime(
                LunarCalendar.ToDateTime(lunarYear + 1, 1, 1, 0, 0, 0, 0));
        }
        var dayOffset = date.DayNumber - newYear.DayNumber;

        // 2025 年起春节法定假日增加除夕；此前为正月初一至初三。
        return date.Year >= 2025
            ? dayOffset is >= -1 and <= 2
            : dayOffset is >= 0 and <= 2;
    }

    private static bool IsLunarDate(DateOnly date, int expectedMonth, int expectedDay)
    {
        var solarDate = date.ToDateTime(TimeOnly.MinValue);
        if (solarDate < LunarCalendar.MinSupportedDateTime ||
            solarDate > LunarCalendar.MaxSupportedDateTime)
        {
            return false;
        }

        var lunarYear = LunarCalendar.GetYear(solarDate);
        var lunarMonth = LunarCalendar.GetMonth(solarDate);
        var leapMonth = LunarCalendar.GetLeapMonth(lunarYear);
        if (leapMonth > 0)
        {
            if (lunarMonth == leapMonth) return false;
            if (lunarMonth > leapMonth) lunarMonth--;
        }

        return lunarMonth == expectedMonth && LunarCalendar.GetDayOfMonth(solarDate) == expectedDay;
    }

    private static bool IsQingmingFestival(DateOnly date)
    {
        if (date.Month != 4 || date.Year is < 1900 or > 2099) return false;

        // 20、21 世纪清明日通用推算式；当前应用支持的近十年范围均落在该区间。
        var shortYear = date.Year % 100;
        var centuryConstant = date.Year >= 2000 ? 4.81 : 5.59;
        var qingmingDay = (int)(shortYear * 0.2422 + centuryConstant) - shortYear / 4;
        return date.Day == qingmingDay;
    }
}
