using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace QHR.Models;

public sealed class NetworkCaptureRecord : INotifyPropertyChanged
{
    private int? _statusCode;
    private string _responseHeaders = string.Empty;
    private string _responseBody = string.Empty;

    public string RequestId { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
    public string ResourceType { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string RequestHeaders { get; init; } = string.Empty;
    public string RequestBody { get; init; } = string.Empty;

    public int? StatusCode
    {
        get => _statusCode;
        set
        {
            if (_statusCode == value) return;
            _statusCode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(Details));
        }
    }

    public string StatusText => StatusCode?.ToString() ?? "…";

    public string ResponseHeaders
    {
        get => _responseHeaders;
        set
        {
            if (_responseHeaders == value) return;
            _responseHeaders = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Details));
        }
    }

    public string ResponseBody
    {
        get => _responseBody;
        set
        {
            if (_responseBody == value) return;
            _responseBody = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Details));
        }
    }

    public string Details
    {
        get
        {
            var builder = new StringBuilder();
            builder.AppendLine($"[{Method}] {Url}");
            builder.AppendLine($"类型：{ResourceType}    状态：{StatusText}    时间：{Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            AppendSection(builder, "请求头（已脱敏）", RequestHeaders);
            AppendSection(builder, "请求正文（已脱敏）", RequestBody);
            AppendSection(builder, "响应头（已脱敏）", ResponseHeaders);
            AppendSection(builder, "响应正文", ResponseBody);
            return builder.ToString();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static void AppendSection(StringBuilder builder, string title, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        builder.AppendLine();
        builder.AppendLine($"--- {title} ---");
        builder.AppendLine(content);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
