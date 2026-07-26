using System.Windows;
using System.Windows.Input;

namespace QHR.Views;

public partial class BackupPasswordWindow : Window
{
    private readonly bool _creatingBackup;

    public BackupPasswordWindow(bool creatingBackup)
    {
        InitializeComponent();
        _creatingBackup = creatingBackup;
        if (!creatingBackup)
        {
            Title = "输入备份密码";
            TitleText.Text = "输入备份密码";
            SubtitleText.Text = "验证成功后才会读取备份内容";
            SecurityNoticeText.Text = "密码只用于本次解密，不会保存到电脑。登录凭据不在备份文件中。";
            PasswordHintText.Text = "请输入创建备份时设置的密码";
            ConfirmPasswordPanel.Visibility = Visibility.Collapsed;
            ConfirmButton.Content = "解锁备份";
        }
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public string BackupPassword { get; private set; } = string.Empty;

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        var password = PasswordInput.Password;
        if (string.IsNullOrEmpty(password))
        {
            ErrorText.Text = "请输入备份密码";
            return;
        }
        if (_creatingBackup && password.Length < 8)
        {
            ErrorText.Text = "备份密码至少需要 8 个字符";
            return;
        }
        if (_creatingBackup && password != ConfirmPasswordInput.Password)
        {
            ErrorText.Text = "两次输入的密码不一致";
            return;
        }
        BackupPassword = password;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
