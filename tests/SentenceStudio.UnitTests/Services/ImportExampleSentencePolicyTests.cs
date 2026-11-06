using FluentAssertions;
using SentenceStudio.Services;
using Xunit;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Which import rows keep their source sentence as the word's example (B72 review). A kept
/// sentence is a TargetToNative quiz hint, so it must be a target-language sentence from the
/// pasted text that does not show the meaning.
/// </summary>
public class ImportExampleSentencePolicyTests
{
    private const string Pasted =
        "사과 - apple\n바나나, banana\n사과apple\nI ate 김치 today.\n시간이 없어요\n" +
        "오늘 밥을 먹었어요. 정말 맛있었어요!\n오늘 TV를 봤어요.";

    private static bool Keeps(string? sentence, string? translation, string term, string native,
        string? source = Pasted, string? wordNative = null) =>
        ImportExampleSentencePolicy.KeepsSentence(sentence, translation, source,
            [term, null], [native, wordNative]);

    [Fact]
    public void AKoreanSentenceFromThePastedText_IsKept()
    {
        Keeps("오늘 밥을 먹었어요.", "I ate rice today.", "밥", "rice").Should().BeTrue();
        // Spacing, punctuation and case may differ from the paste.
        Keeps("오늘  밥을 먹었어요", "I ate rice today.", "밥", "rice").Should().BeTrue();
        // A Latin loanword inside a Korean sentence is not the meaning of the term.
        Keeps("오늘 TV를 봤어요.", "I watched TV today.", "보다", "to watch").Should().BeTrue();
    }

    [Theory]
    [InlineData("사과 - apple", "apple")]
    [InlineData("사과apple", null)]
    public void AWordListLinePairingTheTermWithItsTranslation_IsNotKept(string sentence, string? translation)
    {
        Keeps(sentence, translation, "사과", "apple").Should().BeFalse();
    }

    [Fact]
    public void ALineWithTheSavedWordsTranslation_IsNotKept()
    {
        // The quiz asks for the saved word's translation, which can differ from the row's.
        Keeps("바나나, banana", null, "바나나", "plantain", wordNative: "banana").Should().BeFalse();
        // A one-syllable translation still counts (a Korean speaker learning English).
        Keeps("book - 책", null, "book", "책", source: "book - 책\npen - 펜").Should().BeFalse();
    }

    [Fact]
    public void ANativeLanguageOrMixedLine_IsNotKept()
    {
        Keeps("I ate 김치 today.", "I ate kimchi today.", "김치", "kimchi").Should().BeFalse();
        // A sentence that contains its own translation is not a target-language sentence.
        Keeps("시간이 없어요", "시간이 없어요", "시간", "time").Should().BeFalse();
    }

    [Fact]
    public void TheTermItself_OrAFullLinePhraseRestatingItself_IsNotKept()
    {
        Keeps("사과", null, "사과", "apple", source: "사과\n바나나").Should().BeFalse();
        Keeps("시간이 없어요.", "I have no time.", "시간이 없어요", "to have no time").Should().BeFalse();
    }

    [Fact]
    public void ASentenceNotInThePastedText_OrNoPastedText_IsNotKept()
    {
        Keeps("밥을 많이 먹었어요.", "I ate a lot of rice.", "밥", "rice").Should().BeFalse();
        Keeps("오늘 밥을 먹었어요.", "I ate rice today.", "밥", "rice", source: null).Should().BeFalse();
        Keeps("  ", null, "밥", "rice").Should().BeFalse();
    }

    [Fact]
    public void TermEdited_ComparesTheEditedTermWithTheParsedOne()
    {
        ImportExampleSentencePolicy.TermEdited(new ImportRow
            { ParsedTargetLanguageTerm = "밥", TargetLanguageTerm = "식사" }).Should().BeTrue();
        ImportExampleSentencePolicy.TermEdited(new ImportRow
            { ParsedTargetLanguageTerm = "밥 ", TargetLanguageTerm = "밥" }).Should().BeFalse();
        // A client that does not carry the parsed term never skips the word check.
        ImportExampleSentencePolicy.TermEdited(new ImportRow
            { TargetLanguageTerm = "식사" }).Should().BeFalse();
    }
}
