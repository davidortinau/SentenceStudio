using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Video Watching (source VideoWatching.razor): title row with
/// "Open in YouTube", the 16:9 player (the bridge's privacy-enhanced
/// YouTube embed, the only approved web view) and the collapsible transcript
/// card. The source timer widget is never shown (fromPlan is never set), so
/// timing stays silent. The NavigationStack supplies Back.
/// </summary>
internal sealed class VideoWatchingScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeVideoWatchingPresenter _presenter;
    private NativeVideoWatchingState _state = new(NativePhase.Loading);

    public VideoWatchingScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeVideoWatchingPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeVideoWatchingText.Title;

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        _state = await _presenter.OpenAsync(launch, cancellationToken);

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        _state = await _presenter.ExitAsync(cancellationToken);

    private void OnStateChanged(NativeVideoWatchingState state)
    {
        _state = state;
        _host.Publish();
    }

    private void ToggleTranscript() => _ = FireAsync(_presenter.ToggleTranscriptAsync);

    private async Task FireAsync(Func<CancellationToken, Task<NativeVideoWatchingState>> action)
    {
        try
        {
            _state = await action(_host.Lifetime);
        }
        catch (OperationCanceledException) when (_host.Lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _host.Logger?.LogError(ex, "Native iOS Video Watching action failed.");
        }
        _host.Publish();
    }

    /// <summary>Source target=_blank link: the system opens YouTube or Safari.</summary>
    private void OpenInYouTube(Uri uri) => _ = OpenExternalAsync(uri);

    private async Task OpenExternalAsync(Uri uri)
    {
        try
        {
            if (!await NativeExternalLauncher.OpenAsync(uri))
                _host.Logger?.LogWarning("Video Watching could not open the video link.");
        }
        catch (Exception ex)
        {
            _host.Logger?.LogError(ex, "Video Watching could not open the video link.");
        }
    }

    public SwiftView Render(SourceUiStyle style)
    {
        var text = _host.Text;
        var state = _state;
        var items = new List<SwiftView>();
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                items.Add(Views.IndeterminateProgress(text(NativeVideoWatchingText.LoadingVideo))
                    .WithAccessibilityIdentifier(NativeVideoWatchingAccess.Loading));
                items.Add(style.Text(Views.Text(text(NativeVideoWatchingText.LoadingVideo)),
                    SourceTextRole.Body1, SourceTextTone.Secondary));
                break;
            case NativePhase.Ready when state.VideoId is { } videoId:
                var title = string.IsNullOrEmpty(state.ResourceTitle)
                    ? text(NativeVideoWatchingText.DefaultTitle) : state.ResourceTitle;
                items.Add(TitleRow(state, title, style, text));
                items.Add(Views.YouTubePlayer(videoId, title)
                    .WithFlexibleWidth()
                    .WithAccessibilityIdentifier(NativeVideoWatchingAccess.Player));
                if (state.HasTranscript)
                    items.Add(TranscriptCard(state, style, text));
                break;
            default:
                // Error, Empty and Blocked offer only Go Back (source centered danger/secondary text).
                var empty = state.Phase is NativePhase.Empty or NativePhase.Ready;
                var message = empty ? text(NativeVideoWatchingText.NoVideo)
                    : state.ErrorArgument is { } argument
                        ? string.Format(CultureInfo.CurrentCulture,
                            text(state.ErrorKey ?? NativeVideoWatchingText.NoVideo), argument)
                        : text(state.ErrorKey ?? NativeVideoWatchingText.NoVideo);
                items.Add(style.Text(Views.Text(message), SourceTextRole.Body1,
                        empty ? SourceTextTone.Secondary : SourceTextTone.Danger)
                    .WithAccessibilityIdentifier(empty ? NativeVideoWatchingAccess.Empty
                        : NativeVideoWatchingAccess.Error));
                items.Add(style.SecondaryButton(text(NativeVideoWatchingText.GoBack), _host.Exit)
                    .WithAccessibilityIdentifier(NativeVideoWatchingAccess.GoBack));
                var centered = Views.VStack(items.ToArray())
                    .WithPadding(48, gutter, 48, gutter)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier("native.video.content");
                centered.Spacing = 16;
                return style.Page(Views.Scroll(centered)
                    .WithAccessibilityIdentifier(NativeVideoWatchingAccess.Screen));
        }

        var content = Views.VStack(items.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.video.content");
        content.Spacing = SourceUiLayout.PageGap;
        return style.Page(Views.Scroll(content)
            .WithAccessibilityIdentifier(NativeVideoWatchingAccess.Screen));
    }

    /// <summary>Source title row: h2 title and the small "Open in YouTube" button.</summary>
    private SwiftView TitleRow(NativeVideoWatchingState state, string title,
        SourceUiStyle style, Func<string, string> text)
    {
        var heading = style.Text(Views.Text(title), SourceTextRole.Title2, sourceHeading: true)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeVideoWatchingAccess.Title);
        if (state.ExternalUri is not { } uri)
            return heading;
        var label = text(NativeVideoWatchingText.OpenInYouTube);
        var open = style.SecondaryButton(label, () => OpenInYouTube(uri))
            .WithAccessibilityLabel(NativeVideoWatchingText.Spoken(label))
            .WithAccessibilityIdentifier(NativeVideoWatchingAccess.OpenInYouTube);
        var row = Views.HStack(heading, open).WithFlexibleWidth();
        row.Spacing = 12;
        return row;
    }

    /// <summary>Source transcript card: header with Show/Hide, then the pre-wrapped transcript.</summary>
    private SwiftView TranscriptCard(NativeVideoWatchingState state,
        SourceUiStyle style, Func<string, string> text)
    {
        var toggleLabel = text(state.ShowTranscript
            ? NativeVideoWatchingText.Hide : NativeVideoWatchingText.Show);
        var secondary = style.Appearance.SecondaryText;
        var toggle = Views.Button(style.Text(Views.Text(toggleLabel), SourceTextRole.Body2,
                    SourceTextTone.Secondary), ToggleTranscript)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithForegroundColor(secondary.Light, secondary.Dark)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithAccessibilityLabel(NativeVideoWatchingText.Spoken(toggleLabel) + ", "
                + text(NativeVideoWatchingText.Transcript))
            .WithAccessibilityIdentifier(NativeVideoWatchingAccess.TranscriptToggle);
        var header = Views.HStack(
                style.Text(Views.Text(text(NativeVideoWatchingText.Transcript)), SourceTextRole.Title3,
                        sourceHeading: true)
                    .WithFlexibleWidth(),
                toggle)
            .WithFlexibleWidth();
        var rows = new List<SwiftView> { header };
        if (state.ShowTranscript)
            rows.Add(style.Text(Views.Text(state.Transcript!), SourceTextRole.Body1)
                .WithSpeechLanguage(state.TranscriptLanguageTag)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(NativeVideoWatchingAccess.TranscriptText));
        var card = Views.VStack(rows.ToArray()).WithPadding(16);
        card.Spacing = 8;
        return style.Card(card).WithAccessibilityIdentifier(NativeVideoWatchingAccess.Transcript);
    }

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
