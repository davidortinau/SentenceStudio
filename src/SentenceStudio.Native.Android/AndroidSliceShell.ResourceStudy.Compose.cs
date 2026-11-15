using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Native;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Native.Android;

/// <summary>
/// The resource's study history (issue #219) as two cards of read-only label-over-value rows,
/// as Basic Info's Created and Updated rows: how much and how recently, then how well (the
/// word-status spread). The wording comes from the shared <see cref="ResourceStudyText"/>.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private const string StudyId = "native.resources.form.";

    private void AddResourceStudyRows(List<ResourceFormRow> rows,
        AndroidResourcesCoordinator resources, NativeResourceStudyState study,
        AndroidVisualPalette palette)
    {
        switch (study.Phase)
        {
            case NativeResourceStudyPhase.Loading:
                rows.Add(new("study", () => Card(palette, StudyId + "study.card",
                    StudyHeading(Localize("ResourceEdit_StudyHistory"), "study", palette),
                    new CircularProgressIndicator
                    {
                        Modifier = Modifier.Size(24).TestTag(StudyId + "study.loading")
                            .Semantics(semantics => semantics.ContentDescription(
                                Localize("Common_Loading"))),
                    })));
                return;
            case NativeResourceStudyPhase.Failed:
                rows.Add(new("study", () => Card(palette, StudyId + "study.card",
                    StudyHeading(Localize("ResourceEdit_StudyHistory"), "study", palette),
                    StatusAlert(Localize("ResourceEdit_StudyLoadFailed"), palette,
                        error: true, automationId: StudyId + "study.error"),
                    Action(Localize(NativeText.Retry), StudyId + "study.retry",
                        resources.RetryStudyHistory, enabled: true, primary: false))));
                return;
            case NativeResourceStudyPhase.Ready when study.Summary is { } summary:
                var text = ResourceStudyText.From(summary, Localize, CultureInfo.CurrentUICulture);
                rows.Add(new("study", () => StudyCard(text.StudyTitle, "study",
                    text.StudyEmpty, text.Study, palette)));
                rows.Add(new("mastery", () => StudyCard(text.MasteryTitle, "mastery",
                    text.MasteryEmpty, text.Mastery, palette)));
                return;
            default:
                // Idle or Unavailable: nothing to show.
                return;
        }
    }

    private static ComposableNode StudyCard(string title, string group, string? empty,
        IReadOnlyList<ResourceStudyLine> lines, AndroidVisualPalette palette)
    {
        var children = new List<ComposableNode> { StudyHeading(title, group, palette) };
        if (empty is { } message)
            children.Add(Body(message, palette, secondary: true,
                modifier: Modifier.TestTag(StudyId + group + ".empty")));
        else
            children.AddRange(lines.Select(line =>
                LabeledValue(line.Label, line.Value, StudyId + line.Id, palette)));
        return Card(palette, StudyId + group + ".card", children.ToArray());
    }

    private static ComposableNode StudyHeading(string title, string group,
        AndroidVisualPalette palette) =>
        Heading(title, palette, 18, Modifier.TestTag(StudyId + group + ".heading")
            .Semantics(semantics => semantics.Heading()));
}
