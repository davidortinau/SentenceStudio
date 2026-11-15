using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

/// <summary>
/// The resource's study history (issue #219) as two Form Sections of read-only label-beside
/// rows, as Basic Info's Created and Updated rows: how much and how recently, then how well
/// (the word-status spread). The wording comes from the shared <see cref="ResourceStudyText"/>.
/// </summary>
internal static class LearningResourceStudyHistoryView
{
    private const string Id = "native.resources.form.";

    internal static void Add(
        List<SwiftView> sections, SourceUiStyle style, NativeResourceStudyState study,
        LearningResourceActions actions, Func<string, string> text)
    {
        switch (study.Phase)
        {
            case NativeResourceStudyPhase.Loading:
                sections.Add(Views.Section(text("ResourceEdit_StudyHistory"),
                    Views.IndeterminateProgress(text("Common_Loading"))
                        .WithAccessibilityIdentifier(Id + "study.loading")));
                return;
            case NativeResourceStudyPhase.Failed:
                sections.Add(Views.Section(text("ResourceEdit_StudyHistory"),
                    style.Text(Views.Text(text("ResourceEdit_StudyLoadFailed")),
                            SourceTextRole.Body2, SourceTextTone.Danger)
                        .WithAccessibilityIdentifier(Id + "study.error"),
                    Views.Button(text(NativeText.Retry), actions.RetryStudy)
                        .WithAccessibilityIdentifier(Id + "study.retry")));
                return;
            case NativeResourceStudyPhase.Ready when study.Summary is { } summary:
                var lines = ResourceStudyText.From(summary, text, CultureInfo.CurrentUICulture);
                sections.Add(Views.Section(lines.StudyTitle,
                    Rows(style, lines.StudyEmpty, "study.empty", lines.Study)));
                sections.Add(Views.Section(lines.MasteryTitle,
                    Rows(style, lines.MasteryEmpty, "mastery.empty", lines.Mastery)));
                return;
            default:
                // Idle or Unavailable: nothing to show.
                return;
        }
    }

    private static SwiftView[] Rows(SourceUiStyle style, string? empty, string emptyId,
        IReadOnlyList<ResourceStudyLine> lines) =>
        empty is { } message
            ? [style.Text(Views.Text(message), SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(Id + emptyId)]
            : lines.Select(line => LearningResourceFieldsView.Value(
                line.Label, line.Value, Id + line.Id)).ToArray();
}
