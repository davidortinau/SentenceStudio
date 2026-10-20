using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Dashboard;

internal static class PlanCardView
{
    internal static SwiftView Create(
        NativePlanCard card, SourceUiStyle style, Func<string, string> text,
        Action<string>? openQuiz)
    {
        var name = card.TitleKey is { } key ? text(key) : card.Title;
        var minutes = card.MinutesSpent > 0
            ? string.Format(CultureInfo.CurrentCulture,
                text("Dashboard_SpentOfEstimate"),
                card.MinutesSpent, card.EstimatedMinutes)
            : string.Format(CultureInfo.CurrentCulture,
                text("Dashboard_EstimateMinutes"), card.EstimatedMinutes);
        var metadata = card.ResourceTitle is { Length: > 0 }
            ? $"{card.ResourceTitle} \u00b7 {minutes}" : minutes;
        if (card.VocabDueCount > 0)
            metadata += " \u00b7 " + string.Format(CultureInfo.CurrentCulture,
                text("Dashboard_VocabDueCount"), card.VocabDueCount.Value);
        var cardContent = new List<SwiftView>
        {
            style.Text(Views.Text(name), SourceTextRole.Body1Strong,
                    card.IsCompleted
                        ? SourceTextTone.Secondary : SourceTextTone.Body)
                .WithStrikethrough(card.IsCompleted)
                .WithAccessibilityIdentifier("native.plan.card.title"),
        };
        cardContent.Add(style.Text(Views.Text(metadata),
                SourceTextRole.Small, SourceTextTone.Secondary)
            .WithAccessibilityIdentifier("native.plan.card.metadata"));
        if (card.MinutesSpent > 0 && !card.IsCompleted)
            cardContent.Add(style.Progress(Views.Progress(
                    Math.Min(card.MinutesSpent,
                        Math.Max(1, card.EstimatedMinutes)),
                    Math.Max(1, card.EstimatedMinutes), 4, minutes),
                    completion: false)
                .WithAccessibilityIdentifier(
                    $"native.plan.card.progress.{card.Id}"));
        if (card.IsCompleted)
            cardContent.Add(style.Text(
                    Views.Text(text("PlanSummary_Completed")),
                    SourceTextRole.Small, SourceTextTone.Success)
                .WithAccessibilityIdentifier("native.plan.card.completed"));
        var accessibility = NativeDashboardCardAccessibility.Label(
            name, metadata,
            card.IsCompleted ? text("PlanSummary_Completed") : null);
        var description = Views.VStack(cardContent.ToArray())
            .WithFlexibleWidth();
        description.Spacing = SourceUiLayout.ItemGap;
        var row = Views.HStack(
            Views.Image(card.IsCompleted
                ? "checkmark.circle.fill" : ActivitySymbol(card.Activity))
                .WithForegroundColor(card.IsCompleted
                        ? style.Appearance.Success.Light
                        : style.Appearance.Primary.Light,
                    card.IsCompleted
                        ? style.Appearance.Success.Dark
                        : style.Appearance.Primary.Dark),
            description,
            Views.Image("chevron.right")
                .WithForegroundColor(style.Appearance.SecondaryText.Light,
                    style.Appearance.SecondaryText.Dark)
                .WithAccessibilityIdentifier(
                    $"native.plan.card.chevron.{card.Id}"))
            .WithFlexibleWidth();
        row.Spacing = SourceUiLayout.PageGap;
        var content = style.Card(
            row.WithPadding(SourceUiLayout.CardInset))
            .WithOpacity(card.IsCompleted ? 0.75 : 1);
        var canOpen = card.Activity == PlanActivityType.VocabularyReview
            && openQuiz is not null;
        var button = Views.Button(content,
                () =>
                {
                    if (canOpen)
                        openQuiz!(card.Id);
                })
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithAccessibilityLabel(accessibility)
            .WithAccessibilityIdentifier(
                $"{NativeAccess.PlanCard}.{card.Id}");
        button.IsEnabled = canOpen;
        return button;
    }

    private static string ActivitySymbol(PlanActivityType activity) => activity switch
    {
        PlanActivityType.VocabularyReview => "checklist",
        PlanActivityType.Reading => "book",
        PlanActivityType.Listening => "headphones",
        PlanActivityType.Shadowing => "waveform",
        PlanActivityType.Cloze => "puzzlepiece",
        PlanActivityType.Translation => "character.book.closed",
        PlanActivityType.Writing => "square.and.pencil",
        PlanActivityType.SceneDescription => "photo",
        PlanActivityType.Conversation => "bubble.left.and.bubble.right",
        PlanActivityType.VocabularyGame => "square.grid.3x3",
        PlanActivityType.VideoWatching => "play.circle",
        PlanActivityType.NumberDrill => "number",
        _ => "checkmark.circle"
    };

}
