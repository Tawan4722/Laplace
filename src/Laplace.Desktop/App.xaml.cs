using System.Windows;

namespace Laplace.Desktop;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var mainWindow = new MainWindow(e.Args);
        mainWindow.Show();
    }
}
