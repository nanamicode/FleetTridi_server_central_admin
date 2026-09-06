using System.Windows;

namespace FleetTridi.Admin;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.ApplyStartupConfiguration();
        window.Show();

        await window.TryAutoLoginFromEnvironmentAsync();
    }
}
