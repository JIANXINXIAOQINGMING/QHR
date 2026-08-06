using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using QHR.Models;
using QHR.Services;

namespace QHR.Views;

public partial class LoginWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly WindowsCredentialService _credentialService = new();
    private bool _authenticated;
    private bool _isLoggingIn;

    public LoginWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _settings = settingsService.Load();
        UsernameTextBox.Text = _settings.LastUsername;
        RememberUsernameCheckBox.IsChecked = _settings.AutoLoginEnabled;
        Loaded += LoginWindow_Loaded;
        Closed += LoginWindow_Closed;
        Activated += (_, _) => UpdateCapsLockWarning();
    }

    private bool IsTokenMode =>
        LoginModeCombo.SelectedItem is ComboBoxItem { Tag: "token" };

    private async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await UpdateOfflineAvailabilityAsync();
        if (!_settings.AutoLoginEnabled) return;
        try
        {
            var credential = _credentialService.Read();
            if (credential is null)
            {
                _settings.AutoLoginEnabled = false;
                RememberUsernameCheckBox.IsChecked = true;
                if (await TryOpenOfflineAsync(false)) return;
                LoginStatusText.Text = "首次登录成功后将启用自动登录";
                return;
            }

            LoginModeCombo.SelectedIndex = 0;
            UsernameTextBox.Text = credential.Username;
            SecretPasswordBox.Password = credential.Secret;
            LoginStatusText.Text = "正在使用 Windows 凭据自动登录…";
            await LoginAsync(true);
        }
        catch (Exception ex)
        {
            if (await TryOpenOfflineAsync(false)) return;
            ErrorTextBlock.Text = $"读取自动登录凭据失败：{GetFriendlyMessage(ex)}";
            ErrorBorder.Visibility = Visibility.Visible;
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e) => await LoginAsync(false);

    private async Task LoginAsync(bool automatic)
    {
        if (_isLoggingIn) return;
        _isLoggingIn = true;
        ErrorBorder.Visibility = Visibility.Collapsed;
        LoginButton.IsEnabled = false;
        LoginStatusText.Text = IsTokenMode ? "正在建立 QHR 会话…" : "正在向 SSO 验证并建立 QHR 会话…";

        QhrClient? qhrClient = null;
        var username = UsernameTextBox.Text.Trim();
        var secret = SecretPasswordBox.Password;
        var tokenMode = IsTokenMode;
        try
        {
            string displayUsername;
            string token;
            if (tokenMode)
            {
                token = secret.Trim();
                if (token.Length == 0) throw new ArgumentException("请输入 QHR Token");
                displayUsername = string.IsNullOrWhiteSpace(username) ? "QHR 用户" : username;
            }
            else
            {
                var authService = new SsoAuthService();
                var result = await authService.LoginAsync(username, secret);
                displayUsername = result.Username;
                token = result.AccessToken;
            }

            var attendanceCache = new EncryptedAttendanceCache(_settingsService, displayUsername);
            qhrClient = new QhrClient(_settings.QhrBaseUrl, token, attendanceCache);
            await qhrClient.LoginAsync();

            var enableAutoLogin = !tokenMode && RememberUsernameCheckBox.IsChecked == true;
            if (enableAutoLogin)
            {
                _credentialService.Save(username, secret);
            }
            else
            {
                _credentialService.Delete();
            }
            _settings.AutoLoginEnabled = enableAutoLogin;
            _settings.LastUsername = username;
            _settings.LastAuthenticatedUsername = displayUsername;
            await _settingsService.SaveAsync(_settings);

            OpenMainWindow(displayUsername, qhrClient);
            qhrClient = null;
        }
        catch (Exception ex)
        {
            qhrClient?.Dispose();
            if (automatic && await TryOpenOfflineAsync(false)) return;
            ErrorTextBlock.Text = automatic
                ? $"自动登录失败：{GetFriendlyMessage(ex)} 可使用本地加密记录离线进入。"
                : GetFriendlyMessage(ex);
            ErrorBorder.Visibility = Visibility.Visible;
            LoginStatusText.Text = automatic
                ? "凭据仍已保留，可检查网络后点击重试"
                : "登录未完成，请检查账号或网络后重试";
        }
        finally
        {
            _isLoggingIn = false;
            LoginButton.IsEnabled = true;
            if (!_authenticated) await UpdateOfflineAvailabilityAsync();
        }
    }

    private async void OfflineButton_Click(object sender, RoutedEventArgs e) =>
        await TryOpenOfflineAsync(true);

    private async Task<bool> TryOpenOfflineAsync(bool showErrors)
    {
        var offlineUsername = GetOfflineUsername();
        if (string.IsNullOrWhiteSpace(offlineUsername))
        {
            if (showErrors) ShowOfflineError("这台电脑还没有可识别的已登录账户，请先成功登录一次 QHR。");
            return false;
        }

        try
        {
            OfflineButton.IsEnabled = false;
            LoginStatusText.Text = "正在读取本地加密记录…";
            var attendanceCache = new EncryptedAttendanceCache(_settingsService, offlineUsername);
            var cachedRecords = await attendanceCache.LoadAsync();
            var goalService = new FinancialGoalService(_settingsService, offlineUsername);
            if (cachedRecords.Count == 0 && !File.Exists(goalService.StoragePath))
            {
                if (showErrors) ShowOfflineError("该账户尚未保存本地考勤或目标记录，暂时无法离线进入。");
                return false;
            }

            var qhrClient = new QhrClient(_settings.QhrBaseUrl, string.Empty, attendanceCache, offlineOnly: true);
            OpenMainWindow(offlineUsername, qhrClient);
            return true;
        }
        catch (Exception ex)
        {
            if (showErrors) ShowOfflineError($"无法读取本地加密记录：{GetFriendlyMessage(ex)}");
            return false;
        }
        finally
        {
            if (!_authenticated) OfflineButton.IsEnabled = true;
        }
    }

    private async Task UpdateOfflineAvailabilityAsync()
    {
        var offlineUsername = GetOfflineUsername();
        if (string.IsNullOrWhiteSpace(offlineUsername))
        {
            OfflineButton.IsEnabled = false;
            OfflineButton.ToolTip = "成功登录并保存过本地记录后可用";
            return;
        }

        try
        {
            var attendanceCache = new EncryptedAttendanceCache(_settingsService, offlineUsername);
            var records = await attendanceCache.LoadAsync();
            var goalService = new FinancialGoalService(_settingsService, offlineUsername);
            var available = records.Count > 0 || File.Exists(goalService.StoragePath);
            OfflineButton.IsEnabled = available;
            OfflineButton.ToolTip = available
                ? $"以 {offlineUsername} 离线打开本地加密记录"
                : "该账户尚无本地记录";
        }
        catch
        {
            OfflineButton.IsEnabled = false;
            OfflineButton.ToolTip = "本地记录暂时无法读取";
        }
    }

    private string GetOfflineUsername()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LastAuthenticatedUsername))
            return _settings.LastAuthenticatedUsername.Trim();
        if (!string.IsNullOrWhiteSpace(_settings.LastUsername)) return _settings.LastUsername.Trim();
        return UsernameTextBox.Text.Trim();
    }

    private void OpenMainWindow(string username, QhrClient qhrClient)
    {
        var mainWindow = new MainWindow(username, qhrClient, _settingsService, _settings);
        Application.Current.MainWindow = mainWindow;
        Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
        _authenticated = true;
        mainWindow.Show();
        Close();
    }

    private void ShowOfflineError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorBorder.Visibility = Visibility.Visible;
        LoginStatusText.Text = "未找到可用的本地记录";
    }

    private void LoginModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var tokenMode = IsTokenMode;
        UsernamePanel.Visibility = tokenMode ? Visibility.Collapsed : Visibility.Visible;
        RememberUsernameCheckBox.Visibility = tokenMode ? Visibility.Collapsed : Visibility.Visible;
        SecretLabel.Text = tokenMode ? "QHR Token" : "密码";
        LoginStatusText.Text = tokenMode
            ? "Token 仅保存在当前进程，不会写入磁盘"
            : "直连 SSO，仅用于获取当前会话 token";
        SecretPasswordBox.Clear();
        UpdateCapsLockWarning();
    }

    private async void SecretPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !LoginButton.IsEnabled) return;
        e.Handled = true;
        await LoginAsync(false);
    }

    private void SecretPasswordBox_KeyUp(object sender, KeyEventArgs e) => UpdateCapsLockWarning();

    private void SecretPasswordBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        UpdateCapsLockWarning();

    private void SecretPasswordBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        UpdateCapsLockWarning();

    private void UpdateCapsLockWarning()
    {
        var shouldShow = !IsTokenMode &&
                         SecretPasswordBox.IsKeyboardFocusWithin &&
                         Keyboard.IsKeyToggled(Key.CapsLock);
        CapsLockWarningBorder.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void LoginWindow_Closed(object? sender, EventArgs e)
    {
        if (!_authenticated) Application.Current.Shutdown();
    }

    private static string GetFriendlyMessage(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        if (message.Contains("Name or service", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("不知道这样的主机", StringComparison.OrdinalIgnoreCase))
        {
            return "无法连接公司服务，请确认已连接公司网络或 VPN。";
        }
        if (message.Contains("401") || message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            return "账号、密码或 Token 无效。";
        }
        return message;
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is ButtonBase or TextBoxBase or PasswordBox or ComboBox or CheckBox or
                ScrollBar or Thumb or ResizeGrip)
            {
                return true;
            }

            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
}
