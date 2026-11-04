using System.Collections.Immutable;
using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Native;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Android;

/// <summary>
/// "Use in a sentence" (source VocabQuiz.razor sentence shortcut): the target
/// word and gloss, one or more learner sentences graded by the source
/// GradeSentence path, mastery credit only for UsageCorrect results, and
/// Cancel before grading or Continue quiz after it. A sentence turn saved by
/// any client renders here on resume.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private void RenderQuizSentence(
        Column viewport, NativeQuizState state, AndroidVisualPalette palette)
    {
        var sentence = state.Sentence!;
        var results = state.SentenceResults.IsDefault
            ? ImmutableArray<VocabQuizSentenceGrade>.Empty
            : state.SentenceResults;
        var graded = !results.IsEmpty;
        var allUnavailable = graded && results.All(result => result.GradeUnavailable);
        var submitting = state.IsSentenceSubmitting || state.Phase == NativePhase.Loading;
        var busy = Busy.Value || submitting;

        var badge = Body(Localize("VocabQuiz_SentenceShortcut"), palette,
            modifier: Modifier.TestTag("native.quiz.sentence.badge"));
        badge.Color = palette.Primary;
        badge.FontSize = new Sp(AndroidVisualTheme.CaptionSize);
        badge.FontWeight = FontWeight.SemiBold;
        var term = Heading(sentence.TargetLanguageTerm, palette, 24,
            Modifier.TestTag("native.quiz.sentence.target-term")
                .Semantics(semantics => semantics.Heading()));
        term.Color = palette.Primary;
        var heading = new Column(Arrangement.SpacedBy(4))
        {
            Modifier.Weight(1f),
            badge,
            term,
            Body(sentence.NativeLanguageGloss, palette, secondary: true,
                modifier: Modifier.TestTag("native.quiz.sentence.gloss")),
        };
        var header = new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            heading,
        };
        // As iOS: when no sentence could be graded the header action gives way to
        // Retry on the grade button; Back still leaves the quiz.
        if (!allUnavailable)
        {
            var label = Localize(graded ? "VocabQuiz_ContinueQuiz" : "Common_Cancel");
            var action = new OutlinedButton(
                onClick: graded ? ContinueAfterQuizSentence : CancelQuizSentence,
                enabled: !busy && (!graded || state.Phase == NativePhase.Ready))
            {
                Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag(graded ? "native.quiz.sentence.continue"
                        : "native.quiz.sentence.cancel")
                    .Semantics(semantics => semantics.ContentDescription(label)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            action.Add(new Text(label));
            header.Add(action);
        }

        var instruction = string.Format(CultureInfo.CurrentCulture,
            Localize(sentence.InstructionKey), sentence.TargetLanguageTerm);
        // RenderSentence guarantees a usable target term.
        var placeholder = string.Format(CultureInfo.CurrentCulture,
            Localize("VocabQuiz_SentencePlaceholder"), sentence.TargetLanguageTerm);
        var input = new OutlinedTextField(SentenceDraft, enabled: !busy,
            singleLine: false, minLines: 5)
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.quiz.sentence.input")
                .Semantics(semantics => semantics.ContentDescription(instruction)),
            Placeholder = new Text(placeholder),
        };
        var gradeLabel = Localize(submitting ? "Common_Loading"
            : allUnavailable ? NativeText.Retry : "VocabQuiz_GradeSentences");
        var grade = Action(gradeLabel, "native.quiz.sentence.grade", SubmitQuizSentence,
            !busy && !string.IsNullOrWhiteSpace(SentenceDraft.Value.Text));
        var card = new List<ComposableNode>
        {
            header,
            Body(instruction, palette, modifier: Modifier.TestTag("native.quiz.sentence.instruction")),
            Body(Localize("VocabQuiz_OnlyMeaningful"), palette, secondary: true),
            input,
            SentenceCaption(Localize("VocabQuiz_SeparateMultiple"), palette),
        };
        if (submitting)
            card.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(24).TestTag("native.quiz.sentence.grade.spinner")
                },
                Body(Localize("Common_Loading"), palette, secondary: true),
            });
        card.Add(grade);
        viewport.Add(Card(palette, "native.quiz.sentence.screen", card.ToArray()));

        if (state.ErrorKey is not null)
            viewport.Add(StatusAlert(Localize(state.ErrorKey), palette, error: true,
                automationId: "native.quiz.sentence.error"));
        if (state.Phase == NativePhase.Error
            && AndroidQuizTurnPolicy.Route(state, _quizFailedCommand, _quizLaunch is not null)
                == AndroidQuizRetry.Sentence)
            viewport.Add(Action(Localize(NativeText.Retry), "native.quiz.sentence.retry",
                RetryQuiz, !Busy.Value));
        if (!graded)
            return;

        viewport.Add(StatusAlert(string.Format(CultureInfo.CurrentCulture,
                Localize("NativeVocabQuiz_SentenceResults"),
                state.SentenceSuccessCount, results.Length, sentence.TargetLanguageTerm),
            palette, success: state.SentenceSuccessCount > 0,
            automationId: "native.quiz.sentence.summary"));
        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            var verified = result.HasVerifiedTargetUse
                && result.TargetUsedForm is { Length: > 0 }
                && !result.GradeUnavailable;
            var status = Body(Localize(result.GradeUnavailable
                    ? NativeText.SentenceGradeUnavailable
                    : result.UsageCorrect
                        ? "VocabQuiz_CountsTowardMastery" : "VocabQuiz_NeedsAnotherTry"),
                palette, modifier: Modifier.TestTag($"native.quiz.sentence.result.{index}.status"));
            status.FontWeight = FontWeight.SemiBold;
            status.Color = result.UsageCorrect && !result.GradeUnavailable
                ? palette.Success : palette.SecondaryText;
            var body = new List<ComposableNode>
            {
                status,
                Body(result.Sentence, palette,
                    modifier: Modifier.TestTag($"native.quiz.sentence.result.{index}.sentence")),
            };
            if (!result.GradeUnavailable)
                body.Add(Body($"{Localize("VocabQuiz_TargetWordFeedback")} " + (verified
                        ? result.UsageExplanation
                        : string.Format(CultureInfo.CurrentCulture,
                            Localize(NativeText.SentenceTargetUnconfirmed),
                            sentence.TargetLanguageTerm)),
                    palette, secondary: true));
            if (verified)
            {
                if (result.Accuracy is { } accuracy && result.Fluency is { } fluency)
                    body.Add(SentenceCaption(
                        $"{Localize("Common_Accuracy")}: {accuracy.ToString("F0", CultureInfo.CurrentCulture)}% · "
                        + $"{Localize("Writing_Fluency")}: {fluency.ToString("F0", CultureInfo.CurrentCulture)}%",
                        palette));
                if (!string.IsNullOrWhiteSpace(result.RecommendedSentence))
                    body.Add(Body($"{Localize("Common_Suggested")} {result.RecommendedSentence}",
                        palette, secondary: true));
                foreach (var note in new[] { result.AccuracyExplanation,
                             result.FluencyExplanation, result.GrammarNotes })
                    if (!string.IsNullOrWhiteSpace(note))
                        body.Add(SentenceCaption(note, palette));
            }
            viewport.Add(Card(palette, $"native.quiz.sentence.result.{index}", body.ToArray()));
        }
    }

    private static Text SentenceCaption(string value, AndroidVisualPalette palette)
    {
        var caption = Body(value, palette, secondary: true);
        caption.FontSize = new Sp(AndroidVisualTheme.CaptionSize);
        return caption;
    }
}
