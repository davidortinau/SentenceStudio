using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;

namespace HelpKitSample.Shell.Pages;

public class DashboardPage : ContentPage
{
    public DashboardPage()
    {
        Title = "Dashboard";
        Padding = 24;
        Content = new VerticalStackLayout
        {
            Spacing = 16,
            Children =
            {
                new Label { Text = "Dashboard", FontSize = 28, FontAttributes = FontAttributes.Bold },
                new Label
                {
                    Text = "This is a dummy dashboard. Open the flyout to find the Help entry injected by Plugin.Maui.HelpKit.",
                    LineBreakMode = LineBreakMode.WordWrap,
                },
            },
        };
    }
}
