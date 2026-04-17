using Microsoft.Maui.Controls;

namespace HelpKitSample.Shell.Pages;

public class AboutPage : ContentPage
{
    public AboutPage()
    {
        Title = "About";
        Padding = 24;
        Content = new VerticalStackLayout
        {
            Spacing = 16,
            Children =
            {
                new Label { Text = "About this sample", FontSize = 28, FontAttributes = FontAttributes.Bold },
                new Label
                {
                    Text = "HelpKitSample.Shell demonstrates how a Shell-hosted MAUI app integrates Plugin.Maui.HelpKit. The flyout Help entry is added via builder.AddHelpKitShellFlyout(...).",
                    LineBreakMode = LineBreakMode.WordWrap,
                },
            },
        };
    }
}
