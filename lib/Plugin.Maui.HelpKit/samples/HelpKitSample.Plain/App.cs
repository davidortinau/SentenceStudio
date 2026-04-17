using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace HelpKitSample.Plain;

public partial class App : Application
{
    public App(IServiceProvider services)
    {
        // Plain NavigationPage hosting — no Shell. This is the scenario the
        // WindowPresenter is designed for.
        var mainPage = services.GetRequiredService<MainPage>();
        MainPage = new NavigationPage(mainPage);
    }
}
