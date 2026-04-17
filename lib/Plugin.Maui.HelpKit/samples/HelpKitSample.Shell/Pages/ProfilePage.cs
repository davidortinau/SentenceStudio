using Microsoft.Maui.Controls;

namespace HelpKitSample.Shell.Pages;

public class ProfilePage : ContentPage
{
    public ProfilePage()
    {
        Title = "Profile";
        Padding = 24;
        Content = new VerticalStackLayout
        {
            Spacing = 16,
            Children =
            {
                new Label { Text = "Profile", FontSize = 28, FontAttributes = FontAttributes.Bold },
                new Label { Text = "Dummy profile placeholder. Nothing to configure here." },
            },
        };
    }
}
