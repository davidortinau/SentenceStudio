using System.Collections.Immutable;
using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Styling;

/// <summary>
/// The two activity message presentations (Captain, 2026-11-02): an error or
/// warning is an inline banner that stays until the learner's next action; a
/// success or info message is a transient toast over the page. Each shows a
/// kind icon whose accessibility label names the kind, so meaning never relies
/// on color alone.
/// </summary>
internal static class SourceUiMessages
{
    internal static string KindLabel(NativeMessageKind kind, Func<string, string> text) => text(kind switch
    {
        NativeMessageKind.Success => "Common_Success",
        NativeMessageKind.Info => "NativeMessage_Information",
        NativeMessageKind.Warning => "NativeMessage_Warning",
        _ => "Common_Error"
    });

    private static string Symbol(NativeMessageKind kind) => kind switch
    {
        NativeMessageKind.Success => "checkmark.circle.fill",
        NativeMessageKind.Info => "info.circle.fill",
        NativeMessageKind.Warning => "exclamationmark.triangle.fill",
        _ => "xmark.octagon.fill"
    };

    private static SwiftView Row(SourceUiStyle style, NativeMessage message, Func<string, string> text)
    {
        var icon = Views.Image(Symbol(message.Kind))
            .WithAccessibilityLabel(KindLabel(message.Kind, text));
        var label = style.Text(Views.Text(message.Format(text)), SourceTextRole.Body2)
            .WithFlexibleWidth();
        var row = Views.HStack(icon, label);
        row.Spacing = 8;
        return row;
    }

    /// <summary>One banner row: danger colors for an error, info colors for a warning.</summary>
    internal static SwiftView MessageBanner(this SourceUiStyle style, NativeMessage message,
        Func<string, string> text, string automationId)
    {
        var row = Row(style, message, text);
        return (message.IsError ? style.Alert(row, success: false, compact: true)
                : style.InfoAlert(row, compact: true))
            .WithAccessibilityIdentifier(automationId);
    }

    /// <summary>
    /// The banners among <paramref name="messages"/> (errors and warnings) in a
    /// "native.message.banner" group, each row keeping the activity's own ID, with
    /// an optional dismiss button. Null when there is nothing to show; toasts are
    /// never drawn inline.
    /// </summary>
    internal static SwiftView? MessageBanners(this SourceUiStyle style,
        ImmutableArray<NativeMessage> messages, Func<string, string> text,
        Func<int, string> rowId, Action? dismiss = null, string? dismissId = null,
        string? dismissLabel = null)
    {
        var banners = NativeMessages.Banners(messages);
        if (banners.IsEmpty)
            return null;
        var rows = new SwiftView[banners.Length];
        for (var index = 0; index < banners.Length; index++)
            rows[index] = style.MessageBanner(banners[index], text, rowId(index));
        var stack = Views.VStack(rows).WithFlexibleWidth();
        stack.Spacing = 6;
        if (dismiss is null)
            return stack.WithAccessibilityIdentifier(NativeMessageAccess.Banner);
        var close = Views.Button(Views.Image("xmark"), dismiss)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel(dismissLabel ?? text("Common_Close"))
            .WithAccessibilityIdentifier(dismissId ?? $"{NativeMessageAccess.Banner}.dismiss");
        var group = Views.HStack(stack, close).WithAccessibilityIdentifier(NativeMessageAccess.Banner);
        group.Spacing = 8;
        return group;
    }

    internal static SwiftView? MessageBanners(this SourceUiStyle style, NativeMessage? message,
        Func<string, string> text, string rowId) =>
        style.MessageBanners(NativeMessages.Banners(message), text, _ => rowId);

    /// <summary>
    /// The transient toast: kind icon, text and a close button on an elevated
    /// alert surface, announced to VoiceOver when it appears (source
    /// ToastContainer: icon, text, close).
    /// </summary>
    internal static SwiftView MessageToast(this SourceUiStyle style, NativeMessage message,
        Func<string, string> text, Action dismiss)
    {
        var body = message.Format(text);
        var close = Views.Button(Views.Image("xmark"), dismiss)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel(text("Common_Close"))
            .WithAccessibilityIdentifier(NativeMessageAccess.ToastDismiss);
        var row = Views.HStack(Row(style, message, text), close);
        row.Spacing = 8;
        var surface = message.Kind == NativeMessageKind.Success
            ? style.Alert(row, success: true, compact: true)
            : style.InfoAlert(row, compact: true);
        return surface.WithShadow("#000000", "#000000", 12, 0.18, 4)
            .WithAccessibilityAnnouncement($"{KindLabel(message.Kind, text)}: {body}")
            .WithAccessibilityIdentifier(NativeMessageAccess.Toast);
    }
}
