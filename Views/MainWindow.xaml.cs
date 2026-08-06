using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using QHR.Models;
using QHR.Services;

namespace QHR.Views;

public partial class MainWindow : Window
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;

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
    private readonly ObservableCollection<FinancialGoalProfile> _goals = [];
    private readonly ObservableCollection<CompletedFinancialGoal> _completedGoals = [];
    private readonly ObservableCollection<GoalExpense> _selectedDayExpenses = [];
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
    private bool _isCompletingGoal;
    private int _analyticsRangeYears = 1;
    private int _analyticsSelectedYear = DateTime.Today.Year;
    private DateOnly? _openedDetailDate;
    private DateOnly _expenseSelectedMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateOnly? _openedExpenseDate;
    private string? _editingExpenseId;
    private string? _editingCompletedGoalId;
    private string? _editingGoalId;
    private decimal? _expenseCalculatorAccumulator;
    private string? _expenseCalculatorPendingOperator;
    private bool _expenseCalculatorEnteringNewValue = true;
    private bool _updatingOverviewMonthSelection;
    private DateOnly? _pendingOvertimePayCapEffectiveDate;
    private bool _overtimeCapScopeChosenSinceLoad;

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
        if (_qhrClient.IsOfflineMode)
        {
            HeaderStatusText.Text = "离线模式 · 正在读取本地加密档案";
            HeaderConnectionStatusText.Text = "离线";
            HeaderConnectionStatusText.Foreground = FindResource("WarningBrush") as Brush;
            ProfileConnectionStatusText.Text = "离线查看本地记录";
            ProfileConnectionStatusText.Foreground = FindResource("WarningBrush") as Brush;
        }
        VersionMenuItem.Header = $"版本 v{LocalUpdateService.CurrentDisplayVersion}";
        var today = DateTime.Today;
        OverviewYearComboBox.ItemsSource = Enumerable.Range(today.Year - 9, 10).Reverse().ToArray();
        OverviewMonthComboBox.ItemsSource = Enumerable.Range(1, 12).ToArray();
        OverviewYearComboBox.SelectedItem = today.Year;
        OverviewMonthComboBox.SelectedItem = today.Month;
        DailyDataGrid.ItemsSource = _dailyRecords;
        MonthlyDataGrid.ItemsSource = _monthlyRecords;
        YearlyDataGrid.ItemsSource = _yearlyRecords;
        AnalyticsYearDataGrid.ItemsSource = _analyticsYearRecords;
        AnalyticsYearComboBox.ItemsSource = Enumerable.Range(today.Year - 9, 10).Reverse().ToArray();
        AnalyticsYearComboBox.SelectedItem = today.Year;
        ExpenseDayItemsControl.ItemsSource = _selectedDayExpenses;
        GoalsItemsControl.ItemsSource = _goals;
        CompletedGoalsItemsControl.ItemsSource = _completedGoals;
        DayEvidenceItemsControl.ItemsSource = _dayEvidenceImages;
        LoadSettingsIntoControls();
        UpdateMonthNavigationButtons();
        SelectNavigation(0);

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var preference = DwmWindowCornerRound;
            _ = DwmSetWindowAttribute(
                handle,
                DwmWindowCornerPreference,
                ref preference,
                Marshal.SizeOf<int>());
        }
        catch (DllNotFoundException)
        {
            // Older Windows builds do not expose the DWM corner preference API.
        }
        catch (EntryPointNotFoundException)
        {
            // Keep the WindowChrome radius as a visual fallback.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

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
            HeaderStatusText.Text = _qhrClient.IsOfflineMode
                ? $"离线查看 · {_qhrClient.LastCacheStatus}"
                : $"更新完成 · {DateTime.Now:HH:mm} · {_qhrClient.LastCacheStatus}";
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

        var hours = calculated.Sum(item => item.ActualHours);
        var amount = calculated.Sum(item => item.OvertimePay);
        var grossOvertimePay = OvertimeCalculator.CalculateGrossOvertimePay(calculated);
        var capDeductedPay = calculated.Sum(item => item.CapDeductedPay);
        var capExcludedHours = calculated.Sum(item => item.CapExcludedHours);
        var mealAllowanceCount = calculated.Sum(item => item.MealAllowanceCount);
        var mealAllowanceAmount = calculated.Sum(item => item.MealAllowance);
        var days = calculated.Count(item => item.ActualHours > 0);
        TotalHoursText.Text = hours.ToString("0.##");
        GrossOvertimePayText.Text = $"¥ {grossOvertimePay:N2}";
        TotalAmountText.Text = $"¥ {amount:N2}";
        var capNotEffectiveForRange = _settings.EnableOvertimePayCap &&
                                      _settings.OvertimePayCapEffectiveDate is DateOnly capEffectiveDate &&
                                      calculated.All(item => item.Date < capEffectiveDate);
        CapImpactText.Text = capDeductedPay > 0
            ? $"封顶无效 {FormatDuration(capExcludedHours)} · 少计 ¥ {capDeductedPay:N2}"
            : capNotEffectiveForRange
                ? $"封顶自 {_settings.OvertimePayCapEffectiveDate:yyyy-MM-dd} 起生效 · 本月不适用"
            : _settings.EnableOvertimePayCap
                ? $"月度封顶 ¥ {_settings.MonthlyOvertimePayCap:N2} · 本月未超额" +
                  (_settings.ExcludeHolidayPayFromCap ? " · 节假日不占额度" : string.Empty)
                : "未启用加班费封顶";
        OvertimeDaysText.Text = days.ToString(CultureInfo.InvariantCulture);
        AverageHoursText.Text = $"日均 {(days == 0 ? 0 : hours / days):0.##} 小时";
        MealAllowanceCountText.Text = mealAllowanceCount.ToString(CultureInfo.InvariantCulture);
        MealAllowanceAmountText.Text = $"餐补合计 ¥ {mealAllowanceAmount:N2}";
        if (TryGetDateRange(out var startDate, out var endDate, false))
        {
            RangeText.Text = $"{startDate:yyyy 年 MM 月}";
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
                if (holiday.IsOffDay)
                {
                    isHoliday = ChineseStatutoryHoliday.IsStatutory(holiday);
                    kindText = isHoliday ? holiday.Name : $"{holiday.Name}休息";
                }
            }
            else if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                kindText = "周末";
            }

            var hasOvertime = record is not null && record.ActualHours > 0.0001;
            var hasLeave = record is not null && !string.IsNullOrWhiteSpace(record.LeaveSummaryText);
            cells.Add(new CalendarDayCell
            {
                Date = date,
                DayText = day.ToString(CultureInfo.InvariantCulture),
                KindText = kindText,
                HoursText = hasOvertime ? $"加班 {record!.ActualDurationText}" : string.Empty,
                AmountText = hasOvertime ? $"加班费 ¥{record!.OvertimePay:N2}" : string.Empty,
                LeaveText = hasLeave ? record!.LeaveSummaryText : string.Empty,
                HasOvertime = hasOvertime,
                HasLeave = hasLeave,
                HasPersonalLeave = record?.PersonalLeaveHours > 0,
                IsToday = date == today,
                IsHoliday = isHoliday || record?.Kind == DayKind.Holiday
            });
        }

        OvertimeCalendar.ItemsSource = cells;
        var monthRecords = recordsByDate.Values.ToArray();
        CalendarMonthSummaryText.Text = $"{month:yyyy 年 MM 月} · " +
                                        $"加班 {FormatDuration(monthRecords.Sum(item => item.ActualHours))} · " +
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
        var leaveSummary = record is null || string.IsNullOrWhiteSpace(record.LeaveSummaryText)
            ? "无当天请假"
            : record.LeaveSummaryText;
        DayDetailLeaveText.Text = record is { LeaveDeductedHours: > 0 }
            ? $"{leaveSummary} · 月度抵扣 {record.LeaveDeductedDurationText}"
            : record is null || string.IsNullOrWhiteSpace(record.LeaveSummaryText)
                ? "无"
                : $"{leaveSummary} · 不抵扣";
        DayDetailActualText.Text = record?.PaidHoursDurationText ?? "0h00m";
        DayDetailCapText.Text = record is null
            ? "0h00m / ¥ 0.00"
            : $"{record.CapExcludedDurationText} / ¥ {record.CapDeductedPay:N2}";
        DayDetailMealCountText.Text = $"{record?.MealAllowanceCount ?? 0} 次";
        DayDetailOvertimePayText.Text = $"¥ {record?.GrossOvertimePay ?? 0m:N2} / ¥ {record?.OvertimePay ?? 0m:N2}";
        DayDetailMealPayText.Text = $"¥ {record?.MealAllowance ?? 0m:N2}";
        DayDetailTotalText.Text = $"¥ {record?.GrossAmount ?? 0m:N2} / ¥ {record?.Amount ?? 0m:N2}";
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

    private async void ExportYearEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var year = GetSelectedMonth().Year;
        var dialog = new SaveFileDialog
        {
            Title = $"导出 {year} 年加班资料",
            Filter = "ZIP 压缩包|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"QHR-年度加班资料-{year}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusy(true, $"正在整理 {year} 年加班资料…");
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var startDate = new DateOnly(year, 1, 1);
            var endDate = year == today.Year ? today : new DateOnly(year, 12, 31);
            var progress = new Progress<string>(message =>
            {
                HeaderStatusText.Text = message;
                RefreshOverlayStatusText.Text = message;
            });

            var cached = await _qhrClient.LoadCachedAttendanceAsync(startDate, endDate);
            var attendance = cached.HasAllRequestedMonths
                ? cached.Records
                : await _qhrClient.FetchAttendanceAsync(
                    startDate,
                    endDate,
                    progress,
                    refreshRecentMonths: false);
            var calendar = await _holidayService.GetCalendarAsync(startDate, endDate, false);
            var calculated = _calculator.Calculate(attendance, calendar, _settings)
                .OrderBy(item => item.Date)
                .ToArray();
            var recordsByDate = calculated
                .GroupBy(item => item.Date)
                .ToDictionary(group => group.Key, group => group.Last());
            var detailMap = new Dictionary<DateOnly, string>();
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                recordsByDate.TryGetValue(date, out var record);
                detailMap[date] = BuildDayExportDetails(date, record, calendar);
            }

            var monthlyCsv = Enumerable.Range(1, endDate.Month)
                .ToDictionary(
                    month => month,
                    month => BuildMonthExportCsv(calculated
                        .Where(item => item.Date.Month == month)
                        .ToArray()));
            await _dailyEvidenceService.ExportYearAsync(
                year,
                dialog.FileName,
                BuildMonthExportCsv(calculated),
                monthlyCsv,
                detailMap);
            HeaderStatusText.Text = $"{year} 年加班资料已导出";
            MessageBox.Show(this,
                $"全年资料已导出：\n{dialog.FileName}\n\n" +
                $"ZIP 内按 {year}年 / 月份 / 日期 分层，每天包含加班数据和加班证据目录。",
                "导出完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            HeaderStatusText.Text = "年度资料导出失败";
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, HeaderStatusText.Text);
        }
    }

    private string BuildDayExportDetails(DateOnly date) =>
        BuildDayExportDetails(
            date,
            _currentCalculatedRecords.LastOrDefault(item => item.Date == date),
            _lastCalendar);

    private static string BuildDayExportDetails(
        DateOnly date,
        OvertimeRecord? record,
        IReadOnlyDictionary<DateOnly, HolidayInfo> calendar)
    {
        var builder = new StringBuilder();
        builder.AppendLine("QHR 加班助手 · 当天计算明细");
        builder.AppendLine($"日期：{date:yyyy-MM-dd} {GetWeekText(date.DayOfWeek)}");
        builder.AppendLine($"日期类型：{record?.DateTypeText ?? GetDateTypeText(date, calendar)}");
        builder.AppendLine($"首卡 / 末卡：{record?.ClockInText ?? "--:--"} / {record?.ClockOutText ?? "--:--"}");
        builder.AppendLine($"原始加班：{record?.GrossDurationText ?? "0h00m"}");
        builder.AppendLine($"延时工时抵扣：{record?.DelayDeductedDurationText ?? "0h00m"}");
        builder.AppendLine($"实际加班：{record?.ActualDurationText ?? "0h00m"}");
        builder.AppendLine($"请假：{(string.IsNullOrWhiteSpace(record?.LeaveSummaryText) ? "无" : record.LeaveSummaryText)}");
        builder.AppendLine($"月度事假抵扣：{record?.LeaveDeductedDurationText ?? "0h00m"}");
        builder.AppendLine($"封顶前加班：{record?.HoursDurationText ?? "0h00m"}");
        builder.AppendLine($"封顶无效：{record?.CapExcludedDurationText ?? "0h00m"} / ¥ {record?.CapDeductedPay ?? 0m:N2}");
        builder.AppendLine($"有效计费加班：{record?.PaidHoursDurationText ?? "0h00m"}");
        builder.AppendLine($"总加班费：¥ {record?.GrossOvertimePay ?? 0m:N2}");
        builder.AppendLine($"应计加班费（封顶前）：¥ {record?.UncappedOvertimePay ?? 0m:N2}");
        builder.AppendLine($"实际加班费：¥ {record?.OvertimePay ?? 0m:N2}");
        builder.AppendLine($"餐补：{record?.MealAllowanceCount ?? 0} 次 / ¥ {record?.MealAllowance ?? 0m:N2}");
        builder.AppendLine($"当天总合计 / 实际合计：¥ {record?.GrossAmount ?? 0m:N2} / ¥ {record?.Amount ?? 0m:N2}");
        builder.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        return builder.ToString().TrimEnd();
    }

    private static string BuildMonthExportCsv(IReadOnlyList<OvertimeRecord> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("日期,日期类型,首卡,末卡,原始加班,延时工时抵扣,实际加班,请假类型与时长,月度事假抵扣,封顶前加班,封顶无效加班,有效计费加班,总加班费,应计加班费,封顶少计,实际加班费,餐补次数,餐补金额,总合计,实际合计");
        foreach (var record in records)
        {
            builder.AppendLine(string.Join(",",
                EscapeCsv(record.DateText),
                EscapeCsv(record.DateTypeText),
                EscapeCsv(record.ClockInText),
                EscapeCsv(record.ClockOutText),
                EscapeCsv(record.GrossDurationText),
                EscapeCsv(record.DelayDeductedDurationText),
                EscapeCsv(record.ActualDurationText),
                EscapeCsv(record.LeaveSummaryText),
                EscapeCsv(record.LeaveDeductedDurationText),
                EscapeCsv(record.HoursDurationText),
                EscapeCsv(record.CapExcludedDurationText),
                EscapeCsv(record.PaidHoursDurationText),
                record.GrossOvertimePay.ToString("0.00", CultureInfo.InvariantCulture),
                record.UncappedOvertimePay.ToString("0.00", CultureInfo.InvariantCulture),
                record.CapDeductedPay.ToString("0.00", CultureInfo.InvariantCulture),
                record.OvertimePay.ToString("0.00", CultureInfo.InvariantCulture),
                record.MealAllowanceCount.ToString(CultureInfo.InvariantCulture),
                record.MealAllowance.ToString("0.00", CultureInfo.InvariantCulture),
                record.GrossAmount.ToString("0.00", CultureInfo.InvariantCulture),
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

    private string GetDateTypeText(DateOnly date) => GetDateTypeText(date, _lastCalendar);

    private static string GetDateTypeText(
        DateOnly date,
        IReadOnlyDictionary<DateOnly, HolidayInfo> calendar)
    {
        if (calendar.TryGetValue(date, out var holiday))
        {
            if (!holiday.IsOffDay) return $"调休工作日 · {holiday.Name}";
            return ChineseStatutoryHoliday.IsStatutory(holiday)
                ? $"法定节假日 · {holiday.Name}"
                : $"周末 · {holiday.Name}假期";
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
        var totalMinutes = (int)Math.Round(hours * 60d, MidpointRounding.AwayFromZero);
        var sign = totalMinutes < 0 ? "-" : string.Empty;
        var absoluteMinutes = Math.Abs(totalMinutes);
        return $"{sign}{absoluteMinutes / 60}h{absoluteMinutes % 60:00}m";
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
        var grossOvertimePay = focusYearRecords.Sum(item => item.GrossOvertimePay);
        var capExcludedHours = focusYearRecords.Sum(item => item.CapExcludedHours);
        var capDeductedPay = focusYearRecords.Sum(item => item.CapDeductedPay);
        var mealCount = focusYearRecords.Sum(item => item.MealAllowanceCount);
        var mealAmount = focusYearRecords.Sum(item => item.MealAllowance);
        AnalyticsOvertimePayText.Text = $"¥ {overtimePay:N2}";
        AnalyticsGrossOvertimePayText.Text = $"¥ {grossOvertimePay:N2}";
        AnalyticsCapExcludedHoursText.Text = FormatDuration(capExcludedHours);
        AnalyticsCapDeductedPayText.Text = $"少计 ¥ {capDeductedPay:N2}";
        AnalyticsMealCountText.Text = $"{mealCount} 次";
        AnalyticsMealAmountText.Text = $"¥ {mealAmount:N2}";
        AnalyticsTotalAmountText.Text = $"¥ {overtimePay + mealAmount:N2}";
        var selectedYears = GetSelectedAnalysisYears(currentYear);
        var rangeText = _analyticsRangeYears == 1
            ? $"{focusYear} 年单年度分析"
            : $"{selectedYears[0]}—{selectedYears[^1]} 年（近 {_analyticsRangeYears} 年）对比";
        AnalyticsRangeText.Text = $"{focusYear} 年 1—12 月趋势 · {rangeText} · 封顶按月计算 · 不受概览月份影响";
        YearPaySubtitleText.Text = _analyticsRangeYears == 1
            ? $"{focusYear} 年实际加班费规模"
            : $"近 {_analyticsRangeYears} 年实际加班费规模对比";

        var monthly = Enumerable.Range(1, 12)
            .Select(month =>
            {
                var monthRecords = focusYearRecords.Where(item => item.Date.Month == month).ToArray();
                return new
                {
                    Label = $"{month:00}月",
                    Pay = monthRecords.Sum(item => item.OvertimePay),
                    MealCount = monthRecords.Sum(item => item.MealAllowanceCount),
                    WorkdayHours = monthRecords.Where(item => item.Kind == DayKind.Workday).Sum(item => item.ActualHours),
                    WeekendHours = monthRecords.Where(item => item.Kind == DayKind.Weekend).Sum(item => item.ActualHours),
                    HolidayHours = monthRecords.Where(item => item.Kind == DayKind.Holiday).Sum(item => item.ActualHours),
                    CapExcludedHours = monthRecords.Sum(item => item.CapExcludedHours),
                    TotalHours = monthRecords.Sum(item => item.ActualHours)
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
            HoursText = item.CapExcludedHours > 0
                ? $"{FormatDuration(item.TotalHours)} / 无效 {FormatDuration(item.CapExcludedHours)}"
                : FormatDuration(item.TotalHours),
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

    private void HorizontalChartScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer chartScroller) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            chartScroller.ScrollToHorizontalOffset(chartScroller.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }

        var pageScroller = FindVisualAncestor<ScrollViewer>(VisualTreeHelper.GetParent(chartScroller));
        if (pageScroller is null) return;
        pageScroller.ScrollToVerticalOffset(pageScroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
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
        RefreshGoalProfiles();
        _editingGoalId = _goalData.ActiveGoalId;
        LoadGoalEditor(_goalData.ActiveGoal);
        ReplaceItems(_goalExpenses, _goalData.Expenses.OrderByDescending(item => item.Date));
        ReplaceItems(_completedGoals, _goalData.CompletedGoals.OrderByDescending(item => item.CompletedDate));
        UpdateCompletedGoalsVisibility();
        GoalStatusText.Text = "目标与消费记录已从本地加密档案载入";
        UpdateGoalSummary();
    }

    private void RefreshGoalProfiles()
    {
        foreach (var goal in _goalData.Goals)
        {
            goal.IsActive = goal.Id == _goalData.ActiveGoalId;
        }
        ReplaceItems(_goals, _goalData.Goals
            .OrderByDescending(item => item.IsActive)
            .ThenByDescending(item => item.CreatedAt));
        GoalsSummaryText.Text = _goals.Count == 0
            ? "暂无目标，请在下方新增"
            : $"共 {_goals.Count} 个目标 · {(_goalData.ActiveGoal is null ? "当前没有生效目标" : $"当前：{_goalData.ActiveGoal.GoalName}")}";
    }

    private void LoadGoalEditor(FinancialGoalProfile? goal)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        GoalEditorTitleText.Text = goal is null ? "新增目标" : $"编辑目标 · {goal.GoalName}";
        GoalNameTextBox.Text = goal?.GoalName ?? string.Empty;
        GoalTargetAmountTextBox.Text = goal is { TargetAmount: > 0 }
            ? goal.TargetAmount.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        GoalStartDatePicker.SelectedDate = (goal?.StartDate ?? today).ToDateTime(TimeOnly.MinValue);
        GoalIncomeModeComboBox.SelectedIndex = goal?.IncludeMealAllowance == true ? 1 : 0;
        DeleteCurrentGoalButton.IsEnabled = goal is not null;
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
        var totalHours = scopedRecords.Sum(item => item.ActualHours);
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
        DeleteCurrentGoalButton.IsEnabled =
            _goalData.Goals.Any(item => item.Id == _editingGoalId);
        UpdateExpenseCalendar();

        if (target > 0 && !string.IsNullOrWhiteSpace(_goalData.GoalName) &&
            !_goalData.SuppressAutomaticCompletion &&
            effective >= target && !_isCompletingGoal)
        {
            var completedDate = DateOnly.FromDateTime(DateTime.Today);
            var completedGoal = new CompletedFinancialGoal
            {
                GoalName = _goalData.GoalName,
                TargetAmount = target,
                StartDate = _goalData.StartDate,
                CompletedDate = completedDate,
                DurationDays = Math.Max(1, completedDate.DayNumber - _goalData.StartDate.DayNumber + 1),
                IncludedMealAllowance = _goalData.IncludeMealAllowance,
                OvertimeHours = totalHours,
                OvertimeDays = scopedRecords.Count(item => item.ActualHours > 0),
                WorkdayHours = scopedRecords.Where(item => item.Kind == DayKind.Workday).Sum(item => item.ActualHours),
                WeekendHours = scopedRecords.Where(item => item.Kind == DayKind.Weekend).Sum(item => item.ActualHours),
                HolidayHours = scopedRecords.Where(item => item.Kind == DayKind.Holiday).Sum(item => item.ActualHours),
                OvertimePay = overtimePay,
                MealAllowance = mealAllowance,
                EarnedAmount = earned,
                ExpenseAmount = expenses,
                EffectiveAmount = effective,
                Expenses = _goalExpenses.ToList()
            };
            _ = CompleteGoalAsync(completedGoal);
        }
    }

    private async Task CompleteGoalAsync(CompletedFinancialGoal completedGoal)
    {
        if (_isCompletingGoal) return;
        _isCompletingGoal = true;
        try
        {
            var completedProfileId = _goalData.ActiveGoalId;
            var completedProfile = _goalData.ActiveGoal;
            if (completedProfile is not null)
            {
                CloseOpenGoalPeriod(completedProfile, DateTimeOffset.Now, "目标已完成");
            }
            var history = _goalData.CompletedGoals
                .Where(item => item.Id != completedGoal.Id)
                .Prepend(completedGoal)
                .OrderByDescending(item => item.CompletedDate)
                .ToList();
            var today = DateOnly.FromDateTime(DateTime.Today);
            var nextGoalData = new FinancialGoalData
            {
                Version = 5,
                ActiveGoalId = null,
                Goals = _goalData.Goals.Where(item => item.Id != completedProfileId).ToList(),
                GoalName = string.Empty,
                TargetAmount = 0,
                StartDate = new DateOnly(today.Year, 1, 1),
                IncludeMealAllowance = false,
                SuppressAutomaticCompletion = false,
                Expenses = [],
                CompletedGoals = history
            };

            await _financialGoalService.SaveAsync(nextGoalData);
            _goalData = nextGoalData;
            _goalExpenses.Clear();
            RefreshGoalProfiles();
            ReplaceItems(_completedGoals, history);
            _editingGoalId = null;
            LoadGoalEditor(null);
            GoalStatusText.Text = $"“{completedGoal.GoalName}”已完成并移入历史目标";
            UpdateCompletedGoalsVisibility();
            UpdateGoalSummary();

            while (_isBusy && !_isLoggingOut) await Task.Delay(100);
            if (!_isLoggingOut) ShowGoalCelebration(completedGoal);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "目标归档失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isCompletingGoal = false;
        }
    }

    private void UpdateCompletedGoalsVisibility()
    {
        CompletedGoalsBorder.Visibility = _completedGoals.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        CompletedGoalsSummaryText.Text = _completedGoals.Count == 0
            ? "达成的目标会自动归档到这里"
            : $"共完成 {_completedGoals.Count} 个目标，最近一次完成于 {_completedGoals[0].CompletedDate:yyyy-MM-dd}";
    }

    private void ShowGoalCelebration(CompletedFinancialGoal completedGoal)
    {
        GoalCelebrationNameText.Text = $"“{completedGoal.GoalName}”已完成并安全归档";
        GoalCelebrationAmountText.Text = completedGoal.TargetAmountText;
        GoalCelebrationDurationText.Text = completedGoal.DurationText;
        GoalCelebrationOvertimeText.Text = completedGoal.OvertimeHoursText;
        GoalCelebrationSummaryText.Text =
            $"加班 {completedGoal.OvertimeDays} 天 · 加班费 {completedGoal.OvertimePayText} · 餐补 {completedGoal.MealAllowanceText}";
        GoalCelebrationOverlay.Visibility = Visibility.Visible;
        GoalCelebrationCard.Opacity = 0;
        GoalCelebrationScale.ScaleX = 0.75;
        GoalCelebrationScale.ScaleY = 0.75;

        var easing = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
        GoalCelebrationCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)));
        GoalCelebrationScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.75, 1, TimeSpan.FromMilliseconds(560)) { EasingFunction = easing });
        GoalCelebrationScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.75, 1, TimeSpan.FromMilliseconds(560)) { EasingFunction = easing });
        GoalCelebrationRoot.Dispatcher.BeginInvoke(SpawnGoalCelebrationConfetti, DispatcherPriority.Loaded);
    }

    private void SpawnGoalCelebrationConfetti()
    {
        GoalCelebrationConfettiCanvas.Children.Clear();
        var width = Math.Max(700, GoalCelebrationRoot.ActualWidth);
        var height = Math.Max(600, GoalCelebrationRoot.ActualHeight);
        var colors = new[] { "#1473E6", "#199C6C", "#F4A12C", "#8A4BD6", "#E45C78" };

        for (var index = 0; index < 44; index++)
        {
            var piece = new Rectangle
            {
                Width = Random.Shared.Next(5, 12),
                Height = Random.Shared.Next(10, 23),
                RadiusX = 2,
                RadiusY = 2,
                Fill = (Brush)new BrushConverter().ConvertFromString(colors[index % colors.Length])!,
                RenderTransform = new RotateTransform(Random.Shared.Next(0, 180)),
                Opacity = 0.95
            };
            var startLeft = Random.Shared.NextDouble() * width;
            var startTop = -Random.Shared.Next(20, 240);
            Canvas.SetLeft(piece, startLeft);
            Canvas.SetTop(piece, startTop);
            GoalCelebrationConfettiCanvas.Children.Add(piece);

            var duration = TimeSpan.FromMilliseconds(Random.Shared.Next(2100, 3900));
            var delay = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 650));
            piece.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(startTop, height + 40, duration)
            {
                BeginTime = delay,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop
            });
            piece.BeginAnimation(OpacityProperty, new DoubleAnimation(0.95, 0.15, duration)
            {
                BeginTime = delay,
                FillBehavior = FillBehavior.Stop
            });
        }
    }

    private void ViewCompletedGoalsButton_Click(object sender, RoutedEventArgs e)
    {
        GoalCelebrationOverlay.Visibility = Visibility.Collapsed;
        GoalCelebrationConfettiCanvas.Children.Clear();
        CompletedGoalsBorder.BringIntoView();
    }

    private void EditCompletedGoalButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CompletedFinancialGoal goal }) return;
        _editingCompletedGoalId = goal.Id;
        CompletedGoalNameTextBox.Text = goal.GoalName;
        CompletedGoalTargetAmountTextBox.Text = goal.TargetAmount.ToString("0.##", CultureInfo.InvariantCulture);
        CompletedGoalStartDatePicker.SelectedDate = goal.StartDate.ToDateTime(TimeOnly.MinValue);
        CompletedGoalCompletedDatePicker.SelectedDate = goal.CompletedDate.ToDateTime(TimeOnly.MinValue);
        CompletedGoalIncomeModeComboBox.SelectedIndex = goal.IncludedMealAllowance ? 1 : 0;
        CompletedGoalEditStatusText.Text = "归档的加班、餐补和消费快照不会被删除";
        CompletedGoalEditOverlay.Visibility = Visibility.Visible;
        CompletedGoalNameTextBox.Focus();
    }

    private void CloseCompletedGoalEditButton_Click(object sender, RoutedEventArgs e) =>
        CloseCompletedGoalEditor();

    private void CloseCompletedGoalEditor()
    {
        CompletedGoalEditOverlay.Visibility = Visibility.Collapsed;
        _editingCompletedGoalId = null;
        CompletedGoalEditStatusText.Text = string.Empty;
    }

    private async void SaveCompletedGoalEditButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureGoalDataAsync();
            var source = _goalData.CompletedGoals.FirstOrDefault(item => item.Id == _editingCompletedGoalId)
                         ?? throw new InvalidOperationException("该历史目标已不存在");
            var name = CompletedGoalNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请输入目标名称");
            var target = ReadDecimal(CompletedGoalTargetAmountTextBox.Text, "目标金额");
            if (target <= 0) throw new ArgumentException("目标金额必须大于 0");
            if (CompletedGoalStartDatePicker.SelectedDate is not DateTime startValue)
                throw new ArgumentException("请选择起算日");
            if (CompletedGoalCompletedDatePicker.SelectedDate is not DateTime completedValue)
                throw new ArgumentException("请选择完成日");
            var startDate = DateOnly.FromDateTime(startValue);
            var completedDate = DateOnly.FromDateTime(completedValue);
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (completedDate < startDate) throw new ArgumentException("完成日不能早于起算日");
            if (completedDate > today) throw new ArgumentException("完成日不能晚于今天");

            var includesMealAllowance = CompletedGoalIncomeModeComboBox.SelectedIndex == 1;
            var earnedAmount = source.OvertimePay + (includesMealAllowance ? source.MealAllowance : 0);
            var replacement = new CompletedFinancialGoal
            {
                Id = source.Id,
                GoalName = name,
                TargetAmount = decimal.Round(target, 2, MidpointRounding.AwayFromZero),
                StartDate = startDate,
                CompletedDate = completedDate,
                DurationDays = Math.Max(1, completedDate.DayNumber - startDate.DayNumber + 1),
                IncludedMealAllowance = includesMealAllowance,
                OvertimeHours = source.OvertimeHours,
                OvertimeDays = source.OvertimeDays,
                WorkdayHours = source.WorkdayHours,
                WeekendHours = source.WeekendHours,
                HolidayHours = source.HolidayHours,
                OvertimePay = source.OvertimePay,
                MealAllowance = source.MealAllowance,
                EarnedAmount = earnedAmount,
                ExpenseAmount = source.ExpenseAmount,
                EffectiveAmount = earnedAmount - source.ExpenseAmount,
                Expenses = source.Expenses.ToList()
            };
            var history = _goalData.CompletedGoals
                .Select(item => item.Id == source.Id ? replacement : item)
                .OrderByDescending(item => item.CompletedDate)
                .ToList();
            var previousHistory = _goalData.CompletedGoals;
            _goalData.CompletedGoals = history;
            _goalData.Version = 5;
            try
            {
                await _financialGoalService.SaveAsync(_goalData);
            }
            catch
            {
                _goalData.CompletedGoals = previousHistory;
                throw;
            }

            ReplaceItems(_completedGoals, history);
            UpdateCompletedGoalsVisibility();
            GoalStatusText.Text = $"已修改历史目标“{name}”";
            CloseCompletedGoalEditor();
        }
        catch (Exception ex)
        {
            CompletedGoalEditStatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "无法修改历史目标", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RestoreCompletedGoalButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CompletedFinancialGoal goal }) return;
        try
        {
            await EnsureGoalDataAsync();
            if (_goalData.ActiveGoal is not null)
            {
                MessageBox.Show(this, "当前已有目标，请先完成或清理当前目标后再恢复历史目标。",
                    "无法设为当前目标", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this,
                    $"确定将“{goal.GoalName}”从历史记录恢复为当前目标吗？\n\n归档时的 {goal.Expenses.Count} 笔消费也会一起恢复。",
                    "恢复当前目标", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var history = _goalData.CompletedGoals
                .Where(item => item.Id != goal.Id)
                .OrderByDescending(item => item.CompletedDate)
                .ToList();
            var restoredExpenses = goal.Expenses
                .Concat(_goalData.Expenses)
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderByDescending(item => item.Date)
                .ToList();
            var restoredProfile = new FinancialGoalProfile
            {
                GoalName = goal.GoalName,
                TargetAmount = goal.TargetAmount,
                StartDate = goal.StartDate,
                IncludeMealAllowance = goal.IncludedMealAllowance,
                SuppressAutomaticCompletion = true,
                CreatedAt = DateTimeOffset.Now,
                ActivationPeriods = [new GoalActivationPeriod { StartedAt = DateTimeOffset.Now }]
            };
            var restoredData = new FinancialGoalData
            {
                Version = 5,
                ActiveGoalId = restoredProfile.Id,
                Goals = _goalData.Goals.Append(restoredProfile).ToList(),
                GoalName = goal.GoalName,
                TargetAmount = goal.TargetAmount,
                StartDate = goal.StartDate,
                IncludeMealAllowance = goal.IncludedMealAllowance,
                SuppressAutomaticCompletion = true,
                Expenses = restoredExpenses,
                CompletedGoals = history
            };
            await _financialGoalService.SaveAsync(restoredData);
            _goalData = restoredData;
            _editingGoalId = restoredProfile.Id;
            RefreshGoalProfiles();
            ReplaceItems(_goalExpenses, restoredData.Expenses);
            ReplaceItems(_completedGoals, history);
            LoadGoalEditor(restoredProfile);
            if (restoredData.Expenses.Count > 0)
            {
                var latestExpenseDate = restoredData.Expenses.Max(item => item.Date);
                _expenseSelectedMonth = new DateOnly(latestExpenseDate.Year, latestExpenseDate.Month, 1);
            }
            UpdateCompletedGoalsVisibility();
            GoalStatusText.Text = "历史目标已恢复；请按需调整后点“保存目标”重新启用自动完成";

            var requiresEarlierHistory = _analysisStartDate is null || restoredData.StartDate < _analysisStartDate.Value;
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
            MessageBox.Show(this, ex.Message, "无法恢复历史目标", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
            var goal = _goalData.Goals.FirstOrDefault(item => item.Id == _editingGoalId);
            var isNew = goal is null;
            if (goal is null)
            {
                goal = new FinancialGoalProfile
                {
                    CreatedAt = DateTimeOffset.Now
                };
                _goalData.Goals.Add(goal);
                _editingGoalId = goal.Id;
            }

            goal.GoalName = goalName;
            goal.TargetAmount = decimal.Round(target, 2, MidpointRounding.AwayFromZero);
            goal.StartDate = startDate;
            goal.IncludeMealAllowance = GoalIncomeModeComboBox.SelectedIndex == 1;
            goal.SuppressAutomaticCompletion = false;
            goal.ActivationPeriods ??= [];

            var becameActive = false;
            if (_goalData.ActiveGoal is null)
            {
                _goalData.ActiveGoalId = goal.Id;
                goal.ActivationPeriods.Add(new GoalActivationPeriod
                {
                    StartedAt = GetInitialGoalActivationTime(startDate)
                });
                becameActive = true;
            }
            if (_goalData.ActiveGoalId == goal.Id)
            {
                FinancialGoalService.ApplyActiveGoalToLegacy(_goalData);
            }
            _goalData.Expenses = _goalExpenses.ToList();
            await _financialGoalService.SaveAsync(_goalData);
            RefreshGoalProfiles();
            LoadGoalEditor(goal);
            GoalStatusText.Text = isNew
                ? becameActive
                    ? $"已新增“{goalName}”并设为当前生效目标"
                    : $"已新增“{goalName}”；可在目标列表中设为当前目标"
                : $"目标“{goalName}”已加密保存";
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

    private async void DeleteCurrentGoalButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureGoalDataAsync();
            var goal = _goalData.Goals.FirstOrDefault(item => item.Id == _editingGoalId);
            if (goal is null)
            {
                MessageBox.Show(this, "请先从目标列表中选择要删除的目标。", "删除目标",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show(this,
                    $"确定删除“{goal.GoalName}”吗？\n\n消费账本、其他目标和历史已完成目标都会保留。",
                    "删除目标", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            if (_goalData.ActiveGoalId == goal.Id)
            {
                _goalData.ActiveGoalId = null;
                FinancialGoalService.ApplyActiveGoalToLegacy(_goalData);
            }
            _goalData.Goals.Remove(goal);
            _goalData.Expenses = _goalExpenses.ToList();
            await _financialGoalService.SaveAsync(_goalData);
            _editingGoalId = null;
            RefreshGoalProfiles();
            LoadGoalEditor(null);
            GoalStatusText.Text = $"目标“{goal.GoalName}”已删除；其他数据均已保留";
            UpdateGoalSummary();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法删除目标", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void NewGoalButton_Click(object sender, RoutedEventArgs e)
    {
        _editingGoalId = null;
        LoadGoalEditor(null);
        GoalStatusText.Text = "正在新增目标；保存后会保留现有目标";
        GoalNameTextBox.Focus();
    }

    private void EditGoalButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FinancialGoalProfile goal }) return;
        _editingGoalId = goal.Id;
        LoadGoalEditor(goal);
        GoalStatusText.Text = $"正在编辑“{goal.GoalName}”";
        GoalNameTextBox.Focus();
    }

    private async void SetActiveGoalButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FinancialGoalProfile goal }) return;
        try
        {
            await EnsureGoalDataAsync();
            if (_goalData.ActiveGoalId == goal.Id)
            {
                GoalStatusText.Text = $"“{goal.GoalName}”已经是当前生效目标";
                return;
            }

            var previous = _goalData.ActiveGoal;
            var now = DateTimeOffset.Now;
            FinancialGoalService.CaptureActiveGoalFromLegacy(_goalData);
            if (previous is not null)
            {
                CloseOpenGoalPeriod(previous, now, "被替换", goal.Id, goal.GoalName);
            }

            goal.ActivationPeriods ??= [];
            goal.ActivationPeriods.Add(new GoalActivationPeriod { StartedAt = now });
            _goalData.ActiveGoalId = goal.Id;
            FinancialGoalService.ApplyActiveGoalToLegacy(_goalData);
            await _financialGoalService.SaveAsync(_goalData);

            _editingGoalId = goal.Id;
            RefreshGoalProfiles();
            LoadGoalEditor(goal);
            GoalStatusText.Text = previous is null
                ? $"“{goal.GoalName}”已设为当前生效目标"
                : $"“{previous.GoalName}”已于 {now:yyyy-MM-dd HH:mm} 被“{goal.GoalName}”替换";

            if (_analysisStartDate is null || goal.StartDate < _analysisStartDate.Value)
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
            MessageBox.Show(this, ex.Message, "无法切换当前目标", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void GoalCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: FinancialGoalProfile goal }) return;
        ShowGoalHistory(goal);
    }

    private void ShowGoalHistory(FinancialGoalProfile goal)
    {
        GoalHistoryNameText.Text = goal.GoalName;
        GoalHistorySummaryText.Text =
            $"{goal.TargetAmountText} · {goal.StartDateText} · {goal.IncomeModeText} · {goal.StatusText}";
        var periods = goal.ActivationPeriods
            .OrderByDescending(item => item.StartedAt)
            .ToArray();
        GoalHistoryItemsControl.ItemsSource = periods;
        GoalHistoryEmptyText.Visibility = periods.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        GoalHistoryOverlay.Visibility = Visibility.Visible;
    }

    private void CloseGoalHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        GoalHistoryOverlay.Visibility = Visibility.Collapsed;
        GoalHistoryItemsControl.ItemsSource = null;
    }

    private static DateTimeOffset GetInitialGoalActivationTime(DateOnly startDate)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (startDate >= today) return DateTimeOffset.Now;
        var localStart = startDate.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(localStart, TimeZoneInfo.Local.GetUtcOffset(localStart));
    }

    private static void CloseOpenGoalPeriod(
        FinancialGoalProfile goal,
        DateTimeOffset endedAt,
        string reason,
        string? replacedByGoalId = null,
        string? replacedByGoalName = null)
    {
        var openPeriod = goal.ActivationPeriods
            .Where(item => item.EndedAt is null)
            .OrderByDescending(item => item.StartedAt)
            .FirstOrDefault();
        if (openPeriod is null) return;
        openPeriod.EndedAt = endedAt < openPeriod.StartedAt ? openPeriod.StartedAt : endedAt;
        openPeriod.EndReason = reason;
        openPeriod.ReplacedByGoalId = replacedByGoalId;
        openPeriod.ReplacedByGoalName = replacedByGoalName;
    }

    private async void AddExpenseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureGoalDataAsync();
            if (ExpenseDatePicker.SelectedDate is not DateTime selectedDateValue)
                throw new ArgumentException("请选择消费日期");
            var selectedDate = DateOnly.FromDateTime(selectedDateValue);
            if (selectedDate > DateOnly.FromDateTime(DateTime.Today))
                throw new ArgumentException("不能记录未来日期的消费");
            var description = ExpenseDescriptionTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("请输入消费说明");
            var amount = ReadDecimal(ExpenseAmountTextBox.Text, "消费金额");
            if (amount <= 0) throw new ArgumentException("消费金额必须大于 0");
            amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

            var previousExpenses = _goalData.Expenses.ToList();
            var savedExpense = new GoalExpense
            {
                Id = _editingExpenseId ?? Guid.NewGuid().ToString("N"),
                Date = selectedDate,
                Description = description,
                Amount = amount
            };
            var wasEditing = _editingExpenseId is not null;
            List<GoalExpense> updatedExpenses;
            if (wasEditing)
            {
                if (!previousExpenses.Any(item => item.Id == _editingExpenseId))
                    throw new InvalidOperationException("该消费记录已不存在");
                updatedExpenses = previousExpenses
                    .Select(item => item.Id == _editingExpenseId ? savedExpense : item)
                    .OrderByDescending(item => item.Date)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .ToList();
            }
            else
            {
                updatedExpenses = previousExpenses
                    .Prepend(savedExpense)
                    .OrderByDescending(item => item.Date)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .ToList();
            }

            _goalData.Expenses = updatedExpenses;
            try
            {
                await _financialGoalService.SaveAsync(_goalData);
            }
            catch
            {
                _goalData.Expenses = previousExpenses;
                throw;
            }

            ReplaceItems(_goalExpenses, updatedExpenses);
            _openedExpenseDate = selectedDate;
            _expenseSelectedMonth = new DateOnly(selectedDate.Year, selectedDate.Month, 1);
            ResetExpenseEditor(selectedDate);
            GoalStatusText.Text = wasEditing ? "消费记录已修改并加密保存" : "消费记录已加密保存";
            UpdateGoalSummary();
            UpdateExpenseDayDetails();
            ExpenseEntryStatusText.Text = $"{(wasEditing ? "已修改" : "已保存")}：{description} · ¥{amount:N2}";
            ExpenseDescriptionTextBox.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法添加消费", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EditExpenseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GoalExpense expense }) return;
        _editingExpenseId = expense.Id;
        ExpenseEntryTitleText.Text = "修改消费";
        ExpenseDescriptionTextBox.Text = expense.Description;
        ExpenseAmountTextBox.Text = expense.Amount.ToString("0.##", CultureInfo.InvariantCulture);
        ExpenseDatePicker.SelectedDate = expense.Date.ToDateTime(TimeOnly.MinValue);
        SaveExpenseButton.Content = "保存修改";
        CancelExpenseEditButton.Visibility = Visibility.Visible;
        ExpenseEntryStatusText.Text = "可修改日期、说明或金额";
        ExpenseDescriptionTextBox.Focus();
        ExpenseDescriptionTextBox.SelectAll();
    }

    private void CancelExpenseEditButton_Click(object sender, RoutedEventArgs e) =>
        ResetExpenseEditor(_openedExpenseDate);

    private void ResetExpenseEditor(DateOnly? date)
    {
        _editingExpenseId = null;
        ExpenseEntryTitleText.Text = "记一笔消费";
        ExpenseDescriptionTextBox.Clear();
        ExpenseAmountTextBox.Clear();
        ExpenseDatePicker.SelectedDate = date?.ToDateTime(TimeOnly.MinValue);
        SaveExpenseButton.Content = "保存这笔消费";
        CancelExpenseEditButton.Visibility = Visibility.Collapsed;
        ExpenseCalculatorPanel.Visibility = Visibility.Collapsed;
        ResetExpenseCalculator(0);
        ExpenseEntryStatusText.Text = date is DateOnly selectedDate
            ? $"正在记录 {selectedDate:MM 月 dd 日} 的消费"
            : "请先选择消费日期";
    }

    private async void DeleteExpenseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: GoalExpense expense }) return;
        if (_editingExpenseId == expense.Id) ResetExpenseEditor(_openedExpenseDate);
        var index = _goalExpenses.IndexOf(expense);
        try
        {
            _goalExpenses.Remove(expense);
            _goalData.Expenses = _goalExpenses.ToList();
            await _financialGoalService.SaveAsync(_goalData);
            GoalStatusText.Text = "消费记录已删除并加密保存";
            UpdateGoalSummary();
            UpdateExpenseDayDetails();
        }
        catch (Exception ex)
        {
            _goalExpenses.Insert(Math.Max(0, index), expense);
            _goalData.Expenses = _goalExpenses.ToList();
            UpdateGoalSummary();
            UpdateExpenseDayDetails();
            MessageBox.Show(this, ex.Message, "无法删除消费", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateExpenseCalendar()
    {
        if (!_goalLoaded || ExpenseCalendar is null) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstDay = new DateOnly(_expenseSelectedMonth.Year, _expenseSelectedMonth.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(firstDay.Year, firstDay.Month);
        var leadingBlankCount = ((int)firstDay.DayOfWeek + 6) % 7;
        var cellCount = (int)Math.Ceiling((leadingBlankCount + daysInMonth) / 7d) * 7;
        var expensesByDay = _goalExpenses
            .Where(item => item.Date.Year == firstDay.Year && item.Date.Month == firstDay.Month)
            .GroupBy(item => item.Date)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var cells = new List<ExpenseCalendarDayCell>(cellCount);
        for (var index = 0; index < cellCount; index++)
        {
            var day = index - leadingBlankCount + 1;
            if (day is < 1 || day > daysInMonth)
            {
                cells.Add(new ExpenseCalendarDayCell());
                continue;
            }

            var date = new DateOnly(firstDay.Year, firstDay.Month, day);
            expensesByDay.TryGetValue(date, out var dayExpenses);
            dayExpenses ??= [];
            var total = dayExpenses.Sum(item => item.Amount);
            cells.Add(new ExpenseCalendarDayCell
            {
                Date = date,
                DayText = day.ToString(CultureInfo.InvariantCulture),
                AmountText = dayExpenses.Length == 0 ? string.Empty : $"消费 ¥{total:N2}",
                CountText = dayExpenses.Length == 0 ? string.Empty : $"{dayExpenses.Length} 笔",
                HasExpense = dayExpenses.Length > 0,
                IsToday = date == today,
                IsFuture = date > today
            });
        }

        ExpenseCalendar.ItemsSource = cells;
        var monthExpenses = expensesByDay.Values.SelectMany(items => items).ToArray();
        var monthTotal = monthExpenses.Sum(item => item.Amount);
        ExpenseCalendarMonthSummaryText.Text = monthExpenses.Length == 0
            ? $"{firstDay:yyyy 年 MM 月} · 暂无消费"
            : $"{firstDay:yyyy 年 MM 月} · 消费 ¥{monthTotal:N2} · {monthExpenses.Length} 笔";
        NextExpenseMonthButton.IsEnabled = firstDay < new DateOnly(today.Year, today.Month, 1);
    }

    private void SetExpenseSelectedMonth(DateOnly month)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        _expenseSelectedMonth = month > currentMonth ? currentMonth : new DateOnly(month.Year, month.Month, 1);
        UpdateExpenseCalendar();
    }

    private void PreviousExpenseMonthButton_Click(object sender, RoutedEventArgs e) =>
        SetExpenseSelectedMonth(_expenseSelectedMonth.AddMonths(-1));

    private void NextExpenseMonthButton_Click(object sender, RoutedEventArgs e)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        if (_expenseSelectedMonth < currentMonth) SetExpenseSelectedMonth(_expenseSelectedMonth.AddMonths(1));
    }

    private void CurrentExpenseMonthButton_Click(object sender, RoutedEventArgs e)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        SetExpenseSelectedMonth(new DateOnly(today.Year, today.Month, 1));
    }

    private void ExpenseCalendarDay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ExpenseCalendarDayCell { Date: DateOnly date } cell } ||
            cell.IsFuture) return;

        _openedExpenseDate = date;
        ResetExpenseEditor(date);
        UpdateExpenseDayDetails();
        ExpenseDetailOverlay.Visibility = Visibility.Visible;
        ExpenseDescriptionTextBox.Focus();
        e.Handled = true;
    }

    private void CloseExpenseDetailButton_Click(object sender, RoutedEventArgs e)
    {
        ExpenseDetailOverlay.Visibility = Visibility.Collapsed;
        ResetExpenseEditor(null);
        _openedExpenseDate = null;
    }

    private void UpdateExpenseDayDetails()
    {
        if (_openedExpenseDate is not DateOnly date || ExpenseDayItemsControl is null) return;
        var expenses = _goalExpenses.Where(item => item.Date == date).ToArray();
        ReplaceItems(_selectedDayExpenses, expenses);
        var total = expenses.Sum(item => item.Amount);
        ExpenseDetailTitleText.Text = $"{date:yyyy 年 MM 月 dd 日} · {GetWeekText(date.DayOfWeek)}";
        ExpenseDetailSummaryText.Text = expenses.Length == 0
            ? "当天暂无消费记录"
            : $"当天共 {expenses.Length} 笔 · 合计 ¥{total:N2}";
        ExpenseDayEmptyText.Visibility = expenses.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExpenseEntryStatusText.Text = $"正在记录 {date:MM 月 dd 日} 的消费";
    }

    private void ExpenseAmountTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        e.Handled = !IsValidExpenseAmountText(BuildTextAfterInput(textBox, e.Text));
    }

    private void ExpenseAmountTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox textBox || !e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            e.CancelCommand();
            return;
        }

        var pastedText = e.DataObject.GetData(DataFormats.UnicodeText)?.ToString() ?? string.Empty;
        if (!IsValidExpenseAmountText(BuildTextAfterInput(textBox, pastedText))) e.CancelCommand();
    }

    private static string BuildTextAfterInput(TextBox textBox, string input)
    {
        var text = textBox.Text ?? string.Empty;
        var selectionStart = Math.Clamp(textBox.SelectionStart, 0, text.Length);
        var selectionLength = Math.Clamp(textBox.SelectionLength, 0, text.Length - selectionStart);
        return text.Remove(selectionStart, selectionLength).Insert(selectionStart, input);
    }

    private static bool IsValidExpenseAmountText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        if (text.Length > 15 || text.Any(character => !char.IsDigit(character) && character != '.')) return false;
        var separatorIndex = text.IndexOf('.');
        if (separatorIndex != text.LastIndexOf('.')) return false;
        return separatorIndex < 0 || text.Length - separatorIndex - 1 <= 2;
    }

    private void ToggleExpenseCalculatorButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExpenseCalculatorPanel.Visibility == Visibility.Visible)
        {
            ExpenseCalculatorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var initialValue = TryReadDecimal(ExpenseAmountTextBox.Text, out var parsed) ? parsed : 0;
        ResetExpenseCalculator(initialValue);
        ExpenseCalculatorPanel.Visibility = Visibility.Visible;
        ExpenseCalculatorPanel.Focus();
    }

    private void ExpenseCalculatorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        ExecuteExpenseCalculatorCommand(key);
    }

    private void ExpenseCalculatorPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var command = GetExpenseCalculatorKeyboardCommand(e.Key, Keyboard.Modifiers);
        if (command is null) return;

        e.Handled = true;
        ExecuteExpenseCalculatorCommand(command);
    }

    private void ExecuteExpenseCalculatorCommand(string key)
    {
        try
        {
            switch (key)
            {
                case "clear":
                    ResetExpenseCalculator(0);
                    break;
                case "back":
                    BackspaceExpenseCalculator();
                    break;
                case "+" or "-" or "*" or "/":
                    SelectExpenseCalculatorOperator(key);
                    break;
                case "=":
                    CompleteExpenseCalculatorOperation();
                    break;
                case "cancel":
                    ExpenseCalculatorPanel.Visibility = Visibility.Collapsed;
                    break;
                case "use":
                    CompleteExpenseCalculatorOperation();
                    var result = decimal.Round(ReadExpenseCalculatorDisplay(), 2, MidpointRounding.AwayFromZero);
                    if (result < 0) throw new InvalidOperationException("消费金额不能为负数");
                    ExpenseAmountTextBox.Text = result.ToString("0.##", CultureInfo.InvariantCulture);
                    ExpenseAmountTextBox.CaretIndex = ExpenseAmountTextBox.Text.Length;
                    ExpenseCalculatorPanel.Visibility = Visibility.Collapsed;
                    ExpenseEntryStatusText.Text = $"计算结果已回填：¥{result:N2}";
                    break;
                case ".":
                    AppendExpenseCalculatorDecimalPoint();
                    break;
                default:
                    if (key.Length == 1 && char.IsDigit(key[0])) AppendExpenseCalculatorDigit(key);
                    break;
            }
        }
        catch (Exception ex)
        {
            ExpenseEntryStatusText.Text = ex.Message;
        }
    }

    private static string? GetExpenseCalculatorKeyboardCommand(Key key, ModifierKeys modifiers)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return null;

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return ((int)key - (int)Key.NumPad0).ToString(CultureInfo.InvariantCulture);

        if (key is >= Key.D0 and <= Key.D9)
        {
            if ((modifiers & ModifierKeys.Shift) != 0)
                return key == Key.D8 ? "*" : null;
            return ((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture);
        }

        return key switch
        {
            Key.Decimal or Key.OemPeriod => ".",
            Key.Add => "+",
            Key.OemPlus => (modifiers & ModifierKeys.Shift) != 0 ? "+" : "=",
            Key.Subtract or Key.OemMinus => "-",
            Key.Multiply => "*",
            Key.Divide => "/",
            Key.OemQuestion when (modifiers & ModifierKeys.Shift) == 0 => "/",
            Key.Enter => "=",
            Key.Back => "back",
            Key.Delete or Key.C => "clear",
            Key.Escape => "cancel",
            _ => null
        };
    }

    private void ResetExpenseCalculator(decimal value)
    {
        ExpenseCalculatorDisplayTextBox.Text = FormatExpenseCalculatorValue(value);
        _expenseCalculatorAccumulator = null;
        _expenseCalculatorPendingOperator = null;
        _expenseCalculatorEnteringNewValue = true;
    }

    private void AppendExpenseCalculatorDigit(string digit)
    {
        var display = ExpenseCalculatorDisplayTextBox.Text;
        if (_expenseCalculatorEnteringNewValue || display == "0")
        {
            ExpenseCalculatorDisplayTextBox.Text = digit;
            _expenseCalculatorEnteringNewValue = false;
            return;
        }
        if (display.Length < 18) ExpenseCalculatorDisplayTextBox.Text += digit;
    }

    private void AppendExpenseCalculatorDecimalPoint()
    {
        if (_expenseCalculatorEnteringNewValue)
        {
            ExpenseCalculatorDisplayTextBox.Text = "0.";
            _expenseCalculatorEnteringNewValue = false;
            return;
        }
        if (!ExpenseCalculatorDisplayTextBox.Text.Contains('.', StringComparison.Ordinal))
            ExpenseCalculatorDisplayTextBox.Text += ".";
    }

    private void BackspaceExpenseCalculator()
    {
        if (_expenseCalculatorEnteringNewValue) return;
        var display = ExpenseCalculatorDisplayTextBox.Text;
        display = display.Length > 1 ? display[..^1] : "0";
        if (display == "-") display = "0";
        ExpenseCalculatorDisplayTextBox.Text = display;
    }

    private void SelectExpenseCalculatorOperator(string operation)
    {
        var current = ReadExpenseCalculatorDisplay();
        if (_expenseCalculatorPendingOperator is not null && !_expenseCalculatorEnteringNewValue)
        {
            _expenseCalculatorAccumulator = ApplyExpenseCalculatorOperation(
                _expenseCalculatorAccumulator ?? 0,
                current,
                _expenseCalculatorPendingOperator);
            ExpenseCalculatorDisplayTextBox.Text = FormatExpenseCalculatorValue(_expenseCalculatorAccumulator.Value);
        }
        else if (_expenseCalculatorAccumulator is null)
        {
            _expenseCalculatorAccumulator = current;
        }

        _expenseCalculatorPendingOperator = operation;
        _expenseCalculatorEnteringNewValue = true;
    }

    private void CompleteExpenseCalculatorOperation()
    {
        if (_expenseCalculatorPendingOperator is null || _expenseCalculatorEnteringNewValue) return;
        var result = ApplyExpenseCalculatorOperation(
            _expenseCalculatorAccumulator ?? 0,
            ReadExpenseCalculatorDisplay(),
            _expenseCalculatorPendingOperator);
        ExpenseCalculatorDisplayTextBox.Text = FormatExpenseCalculatorValue(result);
        _expenseCalculatorAccumulator = result;
        _expenseCalculatorPendingOperator = null;
        _expenseCalculatorEnteringNewValue = true;
    }

    private decimal ReadExpenseCalculatorDisplay() =>
        decimal.TryParse(ExpenseCalculatorDisplayTextBox.Text, NumberStyles.Number,
            CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static decimal ApplyExpenseCalculatorOperation(decimal left, decimal right, string operation) =>
        operation switch
        {
            "+" => left + right,
            "-" => left - right,
            "*" => left * right,
            "/" when right == 0 => throw new DivideByZeroException("除数不能为 0"),
            "/" => left / right,
            _ => right
        };

    private static string FormatExpenseCalculatorValue(decimal value) =>
        value.ToString("0.############", CultureInfo.InvariantCulture);

    private static bool TryReadDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

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

    private async void EnableOvertimePayCapCheckBox_Click(object sender, RoutedEventArgs e)
    {
        SettingsStatusText.Text = string.Empty;
        var originalEnabled = _settings.EnableOvertimePayCap;
        var originalCap = _settings.MonthlyOvertimePayCap;
        var originalHolidayExclusion = _settings.ExcludeHolidayPayFromCap;
        var originalEffectiveDate = _settings.OvertimePayCapEffectiveDate;

        try
        {
            var requestedEnabled = EnableOvertimePayCapCheckBox.IsChecked == true;
            var requestedCap = requestedEnabled
                ? ReadDecimal(MonthlyOvertimePayCapTextBox.Text, "每月加班费封顶金额")
                : originalCap;
            if (requestedEnabled && requestedCap <= 0)
                throw new ArgumentException("启用加班费封顶时，封顶金额必须大于 0");

            if (requestedEnabled && !originalEnabled && !_overtimeCapScopeChosenSinceLoad &&
                !ChooseOvertimeCapEffectiveScope())
            {
                EnableOvertimePayCapCheckBox.IsChecked = originalEnabled;
                SettingsStatusText.Text = "已取消启用，封顶设置未更改";
                return;
            }

            _settings.EnableOvertimePayCap = requestedEnabled;
            _settings.MonthlyOvertimePayCap = decimal.Round(
                requestedCap,
                2,
                MidpointRounding.AwayFromZero);
            _settings.ExcludeHolidayPayFromCap = ExcludeHolidayPayFromCapCheckBox.IsChecked == true;
            _settings.OvertimePayCapEffectiveDate = _pendingOvertimePayCapEffectiveDate;
            await _settingsService.SaveAsync(_settings);
            RecalculateCurrentResults();
            if (_analysisLoadedOn is not null) RecalculateAnalysisResults();
            _overtimeCapScopeChosenSinceLoad = false;
            SettingsStatusText.Text = requestedEnabled
                ? $"月度封顶已启用并保存 · {GetOvertimeCapScopeText(_settings.OvertimePayCapEffectiveDate)}"
                : "月度封顶已关闭并保存";
        }
        catch (Exception ex)
        {
            _settings.EnableOvertimePayCap = originalEnabled;
            _settings.MonthlyOvertimePayCap = originalCap;
            _settings.ExcludeHolidayPayFromCap = originalHolidayExclusion;
            _settings.OvertimePayCapEffectiveDate = originalEffectiveDate;
            _pendingOvertimePayCapEffectiveDate = originalEffectiveDate;
            _overtimeCapScopeChosenSinceLoad = false;
            EnableOvertimePayCapCheckBox.IsChecked = originalEnabled;
            UpdateOvertimeCapScopeText();
            MessageBox.Show(this, ex.Message, "封顶设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsStatusText.Text = string.Empty;
        try
        {
            var requestedCapEnabled = EnableOvertimePayCapCheckBox.IsChecked == true;
            var requestedCap = ReadDecimal(MonthlyOvertimePayCapTextBox.Text, "每月加班费封顶金额");
            if (requestedCapEnabled && requestedCap <= 0)
                throw new ArgumentException("启用加班费封顶时，封顶金额必须大于 0");
            var requestedHolidayExclusion = ExcludeHolidayPayFromCapCheckBox.IsChecked == true;
            var capDefinitionChanged = requestedCapEnabled &&
                                       (!_settings.EnableOvertimePayCap ||
                                        decimal.Round(requestedCap, 2, MidpointRounding.AwayFromZero) !=
                                        decimal.Round(_settings.MonthlyOvertimePayCap, 2, MidpointRounding.AwayFromZero) ||
                                        requestedHolidayExclusion != _settings.ExcludeHolidayPayFromCap);
            if (capDefinitionChanged && !_overtimeCapScopeChosenSinceLoad &&
                !ChooseOvertimeCapEffectiveScope())
            {
                SettingsStatusText.Text = "已取消保存，封顶设置未更改";
                return;
            }

            ReadSettingsFromControls();
            await _settingsService.SaveAsync(_settings);
            RecalculateCurrentResults();
            if (_analysisLoadedOn is not null) RecalculateAnalysisResults();
            _overtimeCapScopeChosenSinceLoad = false;
            SettingsStatusText.Text = _settings.EnableOvertimePayCap
                ? $"已保存并重新计算 · {GetOvertimeCapScopeText(_settings.OvertimePayCapEffectiveDate)}"
                : "已保存并重新计算";
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

    private void ChooseOvertimeCapScopeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseOvertimeCapEffectiveScope())
            SettingsStatusText.Text = "生效范围已选择，点击保存后应用";
    }

    private bool ChooseOvertimeCapEffectiveScope()
    {
        var window = new OvertimeCapScopeWindow(_pendingOvertimePayCapEffectiveDate) { Owner = this };
        if (window.ShowDialog() != true) return false;

        _pendingOvertimePayCapEffectiveDate = window.EffectiveDate;
        _overtimeCapScopeChosenSinceLoad = true;
        UpdateOvertimeCapScopeText();
        return true;
    }

    private void UpdateOvertimeCapScopeText()
    {
        OvertimeCapEffectiveScopeText.Text =
            $"生效范围：{GetOvertimeCapScopeText(_pendingOvertimePayCapEffectiveDate)}";
    }

    private static string GetOvertimeCapScopeText(DateOnly? effectiveDate) =>
        effectiveDate is DateOnly date ? $"{date:yyyy-MM-dd} 起" : "全部历史数据";

    private void LoadSettingsIntoControls()
    {
        WorkdayRateTextBox.Text = _settings.WorkdayRate.ToString(CultureInfo.InvariantCulture);
        WeekendRateTextBox.Text = _settings.WeekendRate.ToString(CultureInfo.InvariantCulture);
        HolidayRateTextBox.Text = _settings.HolidayRate.ToString(CultureInfo.InvariantCulture);
        EnableOvertimePayCapCheckBox.IsChecked = _settings.EnableOvertimePayCap;
        MonthlyOvertimePayCapTextBox.Text = _settings.MonthlyOvertimePayCap.ToString(CultureInfo.InvariantCulture);
        ExcludeHolidayPayFromCapCheckBox.IsChecked = _settings.ExcludeHolidayPayFromCap;
        _pendingOvertimePayCapEffectiveDate = _settings.OvertimePayCapEffectiveDate;
        _overtimeCapScopeChosenSinceLoad = false;
        UpdateOvertimeCapScopeText();
        MealAllowanceTextBox.Text = _settings.MealAllowanceAmount.ToString(CultureInfo.InvariantCulture);
        FlexibleStartEarliestTextBox.Text = _settings.FlexibleWorkStartEarliest;
        FlexibleStartLatestTextBox.Text = _settings.FlexibleWorkStartLatest;
        WorkdayStartTextBox.Text = _settings.WorkdayOvertimeStart;
        MinimumHoursTextBox.Text = _settings.MinimumOvertimeHours.ToString(CultureInfo.InvariantCulture);
        RoundingMinutesTextBox.Text = _settings.RoundingMinutes.ToString(CultureInfo.InvariantCulture);
        WorkdayMealHoursTextBox.Text = _settings.WorkdayMealAllowanceMinimumHours.ToString(CultureInfo.InvariantCulture);
        NonWorkdayMealHoursTextBox.Text = _settings.NonWorkdayMealAllowanceMinimumHours.ToString(CultureInfo.InvariantCulture);
        DeductLeaveCheckBox.IsChecked = _settings.DeductLeaveFromOvertime;
        AutoSyncCheckBox.IsChecked = _settings.AutoSyncHolidays;
        HolidaySourceTextBox.Text = _settings.HolidaySourceUrl;
        HolidaySourceDisplayTextBox.Text = _settings.HolidaySourceUrl;
    }

    private void ReadSettingsFromControls()
    {
        _settings.WorkdayRate = ReadDecimal(WorkdayRateTextBox.Text, "工作日费率");
        _settings.WeekendRate = ReadDecimal(WeekendRateTextBox.Text, "周末费率");
        _settings.HolidayRate = ReadDecimal(HolidayRateTextBox.Text, "节假日费率");
        _settings.EnableOvertimePayCap = EnableOvertimePayCapCheckBox.IsChecked == true;
        _settings.MonthlyOvertimePayCap = decimal.Round(
            ReadDecimal(MonthlyOvertimePayCapTextBox.Text, "每月加班费封顶金额"),
            2,
            MidpointRounding.AwayFromZero);
        if (_settings.EnableOvertimePayCap && _settings.MonthlyOvertimePayCap <= 0)
            throw new ArgumentException("启用加班费封顶时，封顶金额必须大于 0");
        _settings.ExcludeHolidayPayFromCap = ExcludeHolidayPayFromCapCheckBox.IsChecked == true;
        _settings.OvertimePayCapEffectiveDate = _pendingOvertimePayCapEffectiveDate;
        _settings.MealAllowanceAmount = ReadDecimal(MealAllowanceTextBox.Text, "餐补金额");
        ValidateTime(FlexibleStartEarliestTextBox.Text, "最早上班时间");
        ValidateTime(FlexibleStartLatestTextBox.Text, "最晚上班时间");
        ValidateTime(WorkdayStartTextBox.Text, "工作日起算时间");
        var earliest = TimeOnly.ParseExact(FlexibleStartEarliestTextBox.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture);
        var latest = TimeOnly.ParseExact(FlexibleStartLatestTextBox.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture);
        if (latest < earliest) throw new ArgumentException("最晚上班时间不能早于最早上班时间");
        _settings.FlexibleWorkStartEarliest = FlexibleStartEarliestTextBox.Text.Trim();
        _settings.FlexibleWorkStartLatest = FlexibleStartLatestTextBox.Text.Trim();
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
    }

    private void CurrentMonthButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectedMonth(new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1));
    }

    private void PreviousMonthButton_Click(object sender, RoutedEventArgs e) =>
        SetSelectedMonth(CanNavigatePreviousMonth()
            ? GetSelectedMonth().AddMonths(-1)
            : GetSelectedMonth());

    private void NextMonthButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigateNextMonth()) return;
        SetSelectedMonth(GetSelectedMonth().AddMonths(1));
    }

    private void OverviewMonthComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingOverviewMonthSelection) return;
        var selectedMonth = GetSelectedMonth();
        var currentMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        if (selectedMonth > currentMonth)
        {
            SetSelectedMonth(currentMonth);
            return;
        }
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
            // 年份、月份下拉框和月份按钮可能连续修改选择，防抖后只发起一次请求。
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
        var today = DateTime.Today;
        var year = OverviewYearComboBox.SelectedItem is int selectedYear ? selectedYear : today.Year;
        var month = OverviewMonthComboBox.SelectedItem is int selectedMonth ? selectedMonth : today.Month;
        return new DateOnly(year, month, 1);
    }

    private void SetSelectedMonth(DateOnly month)
    {
        var currentMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var earliestMonth = new DateOnly(DateTime.Today.Year - 9, 1, 1);
        if (month > currentMonth) month = currentMonth;
        if (month < earliestMonth) month = earliestMonth;
        _updatingOverviewMonthSelection = true;
        try
        {
            OverviewYearComboBox.SelectedItem = month.Year;
            OverviewMonthComboBox.SelectedItem = month.Month;
        }
        finally
        {
            _updatingOverviewMonthSelection = false;
        }
        UpdateMonthNavigationButtons();
        ScheduleAutoRefresh();
    }

    private bool CanNavigateNextMonth()
    {
        return MonthNavigation.CanNavigateNext(
            GetSelectedMonth(),
            DateOnly.FromDateTime(DateTime.Today));
    }

    private bool CanNavigatePreviousMonth() =>
        GetSelectedMonth() > new DateOnly(DateTime.Today.Year - 9, 1, 1);

    private void UpdateMonthNavigationButtons()
    {
        if (NextMonthButton is null) return;
        PreviousMonthButton.IsEnabled = !_isBusy && CanNavigatePreviousMonth();
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
        if (busy)
        {
            DayDetailOverlay.Visibility = Visibility.Collapsed;
            ExpenseDetailOverlay.Visibility = Visibility.Collapsed;
            GoalHistoryOverlay.Visibility = Visibility.Collapsed;
            CompletedGoalEditOverlay.Visibility = Visibility.Collapsed;
            _editingCompletedGoalId = null;
        }
        SyncHolidayButton.IsEnabled = !busy;
        OverviewYearComboBox.IsEnabled = !busy;
        OverviewMonthComboBox.IsEnabled = !busy;
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
        if (OverviewYearComboBox.SelectedItem is not int || OverviewMonthComboBox.SelectedItem is not int)
        {
            start = default;
            end = default;
            if (showMessage) MessageBox.Show(this, "请选择统计年份和月份。", "月份不完整");
            return false;
        }

        var range = MonthNavigation.GetRange(GetSelectedMonth(), DateOnly.FromDateTime(DateTime.Today));
        start = range.Start;
        end = range.End;
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
    private void UpdateMaximizeGlyph()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = isMaximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = isMaximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.ToolTip = isMaximized ? "还原" : "最大化";
        AutomationProperties.SetName(MaximizeButton, isMaximized ? "还原" : "最大化");
    }
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
        var localUpdateService = new LocalUpdateService();
        var onlineUpdateService = new GitHubReleaseUpdateService();
        var updaterLaunched = false;
        SetBusy(true, "正在从 GitHub Releases 检查更新…");
        try
        {
            GitHubReleaseInfo? release = null;
            Exception? onlineError = null;
            try
            {
                release = await onlineUpdateService.CheckLatestAsync();
            }
            catch (Exception ex)
            {
                onlineError = ex;
            }

            var localPackage = localUpdateService.FindPackages()
                .FirstOrDefault(package =>
                    package.Version > LocalUpdateService.NormalizeVersion(LocalUpdateService.CurrentVersion));
            LocalUpdatePackage? selectedPackage = null;

            if (release?.HasUpdate == true)
            {
                if (!release.HasDownloadableAsset)
                {
                    if (localPackage is not null)
                    {
                        selectedPackage = localPackage;
                    }
                    else
                    {
                        SetBusy(false, $"发现新版本 v{release.DisplayVersion}，但 Release 中没有安装包");
                        var openRelease = MessageBox.Show(this,
                            $"检测到新版本 v{release.DisplayVersion}，但 GitHub Release 中没有符合命名规则的 Windows x64 ZIP。\n\n是否打开发布页面？",
                            "新版本缺少安装包",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information);
                        if (openRelease == MessageBoxResult.Yes) OpenWebPage(release.ReleaseUrl);
                        return;
                    }
                }
                else
                {
                    selectedPackage = localUpdateService.FindPackages()
                        .FirstOrDefault(package => package.Version == release.Version);
                    if (selectedPackage is null)
                    {
                        SetBusy(false, $"发现新版本 v{release.DisplayVersion}");
                        var packageName = release.AssetName ?? "QHR Windows x64 ZIP";
                        var confirm = MessageBox.Show(this,
                            $"检测到新版本。\n\n当前版本：v{LocalUpdateService.CurrentDisplayVersion}\n最新版本：v{release.DisplayVersion}\n安装包：{packageName}\n大小：{FormatByteSize(release.AssetSizeBytes)}\n\n是否现在下载并自动更新？",
                            "检测到新版本",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);
                        if (confirm != MessageBoxResult.Yes) return;

                        SetBusy(true, $"正在下载 v{release.DisplayVersion}…");
                        var progress = new Progress<UpdateDownloadProgress>(value =>
                        {
                            var progressText = value.TotalBytes > 0
                                ? $"正在下载 v{release.DisplayVersion}：{FormatByteSize(value.BytesReceived)} / {FormatByteSize(value.TotalBytes)}（{value.Percent:0.0}%）"
                                : $"正在下载 v{release.DisplayVersion}：已接收 {FormatByteSize(value.BytesReceived)}";
                            HeaderStatusText.Text = progressText;
                            RefreshOverlayStatusText.Text = progressText;
                        });
                        selectedPackage = await onlineUpdateService.DownloadAsync(release, progress);
                    }
                }
            }
            else if (localPackage is not null)
            {
                selectedPackage = localPackage;
            }
            else
            {
                if (onlineError is not null)
                {
                    throw new InvalidOperationException(
                        $"在线更新检查失败，安装目录中也没有可用的本地更新包。\n{onlineError.GetBaseException().Message}",
                        onlineError);
                }

                SetBusy(false, $"当前已是最新版本 v{LocalUpdateService.CurrentDisplayVersion}");
                MessageBox.Show(this,
                    $"当前已是最新版本：v{LocalUpdateService.CurrentDisplayVersion}",
                    "当前已是最新版本",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            SetBusy(true, $"正在校验 v{selectedPackage.DisplayVersion} 并准备安装…");
            await localUpdateService.LaunchUpdaterAsync(selectedPackage);
            updaterLaunched = true;
            HeaderStatusText.Text = "更新包校验完成，即将自动安装并重启…";
            RefreshOverlayStatusText.Text = HeaderStatusText.Text;
            await Task.Delay(600);
            _isLoggingOut = true;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            HeaderStatusText.Text = "检查或安装更新失败";
            MessageBox.Show(this, ex.GetBaseException().Message, "更新失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!updaterLaunched) SetBusy(false, HeaderStatusText.Text);
        }
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes <= 0) return "未知";
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }

    private static void OpenWebPage(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OpenDiagnosticsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = new QhrCaptureWindow(_settings.QhrBaseUrl) { Owner = this };
        window.Show();
    }

    private async void BackupAllDataMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var saveDialog = new SaveFileDialog
        {
            Title = "备份 QHR 加班助手全部数据",
            Filter = "QHR 加密备份 (*.qhrbackup)|*.qhrbackup",
            DefaultExt = ".qhrbackup",
            AddExtension = true,
            FileName = $"QHR-全部数据-{DateTime.Now:yyyyMMdd-HHmm}.qhrbackup"
        };
        if (saveDialog.ShowDialog(this) != true) return;
        var passwordWindow = new BackupPasswordWindow(true) { Owner = this };
        if (passwordWindow.ShowDialog() != true) return;

        SetBusy(true, "正在备份全部本地数据…");
        try
        {
            var progress = new Progress<string>(UpdateBackupProgress);
            var service = new DataBackupService(_settingsService, _settings, _username);
            var result = await service.ExportAsync(
                saveDialog.FileName,
                passwordWindow.BackupPassword,
                progress);
            HeaderStatusText.Text = $"完整备份已创建 · {DateTime.Now:HH:mm}";
            MessageBox.Show(this,
                $"备份完成。\n\n" +
                $"考勤：{result.Manifest.AttendanceCount} 天\n" +
                $"消费：{result.Manifest.ExpenseCount} 笔\n" +
                $"证据：{result.Manifest.EvidenceDayCount} 天 / {result.Manifest.EvidenceImageCount} 张图片\n" +
                $"大小：{FormatFileSize(result.FileSize)}\n\n" +
                $"文件：\n{result.FilePath}\n\n" +
                "请妥善保存备份密码。SSO 登录密码没有写入备份。",
                "全部数据已备份",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            HeaderStatusText.Text = "备份失败";
            MessageBox.Show(this, ex.GetBaseException().Message, "无法创建备份",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, HeaderStatusText.Text);
        }
    }

    private async void ImportBackupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var openDialog = new OpenFileDialog
        {
            Title = "导入 QHR 全量备份",
            Filter = "QHR 加密备份 (*.qhrbackup)|*.qhrbackup|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (openDialog.ShowDialog(this) != true) return;
        var passwordWindow = new BackupPasswordWindow(false) { Owner = this };
        if (passwordWindow.ShowDialog() != true) return;

        BackupImportPackage? package = null;
        try
        {
            var service = new DataBackupService(_settingsService, _settings, _username);
            var progress = new Progress<string>(UpdateBackupProgress);
            SetBusy(true, "正在检查备份…");
            package = await service.OpenAsync(openDialog.FileName, passwordWindow.BackupPassword, progress);
            var conflicts = await service.InspectConflictsAsync(package);
            SetBusy(false, "备份校验完成，等待确认");

            if (!string.Equals(package.Manifest.Account.Trim(), _username.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var accountResult = MessageBox.Show(this,
                    $"此备份由账户“{package.Manifest.Account}”创建，当前登录账户是“{_username}”。\n\n" +
                    "继续导入会把备份数据合并到当前账户。确定继续吗？",
                    "备份账户不一致",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (accountResult != MessageBoxResult.Yes) return;
            }

            var conflictMode = BackupConflictMode.KeepLocal;
            if (conflicts.TotalConflicts > 0)
            {
                var conflictWindow = new BackupConflictWindow(conflicts, package.Manifest) { Owner = this };
                if (conflictWindow.ShowDialog() != true) return;
                conflictMode = conflictWindow.SelectedMode;
            }
            else
            {
                var confirmResult = MessageBox.Show(this,
                    $"备份创建于 {package.Manifest.CreatedAt:yyyy-MM-dd HH:mm}，未发现冲突。\n\n" +
                    $"将合并 {package.Manifest.AttendanceCount} 天考勤、{package.Manifest.ExpenseCount} 笔消费、" +
                    $"{package.Manifest.EvidenceImageCount} 张证据图片。\n\n确定开始导入吗？",
                    "确认导入备份",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirmResult != MessageBoxResult.Yes) return;
            }

            SetBusy(true, "正在导入并重新加密数据…");
            var result = await service.ImportAsync(package, conflictMode, progress);
            await ReloadImportedDataAsync();
            HeaderStatusText.Text = $"备份导入完成 · {DateTime.Now:HH:mm}";
            MessageBox.Show(this,
                $"导入完成。所有数据已使用当前 Windows 用户重新加密。\n\n" +
                $"本地考勤：{result.AttendanceCount} 天\n" +
                $"消费记录：{result.ExpenseCount} 笔\n" +
                $"证据资料：{result.EvidenceDayCount} 天 / {result.EvidenceImageCount} 张图片\n\n" +
                "登录凭据不会从备份恢复，当前电脑仍使用自己的 Windows 凭据。",
                "备份已导入",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            HeaderStatusText.Text = "导入备份失败";
            MessageBox.Show(this, ex.GetBaseException().Message, "无法导入备份",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            package?.Dispose();
            SetBusy(false, HeaderStatusText.Text);
        }
    }

    private void UpdateBackupProgress(string message)
    {
        HeaderStatusText.Text = message;
        RefreshOverlayStatusText.Text = message;
    }

    private async Task ReloadImportedDataAsync()
    {
        LoadSettingsIntoControls();
        DayDetailOverlay.Visibility = Visibility.Collapsed;
        ExpenseDetailOverlay.Visibility = Visibility.Collapsed;
        GoalHistoryOverlay.Visibility = Visibility.Collapsed;
        CompletedGoalEditOverlay.Visibility = Visibility.Collapsed;
        _openedDetailDate = null;
        _openedExpenseDate = null;
        _editingExpenseId = null;
        _editingCompletedGoalId = null;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var attendanceCache = new EncryptedAttendanceCache(_settingsService, _username);
        var allAttendance = await attendanceCache.LoadAsync();
        if (TryGetDateRange(out var startDate, out var endDate, false))
        {
            _lastAttendance = allAttendance
                .Where(item => item.Date >= startDate && item.Date <= endDate)
                .OrderBy(item => item.Date)
                .ToArray();
            _lastCalendar = await _holidayService.GetCalendarAsync(
                new DateOnly(startDate.Year, 1, 1),
                new DateOnly(endDate.Year, 12, 31),
                false);
            RecalculateCurrentResults();
        }

        var analysisStart = allAttendance.Count == 0
            ? new DateOnly(today.Year, 1, 1)
            : new DateOnly(allAttendance.Min(item => item.Date).Year, 1, 1);
        _analysisAttendance = allAttendance;
        _analysisCalendar = await _holidayService.GetCalendarAsync(
            analysisStart,
            new DateOnly(today.Year, 12, 31),
            false);
        _analysisStartDate = analysisStart;
        _analysisLoadedOn = today;
        RecalculateAnalysisResults();

        _goalLoaded = false;
        await EnsureGoalDataAsync();
        SettingsStatusText.Text = "设置已从备份重新载入";
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
        target.EnableOvertimePayCap = source.EnableOvertimePayCap;
        target.MonthlyOvertimePayCap = source.MonthlyOvertimePayCap;
        target.ExcludeHolidayPayFromCap = source.ExcludeHolidayPayFromCap;
        target.OvertimePayCapEffectiveDate = source.OvertimePayCapEffectiveDate;
        target.MealAllowanceAmount = source.MealAllowanceAmount;
        target.FlexibleWorkStartEarliest = source.FlexibleWorkStartEarliest;
        target.FlexibleWorkStartLatest = source.FlexibleWorkStartLatest;
        target.WorkdayOvertimeStart = source.WorkdayOvertimeStart;
        target.MinimumOvertimeHours = source.MinimumOvertimeHours;
        target.RoundingMinutes = source.RoundingMinutes;
        target.DeductLeaveFromOvertime = source.DeductLeaveFromOvertime;
        target.WorkdayMealAllowanceMinimumHours = source.WorkdayMealAllowanceMinimumHours;
        target.NonWorkdayMealAllowanceMinimumHours = source.NonWorkdayMealAllowanceMinimumHours;
        target.AutoSyncHolidays = source.AutoSyncHolidays;
        target.HolidaySourceUrl = source.HolidaySourceUrl;
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
            TypeText = !info.IsOffDay
                ? "调休工作日"
                : ChineseStatutoryHoliday.IsStatutory(info)
                    ? "法定节假日"
                    : "假期休息日（按周末）";
        }

        public string DateText { get; }
        public string Name { get; }
        public string TypeText { get; }
    }
}
