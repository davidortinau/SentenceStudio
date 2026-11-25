using System.Text.RegularExpressions;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Issue 261 (Captain, 2026-11-24): the typed-answer field's hint says what to do, so no label
/// above it says so again. With the label gone the field takes its accessible name from the
/// same words as its hint. Runs the real page on a typed turn, in English.
/// </summary>
public sealed class VocabQuizTypedAnswerTests
{
    [Fact]
    public async Task TheTypedAnswerField_HasNoLabelAboveItThatRepeatsItsHint()
    {
        await using var harness = await VocabQuizSentenceShortcutTests.OpenQuizAsync();
        harness.Set("userMode", "Text");
        await harness.DispatchAsync("StateHasChanged");

        var page = harness.Html();
        page.Should().NotContain("Type the translation");
        var field = Regex.Match(page, @"<input id=""quiz-text-input""[^>]*>").Value;
        field.Should().Contain("placeholder=\"Type your answer\"", "the hint is the instruction")
            .And.Contain("aria-label=\"Type your answer\"", "the field keeps an accessible name");
        Regex.IsMatch(page, @"<label[^>]*>[^<]*</label>\s*<input id=""quiz-text-input""")
            .Should().BeFalse("nothing sits between the prompt and the field");
    }
}
