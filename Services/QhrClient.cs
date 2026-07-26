using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QHR.Models;

namespace QHR.Services;

public sealed class QhrClient : IDisposable
{
    private const string AttendanceAuthKey = "G8_TRwtFegd0QeO2BfN6kg";
    private const string CardFormId = "220302";
    private const string AttendanceSummaryFormId = "220398";
    private const string WorkflowAuthKey = "EemqEmZ9Q66gX0E9WLPWww";
    private const string WorkflowTreeFormId = "290101";
    private const string WorkflowListFormId = "290104";
    private const string DelayedDeductionFlowId = "49";
    private const string LeaveApplicationFlowId = "7";
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly string _token;
    private readonly EncryptedAttendanceCache? _attendanceCache;
    private readonly HashSet<DateOnly> _monthsFetchedThisSession = [];
    private string _cookieHeader = string.Empty;

    public QhrClient(
        string baseUrl,
        string token,
        EncryptedAttendanceCache? attendanceCache = null)
    {
        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        _token = NormalizeToken(token);
        _attendanceCache = attendanceCache;
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
    }

    public bool IsLoggedIn => _cookieHeader.Length > 0;
    public string LastCacheStatus { get; private set; } = "本地加密档案尚无数据";

    public async Task<(IReadOnlyList<AttendanceRecord> Records, bool HasAllRequestedMonths)> LoadCachedAttendanceAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (_attendanceCache is null) return (Array.Empty<AttendanceRecord>(), false);
        try
        {
            var cachedRecords = await _attendanceCache.LoadAsync(cancellationToken);
            var requestedMonths = EnumerateMonths(startDate, endDate);
            bool HasMonth(DateOnly month) => _monthsFetchedThisSession.Contains(month) || cachedRecords.Any(item =>
                item.Date.Year == month.Year && item.Date.Month == month.Month);
            var result = cachedRecords
                .Where(item => item.Date >= startDate && item.Date <= endDate)
                .OrderBy(item => item.Date)
                .ToArray();
            var hasAllRequestedMonths = requestedMonths.All(HasMonth);
            LastCacheStatus = cachedRecords.Count > 0
                ? $"本地加密档案 {cachedRecords.Count} 天"
                : "本地加密档案尚无数据";
            return (result, hasAllRequestedMonths);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastCacheStatus = "本地加密档案读取失败";
            return (Array.Empty<AttendanceRecord>(), false);
        }
    }

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            throw new InvalidOperationException("QHR token 为空");
        }

        var baseCookie = $"quectel_token={_token}; quectel_lang=cn; MCLGID=1";
        using var initialRequest = CreateRequest(HttpMethod.Get, new Uri(_baseUri, "portal/custom"), baseCookie);
        using var initialResponse = await _httpClient.SendAsync(initialRequest, cancellationToken);
        var location = initialResponse.Headers.Location;
        if (location is null)
        {
            var body = await initialResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"QHR 未返回 SSO 跳转地址：{Shorten(body)}");
        }

        if (!location.IsAbsoluteUri)
        {
            location = new Uri(_baseUri, location);
        }

        var postUri = new UriBuilder(_baseUri) { Query = location.Query.TrimStart('?') }.Uri;
        using var postRequest = CreateRequest(HttpMethod.Post, postUri, baseCookie);
        postRequest.Headers.Referrer = location;
        postRequest.Headers.TryAddWithoutValidation("Origin", _baseUri.GetLeftPart(UriPartial.Authority));
        postRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["tk"] = _token,
            ["quectel_token"] = _token,
            ["lang"] = "cn",
            ["t"] = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
        });

        using var postResponse = await _httpClient.SendAsync(postRequest, cancellationToken);
        var mchrid = ReadCookie(postResponse, "MCHRID");
        if (string.IsNullOrWhiteSpace(mchrid) && IsRedirect(postResponse.StatusCode))
        {
            mchrid = await FollowForCookieAsync(postResponse, baseCookie, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(mchrid))
        {
            var body = await postResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"QHR 登录未返回 MCHRID：{Shorten(body)}");
        }

        _cookieHeader = $"MCHRID={mchrid}; MCLGID=1; quectel_token={_token}; quectel_lang=cn";
    }

    public async Task<IReadOnlyList<AttendanceRecord>> FetchAttendanceAsync(
        DateOnly startDate,
        DateOnly endDate,
        IProgress<string>? progress = null,
        bool refreshRecentMonths = true,
        CancellationToken cancellationToken = default)
    {
        if (endDate < startDate) throw new ArgumentException("结束日期不能早于开始日期");

        IReadOnlyList<AttendanceRecord> cachedRecords = Array.Empty<AttendanceRecord>();
        if (_attendanceCache is not null)
        {
            try
            {
                cachedRecords = await _attendanceCache.LoadAsync(cancellationToken);
                LastCacheStatus = cachedRecords.Count > 0
                    ? $"本地加密档案 {cachedRecords.Count} 天"
                    : "本地加密档案尚无数据";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastCacheStatus = "本地加密档案读取失败";
            }
        }

        var rawRecords = new List<CardLog>();
        var summaryRecords = new List<AttendanceSummary>();
        var onlineMonths = new List<DateOnly>();
        try
        {
            var currentMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
            var previousMonth = currentMonth.AddMonths(-1);
            var requestedStartMonth = new DateOnly(startDate.Year, startDate.Month, 1);
            var requestedEndMonth = new DateOnly(endDate.Year, endDate.Month, 1);
            var month = refreshRecentMonths && requestedStartMonth > previousMonth
                ? previousMonth
                : requestedStartMonth;
            var endMonth = refreshRecentMonths && requestedEndMonth < currentMonth
                ? currentMonth
                : requestedEndMonth;
            while (month <= endMonth)
            {
                var hasEncryptedMonth = cachedRecords.Any(item =>
                    item.Date.Year == month.Year && item.Date.Month == month.Month);
                if (month > currentMonth)
                {
                    progress?.Report($"{month:yyyy 年 MM 月} 尚未发生，已跳过");
                    month = month.AddMonths(1);
                    continue;
                }
                if ((!refreshRecentMonths || month < previousMonth) &&
                    (hasEncryptedMonth || _monthsFetchedThisSession.Contains(month)))
                {
                    progress?.Report($"正在使用 {month:yyyy 年 MM 月} 本地加密历史档案…");
                    month = month.AddMonths(1);
                    continue;
                }
                progress?.Report(month < previousMonth
                    ? $"{month:yyyy 年 MM 月} 本地无数据，正在尝试从 QHR 补取…"
                    : $"正在联网更新 {month:yyyy 年 MM 月} 打卡与考勤汇总…");
                // 同一运行会话内，同一个月无论成功、返回空或失败都只自动尝试一次；
                // 用户仍可通过头像菜单的主动刷新重新尝试本月和上月。
                _monthsFetchedThisSession.Add(month);
                if (!IsLoggedIn) await LoginAsync(cancellationToken);
                var cardsTask = FetchCardMonthAsync(month, cancellationToken);
                var summaryTask = FetchAttendanceSummaryMonthAsync(month, cancellationToken);
                await Task.WhenAll(cardsTask, summaryTask);
                rawRecords.AddRange(await cardsTask);
                summaryRecords.AddRange(await summaryTask);
                onlineMonths.Add(month);
                month = month.AddMonths(1);
            }
        }

        catch (Exception ex) when (ex is not OperationCanceledException &&
                                   (cachedRecords.Any(item => item.Date >= startDate && item.Date <= endDate) ||
                                    endDate < new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1)))
        {
            var fallback = cachedRecords
                .Where(item => item.Date >= startDate && item.Date <= endDate)
                .OrderBy(item => item.Date)
                .ToArray();
            LastCacheStatus = $"QHR 读取失败，已使用本地加密档案 {fallback.Length} 天";
            progress?.Report(LastCacheStatus);
            return fallback;
        }

        if (onlineMonths.Count == 0)
        {
            var localResult = cachedRecords
                .Where(item => item.Date >= startDate && item.Date <= endDate)
                .OrderBy(item => item.Date)
                .ToArray();
            LastCacheStatus = $"本地加密档案 {localResult.Length} 天（未联网）";
            progress?.Report(LastCacheStatus);
            return localResult;
        }

        var cardsByDate = rawRecords
            .GroupBy(item => item.ShiftDate)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.CardTime).OrderBy(value => value).ToArray());
        var summaryByDate = summaryRecords
            .GroupBy(item => item.Date)
            .ToDictionary(group => group.Key, group => group.Last());
        IReadOnlyDictionary<DateOnly, double> delayedDeductionsByDate;
        IReadOnlyDictionary<DateOnly, IReadOnlyList<LeaveEntry>> approvedLeavesByDate;
        var approvalCurrentMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var approvalOnlineStart = refreshRecentMonths
            ? approvalCurrentMonth.AddMonths(-1)
            : onlineMonths.Min();
        var approvalOnlineEnd = refreshRecentMonths
            ? DateOnly.FromDateTime(DateTime.Today)
            : EndOfMonth(onlineMonths.Max()) < DateOnly.FromDateTime(DateTime.Today)
                ? EndOfMonth(onlineMonths.Max())
                : DateOnly.FromDateTime(DateTime.Today);
        var needsHistoricalLeaveBackfill = _attendanceCache?.NeedsLeaveApprovalBackfill == true &&
                                           cachedRecords.Count > 0;
        var leaveSyncStart = needsHistoricalLeaveBackfill
            ? cachedRecords.Min(item => item.Date)
            : approvalOnlineStart;
        var leaveApprovalSyncSucceeded = false;
        var approvalRangeText = refreshRecentMonths
            ? "本月与上月"
            : $"{approvalOnlineStart:yyyy-MM} 至 {approvalOnlineEnd:yyyy-MM}";
        try
        {
            progress?.Report($"正在读取 {approvalRangeText} 已完成的延时工时抵扣申请…");
            delayedDeductionsByDate = await FetchDelayedDeductionsAsync(
                approvalOnlineStart,
                approvalOnlineEnd,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            delayedDeductionsByDate = new Dictionary<DateOnly, double>();
            progress?.Report("延时工时同步失败，正在使用本地加密档案补回…");
        }
        try
        {
            progress?.Report(needsHistoricalLeaveBackfill
                ? "正在一次性补齐本地历史请假审批档案…"
                : $"正在读取 {approvalRangeText} 已完成的请假申请…");
            var approvedLeaveResult = await FetchApprovedLeavesByDateAsync(
                leaveSyncStart,
                approvalOnlineEnd,
                cancellationToken);
            approvedLeavesByDate = approvedLeaveResult.LeavesByDate;
            leaveApprovalSyncSucceeded = approvedLeaveResult.BackfillComplete;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 单个审批流偶发不可用时不影响其他数据源，历史请假随后由加密档案补回。
            approvedLeavesByDate = new Dictionary<DateOnly, IReadOnlyList<LeaveEntry>>();
            progress?.Report("请假申请同步失败，正在使用本地加密档案补回…");
        }
        var today = DateOnly.FromDateTime(DateTime.Today);

        var freshRecords = cardsByDate.Keys
            .Concat(summaryByDate.Keys)
            .Concat(delayedDeductionsByDate.Keys)
            .Concat(approvedLeavesByDate.Keys)
            .Distinct()
            .OrderBy(date => date)
            .Select(date =>
            {
                var times = cardsByDate.GetValueOrDefault(date) ?? Array.Empty<DateTime>();
                var summary = summaryByDate.GetValueOrDefault(date);
                var leaveEntries = approvedLeavesByDate.GetValueOrDefault(date) ?? Array.Empty<LeaveEntry>();
                var approvedLeaveHours = leaveEntries.Sum(item => Math.Max(0, item.Hours));
                return new AttendanceRecord
                {
                    Date = date,
                    ClockIn = times.Length > 0 ? times[0] : null,
                    ClockOut = times.Length > 0 ? times[^1] : null,
                    CardTimes = times,
                    // QHR 当天尚未结算时会临时返回整日缺勤，避免把今天误判成请假。
                    // 请假审批流比月度考勤汇总更及时；取较大值可避免同一请假重复抵扣。
                    // 当天的月度汇总可能临时返回整日缺勤，只忽略汇总值；已完成审批仍按实际日期保留。
                    LeaveHours = Math.Max(
                        date < today ? Math.Max(0, summary?.LeaveHours ?? 0) : 0,
                        approvedLeaveHours),
                    LeaveEntries = leaveEntries,
                    DelayedDeductionMinutes = Math.Max(0, delayedDeductionsByDate.GetValueOrDefault(date)),
                    QhrMealAllowanceCount = Math.Max(0, summary?.MealAllowanceCount ?? 0),
                    ShiftName = summary?.ShiftName ?? string.Empty
                };
            })
            .ToArray();

        var mergedRecords = MergeAttendanceRecords(cachedRecords, freshRecords);
        if (_attendanceCache is not null)
        {
            progress?.Report("正在加密保存本地考勤档案…");
            try
            {
                await _attendanceCache.SaveAsync(mergedRecords, cancellationToken);
                if (leaveApprovalSyncSucceeded)
                {
                    try
                    {
                        await _attendanceCache.MarkLeaveApprovalBackfillCompletedAsync(cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 标记失败只会导致下次再次核对历史请假，不影响已经保存的加密考勤档案。
                    }
                }
                LastCacheStatus = $"本地加密已保存 {mergedRecords.Count} 天";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastCacheStatus = "本地加密保存失败";
            }
        }

        return mergedRecords
            .Where(item => item.Date >= startDate && item.Date <= endDate)
            .OrderBy(item => item.Date)
            .ToArray();
    }

    private static IReadOnlyList<DateOnly> EnumerateMonths(DateOnly startDate, DateOnly endDate)
    {
        var months = new List<DateOnly>();
        var month = new DateOnly(startDate.Year, startDate.Month, 1);
        var endMonth = new DateOnly(endDate.Year, endDate.Month, 1);
        while (month <= endMonth)
        {
            months.Add(month);
            month = month.AddMonths(1);
        }
        return months;
    }

    private static DateOnly EndOfMonth(DateOnly month) =>
        new(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));

    private static IReadOnlyList<AttendanceRecord> MergeAttendanceRecords(
        IEnumerable<AttendanceRecord> cachedRecords,
        IEnumerable<AttendanceRecord> freshRecords)
    {
        var merged = cachedRecords
            .GroupBy(item => item.Date)
            .ToDictionary(group => group.Key, group => group.Last());
        foreach (var fresh in freshRecords)
        {
            if (!merged.TryGetValue(fresh.Date, out var cached))
            {
                merged[fresh.Date] = fresh;
                continue;
            }

            var cardTimes = fresh.CardTimes.Count > 0
                ? fresh.CardTimes.OrderBy(item => item).ToArray()
                : cached.CardTimes.OrderBy(item => item).ToArray();
            var leaveHours = fresh.LeaveHours > 0
                ? fresh.LeaveHours
                : cached.LeaveHours;
            var leaveEntries = fresh.LeaveEntries.Count > 0
                ? fresh.LeaveEntries
                : cached.LeaveEntries;
            merged[fresh.Date] = new AttendanceRecord
            {
                Date = fresh.Date,
                ClockIn = cardTimes.Length > 0 ? cardTimes[0] : fresh.ClockIn ?? cached.ClockIn,
                ClockOut = cardTimes.Length > 0 ? cardTimes[^1] : fresh.ClockOut ?? cached.ClockOut,
                CardTimes = cardTimes,
                LeaveHours = Math.Max(0, leaveHours),
                LeaveEntries = leaveEntries.ToArray(),
                DelayedDeductionMinutes = fresh.DelayedDeductionMinutes > 0
                    ? fresh.DelayedDeductionMinutes
                    : cached.DelayedDeductionMinutes,
                QhrMealAllowanceCount = fresh.QhrMealAllowanceCount > 0
                    ? fresh.QhrMealAllowanceCount
                    : cached.QhrMealAllowanceCount,
                ShiftName = !string.IsNullOrWhiteSpace(fresh.ShiftName)
                    ? fresh.ShiftName
                    : cached.ShiftName
            };
        }

        return merged.Values.OrderBy(item => item.Date).ToArray();
    }

    private async Task<IReadOnlyDictionary<DateOnly, double>> FetchDelayedDeductionsAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var treePayload = JsonSerializer.Serialize(new
        {
            appParam = new { TREEID = 49 },
            appFnKey = "SW0101",
            formData = new { }
        });
        var treePath = $"ajax/function/atree!{WorkflowAuthKey}.{WorkflowTreeFormId}";
        var treeBody = await PostJsonAsync(treePath, treePayload, true, cancellationToken);
        JsonElement delayFlowNode;
        using (var treeDocument = JsonDocument.Parse(treeBody))
        {
            if (!TryFindDelayedFlowNode(treeDocument.RootElement, out delayFlowNode))
            {
                throw new InvalidOperationException("QHR 未返回延时工时扣减流程节点");
            }
        }

        var listPayload = JsonSerializer.Serialize(new
        {
            searchcols = string.Empty,
            order = "desc",
            limit = 500,
            offset = 0,
            total = 0,
            editType = 0,
            form = new
            {
                appParam = new { TREEID = 49 },
                appFnKey = "SW0104",
                formData = new
                {
                    SW0101 = new
                    {
                        fnparam = new { },
                        data = new[] { delayFlowNode },
                        old = Array.Empty<object>(),
                        bnparam = new { }
                    }
                }
            }
        });
        var listPath = $"ajax/function/glist!{WorkflowAuthKey}.{WorkflowListFormId}";
        var listBody = await PostJsonAsync(listPath, listPayload, true, cancellationToken);
        var candidates = new List<DelayedApprovalCandidate>();
        using (var listDocument = JsonDocument.Parse(listBody))
        {
            CollectDelayedApprovalCandidates(listDocument.RootElement, candidates);
        }

        var relevantCandidates = candidates
            .Where(item => item.DateHint is null ||
                           item.DateHint.Value >= startDate && item.DateHint.Value <= endDate)
            .GroupBy(item => item.AuthKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        using var gate = new SemaphoreSlim(4, 4);
        var detailTasks = relevantCandidates.Select(async candidate =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await FetchDelayedDeductionDetailAsync(candidate.AuthKey, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        var detailResults = await Task.WhenAll(detailTasks);

        return detailResults
            .SelectMany(item => item)
            .Where(item => item.Date >= startDate && item.Date <= endDate && item.Minutes > 0)
            .GroupBy(item => item.Date)
            .ToDictionary(
                group => group.Key,
                group => Math.Round(group
                    .GroupBy(item => string.IsNullOrWhiteSpace(item.RecordId)
                        ? $"{item.Date:yyyy-MM-dd}:{item.Minutes:0.####}"
                        : item.RecordId,
                        StringComparer.Ordinal)
                    .Sum(item => item.First().Minutes), 2, MidpointRounding.AwayFromZero));
    }

    private async Task<ApprovedLeaveFetchResult> FetchApprovedLeavesByDateAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var treePayload = JsonSerializer.Serialize(new
        {
            appParam = new { TREEID = 7 },
            appFnKey = "SW0101",
            formData = new { }
        });
        var treePath = $"ajax/function/atree!{WorkflowAuthKey}.{WorkflowTreeFormId}";
        var treeBody = await PostJsonAsync(treePath, treePayload, true, cancellationToken);
        JsonElement leaveFlowNode;
        using (var treeDocument = JsonDocument.Parse(treeBody))
        {
            if (!TryFindWorkflowNode(treeDocument.RootElement, LeaveApplicationFlowId, out leaveFlowNode))
            {
                throw new InvalidOperationException("QHR 未返回请假申请流程节点");
            }
        }

        var listPayload = JsonSerializer.Serialize(new
        {
            searchcols = string.Empty,
            order = "desc",
            limit = 500,
            offset = 0,
            total = 0,
            editType = 0,
            form = new
            {
                appParam = new { TREEID = 7 },
                appFnKey = "SW0104",
                formData = new
                {
                    SW0101 = new
                    {
                        fnparam = new { },
                        data = new[] { leaveFlowNode },
                        old = Array.Empty<object>(),
                        bnparam = new { }
                    }
                }
            }
        });
        var listPath = $"ajax/function/glist!{WorkflowAuthKey}.{WorkflowListFormId}";
        var listBody = await PostJsonAsync(listPath, listPayload, true, cancellationToken);
        var candidates = new List<WorkflowApprovalCandidate>();
        using (var listDocument = JsonDocument.Parse(listBody))
        {
            CollectWorkflowApprovalCandidates(
                listDocument.RootElement,
                LeaveApplicationFlowId,
                candidates);
        }

        var relevantCandidates = candidates
            .Where(item => item.DateHint is null ||
                           item.DateHint.Value >= startDate.AddMonths(-1) &&
                           item.DateHint.Value <= endDate)
            .GroupBy(item => item.AuthKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        using var gate = new SemaphoreSlim(4, 4);
        var detailTasks = relevantCandidates.Select(async candidate =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var details = await FetchApprovedLeaveDetailAsync(candidate.AuthKey, cancellationToken);
                if (details.Count > 0)
                {
                    var normalizedDetails = details
                        .Select(item => item.Kind == LeaveKind.Unknown && candidate.KindHint != LeaveKind.Unknown
                            ? item with { Kind = candidate.KindHint, TypeName = candidate.TypeNameHint }
                            : item)
                        .ToArray();
                    return new ApprovedLeaveCandidateResult(
                        normalizedDetails,
                        normalizedDetails.Any(item => item.Kind == LeaveKind.Unknown));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单条详情失败时保留列表页提示，未知类型只展示、不抵扣，避免误扣加班费。
            }
            finally
            {
                gate.Release();
            }
            // 列表页日期可能是申请日期，不拿它冒充实际请假日期；等待下次详情同步或使用本地档案。
            return new ApprovedLeaveCandidateResult([], true);
        }).ToArray();
        var detailResults = await Task.WhenAll(detailTasks);

        var leavesByDate = detailResults
            .SelectMany(item => item.Leaves)
            .Where(item => item.Date >= startDate && item.Date <= endDate && item.Hours > 0)
            .GroupBy(item => item.Date)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<LeaveEntry>)group
                    .GroupBy(item => string.IsNullOrWhiteSpace(item.RecordId)
                        ? $"{item.Date:yyyy-MM-dd}:{item.Kind}:{item.Hours:0.####}"
                        : item.RecordId,
                        StringComparer.Ordinal)
                    .Select(item => item.First())
                    .GroupBy(item => new { item.Kind, item.TypeName })
                    .Select(items => new LeaveEntry
                    {
                        Kind = items.Key.Kind,
                        SourceTypeName = items.Key.TypeName,
                        Hours = Math.Round(items.Sum(item => item.Hours), 4,
                            MidpointRounding.AwayFromZero)
                    })
                    .OrderBy(item => item.Kind)
                    .ToArray());
        return new ApprovedLeaveFetchResult(
            leavesByDate,
            detailResults.All(item => !item.HadUnresolvedType));
    }

    private async Task<IReadOnlyList<ApprovedLeave>> FetchApprovedLeaveDetailAsync(
        string authKey,
        CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(authKey, "^[A-Za-z0-9_-]+$"))
        {
            throw new InvalidOperationException("QHR 请假详情键格式无效");
        }

        var payload = JsonSerializer.Serialize(new
        {
            formData = new { },
            bizData = new { },
            bizFnKey = (string?)null,
            comments = (string?)null,
            fileIds = (string?)null,
            signData = (string?)null,
            receivers = (string?)null,
            freeNode = (string?)null
        });
        var body = await PostJsonAsync(
            $"ajax/flowform/formlist!{authKey}",
            payload,
            true,
            cancellationToken);
        using var document = JsonDocument.Parse(body);
        var result = new List<ApprovedLeave>();
        CollectApprovedLeaves(document.RootElement, result);
        return result;
    }

    private async Task<IReadOnlyList<DelayedDeduction>> FetchDelayedDeductionDetailAsync(
        string authKey,
        CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(authKey, "^[A-Za-z0-9_-]+$"))
        {
            throw new InvalidOperationException("QHR 延时工时详情键格式无效");
        }

        var payload = JsonSerializer.Serialize(new
        {
            formData = new { },
            bizData = new { },
            bizFnKey = (string?)null,
            comments = (string?)null,
            fileIds = (string?)null,
            signData = (string?)null,
            receivers = (string?)null,
            freeNode = (string?)null
        });
        var body = await PostJsonAsync(
            $"ajax/flowform/formlist!{authKey}",
            payload,
            true,
            cancellationToken);
        using var document = JsonDocument.Parse(body);
        var result = new List<DelayedDeduction>();
        CollectDelayedDeductions(document.RootElement, result);
        return result;
    }

    private static bool TryFindDelayedFlowNode(JsonElement element, out JsonElement nodeData)
        => TryFindWorkflowNode(element, DelayedDeductionFlowId, out nodeData);

    private static bool TryFindWorkflowNode(
        JsonElement element,
        string flowId,
        out JsonElement nodeData)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(element, "id", out var id) && id == flowId &&
                TryGetProperty(element, "data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                nodeData = data.Clone();
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindWorkflowNode(property.Value, flowId, out nodeData)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindWorkflowNode(item, flowId, out nodeData)) return true;
            }
        }
        nodeData = default;
        return false;
    }

    private static void CollectWorkflowApprovalCandidates(
        JsonElement element,
        string flowId,
        ICollection<WorkflowApprovalCandidate> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(element, "FLOWID", out var itemFlowId) &&
                itemFlowId == flowId &&
                TryGetString(element, "STATUS", out var status) && IsCompletedStatus(status) &&
                TryGetString(element, "AUTHKEY", out var authKey))
            {
                var abstracts = TryGetString(element, "ABSTRACTS", out var text) ? text : string.Empty;
                var kindHint = ReadLeaveKind(abstracts, out var typeNameHint);
                result.Add(new WorkflowApprovalCandidate(
                    authKey,
                    TryReadDateHint(abstracts),
                    TryReadLeaveHoursHint(abstracts),
                    kindHint,
                    typeNameHint));
                return;
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectWorkflowApprovalCandidates(property.Value, flowId, result);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectWorkflowApprovalCandidates(item, flowId, result);
            }
        }
    }

    private static void CollectDelayedApprovalCandidates(
        JsonElement element,
        ICollection<DelayedApprovalCandidate> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(element, "FLOWID", out var flowId) &&
                flowId == DelayedDeductionFlowId &&
                TryGetString(element, "STATUS", out var status) && IsCompletedStatus(status) &&
                TryGetString(element, "AUTHKEY", out var authKey))
            {
                var abstracts = TryGetString(element, "ABSTRACTS", out var text) ? text : string.Empty;
                result.Add(new DelayedApprovalCandidate(authKey, TryReadDateHint(abstracts)));
                return;
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectDelayedApprovalCandidates(property.Value, result);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectDelayedApprovalCandidates(item, result);
            }
        }
    }

    private static void CollectDelayedDeductions(
        JsonElement element,
        ICollection<DelayedDeduction> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetDouble(element, "AMOUNT", out var minutes) && minutes > 0)
            {
                var dateText = TryGetString(element, "SHIFTTERM", out var shiftTerm)
                    ? shiftTerm
                    : TryGetString(element, "BEGINTIME", out var beginTime)
                        ? beginTime
                        : string.Empty;
                if (TryParseDate(dateText, out var date))
                {
                    var recordId = TryGetString(element, "ID", out var id) ? id : string.Empty;
                    result.Add(new DelayedDeduction(date, minutes, recordId));
                    return;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectDelayedDeductions(property.Value, result);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectDelayedDeductions(item, result);
            }
        }
    }

    private static void CollectApprovedLeaves(
        JsonElement element,
        ICollection<ApprovedLeave> result,
        LeaveKind inheritedKind = LeaveKind.Unknown,
        string inheritedTypeName = "")
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var localKind = ReadLeaveKindFromObject(element, out var localTypeName);
            if (localKind == LeaveKind.Unknown)
            {
                localKind = inheritedKind;
                localTypeName = inheritedTypeName;
            }
            if (TryGetDouble(element, "AMOUNT", out var hours) && hours > 0 &&
                TryGetString(element, "BEGINTIME", out var beginText) &&
                TryParseDateTime(beginText, out var beginTime))
            {
                var recordId = TryGetString(element, "ID", out var id) ? id : string.Empty;
                DateTime? endTime = null;
                if (TryGetString(element, "ENDTIME", out var endText) &&
                    TryParseDateTime(endText, out var parsedEnd))
                {
                    endTime = parsedEnd;
                }
                AddApprovedLeaveDays(result, beginTime, endTime, hours, localKind, localTypeName, recordId);
                return;
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectApprovedLeaves(property.Value, result, localKind, localTypeName);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectApprovedLeaves(item, result, inheritedKind, inheritedTypeName);
            }
        }
    }

    private static DateOnly? TryReadDateHint(string text)
    {
        var match = Regex.Match(text, @"\b\d{4}-\d{2}-\d{2}\b");
        return match.Success && TryParseDate(match.Value, out var date) ? date : null;
    }

    private static double TryReadLeaveHoursHint(string text)
    {
        var match = Regex.Match(
            text,
            @"\|\s*(?<hours>\d+(?:\.\d+)?)\s*(?:小时|hours?)\s*\|",
            RegexOptions.IgnoreCase);
        return match.Success &&
               double.TryParse(match.Groups["hours"].Value, NumberStyles.Float,
                   CultureInfo.InvariantCulture, out var hours)
            ? Math.Max(0, hours)
            : 0;
    }

    private static LeaveKind ReadLeaveKindFromObject(JsonElement element, out string typeName)
    {
        var preferredNames = new[]
        {
            "LEAVETYPE", "LEAVE_TYPE", "VACATIONTYPE", "VACATION_TYPE", "ABSTYPE", "ABSENCE_TYPE",
            "HOLIDAYTYPE", "HOLIDAY_TYPE", "HOLTYPE", "LEAVEKIND", "LEAVE_KIND", "VACATIONKIND",
            "TYPE", "TYPENAME", "TYPE_NAME", "KIND", "KINDNAME"
        };
        foreach (var name in preferredNames)
        {
            if (!TryGetString(element, name, out var value)) continue;
            var kind = ReadLeaveKind(value, out typeName);
            if (kind != LeaveKind.Unknown) return kind;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            var kind = ReadLeaveKind(property.Value.GetString() ?? string.Empty, out typeName);
            if (kind != LeaveKind.Unknown) return kind;
        }
        typeName = string.Empty;
        return LeaveKind.Unknown;
    }

    private static LeaveKind ReadLeaveKind(string text, out string typeName)
    {
        if (text.Contains("年假", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("annual leave", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("annual vacation", StringComparison.OrdinalIgnoreCase))
        {
            typeName = "年假";
            return LeaveKind.Annual;
        }
        if (text.Contains("事假", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("personal leave", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unpaid leave", StringComparison.OrdinalIgnoreCase))
        {
            typeName = "事假";
            return LeaveKind.Personal;
        }
        typeName = string.Empty;
        return LeaveKind.Unknown;
    }

    private static void AddApprovedLeaveDays(
        ICollection<ApprovedLeave> result,
        DateTime beginTime,
        DateTime? endTime,
        double totalHours,
        LeaveKind kind,
        string typeName,
        string recordId)
    {
        var startDate = DateOnly.FromDateTime(beginTime);
        var finalDate = endTime is null || endTime.Value < beginTime
            ? startDate
            : DateOnly.FromDateTime(endTime.Value);
        var dates = new List<DateOnly>();
        for (var date = startDate; date <= finalDate; date = date.AddDays(1))
        {
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) dates.Add(date);
        }
        if (dates.Count == 0) dates.Add(startDate);

        var remaining = Math.Max(0, totalHours);
        for (var index = 0; index < dates.Count && remaining > 0; index++)
        {
            var date = dates[index];
            var isLast = index == dates.Count - 1;
            var capacity = isLast ? remaining : Math.Min(8d, remaining);
            var allocated = Math.Round(Math.Max(0, capacity), 4, MidpointRounding.AwayFromZero);
            if (allocated <= 0) continue;
            result.Add(new ApprovedLeave(
                date,
                allocated,
                kind,
                typeName,
                string.IsNullOrWhiteSpace(recordId) ? string.Empty : $"{recordId}:{date:yyyyMMdd}"));
            remaining = Math.Max(0, remaining - allocated);
        }
    }

    private static bool IsCompletedStatus(string status) =>
        status.Equals("完成", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Finish", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Finished", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Completed", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<CardLog>> FetchCardMonthAsync(
        DateOnly month,
        CancellationToken cancellationToken)
    {
        var payload = CreateMonthPayload(month, "SE0302");
        var path = $"ajax/function/alist!{AttendanceAuthKey}.{CardFormId}";
        var body = await PostJsonAsync(path, payload, true, cancellationToken);
        using var document = JsonDocument.Parse(body);
        var result = new List<CardLog>();
        CollectCardLogs(document.RootElement, result);
        return result;
    }

    private async Task<IReadOnlyList<AttendanceSummary>> FetchAttendanceSummaryMonthAsync(
        DateOnly month,
        CancellationToken cancellationToken)
    {
        var payload = CreateMonthPayload(month, "SE0398");
        var path = $"ajax/function/alist!{AttendanceAuthKey}.{AttendanceSummaryFormId}";
        var body = await PostJsonAsync(path, payload, true, cancellationToken);
        using var document = JsonDocument.Parse(body);
        var result = new List<AttendanceSummary>();
        CollectAttendanceSummaries(document.RootElement, result);
        return result;
    }

    private static string CreateMonthPayload(DateOnly month, string functionKey) =>
        JsonSerializer.Serialize(new
        {
            appParam = new { TERM = $"{month:yyyy-MM-dd}T00:00:00.000Z" },
            appFnKey = functionKey,
            formData = new { }
        });

    private async Task<string> PostJsonAsync(
        string relativePath,
        string json,
        bool retryOnExpiredSession,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, new Uri(_baseUri, relativePath), _cookieHeader);
        request.Headers.Referrer = _baseUri;
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (retryOnExpiredSession && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _cookieHeader = string.Empty;
            await LoginAsync(cancellationToken);
            return await PostJsonAsync(relativePath, json, false, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"QHR 请求失败 HTTP {(int)response.StatusCode}：{Shorten(body)}");
        }
        return body;
    }

    private async Task<string> FollowForCookieAsync(
        HttpResponseMessage response,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var current = response;
        for (var i = 0; i < 4 && current.Headers.Location is not null; i++)
        {
            var target = current.Headers.Location.IsAbsoluteUri
                ? current.Headers.Location
                : new Uri(_baseUri, current.Headers.Location);
            using var request = CreateRequest(HttpMethod.Get, target, cookieHeader);
            var next = await _httpClient.SendAsync(request, cancellationToken);
            var cookie = ReadCookie(next, "MCHRID");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                next.Dispose();
                return cookie;
            }
            if (!ReferenceEquals(current, response)) current.Dispose();
            current = next;
        }
        if (!ReferenceEquals(current, response)) current.Dispose();
        return string.Empty;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string cookieHeader)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) QHR-Overtime/1.0");
        return request;
    }

    private static string ReadCookie(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return string.Empty;
        var prefix = name + "=";
        foreach (var value in values)
        {
            foreach (var part in value.Split(','))
            {
                var text = part.Trim();
                var index = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                var start = index + prefix.Length;
                var end = text.IndexOf(';', start);
                return (end < 0 ? text[start..] : text[start..end]).Trim();
            }
        }
        return string.Empty;
    }

    private static void CollectCardLogs(JsonElement element, ICollection<CardLog> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(element, "CARDTIME", out var cardTimeText) &&
                DateTime.TryParseExact(cardTimeText, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var cardTime))
            {
                var shiftText = TryGetString(element, "SHIFTTERM", out var value)
                    ? value
                    : cardTime.ToString("yyyy-MM-dd");
                if (DateOnly.TryParseExact(shiftText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var shiftDate))
                {
                    result.Add(new CardLog(shiftDate, cardTime));
                }
                return;
            }
            foreach (var property in element.EnumerateObject()) CollectCardLogs(property.Value, result);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) CollectCardLogs(item, result);
        }
    }

    private static void CollectAttendanceSummaries(
        JsonElement element,
        ICollection<AttendanceSummary> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(element, "TERM", out var termText) &&
                TryParseDate(termText, out var date) &&
                (HasProperty(element, "ABST") || HasProperty(element, "ABST_1") ||
                 HasProperty(element, "C018") || HasProperty(element, "C018_1")))
            {
                var leaveHours = ReadPreferredNumber(element, "ABST_1", "ABST");
                var mealCount = ReadPreferredNumber(element, "C018_1", "C018");
                var shiftName = TryGetString(element, "SHIFT", out var shift) ? shift : string.Empty;
                result.Add(new AttendanceSummary(date, leaveHours, mealCount, shiftName));
                return;
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectAttendanceSummaries(property.Value, result);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectAttendanceSummaries(item, result);
            }
        }
    }

    private static double ReadPreferredNumber(JsonElement element, string preferredName, string fallbackName)
    {
        if (TryGetDouble(element, preferredName, out var preferred) && preferred > 0) return preferred;
        return TryGetDouble(element, fallbackName, out var fallback) ? fallback : Math.Max(0, preferred);
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out value))
            {
                return true;
            }

            var text = property.Value.ToString().Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }
        value = 0;
        return false;
    }

    private static bool HasProperty(JsonElement element, string name) =>
        element.EnumerateObject().Any(property =>
            property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryParseDateTime(string value, out DateTime dateTime)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces, out dateTime) ||
               DateTime.TryParse(value, CultureInfo.CurrentCulture,
                   DateTimeStyles.AllowWhiteSpaces, out dateTime);
    }

    private static bool TryParseDate(string value, out DateOnly date)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date)) return true;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var dateTime))
        {
            date = DateOnly.FromDateTime(dateTime);
            return true;
        }
        date = default;
        return false;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value.ToString().Trim();
            return value.Length > 0;
        }
        value = string.Empty;
        return false;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => (int)statusCode is >= 300 and < 400;
    private static string NormalizeToken(string token) =>
        token.Trim().StartsWith("bearer", StringComparison.OrdinalIgnoreCase)
            ? token.Trim()[6..].Trim()
            : token.Trim();
    private static string Shorten(string text) => text.Length <= 180 ? text : text[..180];

    public void Dispose() => _httpClient.Dispose();

    private sealed record CardLog(DateOnly ShiftDate, DateTime CardTime);
    private sealed record AttendanceSummary(
        DateOnly Date,
        double LeaveHours,
        double MealAllowanceCount,
        string ShiftName);
    private sealed record DelayedApprovalCandidate(string AuthKey, DateOnly? DateHint);
    private sealed record DelayedDeduction(DateOnly Date, double Minutes, string RecordId);
    private sealed record WorkflowApprovalCandidate(
        string AuthKey,
        DateOnly? DateHint,
        double HoursHint,
        LeaveKind KindHint,
        string TypeNameHint);
    private sealed record ApprovedLeave(
        DateOnly Date,
        double Hours,
        LeaveKind Kind,
        string TypeName,
        string RecordId);
    private sealed record ApprovedLeaveCandidateResult(
        IReadOnlyList<ApprovedLeave> Leaves,
        bool HadUnresolvedType);
    private sealed record ApprovedLeaveFetchResult(
        IReadOnlyDictionary<DateOnly, IReadOnlyList<LeaveEntry>> LeavesByDate,
        bool BackfillComplete);
}
