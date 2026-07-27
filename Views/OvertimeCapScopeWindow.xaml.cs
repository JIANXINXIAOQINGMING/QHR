using System.Windows;
using System.Windows.Input;

namespace QHR.Views;

public partial class OvertimeCapScopeWindow : Window
{
    public OvertimeCapScopeWindow(DateOnly? currentEffectiveDate)
    {
        InitializeComponent();
        EffectiveDatePicker.SelectedDate = (currentEffectiveDate ?? DateOnly.FromDateTime(DateTime.Today))
            .ToDateTime(TimeOnly.MinValue);
        if (currentEffectiveDate is not null)
        {
            FromDateRadioButton.IsChecked = true;
        }
        Loaded += (_, _) =>
        {
            UpdateDatePickerState();
            if (FromDateRadioButton.IsChecked == true) EffectiveDatePicker.Focus();
        };
    }

    /// <summary>空值表示应用到全部历史数据。</summary>
    public DateOnly? EffectiveDate { get; private set; }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (FromDateRadioButton.IsChecked == true)
        {
            if (EffectiveDatePicker.SelectedDate is not DateTime selectedDate)
            {
                ErrorText.Text = "请选择封顶规则的生效日期";
                return;
            }
            EffectiveDate = DateOnly.FromDateTime(selectedDate);
        }
        else
        {
            EffectiveDate = null;
        }
        DialogResult = true;
    }

    private void ScopeRadioButton_Checked(object sender, RoutedEventArgs e) => UpdateDatePickerState();

    private void UpdateDatePickerState()
    {
        if (EffectiveDatePicker is not null)
            EffectiveDatePicker.IsEnabled = FromDateRadioButton.IsChecked == true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
