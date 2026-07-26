using System.Text.Json;
using QHR.Models;

namespace QHR.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public SettingsService()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QHR.Overtime");
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string HolidayCacheDirectory => Path.Combine(DataDirectory, "holidays");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                           ?? new AppSettings();
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.EnumerateObject().Any(property =>
                    property.Name.Equals(nameof(AppSettings.SettingsVersion), StringComparison.OrdinalIgnoreCase)))
            {
                settings.SettingsVersion = 1;
            }
            Migrate(settings);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var temporaryPath = SettingsPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, SettingsPath, true);
    }

    private static void Migrate(AppSettings settings)
    {
        if (settings.SettingsVersion < 2)
        {
            settings.FlexibleWorkStartEarliest = "08:30";
            settings.FlexibleWorkStartLatest = "09:30";
            settings.WorkdayOvertimeStart = "19:00";
            settings.DeductLunchBreak = false;
            settings.DeductDinnerBreak = false;
            settings.DeductLeaveFromOvertime = true;
            settings.MealAllowanceAmount = 20m;
            settings.WorkdayMealAllowanceMinimumHours = 1;
            settings.NonWorkdayMealAllowanceMinimumHours = 4;
        }
        if (settings.SettingsVersion < 4)
        {
            // 1.2 起所有有效分钟都参与计费，不再默认按 30 分钟整段向下取整。
            settings.RoundingMinutes = 1;
        }
        if (settings.SettingsVersion < 5)
        {
            // 分钟也参与计费：最小加班门槛只影响旧版本，餐补仍使用独立的 1h/4h 门槛。
            settings.MinimumOvertimeHours = 0;
        }
        settings.SettingsVersion = 5;
    }
}
