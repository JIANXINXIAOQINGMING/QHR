using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using QHR.Models;

namespace QHR.Views;

public partial class QhrCaptureWindow : Window
{
    private const int MaximumRecords = 5000;
    private const int MaximumResponseBodyLength = 1_000_000;
    private static readonly JsonSerializerOptions ExportJsonOptions = new() { WriteIndented = true };

    private readonly string _startUrl;
    private readonly ObservableCollection<NetworkCaptureRecord> _records = [];
    private readonly Dictionary<string, NetworkCaptureRecord> _recordsById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _jsonResponseIds = new(StringComparer.Ordinal);
    private CoreWebView2DevToolsProtocolEventReceiver? _requestReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _responseReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _loadingReceiver;
    private bool _capturing = true;
    private bool _initialized;
    private string _exportPath;

    public QhrCaptureWindow(string qhrBaseUrl = "https://hr.quectel.com")
    {
        InitializeComponent();
        _startUrl = qhrBaseUrl.TrimEnd('/') + "/portal/custom";
        _exportPath = CreateExportPath();
        CaptureDataGrid.ItemsSource = _records;
        AddressTextBox.Text = _startUrl;
        Loaded += QhrCaptureWindow_Loaded;
        Closing += QhrCaptureWindow_Closing;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
    }

    private async void QhrCaptureWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QHR.Overtime",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            ConfigureBrowser();
            await EnableNetworkCaptureAsync();
            _initialized = true;
            BrowserStatusText.Text = "浏览器已就绪 · 网络请求记录中";
            Browser.CoreWebView2.Navigate(_startUrl);
        }
        catch (Exception ex)
        {
            BrowserStatusText.Text = "内置浏览器初始化失败";
            MessageBox.Show(this, $"无法初始化 WebView2：{ex.Message}\n\n请确认已安装 Microsoft Edge WebView2 Runtime。",
                "QHR 请求诊断", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ConfigureBrowser()
    {
        var core = Browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, args) =>
        {
            AddressTextBox.Text = args.Uri;
            BrowserStatusText.Text = "正在加载…";
        };
        core.NavigationCompleted += (_, args) =>
        {
            AddressTextBox.Text = core.Source;
            BrowserStatusText.Text = args.IsSuccess
                ? $"页面已加载 · 捕获 {_records.Count} 条请求"
                : $"页面加载失败：{args.WebErrorStatus}";
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            core.Navigate(args.Uri);
        };
    }

    private async Task EnableNetworkCaptureAsync()
    {
        var core = Browser.CoreWebView2;
        await core.CallDevToolsProtocolMethodAsync("Network.enable",
            "{\"maxTotalBufferSize\":50000000,\"maxResourceBufferSize\":2000000}");

        _requestReceiver = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        _responseReceiver = core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
        _loadingReceiver = core.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
        _requestReceiver.DevToolsProtocolEventReceived += RequestReceiver_DevToolsProtocolEventReceived;
        _responseReceiver.DevToolsProtocolEventReceived += ResponseReceiver_DevToolsProtocolEventReceived;
        _loadingReceiver.DevToolsProtocolEventReceived += LoadingReceiver_DevToolsProtocolEventReceived;
    }

    private void RequestReceiver_DevToolsProtocolEventReceived(
        object? sender,
        CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!_capturing) return;
        try
        {
            using var document = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = document.RootElement;
            var requestId = GetString(root, "requestId");
            if (requestId.Length == 0 || !root.TryGetProperty("request", out var request)) return;

            var record = new NetworkCaptureRecord
            {
                RequestId = requestId,
                Timestamp = DateTimeOffset.Now,
                ResourceType = GetString(root, "type"),
                Method = GetString(request, "method"),
                Url = RedactUrl(GetString(request, "url")),
                RequestHeaders = request.TryGetProperty("headers", out var headers)
                    ? SerializeRedacted(headers)
                    : string.Empty,
                RequestBody = request.TryGetProperty("postData", out var postData) && postData.ValueKind == JsonValueKind.String
                    ? RedactPayload(postData.GetString() ?? string.Empty)
                    : string.Empty
            };

            if (_records.Count >= MaximumRecords)
            {
                var oldest = _records[0];
                _records.RemoveAt(0);
                _recordsById.Remove(oldest.RequestId);
                _jsonResponseIds.Remove(oldest.RequestId);
            }
            _records.Add(record);
            _recordsById[requestId] = record;
            UpdateCaptureCount();
        }
        catch (JsonException)
        {
        }
    }

    private void ResponseReceiver_DevToolsProtocolEventReceived(
        object? sender,
        CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!_capturing) return;
        try
        {
            using var document = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = document.RootElement;
            var requestId = GetString(root, "requestId");
            if (!_recordsById.TryGetValue(requestId, out var record) ||
                !root.TryGetProperty("response", out var response)) return;

            if (response.TryGetProperty("status", out var status) && status.TryGetDouble(out var statusValue))
            {
                record.StatusCode = (int)Math.Round(statusValue);
            }
            record.ResponseHeaders = response.TryGetProperty("headers", out var headers)
                ? SerializeRedacted(headers)
                : string.Empty;

            var mimeType = GetString(response, "mimeType");
            if (mimeType.Contains("json", StringComparison.OrdinalIgnoreCase) && IsQuectelUrl(record.Url))
            {
                _jsonResponseIds.Add(requestId);
            }
        }
        catch (JsonException)
        {
        }
    }

    private async void LoadingReceiver_DevToolsProtocolEventReceived(
        object? sender,
        CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!_capturing || IncludeResponseBodyCheckBox.IsChecked != true || !_initialized) return;
        try
        {
            using var document = JsonDocument.Parse(e.ParameterObjectAsJson);
            var requestId = GetString(document.RootElement, "requestId");
            if (!_jsonResponseIds.Remove(requestId) || !_recordsById.TryGetValue(requestId, out var record)) return;

            var arguments = JsonSerializer.Serialize(new { requestId });
            var responseJson = await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.getResponseBody", arguments);
            using var responseDocument = JsonDocument.Parse(responseJson);
            var body = GetString(responseDocument.RootElement, "body");
            if (responseDocument.RootElement.TryGetProperty("base64Encoded", out var encoded) && encoded.ValueKind == JsonValueKind.True)
            {
                body = Encoding.UTF8.GetString(Convert.FromBase64String(body));
            }
            if (body.Length > MaximumResponseBodyLength)
            {
                body = body[..MaximumResponseBodyLength] + "\n<response truncated at 1 MB>";
            }
            record.ResponseBody = RedactPayload(body);
        }
        catch
        {
            // 某些缓存响应在 loadingFinished 后无法通过 CDP 再次读取正文，不影响请求记录。
        }
    }

    private void CaptureToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _capturing = !_capturing;
        CaptureToggleButton.Content = _capturing ? "暂停记录" : "继续记录";
        CaptureStateText.Text = _capturing ? "记录中" : "已暂停";
        CaptureStateText.Foreground = (Brush)FindResource(_capturing ? "SuccessBrush" : "WarningBrush");
        CaptureStateBorder.Background = new SolidColorBrush(_capturing
            ? Color.FromArgb(24, 27, 154, 104)
            : Color.FromArgb(24, 227, 154, 40));
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _records.Clear();
        _recordsById.Clear();
        _jsonResponseIds.Clear();
        CaptureDetailsTextBox.Clear();
        _exportPath = CreateExportPath();
        UpdateCaptureCount();
    }

    private async void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        _capturing = false;
        CaptureToggleButton.Content = "继续记录";
        CaptureStateText.Text = "已完成";
        CaptureStateText.Foreground = (Brush)FindResource("AccentBrush");
        try
        {
            var path = await ExportAsync();
            BrowserStatusText.Text = $"诊断文件已导出：{path}";
            Clipboard.SetText(path);
            MessageBox.Show(this, $"已导出 {_records.Count} 条脱敏请求：\n{path}\n\n文件路径已复制到剪贴板。",
                "QHR 请求诊断", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "QHR 请求诊断", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task<string> ExportAsync()
    {
        var directory = Path.GetDirectoryName(_exportPath)!;
        Directory.CreateDirectory(directory);
        var payload = new
        {
            createdAt = DateTimeOffset.Now,
            source = _startUrl,
            redacted = true,
            note = "password/token/cookie/authorization/secret/session fields are redacted before export",
            requestCount = _records.Count,
            requests = _records.Select(record => new
            {
                record.RequestId,
                record.Timestamp,
                record.ResourceType,
                record.Method,
                record.Url,
                record.StatusCode,
                record.RequestHeaders,
                record.RequestBody,
                record.ResponseHeaders,
                record.ResponseBody
            }).ToArray()
        };
        var json = JsonSerializer.Serialize(payload, ExportJsonOptions);
        await File.WriteAllTextAsync(_exportPath, json, Encoding.UTF8);
        return _exportPath;
    }

    private void CaptureDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BindingOperations.ClearBinding(CaptureDetailsTextBox, TextBox.TextProperty);
        if (CaptureDataGrid.SelectedItem is not NetworkCaptureRecord record)
        {
            CaptureDetailsTextBox.Clear();
            return;
        }
        CaptureDetailsTextBox.SetBinding(TextBox.TextProperty, new Binding(nameof(NetworkCaptureRecord.Details))
        {
            Source = record,
            Mode = BindingMode.OneWay
        });
    }

    private void GoButton_Click(object sender, RoutedEventArgs e) => NavigateFromAddressBar();

    private void AddressTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        NavigateFromAddressBar();
    }

    private void NavigateFromAddressBar()
    {
        if (!_initialized) return;
        var text = AddressTextBox.Text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri)) return;
        }
        Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack) Browser.GoBack();
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_initialized) Browser.Reload();
    }

    private void UpdateCaptureCount()
    {
        CaptureCountText.Text = $"{_records.Count} 条";
        if (_initialized) BrowserStatusText.Text = $"网络请求记录中 · {_records.Count} 条";
    }

    private static string RedactUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query)) return url;
        var builder = new UriBuilder(uri);
        builder.Query = RedactFormEncoded(uri.Query.TrimStart('?'));
        return builder.Uri.AbsoluteUri;
    }

    private static string RedactPayload(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(text);
            return SerializeRedacted(document.RootElement);
        }
        catch (JsonException)
        {
        }

        if (text.Contains('=')) return RedactFormEncoded(text);
        return Regex.Replace(text,
            @"(?i)(password|passwd|pwd|token|cookie|authorization|secret|session)(\s*[:=]\s*)([^\s,;]+)",
            "$1$2<redacted>");
    }

    private static string RedactFormEncoded(string text)
    {
        return string.Join("&", text.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var separator = part.IndexOf('=');
            if (separator < 0) return part;
            var rawKey = part[..separator];
            var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            return IsSensitiveName(key) ? rawKey + "=<redacted>" : part;
        }));
    }

    private static string SerializeRedacted(JsonElement element) =>
        JsonSerializer.Serialize(RedactElement(element), ExportJsonOptions);

    private static object? RedactElement(JsonElement element, string propertyName = "")
    {
        if (IsSensitiveName(propertyName)) return "<redacted>";
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => RedactElement(property.Value, property.Name)),
            JsonValueKind.Array => element.EnumerateArray().Select(item => RedactElement(item)).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool IsSensitiveName(string name)
    {
        var value = name.Trim().ToLowerInvariant();
        return value.Contains("password") || value.Contains("passwd") || value == "pwd" ||
               value.Contains("token") || value.Contains("cookie") || value.Contains("authorization") ||
               value.Contains("secret") || value.Contains("session") || value == "tk" || value == "mchrid";
    }

    private static bool IsQuectelUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("quectel.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".quectel.com", StringComparison.OrdinalIgnoreCase));

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string CreateExportPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QHR.Overtime",
            "captures");
        return Path.Combine(directory, $"qhr-capture-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    }

    private void QhrCaptureWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_records.Count > 0)
        {
            try
            {
                var directory = Path.GetDirectoryName(_exportPath)!;
                Directory.CreateDirectory(directory);
                var payload = JsonSerializer.Serialize(new
                {
                    createdAt = DateTimeOffset.Now,
                    source = _startUrl,
                    redacted = true,
                    requestCount = _records.Count,
                    requests = _records.ToArray()
                }, ExportJsonOptions);
                File.WriteAllText(_exportPath, payload, Encoding.UTF8);
            }
            catch
            {
            }
        }

        if (_requestReceiver is not null) _requestReceiver.DevToolsProtocolEventReceived -= RequestReceiver_DevToolsProtocolEventReceived;
        if (_responseReceiver is not null) _responseReceiver.DevToolsProtocolEventReceived -= ResponseReceiver_DevToolsProtocolEventReceived;
        if (_loadingReceiver is not null) _loadingReceiver.DevToolsProtocolEventReceived -= LoadingReceiver_DevToolsProtocolEventReceived;
        Browser.Dispose();
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
}
