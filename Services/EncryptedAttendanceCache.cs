using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QHR.Models;

namespace QHR.Services;

/// <summary>
/// 使用 Windows DPAPI（CurrentUser）保存考勤快照。档案只能由当前 Windows 用户解密。
/// </summary>
public sealed class EncryptedAttendanceCache
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EncryptedAttendanceCache(SettingsService settingsService, string username)
        : this(settingsService.DataDirectory, username)
    {
    }

    public EncryptedAttendanceCache(string dataDirectory, string username)
    {
        var normalizedUsername = username.Trim().ToLowerInvariant();
        var usernameBytes = Encoding.UTF8.GetBytes(normalizedUsername);
        var accountHash = Convert.ToHexString(SHA256.HashData(usernameBytes))[..24].ToLowerInvariant();
        var directory = Path.Combine(dataDirectory, "secure");
        Directory.CreateDirectory(directory);
        CachePath = Path.Combine(directory, $"attendance-{accountHash}.qhrcache");
    }

    public string CachePath { get; }
    private string LeaveApprovalBackfillMarkerPath => CachePath + ".leave-v2";
    public bool NeedsLeaveApprovalBackfill =>
        File.Exists(CachePath) && !File.Exists(LeaveApprovalBackfillMarkerPath);

    public async Task MarkLeaveApprovalBackfillCompletedAsync(
        CancellationToken cancellationToken = default)
    {
        var temporaryPath = LeaveApprovalBackfillMarkerPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, "1", cancellationToken);
        File.Move(temporaryPath, LeaveApprovalBackfillMarkerPath, true);
    }

    public async Task<IReadOnlyList<AttendanceRecord>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(CachePath)) return Array.Empty<AttendanceRecord>();

            byte[]? plainBytes = null;
            try
            {
                var encryptedBytes = await File.ReadAllBytesAsync(CachePath, cancellationToken);
                plainBytes = UnprotectForCurrentUser(encryptedBytes);
                var envelope = JsonSerializer.Deserialize<AttendanceArchive>(plainBytes, JsonOptions);
                if (envelope is null || envelope.Version is < 1 or > 2)
                {
                    throw new InvalidDataException("不支持的本地考勤档案版本");
                }

                return envelope.Records
                    .GroupBy(item => item.Date)
                    .Select(group => group.Last().ToAttendanceRecord())
                    .OrderBy(item => item.Date)
                    .ToArray();
            }
            catch (Exception ex) when (ex is Win32Exception or JsonException or InvalidDataException)
            {
                PreserveUnreadableArchive();
                return Array.Empty<AttendanceRecord>();
            }
            finally
            {
                if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IEnumerable<AttendanceRecord> records,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var envelope = new AttendanceArchive
            {
                SavedAt = DateTimeOffset.Now,
                Records = records
                    .GroupBy(item => item.Date)
                    .Select(group => CachedAttendanceRecord.From(group.Last()))
                    .OrderBy(item => item.Date)
                    .ToList()
            };
            var plainBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            try
            {
                var encryptedBytes = ProtectForCurrentUser(plainBytes);
                var temporaryPath = CachePath + ".tmp";
                await File.WriteAllBytesAsync(temporaryPath, encryptedBytes, cancellationToken);
                File.Move(temporaryPath, CachePath, true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PreserveUnreadableArchive()
    {
        if (!File.Exists(CachePath)) return;
        var backupPath = CachePath + $".unreadable-{DateTime.Now:yyyyMMddHHmmss}.bak";
        File.Move(CachePath, backupPath, true);
    }

    internal static byte[] ProtectForCurrentUser(byte[] plainBytes) => Transform(plainBytes, true);
    internal static byte[] UnprotectForCurrentUser(byte[] encryptedBytes) => Transform(encryptedBytes, false);

    private static byte[] Transform(byte[] inputBytes, bool protect)
    {
        var input = new DataBlob
        {
            ByteCount = inputBytes.Length,
            DataPointer = Marshal.AllocHGlobal(inputBytes.Length)
        };
        var output = default(DataBlob);
        var descriptionPointer = IntPtr.Zero;
        try
        {
            Marshal.Copy(inputBytes, 0, input.DataPointer, inputBytes.Length);
            var success = protect
                ? CryptProtectData(
                    ref input,
                    "QHR 加班助手考勤档案",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output)
                : CryptUnprotectData(
                    ref input,
                    out descriptionPointer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output);
            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    protect ? "无法加密本地考勤档案" : "无法解密本地考勤档案");
            }

            var result = new byte[output.ByteCount];
            Marshal.Copy(output.DataPointer, result, 0, output.ByteCount);
            return result;
        }
        finally
        {
            ZeroAndFree(input.DataPointer, input.ByteCount, false);
            ZeroAndFree(output.DataPointer, output.ByteCount, true);
            if (descriptionPointer != IntPtr.Zero) LocalFree(descriptionPointer);
        }
    }

    private static void ZeroAndFree(IntPtr pointer, int length, bool localMemory)
    {
        if (pointer == IntPtr.Zero) return;
        if (length > 0) Marshal.Copy(new byte[length], 0, pointer, length);
        if (localMemory) LocalFree(pointer);
        else Marshal.FreeHGlobal(pointer);
    }

    private sealed class AttendanceArchive
    {
        public int Version { get; init; } = 2;
        public DateTimeOffset SavedAt { get; init; }
        public List<CachedAttendanceRecord> Records { get; init; } = [];
    }

    private sealed class CachedAttendanceRecord
    {
        public DateOnly Date { get; init; }
        public DateTime? ClockIn { get; init; }
        public DateTime? ClockOut { get; init; }
        public List<DateTime> CardTimes { get; init; } = [];
        public double LeaveHours { get; init; }
        public List<LeaveEntry> LeaveEntries { get; init; } = [];
        public double DelayedDeductionMinutes { get; init; }
        public double QhrMealAllowanceCount { get; init; }
        public string ShiftName { get; init; } = string.Empty;

        public static CachedAttendanceRecord From(AttendanceRecord record) => new()
        {
            Date = record.Date,
            ClockIn = record.ClockIn,
            ClockOut = record.ClockOut,
            CardTimes = record.CardTimes.OrderBy(item => item).ToList(),
            LeaveHours = record.LeaveHours,
            LeaveEntries = record.LeaveEntries.ToList(),
            DelayedDeductionMinutes = record.DelayedDeductionMinutes,
            QhrMealAllowanceCount = record.QhrMealAllowanceCount,
            ShiftName = record.ShiftName
        };

        public AttendanceRecord ToAttendanceRecord() => new()
        {
            Date = Date,
            ClockIn = ClockIn,
            ClockOut = ClockOut,
            CardTimes = CardTimes.OrderBy(item => item).ToArray(),
            LeaveHours = LeaveHours,
            LeaveEntries = (LeaveEntries ?? []).ToArray(),
            DelayedDeductionMinutes = DelayedDeductionMinutes,
            QhrMealAllowanceCount = QhrMealAllowanceCount,
            ShiftName = ShiftName
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int ByteCount;
        public IntPtr DataPointer;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
