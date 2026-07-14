using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using QHR.Models;
using QHR.Services;

namespace QHR.Views;

public partial class MainWindow : Window
{
    private readonly string _username;
    private readonly QhrClient _qhrClient;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly HolidayService _holidayService;
    private readonly FinancialGoalService _financialGoalService;
    private readonly DailyEvidenceService _dailyEvidenceService;
    private readonly OvertimeCalculator _calculator = new();
    private readonly ObservableCollection<OvertimeRecord> _dailyRecords = [];
    private readonly ObservableCollection<SummaryRow> _monthlyRecords = [];
    private readonly ObservableCollection<SummaryRow> _yearlyRecords = [];
    private readonly ObservableCollection<SummaryRow> _analyticsYearRecords = [];
    private readonly ObservableCollection<GoalExpense> _goalExpenses = [];
    private readonly ObservableCollection<EvidenceImageItem> _dayEvidenceImages = [];
    private IReadOnlyList<AttendanceRecord> _lastAttendance = Array.Empty<AttendanceRecord>();
    private IReadOnlyList<OvertimeRecord> _currentCalculatedRecords = Array.Empty<OvertimeRecord>();
    private IReadOnlyDictionary<DateOnly, HolidayInfo> _lastCalendar = new Dictionary<DateOnly, HolidayInfo>();
    private IReadOnlyList<AttendanceRecord> _analysisAttendance = Array.Empty<AttendanceRecord>();
    private IReadOnlyDictionary<DateOnly, HolidayInfo> _analysisCalendar = new Dictionary<DateOnly, HolidayInfo>();
    private IReadOnlyList<OvertimeRecord> _analysisRecords = Array.Empty<OvertimeRecord>();
    private DateOnly? _analysisLoadedOn;
    private DateOnly? _analysisStartDate;
    private FinancialGoalData _goalData = new();
    private CancellationTokenSource? _autoRefreshCancellation;
    private bool _isBusy;
    private bool _isLoaded;
    private bool _refreshPending;
    private bool _isLoggingOut;
    private bool _goalLoaded;
    private int _analyticsRangeYears = 3;
    private int _analyticsSelectedYear = DateTime.Today.Year;
    private DateOnly? _openedDetailDate;

    public MainWindow(
        string username,
        QhrClient qhrClient,
        SettingsService settingsService,
        AppSettings settings)
    {
        InitializeComponent();
        _username = username;
        _qhrClient = qhrClient;
        _settingsService = settingsService;
        _settings = settings;
        _holidayService = new HolidayService(settingsService, settings);
        _financialGoalService = new FinancialGoalService(settingsService, username);
        _dailyEvidenceService = new DailyEvidenceService(settingsService, username);

        HeaderUsernameText.Text = GetDisplayName(username);
        ProfileMenuUsernameText.Text = GetDisplayName(username);
        AvatarText.Text = GetAvatarText(username);
        VersionMenuItem.Header = $"版本 v{UpdateService.CurrentDisplayVersion}";
        SettingsVersionText.Text = $"当前版本 v{UpdateService.CurrentDisplayVersion}";
        var today = DateTime.Today;
        StartDatePicker.SelectedDate = new DateTime(today.Year, today.Month, 1);
        EndDatePicker.SelectedDate = today;
        DailyDataGrid.ItemsSource = _dailyRecords;
        MonthlyDataGrid.ItemsSource = _monthlyRecords;
        YearlyDataGrid.ItemsSource = _yearlyRecords;
        AnalyticsYearDataGrid.ItemsSource = _analyticsYearRecords;
        AnalyticsYearComboBox.ItemsSource = Enumerable.Range(today.Year - 9, 10).Reverse().ToArray();
        AnalyticsYearComboBox.SelectedItem = today.Year;
        GoalExpensesDataGrid.ItemsSource = _goalExpenses;
        DayEvidenceItemsControl.ItemsSource = _dayEvidenceImages;
        ExpenseDatePicker.SelectedDate = today;
        LoadSettingsIntoControls();
        UpdateMonthNavigationButtons();
        SelectNavigation(0);

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) return;
        _isLoaded = true;
        await RefreshDataAsync(false);
    }

    private async Task RefreshDataAsync(
        bool forceHolidaySync,
        bool refreshRecentMonths = true,
        string? busyStatus = null)
    {
        if (_isBusy) return;
        if (!TryGetDateRange(out var startDate, out var endDate)) return;

        SetBusy(true, busyStatus ?? "正在读取 QHR 数据…");
        try
        {
            var progress = new Progress<string>(message =>
            {
                HeaderStatusText.Text = message;
                RefreshOverlayStatusText.Text = message;
            });
            var calendarStart = new DateOnly(startDate.Year, 1, 1);
            var calendarEnd = new DateOnly(endDate.Year, 12, 31);
            var attendanceTask = _qhrClient.FetchAttendanceAsync(
                startDate,
                endDate,
                progress,
                refreshRecentMonths);
            var calendarTask = _holidayService.GetCalendarAsync(calendarStart, calendarEnd, forceHolidaySync);
            await Task.WhenAll(attendanceTask, calendarTask);

            _lastAttendance = await attendanceTask;
            _lastCalendar = await calendarTask;
            MergeCurrentRangeIntoAnalysis(startDate, endDate);
            RecalculateCurrentResults();
            if (_analysisLoadedOn is not null) RecalculateAnalysisResults();
            HeaderStatusText.Text = $"更新完成 · {DateTime.Now:HH:mm} · {_qhrClient.LastCacheStatus}";
        }
        catch (Exception ex)
        {
            HeaderStatusText.Text = "更新失败";
            MessageBox.Show(this, GetFriendlyDataError(ex), "无法刷新数据", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, HeaderStatusText.Text);
            if (_refreshPending && !_isLoggingOut)
            {
                _refreshPending = false;
                ScheduleAutoRefresh();
            }
        }
    }

    private void RecalculateCurrentResults()
    {
        var calculated = _calculator.Calculate(_lastAttendance, _lastCalendar, _settings);
        _currentCalculatedRecords = calculated;
        ReplaceItems(_dailyRecords, calculated);
        ReplaceItems(_monthlyRecords, OvertimeCalculator.BuildMonthlySummary(calculated));
        ReplaceItems(_yearlyRecords, OvertimeCalculator.BuildYearlySummary(calculated));
        UpdateCalendar(calculated);
        HolidayDataGrid.ItemsSource = _lastCalendar.Values
            .OrderBy(item => item.Date)
            .Select(item => new HolidayDisplayRow(item))
            .ToArray();

        var hours = calculated.Sum(item => item.Hours);
        var amount = calculated.Sum(item => item.OvertimePay);
        var mealAllowanceCount = calculated.Sum(item => item.MealAllowanceCount);
        var mealAllowanceAmount = calculated.Sum(item => item.MealAllowance);
        var days = calculated.Count(item => item.Hours > 0);
        TotalHoursText.Text = hours.ToString("0.##");
        TotalAmountText.Text = $"¥ {amount:N2}";
        OvertimeDaysText.Text = days.ToString(CultureInfo.InvariantCulture);
        AverageHoursText.Text = $"日均 {(days == 0 ? 0 : hours / days):0.##} 小时";
        MealAllowanceCountText.Text = mealAllowanceCount.ToString(CultureInfo.InvariantCulture);
        MealAllowanceAmountText.Text = $"餐补合计 ¥ {mealAllowanceAmount:N2}";
        if (TryGetDateRange(out var startDate, out var endDate, false))
        {
            RangeText.Text = $"{startDate:yyyy-MM-dd} 至 {endDate:yyyy-MM-dd}";
        }
        HolidayDetailStatusText.Text = _holidayService.LastStatus;
        HolidaySourceDisplayTextBox.Text = _settings.HolidaySourceUrl;
        OverviewStatusText.Text = $"节假日：{_holidayService.LastStatus} · 考勤：{_qhrClient.LastCacheStatus}";
    }

    private void UpdateCalendar(IReadOnlyList<OvertimeRecord> records)
    {
        var month = GetSelectedMonth();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstDay = new DateOnly(month.Year, month.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
        var leadingDays = ((int)firstDay.DayOfWeek + 6) % 7;
        var cellCount = (int)Math.Ceiling((leadingDays + daysInMonth) / 7d) * 7;
        var recordsByDate = records
            .Where(item => item.Date.Year == month.Year && item.Date.Month == month.Month)
            .GroupBy(item => item.Date)
            .ToDictionary(group => group.Key, group => group.Last());
        var cells = new List<CalendarDayCell>(cellCount);

        for (var slot = 0; slot < cellCount; slot++)
        {
            var day = slot - leadingDays + 1;
            if (day < 1 || day > daysInMonth)
            {
                cells.Add(new CalendarDayCell());
                continue;
            }

            var date = new DateOnly(month.Year, month.Month, day);
            recordsByDate.TryGetValue(date, out var record);
            var kindText = string.Empty;
            var isHoliday = false;
            if (_lastCalendar.TryGetValue(date, out var holiday))
            {
                kindText = holiday.IsOffDay ? holiday.Name : string.Empty;
                isHoliday = holiday.IsOffDay;
            }
            else if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                kindText = "周末";
            }

            var hasOvertime = record is { Hours: > 0 };
            cells.Add(new CalendarDayCell
            {
                Date = date,
                DayText = day.ToString(CultureInfo.InvariantCulture),
                KindText = kindText,
                HoursText = hasOvertime ? $"加班 {record!.HoursDurationText}" : string.Empty,
                AmountText = hasOvertime ? $"加班费 ¥{record!.OvertimePay:N2}" : string.Empty,
                HasOvertime = hasOvertime,
                IsToday = date == today,
                IsHoliday = isHoliday || record?.Kind == DayKind.Holiday
            });
        }

        OvertimeCalendar.ItemsSource = cells;
        var monthRecords = recordsByDate.Values.ToArray();
        CalendarMonthSummaryText.Text = $"{month:yyyy 年 MM 月} · " +
                                        $"加班 {FormatDuration(monthRecords.Sum(item => item.Hours))} · " +
                                        $"加班费 ¥{monthRecords.Sum(item => item.OvertimePay):N2} · " +
                                        $"餐补 {monthRecords.Sum(item => item.MealAllowanceCount)} 次";
    }

    private async void CalendarDay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CalendarDayCell cell } ||
            cell.Date is not DateOnly date) return;

        var record = _currentCalculatedRecords.LastOrDefault(item => item.Date == date);
        DayDetailTitleText.Text = $"{date:yyyy 年 MM 月 dd 日} · {GetWeekText(date.DayOfWeek)}";
        DayDetailSubtitleText.Text = record?.DateTypeText ?? GetDateTypeText(date);
        DayDetailClockInText.Text = record?.ClockInText ?? "--:--";
        DayDetailClockOutText.Text = record?.ClockOutText ?? "--:--";
        DayDetailGrossText.Text = record?.GrossDurationText ?? "0h00m";
        DayDetailDelayText.Text = record?.DelayDeductedDurationText ?? "0h00m";
        DayDetailLeaveText.Text = record?.LeaveDeductedDurationText ?? "0h00m";
        DayDetailActualText.Text = record?.HoursDurationText ?? "0h00m";
        DayDetailMealCountText.Text = $"{record?.MealAllowanceCount ?? 0} 次";
        DayDetailOvertimePayText.Text = $"¥ {record?.OvertimePay ?? 0m:N2}";
        DayDetailMealPayText.Text = $"¥ {record?.MealAllowance ?? 0m:N2}";
        DayDetailTotalText.Text = $"¥ {record?.Amount ?? 0m:N2}";
        DayDetailOvertimePayText.Foreground = record?.Kind == DayKind.Holiday
            ? (Brush)FindResource("HolidayBrush")
            : (Brush)FindResource("AccentBrush");
        _openedDetailDate = date;
        DayDetailOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
        await LoadDayEvidenceAsync(date);
    }

    private void CloseDayDetailButton_Click(object sender, RoutedEventArgs e)
    {
        DayDetailOverlay.Visibility = Visibility.Collapsed;
        _openedDetailDate = null;
        _dayEvidenceImages.Clear();
        DayEvidenceNoteTextBox.Clear();
    }

    private async Task LoadDayEvidenceAsync(DateOnly date)
    {
        DayEvidenceStatusText.Text = "正在读取本地加密证据…";
        DayEvidenceNoteTextBox.IsEnabled = false;
        _dayEvidenceImages.Clear();
        DayEvidenceEmptyText.Visibility = Visibility.Visible;
        try
        {
            var evidence = await _dailyEvidenceService.LoadAsync(date);
            if (_openedDetailDate != date) return;
            DayEvidenceNoteTextBox.Text = evidence.Note;
            var failedCount = 0;
            foreach (var attachment in evidence.Images)
            {
                try
                {
                    var preview = await _dailyEvidenceService.LoadPreviewAsync(date, attachment);
                    if (_openedDetailDate != date) return;
                    _dayEvidenceImages.Add(new EvidenceImageItem
                    {
                        Attachment = attachment,
                        Preview = preview
                    });
                }
                catch
                {
                    failedCount++;
                }
            }

            UpdateDayEvidenceStatus(evidence.Images.Count, evidence.Images.Sum(item => item.Length), failedCount);
        }
        catch (Exception ex)
        {
            DayEvidenceNoteTextBox.Clear();
            DayEvidenceStatusText.Text = ex.Message;
        }
        finally
        {
            if (_openedDetailDate == date)
            {
                DayEvidenceNoteTextBox.IsEnabled = true;
                DayEvidenceEmptyText.Visibility = _dayEvidenceImages.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private async void SaveDayEvidenceNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openedDetailDate is not DateOnly date) return;
        try
        {
            await _dailyEvidenceService.SaveNoteAsync(date, DayEvidenceNoteTextBox.Text);
            DayEvidenceStatusText.Text = $"备注已保存 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "备注保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AddEvidenceImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openedDetailDate is not DateOnly date) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择加班证据图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        DayEvidenceStatusText.Text = "正在加密保存图片…";
        var failures = new List<string>();
        try
        {
            await _dailyEvidenceService.SaveNoteAsync(date, DayEvidenceNoteTextBox.Text);
            foreach (var fileName in dialog.FileNames)
            {
                try
                {
                    await _dailyEvidenceService.AddImageAsync(date, fileName);
                }
                catch (Exception ex)
                {
                    failures.Add($"{System.IO.Path.GetFileName(fileName)}：{ex.Message}");
                }
            }
            await LoadDayEvidenceAsync(date);
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }

        if (failures.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, failures), "部分图片未保存", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeleteEvidenceImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openedDetailDate is not DateOnly date ||
            sender is not FrameworkElement { DataContext: EvidenceImageItem item }) return;
        if (MessageBox.Show(this, $"确定删除证据图片“{item.FileName}”吗？", "删除证据",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            await _dailyEvidenceService.DeleteImageAsync(date, item.Attachment.Id);
            await LoadDayEvidenceAsync(date);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ExportDayEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openedDetailDate is not DateOnly date) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出当天加班资料",
            Filter = "ZIP 压缩包|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"QHR-加班资料-{date:yyyy-MM-dd}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            DayEvidenceStatusText.Text = "正在整理当天资料…";
            await _dailyEvidenceService.SaveNoteAsync(date, DayEvidenceNoteTextBox.Text);
            await _dailyEvidenceService.ExportDayAsync(date, dialog.FileName, BuildDayExportDetails(date));
            DayEvidenceStatusText.Text = $"已导出 · {System.IO.Path.GetFileName(dialog.FileName)}";
            MessageBox.Show(this, $"当天资料已导出：\n{dialog.FileName}", "导出完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ExportMonthEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        var month = GetSelectedMonth();
        var dialog = new SaveFileDialog
        {
            Title = "导出本月加班资料",
            Filter = "ZIP 压缩包|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"QHR-月度加班资料-{month:yyyy-MM}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var monthRecords = _currentCalculatedRecords
                .Where(item => item.Date.Year == month.Year && item.Date.Month == month.Month)
                .OrderBy(item => item.Date)
                .ToArray();
            var detailMap = monthRecords.ToDictionary(item => item.Date, item => BuildDayExportDetails(item.Date));
            await _dailyEvidenceService.ExportMonthAsync(month, dialog.FileName, BuildMonthExportCsv(monthRecords), detailMap);
            MessageBox.Show(this, $"本月资料已导出：\n{dialog.FileName}\n\nZIP 内含月度 CSV，以及有备注或图片的每日资料。",
                "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string BuildDayExportDetails(DateOnly date)
    {
        var record = _currentCalculatedRecords.LastOrDefault(item => item.Date == date);
        var builder = new StringBuilder();
        builder.AppendLine("QHR 加班助手 · 当天计算明细");
        builder.AppendLine($"日期：{date:yyyy-MM-dd} {GetWeekText(date.DayOfWeek)}");
        builder.AppendLine($"日期类型：{record?.DateTypeText ?? GetDateTypeText(date)}");
        builder.AppendLine($"首卡 / 末卡：{record?.ClockInText ?? "--:--"} / {record?.ClockOutText ?? "--:--"}");
        builder.AppendLine($"原始加班：{record?.GrossDurationText ?? "0h00m"}");
        builder.AppendLine($"延时工时抵扣：{record?.DelayDeductedDurationText ?? "0h00m"}");
        builder.AppendLine($"请假：{record?.LeaveDeductedDurationText ?? "0h00m"}");
        builder.AppendLine($"实算加班：{record?.HoursDurationText ?? "0h00m"}");
        builder.AppendLine($"加班费：¥ {record?.OvertimePay ?? 0m:N2}");
        builder.AppendLine($"餐补：{record?.MealAllowanceCount ?? 0} 次 / ¥ {record?.MealAllowance ?? 0m:N2}");
        builder.AppendLine($"当天合计：¥ {record?.Amount ?? 0m:N2}");
        builder.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        return builder.ToString().TrimEnd();
    }

    private static string BuildMonthExportCsv(IReadOnlyList<OvertimeRecord> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("日期,日期类型,首卡,末卡,原始加班,延时工时抵扣,请假,实算加班,加班费,餐补次数,餐补金额,合计");
        foreach (var record in records)
        {
            builder.AppendLine(string.Join(",",
                EscapeCsv(record.DateText),
                EscapeCsv(record.DateTypeText),
                EscapeCsv(record.ClockInText),
                EscapeCsv(record.ClockOutText),
                EscapeCsv(record.GrossDurationText),
                EscapeCsv(record.DelayDeductedDurationText),
                EscapeCsv(record.LeaveDeductedDurationText),
                EscapeCsv(record.HoursDurationText),
                record.OvertimePay.ToString("0.00", CultureInfo.InvariantCulture),
                record.MealAllowanceCount.ToString(CultureInfo.InvariantCulture),
                record.MealAllowance.ToString("0.00", CultureInfo.InvariantCulture),
                record.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
        }
        return builder.ToString();
    }

    private static string EscapeCsv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private void UpdateDayEvidenceStatus(int imageCount, long totalBytes, int failedCount = 0)
    {
        DayEvidenceEmptyText.Visibility = imageCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        var failedText = failedCount == 0 ? string.Empty : $" · {failedCount} 张无法预览";
        DayEvidenceStatusText.Text = $"已保存 {imageCount} 张 · {FormatFileSize(totalBytes)}{failedText} · 最多 30 张，单张不超过 20 MB";
    }

    private static string FormatFileSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:N1} MB"
        : $"{bytes / 1024d:N0} KB";

    private string GetDateTypeText(DateOnly date)
    {
        if (_lastCalendar.TryGetValue(date, out var holiday))
        {
            return holiday.IsOffDay ? $"节假日 · {holiday.Name}" : $"调休工作日 · {holiday.Name}";
        }
        return date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "周末" : "工作日";
    }

    private static string GetWeekText(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    private static string FormatDuration(double hours)
    {
        var totalMinutes = Math.Max(0, (int)Math.Round(hours * 60d, MidpointRounding.AwayFromZero));
        return $"{totalMinutes / 60}h{totalMinutes % 60:00}m";
    }

    private void RecalculateAnalysisResults()
    {
        _analysisRecords = _calculator.Calculate(_analysisAttendance, _analysisCalendar, _settings);
        var currentYear = DateTime.Today.Year;
        var selectedYears = GetSelectedAnalysisYears(currentYear);
        var selectedYearSet = selectedYears.ToHashSet();
        var selectedRecords = _analysisRecords
            .Where(item => selectedYearSet.Contains(item.Date.Year))
            .ToArray();
        var yearly = selectedYears
            .Select(year => OvertimeCalculator.BuildYearlySummary(
                    selectedRecords.Where(item => item.Date.Year == year))
                .FirstOrDefault() ?? new SummaryRow { Period = $"{year} 年" })
            .ToArray();
        ReplaceItems(_analyticsYearRecords, yearly);
        UpdateAnalytics(selectedRecords);
        if (_goalLoaded) UpdateGoalSummary();
    }

    private void UpdateAnalytics(IReadOnlyList<OvertimeRecord> records)
    {
        var currentYear = DateTime.Today.Year;
        var focusYear = _analyticsRangeYears == 1 ? _analyticsSelectedYear : currentYear;
        var focusYearRecords = records.Where(item => item.Date.Year == focusYear).ToArray();
        var overtimePay = focusYearRecords.Sum(item => item.OvertimePay);
        var mealCount = focusYearRecords.Sum(item => item.MealAllowanceCount);
        var mealAmount = focusYearRecords.Sum(item => item.MealAllowance);
        AnalyticsOvertimePayText.Text = $"¥ {overtimePay:N2}";
        AnalyticsMealCountText.Text = $"{mealCount} 次";
        AnalyticsMealAmountText.Text = $"¥ {mealAmount:N2}";
        AnalyticsTotalAmountText.Text = $"¥ {overtimePay + mealAmount:N2}";
        var selectedYears = GetSelectedAnalysisYears(currentYear);
        var rangeText = _analyticsRangeYears == 1
            ? $"{focusYear} 年单年度分析"
            : $"{selectedYears[0]}—{selectedYears[^1]} 年（近 {_analyticsRangeYears} 年）对比";
        AnalyticsRangeText.Text = $"{focusYear} 年 1—12 月趋势 · {rangeText} · 不受概览月份影响";
        YearPaySubtitleText.Text = _analyticsRangeYears == 1
            ? $"{focusYear} 年加班费规模"
            : $"近 {_analyticsRangeYears} 年加班费规模对比";

        var monthly = Enumerable.Range(1, 12)
            .Select(month =>
            {
                var monthRecords = focusYearRecords.Where(item => item.Date.Month == month).ToArray();
                return new
                {
                    Label = $"{month:00}月",
                    Pay = monthRecords.Sum(item => item.OvertimePay),
                    MealCount = monthRecords.Sum(item => item.MealAllowanceCount),
                    WorkdayHours = monthRecords.Where(item => item.Kind == DayKind.Workday).Sum(item => item.Hours),
                    WeekendHours = monthRecords.Where(item => item.Kind == DayKind.Weekend).Sum(item => item.Hours),
                    HolidayHours = monthRecords.Where(item => item.Kind == DayKind.Holiday).Sum(item => item.Hours),
                    TotalHours = monthRecords.Sum(item => item.Hours)
                };
            })
            .ToArray();
        var maximumMonthlyPay = monthly.Length == 0 ? 0 : monthly.Max(item => (double)item.Pay);
        var maximumMonthlyMealCount = monthly.Length == 0 ? 0 : monthly.Max(item => (double)item.MealCount);
        var maximumMonthlyHours = monthly.Length == 0 ? 0 : monthly.Max(item => item.TotalHours);
        MonthlyPayMealChart.ItemsSource = monthly.Select(item => new MonthlyAnalyticsPoint
        {
            Label = item.Label,
            PayText = $"¥{item.Pay:N0}",
            MealText = $"{item.MealCount} 次",
            PayHeight = ScaleChartValue((double)item.Pay, maximumMonthlyPay, 150),
            MealHeight = ScaleChartValue(item.MealCount, maximumMonthlyMealCount, 150)
        }).ToArray();
        MonthlyHoursChart.ItemsSource = monthly.Select(item => new MonthlyAnalyticsPoint
        {
            Label = item.Label,
            HoursText = FormatDuration(item.TotalHours),
            WorkdayHoursHeight = ScaleStackChartValue(item.WorkdayHours, maximumMonthlyHours, 150),
            WeekendHoursHeight = ScaleStackChartValue(item.WeekendHours, maximumMonthlyHours, 150),
            HolidayHoursHeight = ScaleStackChartValue(item.HolidayHours, maximumMonthlyHours, 150)
        }).ToArray();

        var years = _analyticsYearRecords.OrderBy(item => item.Period).ToArray();
        var maximumYearPay = years.Length == 0 ? 0 : years.Max(item => (double)item.OvertimePay);
        YearPayChart.ItemsSource = years.Select(item => new AnalyticsChartBar
        {
            Label = item.Period.Replace(" 年", string.Empty, StringComparison.Ordinal),
            DisplayValue = $"¥{item.OvertimePay:N0}",
            BarLength = ScaleChartValue((double)item.OvertimePay, maximumYearPay, 250)
        }).ToArray();
    }

    private IReadOnlyList<int> GetSelectedAnalysisYears(int currentYear)
    {
        if (_analyticsRangeYears == 1) return [_analyticsSelectedYear];
        return Enumerable.Range(currentYear - _analyticsRangeYears + 1, _analyticsRangeYears).ToArray();
    }

    private async void AnalyticsRangeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _analyticsRangeYears = AnalyticsRangeModeComboBox.SelectedIndex switch
        {
            0 => 1,
            2 => 5,
            3 => 10,
            _ => 3
        };
        if (AnalyticsYearComboBox is not null)
            AnalyticsYearComboBox.IsEnabled = _analyticsRangeYears == 1;
        if (_isLoaded && ContentTabs.SelectedIndex == 1) await EnsureAnalyticsDataAsync();
    }

    private async void AnalyticsYearComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnalyticsYearComboBox.SelectedItem is int year) _analyticsSelectedYear = year;
        if (_isLoaded && _analyticsRangeYears == 1 && ContentTabs.SelectedIndex == 1)
            await EnsureAnalyticsDataAsync();
    }

    private void MergeCurrentRangeIntoAnalysis(DateOnly startDate, DateOnly endDate)
    {
        if (_analysisLoadedOn is null || _analysisStartDate is null) return;

        _analysisAttendance = _analysisAttendance
            .Where(item => item.Date < startDate || item.Date > endDate)
            .Concat(_lastAttendance)
            .Where(item => item.Date >= _analysisStartDate.Value)
            .GroupBy(item => item.Date)
            .Select(group => group.Last())
            .OrderBy(item => item.Date)
            .ToArray();

        var mergedCalendar = _analysisCalendar.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var pair in _lastCalendar)
        {
            if (pair.Key >= _analysisStartDate.Value) mergedCalendar[pair.Key] = pair.Value;
        }
        _analysisCalendar = mergedCalendar;
    }

    private async Task EnsureGoalDataAsync()
    {
        if (_goalLoaded) return;
        _goalData = await _financialGoalService.LoadAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_goalData.StartDate > today || _goalData.StartDate < today.AddYears(-10))
        {
            _goalData.StartDate = new DateOnly(today.Year, 1, 1);
        }
        _goalLoaded = true;
        GoalNameTextBox.Text = _goalData.GoalName;
        GoalTargetAmountTextBox.Text = _goalData.TargetAmount > 0
            ? _goalData.TargetAmount.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        GoalStartDatePicker.SelectedDate = _goalData.StartDate.ToDateTime(TimeOnly.MinValue);
        GoalIncomeModeComboBox.SelectedIndex = _goalData.IncludeMealAllowance ? 1 : 0;
        ReplaceItems(_goalExpenses, _goalData.Expenses.OrderByDescending(item => item.Date));
        GoalStatusText.Text = "目标与消费记录已从本地加密档案载入";
        UpdateGoalSummary();
    }

    private async Task EnsureAnalyticsDataAsync()
    {
        try
        {
            await EnsureGoalDataAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法读取目标档案", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var today = DateOnly.FromDateTime(DateTime.Today);
        var comparisonStartYear = _analyticsRangeYears == 1
            ? _analyticsSelectedYear
            : today.Year - _analyticsRangeYears + 1;
        var comparisonStart = new DateOnly(comparisonStartYear, 1, 1);
        var requiredStart = _goalData.StartDate < comparisonStart ? _goalData.StartDate : comparisonStart;
        if (_analysisLoadedOn == today && _analysisStartDate is not null &&
            _analysisStartDate.Value <= requiredStart)
        {
            RecalculateAnalysisResults();
            return;
        }
        if (_isBusy) return;

        SetBusy(true, $"正在准备 {requiredStart:yyyy-MM-dd} 至今的独立分析数据…");
        try
        {
            var progress = new Progress<string>(message =>
            {
                HeaderStatusText.Text = message;
                RefreshOverlayStatusText.Text = message;
            });
            var calendarStart = new DateOnly(requiredStart.Year, 1, 1);
            var calendarEnd = new DateOnly(today.Year, 12, 31);
            var attendanceTask = _qhrClient.FetchAttendanceAsync(
                requiredStart,
                today,
                progress,
                refreshRecentMonths: false);
            var calendarTask = _holidayService.GetCalendarAsync(calendarStart, calendarEnd, false);
            await Task.WhenAll(attendanceTask, calendarTask);
            _analysisAttendance = await attendanceTask;
            _analysisCalendar = await calendarTask;
            _analysisStartDate = requiredStart;
            _analysisLoadedOn = today;
            RecalculateAnalysisResults();
            HeaderStatusText.Text = $"分析数据已更新 · {DateTime.Now:HH:mm}";
        }
        catch (Exception ex)
        {
            HeaderStatusText.Text = "分析数据更新失败";
            MessageBox.Show(this, GetFriendlyDataError(ex), "无法加载分析数据", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, HeaderStatusText.Text);
        }
    }

    private void UpdateGoalSummary()
    {
        if (!_goalLoaded) return;
        var scopedRecords = _analysisRecords
            .Where(item => item.Date >= _goalData.StartDate)
            .ToArray();
        var overtimePay = scopedRecords.Sum(item => item.OvertimePay);
        var mealAllowance = scopedRecords.Sum(item => item.MealAllowance);
        var earned = overtimePay + (_goalData.IncludeMealAllowance ? mealAllowance : 0);
        var expenses = _goalExpenses.Sum(item => item.Amount);
        var effective = earned - expenses;
        var target = _goalData.TargetAmount;
        var remaining = target > 0 ? Math.Max(0, target - effective) : 0;
        var progress = target <= 0 ? 0 : Math.Clamp((double)(effective / target * 100m), 0, 100);
        var totalHours = scopedRecords.Sum(item => item.Hours);
        var averageHourlyRate = totalHours > 0 ? (double)earned / totalHours : 0;

        GoalTitleText.Text = string.IsNullOrWhiteSpace(_goalData.GoalName)
            ? "目标进度"
            : $"{_goalData.GoalName} · 目标进度";
        var incomeModeText = _goalData.IncludeMealAllowance ? "加班费 + 餐补" : "仅加班费，不含餐补";
        GoalScopeText.Text = $"累计范围：{_goalData.StartDate:yyyy-MM-dd} 至今 · {incomeModeText}";
        GoalEarnedLabelText.Text = _goalData.IncludeMealAllowance ? "累计加班收入" : "累计加班费";
        GoalTargetSummaryText.Text = target > 0 ? $"¥ {target:N2}" : "未设置";
        GoalEarnedSummaryText.Text = $"¥ {earned:N2}";
        GoalExpensesSummaryText.Text = $"¥ {expenses:N2}";
        GoalEffectiveSummaryText.Text = $"¥ {effective:N2}";
        GoalProgressBar.Value = progress;
        GoalProgressText.Text = $"{progress:0.##}%";
        GoalRemainingText.Text = target <= 0
            ? "请先设置目标"
            : remaining <= 0
                ? "目标已达成"
                : $"还差 ¥ {remaining:N2}";
        GoalEstimatedHoursText.Text = target <= 0
            ? "填写目标名称、金额和收入计算方式后即可开始追踪"
            : remaining <= 0
                ? "后续加班收入将继续计入有效金额"
            : averageHourlyRate > 0
                ? $"按当前平均 ¥{averageHourlyRate:N2}/h，预计还需加班 {Math.Ceiling((double)remaining / averageHourlyRate):N0} 小时"
                : "已有加班数据后可估算剩余加班时长";
    }

    private async void SaveGoalButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureGoalDataAsync();
            var goalName = GoalNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(goalName)) throw new ArgumentException("请输入目标名称");
            var target = ReadDecimal(GoalTargetAmountTextBox.Text, "目标金额");
            if (target <= 0) throw new ArgumentException("目标金额必须大于 0");
            if (GoalStartDatePicker.SelectedDate is not DateTime selectedStart)
                throw new ArgumentException("请选择加班费起算日");
            var startDate = DateOnly.FromDateTime(selectedStart);
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (startDate > today) throw new ArgumentException("起算日不能晚于今天");
            if (startDate < today.AddYears(-10)) throw new ArgumentException("起算日最多可追溯 10 年");

            var requiresEarlierHistory = _analysisStartDate is null || startDate < _analysisStartDate.Value;
            _goalData.GoalName = goalName;
            _goalData.TargetAmount = target;
            _goalData.StartDate = startDate;
            _goalData.IncludeMealAllowance = GoalIncomeModeComboBox.SelectedIndex == 1;
            _goalData.Expenses = _goalExpenses.ToList();
            await _financialGoalService.SaveAsync(_goalData);
            GoalStatusText.Text = "目标已加密保存";
            if (requiresEarlierHistory)
            {
                _analysisLoadedOn = null;
                await EnsureAnalyticsDataAsync();
            }
            else
            {
                UpdateGoalSummary();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "目标设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AddExpenseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureGoalDataAsync();
            if (ExpenseDatePicker.SelectedDate is not DateTime selectedDate)
                throw new ArgumentException("请选择消费日期");
            var description = ExpenseDescriptionTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("请输入消费说明");
            var amount = ReadDecimal(ExpenseAmountTextBox.Text, "消费金额");
            if (amount <= 0) throw new ArgumentException("消费金额必须大于 0");

            _goalExpenses.Insert(0, new GoalExpense
            {
                Date = DateOnly.FromDateTime(selectedDate),
                Description = description,
                Amount = amount
            });
            _goalData.Expenses = _goalExpenses.ToList();
            await _financialGoalService.SaveAsync(_goalData);
            ExpenseDescriptionTextBox.Clear();
            ExpenseAmountTextBox.Clear();
            GoalStatusText.Text = "消费记录已加密保存";
            UpdateGoalSummary();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法添加消费", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeleteExpenseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GoalExpense expense }) return;
        var index = _goalExpenses.IndexOf(expense);
        try
        {
            _goalExpenses.Remove(expense);
            _goalData.Expenses = _goalExpenses.ToList();
            await _financialGoalService.SaveAsync(_goalData);
            GoalStatusText.Text = "消费记录已删除并加密保存";
            UpdateGoalSummary();
        }
        catch (Exception ex)
        {
            _goalExpenses.Insert(Math.Max(0, index), expense);
            _goalData.Expenses = _goalExpenses.ToList();
            MessageBox.Show(this, ex.Message, "无法删除消费", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static double ScaleChartValue(double value, double maximum, double availableLength)
    {
        if (value <= 0 || maximum <= 0) return 2;
        return Math.Max(7, value / maximum * availableLength);
    }

    private static double ScaleStackChartValue(double value, double maximum, double availableLength) =>
        value <= 0 || maximum <= 0 ? 0 : value / maximum * availableLength;

    private async void SyncHolidayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !TryGetDateRange(out var startDate, out var endDate)) return;
        SetBusy(true, "正在同步节假日…");
        try
        {
            var calendarStart = new DateOnly(startDate.Year, 1, 1);
            var calendarEnd = new DateOnly(endDate.Year, 12, 31);
            _lastCalendar = await _holidayService.GetCalendarAsync(calendarStart, calendarEnd, true);
            MergeCurrentRangeIntoAnalysis(startDate, endDate);
            RecalculateCurrentResults();
            if (_analysisLoadedOn is not null) RecalculateAnalysisResults();
            HeaderStatusText.Text = "节假日同步完成";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "节假日同步失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, HeaderStatusText.Text);
        }
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsStatusText.Text = string.Empty;
        try
        {
            ReadSettingsFromControls();
            await _settingsService.SaveAsync(_settings);
            RecalculateCurrentResults();
            if (_analysisLoadedOn is not null) RecalculateAnalysisResults();
            SettingsStatusText.Text = "已保存并重新计算";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings { LastUsername = _settings.LastUsername, QhrBaseUrl = _settings.QhrBaseUrl };
        CopySettings(defaults, _settings);
        LoadSettingsIntoControls();
        SettingsStatusText.Text = "已恢复默认值，点击保存后生效";
    }

    private void LoadSettingsIntoControls()
    {
        WorkdayRateTextBox.Text = _settings.WorkdayRate.ToString(CultureInfo.InvariantCulture);
        WeekendRateTextBox.Text = _settings.WeekendRate.ToString(CultureInfo.InvariantCulture);
        HolidayRateTextBox.Text = _settings.HolidayRate.ToString(CultureInfo.InvariantCulture);
        MealAllowanceTextBox.Text = _settings.MealAllowanceAmount.ToString(CultureInfo.InvariantCulture);
        FlexibleStartEarliestTextBox.Text = _settings.FlexibleWorkStartEarliest;
        FlexibleStartLatestTextBox.Text = _settings.FlexibleWorkStartLatest;
        StandardWorkSpanTextBox.Text = _settings.StandardWorkSpanHours.ToString(CultureInfo.InvariantCulture);
        WorkdayStartTextBox.Text = _settings.WorkdayOvertimeStart;
        MinimumHoursTextBox.Text = _settings.MinimumOvertimeHours.ToString(CultureInfo.InvariantCulture);
        RoundingMinutesTextBox.Text = _settings.RoundingMinutes.ToString(CultureInfo.InvariantCulture);
        WorkdayMealHoursTextBox.Text = _settings.WorkdayMealAllowanceMinimumHours.ToString(CultureInfo.InvariantCulture);
        NonWorkdayMealHoursTextBox.Text = _settings.NonWorkdayMealAllowanceMinimumHours.ToString(CultureInfo.InvariantCulture);
        DeductLeaveCheckBox.IsChecked = _settings.DeductLeaveFromOvertime;
        AutoSyncCheckBox.IsChecked = _settings.AutoSyncHolidays;
        HolidaySourceTextBox.Text = _settings.HolidaySourceUrl;
        HolidaySourceDisplayTextBox.Text = _settings.HolidaySourceUrl;
        UpdateManifestUrlTextBox.Text = _settings.UpdateManifestUrl;
    }

    private void ReadSettingsFromControls()
    {
        _settings.WorkdayRate = ReadDecimal(WorkdayRateTextBox.Text, "工作日费率");
        _settings.WeekendRate = ReadDecimal(WeekendRateTextBox.Text, "周末费率");
        _settings.HolidayRate = ReadDecimal(HolidayRateTextBox.Text, "节假日费率");
        _settings.MealAllowanceAmount = ReadDecimal(MealAllowanceTextBox.Text, "餐补金额");
        ValidateTime(FlexibleStartEarliestTextBox.Text, "最早上班时间");
        ValidateTime(FlexibleStartLatestTextBox.Text, "最晚上班时间");
        ValidateTime(WorkdayStartTextBox.Text, "工作日起算时间");
        var earliest = TimeOnly.ParseExact(FlexibleStartEarliestTextBox.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture);
        var latest = TimeOnly.ParseExact(FlexibleStartLatestTextBox.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture);
        if (latest < earliest) throw new ArgumentException("最晚上班时间不能早于最早上班时间");
        _settings.FlexibleWorkStartEarliest = FlexibleStartEarliestTextBox.Text.Trim();
        _settings.FlexibleWorkStartLatest = FlexibleStartLatestTextBox.Text.Trim();
        _settings.StandardWorkSpanHours = ReadDouble(StandardWorkSpanTextBox.Text, "标准在岗跨度", false);
        _settings.WorkdayOvertimeStart = WorkdayStartTextBox.Text.Trim();
        _settings.MinimumOvertimeHours = ReadDouble(MinimumHoursTextBox.Text, "最小加班小时", true);
        _settings.RoundingMinutes = ReadDouble(RoundingMinutesTextBox.Text, "取整分钟", false);
        _settings.WorkdayMealAllowanceMinimumHours = ReadDouble(WorkdayMealHoursTextBox.Text, "工作日餐补门槛", true);
        _settings.NonWorkdayMealAllowanceMinimumHours = ReadDouble(NonWorkdayMealHoursTextBox.Text, "周末/节假日餐补门槛", true);
        _settings.DeductLeaveFromOvertime = DeductLeaveCheckBox.IsChecked == true;
        _settings.AutoSyncHolidays = AutoSyncCheckBox.IsChecked == true;
        var source = HolidaySourceTextBox.Text.Trim();
        if (!source.Contains("{year}", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(source.Replace("{year}", "2026", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out _))
        {
            throw new ArgumentException("节假日地址必须是有效 URL，并包含 {year} 占位符");
        }
        _settings.HolidaySourceUrl = source;
        var updateSource = UpdateManifestUrlTextBox.Text.Trim();
        if (updateSource.Length > 0 &&
            (!Uri.TryCreate(updateSource, UriKind.Absolute, out var updateUri) ||
             updateUri.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("远程更新清单必须是有效的 HTTP 或 HTTPS 地址");
        }
        _settings.UpdateManifestUrl = updateSource;
    }

    private void CurrentMonthButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectedMonth(new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1));
    }

    private void PreviousMonthButton_Click(object sender, RoutedEventArgs e) =>
        SetSelectedMonth(GetSelectedMonth().AddMonths(-1));

    private void NextMonthButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigateNextMonth()) return;
        SetSelectedMonth(GetSelectedMonth().AddMonths(1));
    }

    private void DatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateMonthNavigationButtons();
        ScheduleAutoRefresh();
    }

    private void ScheduleAutoRefresh()
    {
        if (!_isLoaded || _isLoggingOut) return;
        _autoRefreshCancellation?.Cancel();
        _autoRefreshCancellation?.Dispose();
        _autoRefreshCancellation = new CancellationTokenSource();
        _ = RunAutoRefreshAsync(_autoRefreshCancellation.Token);
    }

    private async Task RunAutoRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            // DatePicker 和月份按钮会连续修改起止日期，防抖后只发起一次请求。
            await Task.Delay(350, cancellationToken);
            if (cancellationToken.IsCancellationRequested ||
                !TryGetDateRange(out _, out _, false)) return;
            if (_isBusy)
            {
                _refreshPending = true;
                return;
            }
            await LoadSelectedRangeAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 新日期替代旧日期时属于正常防抖流程。
        }
    }

    private async Task LoadSelectedRangeAsync(CancellationToken cancellationToken)
    {
        if (!TryGetDateRange(out var startDate, out var endDate, false)) return;
        HeaderStatusText.Text = "正在读取本地加密档案…";

        var cachedTask = _qhrClient.LoadCachedAttendanceAsync(startDate, endDate, cancellationToken);
        var calendarStart = new DateOnly(startDate.Year, 1, 1);
        var calendarEnd = new DateOnly(endDate.Year, 12, 31);
        var calendarTask = _holidayService.GetCalendarAsync(calendarStart, calendarEnd, false);
        await Task.WhenAll(cachedTask, calendarTask);
        cancellationToken.ThrowIfCancellationRequested();

        var cached = await cachedTask;
        if (!cached.HasAllRequestedMonths)
        {
            await RefreshDataAsync(
                false,
                refreshRecentMonths: false,
                busyStatus: $"{startDate:yyyy-MM} 本地无完整数据，正在从 QHR 补取…");
            return;
        }

        _lastAttendance = cached.Records;
        _lastCalendar = await calendarTask;
        MergeCurrentRangeIntoAnalysis(startDate, endDate);
        RecalculateCurrentResults();
        if (_analysisLoadedOn is not null) RecalculateAnalysisResults();
        HeaderStatusText.Text = $"已切换至 {GetSelectedMonth():yyyy 年 MM 月} · 使用本地数据，未联网";
    }

    private DateOnly GetSelectedMonth()
    {
        var selected = StartDatePicker.SelectedDate ?? DateTime.Today;
        return MonthNavigation.GetMonthStart(selected);
    }

    private void SetSelectedMonth(DateOnly month)
    {
        var range = MonthNavigation.GetRange(month, DateOnly.FromDateTime(DateTime.Today));
        StartDatePicker.SelectedDate = range.Start.ToDateTime(TimeOnly.MinValue);
        EndDatePicker.SelectedDate = range.End.ToDateTime(TimeOnly.MinValue);
        UpdateMonthNavigationButtons();
    }

    private bool CanNavigateNextMonth()
    {
        return StartDatePicker.SelectedDate is not null &&
               MonthNavigation.CanNavigateNext(
                   GetSelectedMonth(),
                   DateOnly.FromDateTime(DateTime.Today));
    }

    private void UpdateMonthNavigationButtons()
    {
        if (NextMonthButton is null) return;
        NextMonthButton.IsEnabled = !_isBusy && CanNavigateNextMonth();
    }

    private async void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } || !int.TryParse(value, out var index)) return;
        ContentTabs.SelectedIndex = index;
        SelectNavigation(index);
        if (index is 1 or 5) await EnsureAnalyticsDataAsync();
    }

    private void SelectNavigation(int index)
    {
        var buttons = new[]
        {
            OverviewNavButton,
            AnalyticsNavButton,
            SettingsNavButton,
            GoalNavButton
        };
        foreach (var button in buttons)
        {
            var isSelected = button.Tag?.ToString() == index.ToString(CultureInfo.InvariantCulture);
            button.Background = isSelected
                ? new SolidColorBrush(Color.FromArgb(32, 20, 115, 230))
                : Brushes.Transparent;
            button.Foreground = isSelected
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("TextBrush");
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        if (busy) DayDetailOverlay.Visibility = Visibility.Collapsed;
        SyncHolidayButton.IsEnabled = !busy;
        StartDatePicker.IsEnabled = !busy;
        EndDatePicker.IsEnabled = !busy;
        PreviousMonthButton.IsEnabled = !busy;
        CurrentMonthButton.IsEnabled = !busy;
        UpdateMonthNavigationButtons();
        HeaderStatusText.Text = status;
        RefreshOverlayStatusText.Text = status;
        RefreshOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
    }

    private bool TryGetDateRange(out DateOnly start, out DateOnly end, bool showMessage = true)
    {
        start = default;
        end = default;
        if (StartDatePicker.SelectedDate is not DateTime startDate || EndDatePicker.SelectedDate is not DateTime endDate)
        {
            if (showMessage) MessageBox.Show(this, "请选择开始和结束日期。", "日期不完整");
            return false;
        }
        start = DateOnly.FromDateTime(startDate);
        end = DateOnly.FromDateTime(endDate);
        if (end < start)
        {
            if (showMessage) MessageBox.Show(this, "结束日期不能早于开始日期。", "日期范围无效");
            return false;
        }
        if (end.DayNumber - start.DayNumber > 3660)
        {
            if (showMessage) MessageBox.Show(this, "单次查询范围不能超过 10 年。", "日期范围过大");
            return false;
        }
        return true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileButton.ContextMenu is null) return;
        ProfileButton.ContextMenu.PlacementTarget = ProfileButton;
        ProfileButton.ContextMenu.IsOpen = true;
    }

    private async void ProfileRefreshMenuItem_Click(object sender, RoutedEventArgs e) =>
        await RefreshDataAsync(false);

    private async void CheckForUpdatesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true, "正在检查软件更新…");
        try
        {
            var result = await new UpdateService().CheckAsync(_settings.UpdateManifestUrl);
            if (!result.HasUpdate)
            {
                HeaderStatusText.Text = $"当前已是最新版本 v{UpdateService.CurrentDisplayVersion}";
                MessageBox.Show(this,
                    $"当前版本 v{UpdateService.CurrentDisplayVersion} 已是最新版本。",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var message = $"发现新版本 v{result.LatestVersion}\n当前版本 v{UpdateService.CurrentDisplayVersion}";
            if (!string.IsNullOrWhiteSpace(result.ReleaseNotes))
                message += $"\n\n{result.ReleaseNotes}";
            if (string.IsNullOrWhiteSpace(result.DownloadUrl))
            {
                MessageBox.Show(this, message + "\n\n更新清单未提供下载地址。",
                    "发现新版本", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (MessageBox.Show(this, message + "\n\n是否打开下载页面？",
                         "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.DownloadUrl)
                {
                    UseShellExecute = true
                });
            }
            HeaderStatusText.Text = $"发现新版本 v{result.LatestVersion}";
        }
        catch (Exception ex)
        {
            HeaderStatusText.Text = "检查更新失败";
            MessageBox.Show(this, ex.GetBaseException().Message, "检查更新失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, HeaderStatusText.Text);
        }
    }

    private void OpenDiagnosticsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = new QhrCaptureWindow(_settings.QhrBaseUrl) { Owner = this };
        window.Show();
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        try
        {
            new WindowsCredentialService().Delete();
            _settings.AutoLoginEnabled = false;
            await _settingsService.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"清除自动登录凭据失败：{ex.Message}", "退出登录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _isLoggingOut = true;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var loginWindow = new LoginWindow(_settingsService);
        Application.Current.MainWindow = loginWindow;
        loginWindow.Show();
        Close();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _autoRefreshCancellation?.Cancel();
        _autoRefreshCancellation?.Dispose();
        Mouse.OverrideCursor = null;
        _holidayService.Dispose();
        _qhrClient.Dispose();
        if (!_isLoggingOut && Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown)
        {
            Application.Current.Shutdown();
        }
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private static decimal ReadDecimal(string text, string fieldName)
    {
        if ((!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) &&
             !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value)) || value < 0)
        {
            throw new ArgumentException($"{fieldName}必须是非负数字");
        }
        return value;
    }

    private static double ReadDouble(string text, string fieldName, bool allowZero)
    {
        if ((!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
             !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) ||
            (allowZero ? value < 0 : value <= 0))
        {
            throw new ArgumentException($"{fieldName}必须是{(allowZero ? "非负" : "正")}数字");
        }
        return value;
    }

    private static void ValidateTime(string text, string fieldName)
    {
        if (!TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new ArgumentException($"{fieldName}格式应为 HH:mm，例如 18:00");
        }
    }

    private static void CopySettings(AppSettings source, AppSettings target)
    {
        target.SettingsVersion = source.SettingsVersion;
        target.WorkdayRate = source.WorkdayRate;
        target.WeekendRate = source.WeekendRate;
        target.HolidayRate = source.HolidayRate;
        target.MealAllowanceAmount = source.MealAllowanceAmount;
        target.FlexibleWorkStartEarliest = source.FlexibleWorkStartEarliest;
        target.FlexibleWorkStartLatest = source.FlexibleWorkStartLatest;
        target.StandardWorkSpanHours = source.StandardWorkSpanHours;
        target.WorkdayOvertimeStart = source.WorkdayOvertimeStart;
        target.MinimumOvertimeHours = source.MinimumOvertimeHours;
        target.RoundingMinutes = source.RoundingMinutes;
        target.DeductLeaveFromOvertime = source.DeductLeaveFromOvertime;
        target.WorkdayMealAllowanceMinimumHours = source.WorkdayMealAllowanceMinimumHours;
        target.NonWorkdayMealAllowanceMinimumHours = source.NonWorkdayMealAllowanceMinimumHours;
        target.AutoSyncHolidays = source.AutoSyncHolidays;
        target.HolidaySourceUrl = source.HolidaySourceUrl;
        target.UpdateManifestUrl = source.UpdateManifestUrl;
    }

    private static string GetDisplayName(string username)
    {
        var value = username.Trim();
        if (value.Contains('@')) value = value.Split('@', 2)[0];
        if (value.Contains('\\')) value = value[(value.LastIndexOf('\\') + 1)..];
        return string.IsNullOrWhiteSpace(value) ? "QHR 用户" : value;
    }

    private static string GetAvatarText(string username)
    {
        var value = GetDisplayName(username);
        return value.Length == 0 ? "U" : value[..1].ToUpperInvariant();
    }

    private static string GetFriendlyDataError(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        if (message.Contains("MCHRID", StringComparison.OrdinalIgnoreCase) || message.Contains("401"))
        {
            return "QHR 会话已失效，请退出后重新登录。";
        }
        return message;
    }

    public sealed class HolidayDisplayRow
    {
        public HolidayDisplayRow(HolidayInfo info)
        {
            DateText = info.Date.ToString("yyyy-MM-dd");
            Name = info.Name;
            TypeText = info.IsOffDay ? "法定休息日" : "调休工作日";
        }

        public string DateText { get; }
        public string Name { get; }
        public string TypeText { get; }
    }
}
