using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using QHR.Models;

namespace QHR.Services;

public sealed class HolidayService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    public HolidayService(SettingsService settingsService, AppSettings settings)
    {
        _settingsService = settingsService;
        _settings = settings;
        Directory.CreateDirectory(_settingsService.HolidayCacheDirectory);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QHR-Overtime", "1.0"));
    }

    public string LastStatus { get; private set; } = "尚未同步节假日";

    public async Task<IReadOnlyDictionary<DateOnly, HolidayInfo>> GetCalendarAsync(
        DateOnly startDate,
        DateOnly endDate,
        bool forceSync = false,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<DateOnly, HolidayInfo>();
        var status = new List<string>();
        for (var year = startDate.Year; year <= endDate.Year; year++)
        {
            var (items, message) = await LoadOrSyncYearAsync(year, forceSync, cancellationToken);
            status.Add(message);
            foreach (var item in items.Where(item => item.Date >= startDate && item.Date <= endDate))
            {
                result[item.Date] = item;
            }
        }
        LastStatus = string.Join("；", status);
        return result;
    }

    private async Task<(IReadOnlyList<HolidayInfo> Items, string Message)> LoadOrSyncYearAsync(
        int year,
        bool forceSync,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(year);
        var cacheIsFresh = File.Exists(cachePath) &&
                           File.GetLastWriteTime(cachePath).Date == DateTime.Today;
        if (!forceSync && (!_settings.AutoSyncHolidays || cacheIsFresh))
        {
            var cached = await ReadCacheAsync(cachePath, cancellationToken);
            return (cached, $"{year} 年：本地缓存");
        }

        try
        {
            var merged = new Dictionary<DateOnly, HolidayInfo>();
            foreach (var sourceYear in new[] { year, year + 1 })
            {
                try
                {
                    var downloaded = await DownloadYearAsync(sourceYear, cancellationToken);
                    foreach (var item in downloaded.Where(item => item.Date.Year == year))
                    {
                        merged[item.Date] = item;
                    }
                }
                catch when (sourceYear == year + 1)
                {
                    // 下一年度文件只用于覆盖可能跨年的国务院安排，尚未发布时不影响当前年度。
                }
            }

            if (merged.Count == 0)
            {
                throw new InvalidOperationException("在线数据为空");
            }

            var items = merged.Values.OrderBy(item => item.Date).ToArray();
            await WriteCacheAsync(cachePath, items, cancellationToken);
            return (items, $"{year} 年：已在线更新 {items.Length} 条");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cached = await ReadCacheAsync(cachePath, cancellationToken);
            if (cached.Count > 0)
            {
                return (cached, $"{year} 年：在线更新失败，已使用缓存");
            }
            return (Array.Empty<HolidayInfo>(), $"{year} 年：无缓存，按普通周末判断（{ex.Message}）");
        }
    }

    private async Task<IReadOnlyList<HolidayInfo>> DownloadYearAsync(
        int year,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var url in BuildSourceUrls(year))
        {
            try
            {
                var json = await _httpClient.GetStringAsync(url, cancellationToken);
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("days", out var days) ||
                    days.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("节假日 JSON 缺少 days 数组");
                }

                var result = new List<HolidayInfo>();
                foreach (var day in days.EnumerateArray())
                {
                    if (!day.TryGetProperty("date", out var dateValue) ||
                        !DateOnly.TryParse(dateValue.GetString(), out var date)) continue;
                    var name = day.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "节假日" : "节假日";
                    var isOffDay = day.TryGetProperty("isOffDay", out var offValue) && offValue.GetBoolean();
                    result.Add(new HolidayInfo { Date = date, Name = name, IsOffDay = isOffDay });
                }
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }
        throw new InvalidOperationException(lastError?.Message ?? "没有可用的节假日数据源");
    }

    private IEnumerable<string> BuildSourceUrls(int year)
    {
        var urls = new[]
        {
            _settings.HolidaySourceUrl.Replace("{year}", year.ToString(), StringComparison.OrdinalIgnoreCase),
            $"https://fastly.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{year}.json",
            $"https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{year}.json"
        };
        return urls.Where(url => Uri.TryCreate(url, UriKind.Absolute, out _)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<HolidayInfo>> ReadCacheAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<HolidayInfo>();
            await using var stream = File.OpenRead(path);
            var result = await JsonSerializer.DeserializeAsync<List<HolidayInfo>>(stream, JsonOptions, cancellationToken);
            return result is null ? Array.Empty<HolidayInfo>() : result;
        }
        catch
        {
            return Array.Empty<HolidayInfo>();
        }
    }

    private static async Task WriteCacheAsync(
        string path,
        IReadOnlyList<HolidayInfo> items,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
        }
        File.Move(temporaryPath, path, true);
    }

    private string GetCachePath(int year) =>
        Path.Combine(_settingsService.HolidayCacheDirectory, $"{year}.json");

    public void Dispose() => _httpClient.Dispose();
}
