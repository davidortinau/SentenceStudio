using System.Globalization;
using AndroidX.Compose;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;
using AndroidContent = global::Android.Content;
using AndroidWebKit = global::Android.Webkit;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Video Watching (source VideoWatching.razor): title row with
/// "Open in YouTube" (the system YouTube app or browser through an
/// ACTION_VIEW intent), the 16:9 player and the collapsible transcript card.
/// The player is the one approved web view (Captain, 2026-11-01): an
/// android.webkit.WebView hosted through Compose AndroidView interop that
/// loads only the privacy-enhanced youtube-nocookie embed for a validated
/// 11-character id, refuses every other main-frame navigation and pop-up,
/// has no file or content access, and enables JavaScript only for the embed.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeVideoWatchingPresenter? _videoWatching;
    private AndroidWebKit.WebView? _videoWebView;

    internal NativeVideoWatchingState VideoWatching { get; private set; } = new(NativePhase.Loading);

    private async Task OpenVideoWatchingAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (ActivityServices is not { } services)
            return;
        var presenter = services.GetRequiredService<NativeVideoWatchingPresenter>();
        presenter.StateChanged += OnVideoWatchingChanged;
        _videoWatching = presenter;
        Publish(() => VideoWatching = new(NativePhase.Loading));
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() =>
        {
            if (ReferenceEquals(_videoWatching, presenter))
                VideoWatching = state;
        });
    }

    private async Task ExitVideoWatchingAsync(CancellationToken cancellationToken)
    {
        StopVideoPlayer();
        if (_videoWatching is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseVideoWatching()
    {
        StopVideoPlayer();
        if (_videoWatching is { } presenter)
        {
            presenter.StateChanged -= OnVideoWatchingChanged;
            presenter.Dispose();
        }
        _videoWatching = null;
    }

    private void StopVideoPlayer()
    {
        if (_videoWebView is not { } webView)
            return;
        _videoWebView = null;
        // Stop playback at once; Compose still owns the attached view and
        // releases it when the activity leaves the composition.
        webView.StopLoading();
        webView.LoadUrl("about:blank");
        webView.OnPause();
    }

    private void OnVideoWatchingChanged(NativeVideoWatchingState state) =>
        Publish(() => VideoWatching = state);

    private void ToggleVideoTranscript()
    {
        if (_videoWatching is not { } presenter)
            return;
        _ = RunVideoWatchingAsync(presenter);
    }

    private async Task RunVideoWatchingAsync(NativeVideoWatchingPresenter presenter)
    {
        try
        {
            var state = await presenter.ToggleTranscriptAsync(_lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_videoWatching, presenter))
                    VideoWatching = state;
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
        }
    }

    /// <summary>Source target=_blank link: hand the URL to the system.</summary>
    private void OpenVideoExternally(Uri uri)
    {
        try
        {
            var intent = new AndroidContent.Intent(AndroidContent.Intent.ActionView,
                global::Android.Net.Uri.Parse(uri.AbsoluteUri));
            intent.AddCategory(AndroidContent.Intent.CategoryBrowsable);
            activity.StartActivity(intent);
        }
        catch (AndroidContent.ActivityNotFoundException)
        {
            global::Android.Util.Log.Warn("SentenceStudio.Native", "No app can open the video link.");
        }
    }

    private ComposableNode RenderVideoWatching(AndroidVisualPalette palette)
    {
        var state = VideoWatching;
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeVideoWatchingAccess.Screen),
        };
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeVideoWatchingAccess.Loading)
                });
                viewport.Add(Body(Localize(NativeVideoWatchingText.LoadingVideo), palette, secondary: true));
                break;
            case NativePhase.Ready when state.VideoId is { } videoId
                && AndroidYouTubeEmbedPolicy.IsVideoId(videoId):
                var title = string.IsNullOrEmpty(state.ResourceTitle)
                    ? Localize(NativeVideoWatchingText.DefaultTitle) : state.ResourceTitle;
                viewport.Add(VideoTitleRow(state, title, palette));
                viewport.Add(VideoPlayer(videoId, title));
                if (state.HasTranscript)
                    viewport.Add(VideoTranscriptCard(state, palette));
                break;
            default:
                var empty = state.Phase is NativePhase.Empty or NativePhase.Ready;
                var key = empty ? NativeVideoWatchingText.NoVideo
                    : state.ErrorKey ?? NativeVideoWatchingText.NoVideo;
                var message = !empty && state.ErrorArgument is { } argument
                    ? string.Format(CultureInfo.CurrentCulture, Localize(key), argument)
                    : Localize(key);
                viewport.Add(StatusAlert(message, palette, error: !empty,
                    automationId: empty ? NativeVideoWatchingAccess.Empty : NativeVideoWatchingAccess.Error));
                viewport.Add(Action(Localize(NativeVideoWatchingText.GoBack),
                    NativeVideoWatchingAccess.GoBack, ExitActivity, true, primary: false));
                break;
        }

        return new Column
        {
            Modifier.FillMaxSize().TestTag("native.video.page"),
            ScreenHeader(Localize(NativeVideoWatchingText.Title), palette,
                back: true, backAutomationId: NativeVideoWatchingAccess.Back),
            viewport,
        };
    }

    private ComposableNode VideoTitleRow(NativeVideoWatchingState state, string title,
        AndroidVisualPalette palette)
    {
        var row = new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            Heading(title, palette, 22, Modifier.Weight(1f)
                .TestTag(NativeVideoWatchingAccess.Title)
                .Semantics(semantics => semantics.Heading())),
        };
        if (state.ExternalUri is { } uri)
        {
            var label = Localize(NativeVideoWatchingText.OpenInYouTube);
            var open = new OutlinedButton(onClick: () => OpenVideoExternally(uri))
            {
                Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag(NativeVideoWatchingAccess.OpenInYouTube)
                    .Semantics(semantics => semantics.ContentDescription(
                        NativeVideoWatchingText.Spoken(label))),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            open.Add(new Text(label));
            row.Add(open);
        }
        return row;
    }

    /// <summary>The 16:9 YouTube embed (source iframe with aspect-ratio 16/9).</summary>
    private ComposableNode VideoPlayer(string videoId, string title) =>
        new AndroidView(
            context =>
            {
                StopVideoPlayer();
                var webView = AndroidYouTubeEmbed.Create(context, videoId, title);
                _videoWebView = webView;
                return webView;
            })
        {
            Modifier = Modifier.FillMaxWidth().AspectRatio(16f / 9f)
                .TestTag(NativeVideoWatchingAccess.Player)
                .Semantics(semantics => semantics.ContentDescription(title)),
        };

    private ComposableNode VideoTranscriptCard(NativeVideoWatchingState state, AndroidVisualPalette palette)
    {
        var toggleLabel = Localize(state.ShowTranscript
            ? NativeVideoWatchingText.Hide : NativeVideoWatchingText.Show);
        var transcriptTitle = Localize(NativeVideoWatchingText.Transcript);
        var toggle = new TextButton(onClick: ToggleVideoTranscript)
        {
            Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(NativeVideoWatchingAccess.TranscriptToggle)
                .Semantics(semantics => semantics.ContentDescription(
                    NativeVideoWatchingText.Spoken(toggleLabel) + ", " + transcriptTitle)),
            new Text(toggleLabel) { Color = palette.SecondaryText },
        };
        var header = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            Heading(transcriptTitle, palette, 18, Modifier.Weight(1f)
                .Semantics(semantics => semantics.Heading())),
            toggle,
        };
        var children = new List<ComposableNode> { header };
        if (state.ShowTranscript)
            children.Add(new Text(state.Transcript!)
            {
                Modifier = Modifier.FillMaxWidth().TestTag(NativeVideoWatchingAccess.TranscriptText),
                Color = palette.Text,
                FontSize = new Sp(15),
                LineHeight = new Sp(27),
            });
        return Card(palette, NativeVideoWatchingAccess.Transcript, children.ToArray());
    }
}

/// <summary>
/// The approved YouTube embed web view, restricted by <see cref="AndroidYouTubeEmbedPolicy"/>
/// like the iOS bridge player: no pop-up windows, no file/content access, no
/// third-party cookies.
/// </summary>
internal static class AndroidYouTubeEmbed
{
    internal static AndroidWebKit.WebView Create(AndroidContent.Context context, string videoId, string label)
    {
        var url = AndroidYouTubeEmbedPolicy.EmbedUrl(videoId) ?? throw new ArgumentException("Invalid YouTube video id.", nameof(videoId));
        var webView = new AndroidWebKit.WebView(context)
        {
            ContentDescription = label,
        };
        var settings = webView.Settings;
        // The embed player is a JavaScript page that keeps its state in DOM storage.
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.AllowFileAccess = false;
        settings.AllowContentAccess = false;
        settings.JavaScriptCanOpenWindowsAutomatically = false;
        settings.SetSupportMultipleWindows(false);
        settings.MediaPlaybackRequiresUserGesture = true;
        settings.SetGeolocationEnabled(false);
        AndroidWebKit.CookieManager.Instance?.SetAcceptThirdPartyCookies(webView, false);
        webView.SetWebViewClient(new EmbedClient(videoId));
        // YouTube requires embedding clients to identify themselves.
        webView.LoadUrl(url, new Dictionary<string, string>
        {
            ["Referer"] = $"https://{context.PackageName?.ToLowerInvariant() ?? "localhost"}"
        });
        return webView;
    }

    private sealed class EmbedClient(string videoId) : AndroidWebKit.WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(AndroidWebKit.WebView? view,
            AndroidWebKit.IWebResourceRequest? request)
        {
            Uri.TryCreate(request?.Url?.ToString(), UriKind.Absolute, out var url);
            return !AndroidYouTubeEmbedPolicy.AllowsNavigation(url, request?.IsForMainFrame ?? true, videoId);
        }
    }
}
