using Microsoft.Maui.Controls;

namespace HelpKitSample.Shell;

public partial class App : Application
{
    public App()
    {
        MainPage = new AppShell();
    }
}
