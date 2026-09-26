#if DEBUG
using Microsoft.Maui.Controls;

namespace SentenceStudio.MacOS;

/// <summary>
/// Minimal native surface for migration validation. It deliberately avoids the Blazor route,
/// authentication, preferences, and other user-facing startup paths.
/// </summary>
internal sealed class MigrationValidationApp : Microsoft.Maui.Controls.Application
{
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new ContentPage
        {
            Content = new Label
            {
                Text = "Migration validation is running.",
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            },
        });
}
#endif
