using System.Windows;
using QHR.Services;
using QHR.Views;

namespace QHR;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        if (e.Args.Any(argument => argument.Equals("--capture", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var captureWindow = new QhrCaptureWindow();
            MainWindow = captureWindow;
            captureWindow.Show();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settingsService = new SettingsService();
        var loginWindow = new LoginWindow(settingsService);
        loginWindow.Show();
    }
}
