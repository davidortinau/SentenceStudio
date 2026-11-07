using System.Collections.Immutable;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Activity messages (Captain, 2026-11-02, "split by kind"): errors and warnings
/// are inline banners that stay until the learner's next action; success and
/// info are a Material 3 Snackbar over the activity that the shared
/// <see cref="NativeToastScheduler"/> removes after about 3 s (source toasts).
/// The Material SnackbarHost is a polite live region, so TalkBack reads it.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeToastScheduler? _activityToasts;

    /// <summary>The newest success or info message in the open activity's state.</summary>
    private NativeMessage? ActivityToastCandidate() => _activityRoute switch
    {
        NativeActivityCatalog.Cloze => NativeMessages.LatestToast(Cloze.Notice),
        NativeActivityCatalog.Translation => Translation.Phase == NativePhase.Blocked ? null
            : NativeMessages.LatestToast(Translation.Notice),
        NativeActivityCatalog.Writing => NativeMessages.LatestToast(Writing.Notices),
        NativeActivityCatalog.Reading => NativeMessages.LatestToast(Reading.Notice),
        NativeActivityCatalog.HowDoYouSay => NativeMessages.LatestToast(HowDoYouSay.Notice),
        NativeActivityCatalog.WordAssociation => NativeMessages.LatestToast(WordAssociation.Notices),
        NativeActivityCatalog.Conversation => NativeMessages.LatestToast(Conversation.Notices),
        NativeActivityCatalog.Diary => NativeMessages.LatestToast(Diary.Notices),
        NativeActivityCatalog.Scene => NativeMessages.LatestToast(Scene.Notices),
        NativeActivityCatalog.MinimalPairs => NativeMessages.LatestToast(MinimalPairs.Notices),
        _ => null
    };

    private NativeToastScheduler ActivityToasts
    {
        get
        {
            if (_activityToasts is null)
            {
                _activityToasts = new NativeToastScheduler();
                _activityToasts.Changed += () => Publish(() => { });
            }
            return _activityToasts;
        }
    }

    /// <summary>The activity page with its toast host.</summary>
    private ComposableNode WithActivityToast(IComposer composer, ComposableNode screen) =>
        WithToast(composer, screen, ActivityToasts, ActivityToastCandidate());

    /// <summary>A More screen with its toast host (Import Content's success messages).</summary>
    private ComposableNode WithMoreToast(IComposer composer, ComposableNode screen) =>
        WithToast(composer, screen, MoreToasts, ImportToastCandidate());

    /// <summary>The screen with a toast host layered over the top edge, clear of input bars.</summary>
    private ComposableNode WithToast(IComposer composer, ComposableNode screen,
        NativeToastScheduler toasts, NativeMessage? candidate)
    {
        toasts.Offer(candidate);
        var host = composer.Remember(() => new SnackbarHostState());
        var toast = toasts.Current;
        var text = toast?.Format(Localize);
        // Keyed by the toast: a new toast, its expiry (key 0) or leaving the
        // activity cancels the previous effect, which removes its snackbar.
        composer.Effect(toast?.Id ?? 0L, async token =>
        {
            if (toast is null || text is null)
                return;
            try
            {
                var result = await host.ShowSnackbarAsync(text, withDismissAction: true,
                    duration: SnackbarDuration.Indefinite, cancellationToken: token);
                if (result == SnackbarResult.Dismissed && ReferenceEquals(toasts.Current, toast)
                    && toasts.Dismiss())
                    Publish(() => { });
            }
            catch (OperationCanceledException)
            {
            }
        });
        return new Box
        {
            Modifier.FillMaxSize(),
            screen,
            new SnackbarHost(host)
            {
                // Below the 64 dp Material top app bar, clear of the bottom input bars.
                Modifier = Modifier.Align(Alignment.TopCenter)
                    .Padding(start: AndroidVisualTheme.PagePadding, top: 64,
                        end: AndroidVisualTheme.PagePadding)
                    .TestTag(NativeMessageAccess.Toast),
            },
        };
    }

    private void ResetActivityToast() => _activityToasts?.Reset();

    private static string MessageKindLabel(NativeMessageKind kind) => Localize(kind switch
    {
        NativeMessageKind.Success => "Common_Success",
        NativeMessageKind.Info => "NativeMessage_Information",
        NativeMessageKind.Warning => "NativeMessage_Warning",
        _ => "Common_Error"
    });

    /// <summary>One banner: kind icon (named for TalkBack) and text; danger border for an error.</summary>
    private static ComposableNode MessageBanner(NativeMessage message, AndroidVisualPalette palette,
        string automationId)
    {
        var kind = MessageKindLabel(message.Kind);
        var text = message.Format(Localize);
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().TestTag(automationId)
                .Border(1, message.IsError ? palette.Danger : palette.Border,
                    AndroidVisualTheme.ControlRadius)
                .Semantics(semantics => semantics.ContentDescription($"{kind}: {text}")),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = message.IsError ? palette.DangerSurface : palette.Elevated,
        };
        surface.Add(new Row(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(12),
            new Icon(message.IsError || message.Kind == NativeMessageKind.Warning
                ? Icons.Default.Warning : Icons.Default.Info, kind)
            {
                Modifier = Modifier.Size(20),
                Tint = message.IsError ? palette.Danger : palette.Text,
            },
            Body(text, palette, modifier: Modifier.Weight(1f)),
        });
        return surface;
    }

    /// <summary>
    /// The banners among the messages in a "native.message.banner" group, each
    /// row keeping the activity's own tag, with an optional dismiss button. Null
    /// when there is nothing to show; toasts are never drawn inline.
    /// </summary>
    private static ComposableNode? MessageBanners(ImmutableArray<NativeMessage> messages,
        AndroidVisualPalette palette, Func<int, string> rowTag, Action? dismiss = null,
        string? dismissTag = null, string? dismissLabel = null)
    {
        var banners = NativeMessages.Banners(messages);
        if (banners.IsEmpty)
            return null;
        var list = new Column(Arrangement.SpacedBy(6))
        {
            dismiss is null ? Modifier.FillMaxWidth() : Modifier.Weight(1f),
        };
        for (var index = 0; index < banners.Length; index++)
            list.Add(MessageBanner(banners[index], palette, rowTag(index)));
        if (dismiss is null)
            return new Column
            {
                Modifier.FillMaxWidth().Padding(bottom: 12).TestTag(NativeMessageAccess.Banner),
                list,
            };
        var label = dismissLabel ?? Localize("Common_Close");
        var close = new IconButton(dismiss)
        {
            Modifier = Modifier.TestTag(dismissTag ?? $"{NativeMessageAccess.Banner}.dismiss")
                .Semantics(semantics => semantics.ContentDescription(label)),
        };
        close.Add(new Icon(Icons.Default.Close, label)
        {
            Modifier = Modifier.Size(20),
            Tint = palette.Text,
        });
        return new Row(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(bottom: 12).TestTag(NativeMessageAccess.Banner),
            list,
            close,
        };
    }

    private static ComposableNode? MessageBanners(NativeMessage? message, AndroidVisualPalette palette,
        string rowTag) =>
        MessageBanners(NativeMessages.Banners(message), palette, _ => rowTag);
}
