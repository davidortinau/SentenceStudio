using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeQuizFeedbackContractTests
{
    [Fact]
    public void AnswerTermThenResultStatus_AppearAboveTheSameMcAndTextControls()
    {
        var feature = FeatureSource();
        var questionStart = feature.IndexOf("internal static void Add(",
            StringComparison.Ordinal);
        var feedbackStart = feature.IndexOf("private static SwiftView QuizChoice(",
            StringComparison.Ordinal);
        Assert.True(questionStart >= 0 && feedbackStart > questionStart);

        var question = feature[questionStart..feedbackStart];
        var graded = question.IndexOf(
            "if (state.Phase == NativePhase.Feedback)", StringComparison.Ordinal);
        var correctTerm = question.IndexOf(
            "native.quiz.correct-answer", StringComparison.Ordinal);
        var status = question.IndexOf(
            "WithAccessibilityIdentifier(NativeAccess.QuizFeedback)",
            StringComparison.Ordinal);
        var choices = question.IndexOf(
            "if (state.Mode == \"MultipleChoice\")",
            StringComparison.Ordinal);
        var textInput = question.IndexOf(
            "else if (state.Mode == \"Text\")",
            StringComparison.Ordinal);
        Assert.True(graded >= 0 && correctTerm > graded
            && status > correctTerm && choices > status && textInput > choices);
        Assert.Contains("state.AnswerWasCorrect == true", question);
        Assert.Contains("state.CompleteForm is { Length: > 0 }", feature);
        Assert.Contains("state.FeedbackMnemonicText is { Length: > 0 }", question);
        Assert.Contains("NativeVocabQuiz_FuzzyFeedback", feature);
        Assert.Contains("NativeVocabQuiz_CorrectFeedback", feature);
        Assert.Contains("NativeVocabQuiz_IncorrectFeedback", feature);
        Assert.Contains("native.quiz.feedback.mnemonic", question);
        Assert.DoesNotContain("native.quiz.feedback.full-form", feature);
    }

    [Fact]
    public void SourceInfoAlerts_PreserveDistinctDashboardQuizAndSmartResourceCallers()
    {
        var feature = FeatureSource();
        var insight = ReadHead("Features/Dashboard/PlanInsightView.cs");
        var resource = ReadHead("Features/LearningResources/LearningResourceView.cs");
        var style = ReadHead("Styling/SourceUiStyle.cs");
        Assert.Contains("style.InfoAlert(pattern, compact: true)", insight);
        Assert.Contains("style.InfoAlert(message, compact: true)", feature);
        Assert.Contains("style.InfoAlert(panel, compact: true)", feature);
        // Captain 2026-11-13: native Resource form. The smart-resource notice is the first
        // row of the form's notices Section (a sparkles Label in the info color), not a custom
        // info alert card.
        Assert.Contains("Views.Label(text(\"ResourceEdit_SmartResource\"), \"sparkles\")", resource);
        Assert.Contains("style.Appearance.InfoAlertText.Light", resource);
        Assert.Contains("notices.Insert(0, smartNotice);", resource);
        Assert.DoesNotContain("style.InfoAlert(smartNotice", resource);
        Assert.Contains("appearance.InfoAlertFill", style);
        Assert.Contains("appearance.InfoAlertText", style);
        Assert.Contains("appearance.InfoAlertBorder", style);
        Assert.DoesNotContain("style.BorderedCard(smartNotice", resource);
    }

    [Fact]
    public void WrongTextCorrection_ReusesTheOriginalInputInsteadOfAddingANewField()
    {
        var feature = FeatureSource();
        var app = File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS/App/ApplicationController.Quiz.cs")));

        Assert.Contains("state.RequiresCorrection", feature);
        Assert.Contains("NativeText.CorrectionRequired", feature);
        Assert.Contains(".WithAccessibilityIdentifier(NativeAccess.QuizAnswer)",
            feature);
        Assert.DoesNotContain("QuizAnswer}.correction", feature);
        Assert.Contains("_quiz.SubmitCorrectionAsync(", app);
        Assert.Contains("_answer = string.Empty;", app);
    }

    [Fact]
    public void SentenceResults_HideUnverifiedAiFeedbackButKeepVerifiedNegativeGrammar()
    {
        var sentence = ReadHead("Features/Quiz/QuizSentenceView.cs");
        var trustedDetails = sentence.IndexOf("if (hasVerifiedTargetUse)",
            StringComparison.Ordinal);
        var scores = sentence.IndexOf("result.Accuracy is { } accuracy",
            StringComparison.Ordinal);
        var recommendation = sentence.IndexOf(
            "!string.IsNullOrWhiteSpace(result.RecommendedSentence)",
            StringComparison.Ordinal);
        var notes = sentence.IndexOf("foreach (var note",
            StringComparison.Ordinal);

        Assert.Contains("result.HasVerifiedTargetUse", sentence);
        Assert.Contains("&& result.TargetUsedForm is { Length: > 0 }", sentence);
        Assert.Contains("&& !result.GradeUnavailable", sentence);
        Assert.Contains("var usageExplanation = hasVerifiedTargetUse", sentence);
        Assert.Contains("if (!result.GradeUnavailable)", sentence);
        Assert.Contains("text(NativeText.SentenceTargetUnconfirmed)", sentence);
        Assert.Contains("sentence.TargetLanguageTerm", sentence);
        Assert.DoesNotContain("result.Accuracy:F0", sentence);
        Assert.Contains("&& result.Fluency is { } fluency", sentence);
        Assert.True(trustedDetails > 0 && scores > trustedDetails
            && recommendation > scores && notes > recommendation);
        var trustedFields = sentence[trustedDetails..sentence.IndexOf(
            "var resultCard =", trustedDetails, StringComparison.Ordinal)];
        Assert.DoesNotContain("if (result.UsageCorrect)", trustedFields);
        Assert.Contains("var graded = !results.IsEmpty;", sentence);
    }

    [Fact]
    public void SummaryUsesRealReadyToRotateOutInsteadOfEquatingCorrectWithMastered()
    {
        var feature = ReadHead("Features/Quiz/QuizSummaryView.cs");
        Assert.Contains("result.ReadyToRotateOut", feature);
        Assert.Contains("? \"VocabQuiz_Mastered\"", feature);
        Assert.Contains(": NativeText.QuizCorrect", feature);
        Assert.Contains(": NativeText.QuizIncorrect", feature);
    }

    // Issue 245: a word the learner marked Known had the wrong-answer symbol and colour.
    [Fact]
    public void SummaryShowsAMarkedWordAsKnownInTheSuccessColour()
    {
        var feature = ReadHead("Features/Quiz/QuizSummaryView.cs");
        Assert.Contains("var status = result.MarkedKnown ? \"Vocabulary_Known\"", feature);
        Assert.Contains("Views.Image(result.MarkedKnown ? \"checkmark.seal\"", feature);
        Assert.Contains("var positive = result.MarkedKnown || result.WasCorrect;", feature);
        Assert.Contains("positive\n                        ? style.Appearance.Success.Light",
            feature.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void SummaryOfAQuizWithEveryWordKnownSaysSoInPlaceOfZeroStatistics()
    {
        // Issue 235.
        var feature = ReadHead("Features/Quiz/QuizSummaryView.cs");
        Assert.Matches(
            @"state\.AllWordsKnown\s*\? style\.Text\(Views\.Text\(text\(NativeText\.QuizAllKnown\)\)",
            feature);
        Assert.Contains("\"native.quiz.summary.all-known\"", feature);
        Assert.Matches(@"\: Views\.HStack\(\s*Statistic\(state\.CorrectCount", feature);
    }

    [Fact]
    public void SummaryUsesOneSourceCardWithInlineResultRowsAndSuccessMetrics()
    {
        var feature = ReadHead("Features/Quiz/QuizSummaryView.cs");
        var start = feature.IndexOf("internal static SwiftView? Add(",
            StringComparison.Ordinal);
        var end = feature.IndexOf("private static SwiftView Statistic(", start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var summary = feature[start..end];

        Assert.Contains("var summary = new List<SwiftView>", summary);
        Assert.Contains("summary.Add(Views.HStack(", summary);
        Assert.Contains("items.Add(style.Card(summaryCard, maxWidth: 500)", summary);
        Assert.Equal(1, summary.Split("style.Card(", StringSplitOptions.None).Length - 1);
        Assert.Contains("\"mastered\", style, success: true)", summary);
        Assert.Contains(".WithAccessibilityIdentifier($\"{id}.status\")", summary);
    }

    [Fact]
    public void Issue225_MultipleChoiceResultShowsOnlyOnTheOptions()
    {
        var feature = FeatureSource();

        // The banner stays for text answers and for a turn whose answer is not an option;
        // written choices also drop the answer line, which audio-letter choices keep.
        Assert.Contains("state.Choices.Any(choice => IsCorrectChoice(state, choice.Text))",
            feature);
        Assert.Contains("var showAnswerLine = !choicesShowResult || audioChoices;", feature);
        Assert.Contains("state.Phase == NativePhase.Feedback && showAnswerLine", feature);
        Assert.Contains("state.Phase == NativePhase.Feedback && !choicesShowResult", feature);
        // Not by color alone: a mark whose place is always kept, and the spoken result.
        Assert.Contains("\"xmark.circle.fill\" : \"checkmark.circle.fill\"", feature);
        Assert.Contains(".WithHidden(!correct && !incorrect)", feature);
        Assert.Contains("title.WithForegroundColor(\"#FFFFFF\", \"#FFFFFF\")", feature);
        Assert.Contains("answer + ChoiceResultCaption(state, answer, text)", feature);
        Assert.Contains("+ ChoiceResultCaption(state, choice.Text, text)", feature);
        Assert.Contains("choiceButton.WithAccessibilityAnnouncement(", feature);
    }

    [Fact]
    public void Issue225_SummaryContinueAndDoneStayOnScreenInAFooter()
    {
        var summary = ReadHead("Features/Quiz/QuizSummaryView.cs");
        var page = ReadHead("Features/Quiz/QuizView.cs");

        Assert.Contains("internal static SwiftView? Add(", summary);
        Assert.Contains("\"native.quiz.summary.footer\"", summary);
        Assert.Contains("style.DockedBar(Views.VStack(row))", summary);
        Assert.Contains("NativeAdaptiveLayout.EffectiveWidth(", summary);
        Assert.DoesNotContain("items.Add(style.PrimaryButton(", summary);
        Assert.Contains("footer = QuizSummaryView.Add(", page);
        Assert.Contains("Views.BottomInset(page, footer)", page);
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void GradedFeedbackAndMasteredCopyExistInBothLocales(string file)
    {
        var stringsPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Shared/Resources/Strings", file));
        var resources = XDocument.Load(stringsPath).Descendants("data").ToArray();
        foreach (var key in new[]
        {
            "NativeVocabQuiz_FuzzyFeedback", "NativeVocabQuiz_CorrectFeedback",
            "NativeVocabQuiz_IncorrectFeedback", "NativeVocabQuiz_Mnemonic",
            "NativeVocabQuiz_SentenceTargetUnconfirmed",
            "NativeVocabQuiz_SentenceGradeUnavailable",
            "VocabQuiz_TargetWordFeedback", "VocabQuiz_Mastered",
            "Common_Correct", "Common_Incorrect"
        })
            Assert.True(resources.Any(item =>
                    item.Attribute("name")?.Value == key
                    && !string.IsNullOrWhiteSpace(item.Element("value")?.Value)),
                $"{file} lacks {key}");
        var unconfirmed = resources.Single(item =>
            item.Attribute("name")?.Value == "NativeVocabQuiz_SentenceTargetUnconfirmed")
            .Element("value")?.Value;
        Assert.NotNull(unconfirmed);
        Assert.Contains("{0}", unconfirmed);
    }

    private static string FeatureSource() => File.ReadAllText(
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS/Features/Quiz/QuizQuestionView.cs")));

    private static string ReadHead(string file) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS", file)));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
