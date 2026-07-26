namespace DesktopPet.Wpf;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        BackgroundTaskBootstrap.Start();

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
