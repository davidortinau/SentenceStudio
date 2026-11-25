using System.Reflection;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// Issue 261 (Captain, 2026-11-24): the typed-answer field's own label says what to do, so the
/// quiz shows no second line above it saying so again.
/// </summary>
public sealed class AndroidQuizTypedAnswerContractTests
{
    [Fact]
    public void TypedAnswerField_IsNamedByItsOwnLabelOnly()
    {
        var root = typeof(AndroidQuizTypedAnswerContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        var compose = File.ReadAllText(Path.Combine(root, "AndroidSliceShell.Compose.cs"));
        var start = compose.IndexOf("else if (state.Mode == \"Text\")", StringComparison.Ordinal);
        var end = compose.IndexOf("Localize(\"VocabQuiz_SubmitAnswer\")", start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var typed = compose[start..end];

        Assert.DoesNotContain("VocabQuiz_TypeTranslation", typed);
        Assert.Contains("? NativeText.CorrectionRequired : NativeText.AnswerRequired", typed);
        Assert.Contains("Label = new Text(label)", typed);
        Assert.Contains(".Semantics(semantics => semantics.ContentDescription(label))", typed);
    }
}
