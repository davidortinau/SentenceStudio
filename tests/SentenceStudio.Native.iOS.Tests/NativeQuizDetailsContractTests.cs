using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SentenceStudio.Native;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeQuizDetailsContractTests
{
    [Fact]
    public void LearningDetailsSheet_UsesOwnerBoundStatsWithoutPreAnswerTerms()
    {
        var feature = Source("Features/Quiz/QuizQuestionView.cs");
        var details = Source("Features/Quiz/QuizLearningDetailsView.cs");
        var app = Source("App/ApplicationController.Quiz.cs");
        var scene = Source("App/ApplicationScene.cs");

        Assert.Contains("NativeAccess.QuizDetails));", feature);
        Assert.Contains("OpenDetails: OpenQuizDetails", app);
        Assert.Contains("_quiz.OpenDetails()", app);
        Assert.Contains("_quizState.Overlay == NativeQuizOverlay.LearningDetails", app);
        Assert.Contains("_quiz.DismissOverlay()", app);
        Assert.Contains("NativeQuizLearningDetails details", details);
        Assert.Contains("native.quiz.details.close", details);
        Assert.Contains("style.GroupedForm(Views.Form(fields.ToArray()))", details);
        Assert.Contains("Views.VStack(Views.Divider()", details);
        Assert.Contains("native.quiz.details.srs.divider", details);
        Assert.Contains("native.quiz.details.context.divider", details);
        Assert.Contains("Views.Sheet(_quizSlot, _quizDetailsSlot,", scene);
        Assert.Contains("_app.DismissQuizDetails)", scene);
        Assert.Contains(".WithSheetDetentFraction(0.7)", scene);
        Assert.Contains(".WithTabBarHidden()", scene);
        Assert.DoesNotContain("TargetLanguageTerm", details);
        Assert.DoesNotContain("NativeLanguageTerm", details);
        Assert.DoesNotContain("CorrectAnswerAfterGrading", details);
    }

    [Fact]
    public void DetailsEntry_WorksBeforeAndAfterGradingWithoutAnswerLeakage()
    {
        var feature = Source("Features/Quiz/QuizQuestionView.cs");
        var app = Source("App/ApplicationController.Quiz.cs");

        Assert.Contains("NativePhase.Ready or NativePhase.Feedback", app);
        Assert.Contains("\"info.circle\", openDetails, text(\"VocabQuiz_LearningDetails\")",
            feature);
        Assert.DoesNotContain("native.quiz.details.unavailable", feature);
        Assert.DoesNotContain("QuizDetailsLocalized", app);
    }

    [Fact]
    public void LearningDetails_UseSwiftUiSheetNotAUIKitOrPlaceholderAlert()
    {
        var feature = Source("Features/Quiz/QuizLearningDetailsView.cs");
        var scene = Source("App/ApplicationScene.cs");
        Assert.Contains("Views.Sheet(_quizSlot, _quizDetailsSlot,", scene);
        Assert.DoesNotContain("UIKit", scene);
        Assert.DoesNotContain("Coach_Hint_Unknown", feature);
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void LearningDetailsMetrics_HaveBothSupportedLocales(string file)
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Shared/Resources/Strings", file));
        var resources = XDocument.Load(path).Descendants("data").ToArray();
        var keys = new[]
        {
            "NativeVocabQuiz_Details_TotalAttempts",
            "NativeVocabQuiz_Details_CorrectAttempts",
            "NativeVocabQuiz_Details_CurrentStreak",
            "NativeVocabQuiz_Details_ProductionInStreak",
            "NativeVocabQuiz_Details_EffectiveStreak",
            "NativeVocabQuiz_Details_MasteryScore",
            "NativeVocabQuiz_Details_SrsSchedule",
            "VocabWordEdit_NextReviewLabel",
            "NotScheduled",
            "NativeVocabQuiz_Details_ReviewInterval",
            "NativeVocabQuiz_Details_Days",
            "NativeVocabQuiz_Details_EaseFactor",
            "NativeVocabQuiz_Details_IsDueForReview",
            "NativeVocabQuiz_Details_QuizContext",
            "NativeVocabQuiz_Details_QuizMode",
            "NativeVocabQuiz_Details_TextMode",
            "NativeVocabQuiz_Details_MultipleChoiceMode",
            "NativeVocabQuiz_Details_DueOnly",
            "NativeVocabQuiz_Details_WhyMode",
            "NativeVocabQuiz_Details_RecognitionDemonstrations",
            "VocabQuiz_LearningDetails",
            "Accuracy",
            "Yes",
            "No",
            "Coach_Hint_Unknown"
        };
        var missing = keys.Where(key => !resources.Any(item =>
            item.Attribute("name")?.Value == key
            && !string.IsNullOrWhiteSpace(item.Element("value")?.Value))).ToArray();
        Assert.Empty(missing);
    }

    private static string Source(string name) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS", name)));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
