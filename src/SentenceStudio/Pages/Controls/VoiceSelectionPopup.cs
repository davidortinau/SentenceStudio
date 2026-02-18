using SentenceStudio.Services.Speech;
using MauiReactor.Shapes;
using UXDivers.Popups.Services;

namespace SentenceStudio.Pages.Controls;

/// <summary>
/// A reusable popup for selecting a voice from a list of available voices.
/// Uses UXDivers PopupPage with proper scrolling support.
/// </summary>
public static class VoiceSelectionPopup
{
    /// <summary>
    /// Shows a voice selection popup and returns the selected voice ID.
    /// </summary>
    /// <param name="title">The popup title (e.g., "German Voices")</param>
    /// <param name="availableVoices">List of available voices to choose from</param>
    /// <param name="selectedVoiceId">Currently selected voice ID (for checkmark display)</param>
    /// <param name="onVoiceSelected">Callback when a voice is selected</param>
    public static async Task ShowAsync(
        string title,
        IEnumerable<VoiceInfo> availableVoices,
        string selectedVoiceId,
        Action<string> onVoiceSelected)
    {
        var localize = LocalizationManager.Instance;

        var theme = BootstrapTheme.Current;

        var popup = new UXDivers.Popups.Maui.PopupPage
        {
            BackgroundColor = Colors.Black.WithAlpha(0.5f),
            CloseWhenBackgroundIsClicked = true
        };

        var border = new MauiControls.Border
        {
            VerticalOptions = LayoutOptions.End,
            HorizontalOptions = LayoutOptions.Fill,
            BackgroundColor = theme.GetSurface(),
            Padding = new Thickness(16),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(20, 20, 0, 0) },
            Stroke = Colors.Transparent
        };

        var mainLayout = new MauiControls.VerticalStackLayout { Spacing = 12 };

        // Title
        var titleLabel = new MauiControls.Label
        {
            Text = title,
            TextColor = theme.GetOnBackground(),
            FontSize = 20,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };
        mainLayout.Children.Add(titleLabel);

        // ScrollView with voice list
        var scrollView = new MauiControls.ScrollView
        {
            HeightRequest = 350,
            VerticalScrollBarVisibility = ScrollBarVisibility.Always
        };

        var voiceStack = new MauiControls.VerticalStackLayout { Spacing = 4 };

        foreach (var voice in availableVoices)
        {
            var isSelected = voice.VoiceId == selectedVoiceId;
            var button = new MauiControls.Button
            {
                Text = $"{(isSelected ? "✓ " : "   ")}{voice.Name} ({voice.Gender})",
                BackgroundColor = Colors.Transparent,
                TextColor = isSelected ? theme.Success : theme.GetOnBackground(),
                FontSize = 16,
                HorizontalOptions = LayoutOptions.Start,
                Padding = new Thickness(8, 12)
            };

            var capturedVoiceId = voice.VoiceId;
            button.Clicked += async (s, e) =>
            {
                await IPopupService.Current.PopAsync();
                onVoiceSelected(capturedVoiceId);
            };

            voiceStack.Children.Add(button);
        }

        scrollView.Content = voiceStack;
        mainLayout.Children.Add(scrollView);

        // Cancel button
        var closeButton = new MauiControls.Button
        {
            Text = $"{localize["Cancel"]}",
            BackgroundColor = theme.GetSurface(),
            TextColor = theme.GetOnBackground(),
            CornerRadius = 8,
            HeightRequest = 44,
            Margin = new Thickness(0, 8, 0, 0)
        };
        closeButton.Clicked += async (s, e) => await IPopupService.Current.PopAsync();
        mainLayout.Children.Add(closeButton);

        border.Content = mainLayout;
        popup.Content = border;

        await IPopupService.Current.PushAsync(popup);
    }
}
