using System.Windows;
using System.Windows.Input;
using QHR.Models;

namespace QHR.Views;

public partial class BackupConflictWindow : Window
{
    public BackupConflictWindow(BackupConflictSummary conflicts, BackupManifest manifest)
    {
        InitializeComponent();
        BackupSourceText.Text = $"备份账户：{manifest.Account} · 创建于 {manifest.CreatedAt:yyyy-MM-dd HH:mm} · v{manifest.AppVersion}";
        SettingsConflictText.Text = $"计算设置：{(conflicts.SettingsConflict ? 1 : 0)}";
        AttendanceConflictText.Text = $"考勤日期：{conflicts.AttendanceConflicts}";
        GoalConflictText.Text = $"目标设置：{(conflicts.GoalSettingsConflict ? 1 : 0)}";
        ExpenseConflictText.Text = $"消费记录：{conflicts.ExpenseConflicts}";
        CompletedGoalConflictText.Text = $"已完成目标：{conflicts.CompletedGoalConflicts}";
        NoteConflictText.Text = $"每日备注：{conflicts.EvidenceNoteConflicts}";
        ImageConflictText.Text = $"证据图片：{conflicts.EvidenceImageConflicts}";
    }

    public BackupConflictMode SelectedMode { get; private set; }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (KeepLocalRadio.IsChecked != true && UseBackupRadio.IsChecked != true)
        {
            ErrorText.Text = "请选择一种冲突处理方式";
            return;
        }
        SelectedMode = UseBackupRadio.IsChecked == true
            ? BackupConflictMode.UseBackup
            : BackupConflictMode.KeepLocal;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
