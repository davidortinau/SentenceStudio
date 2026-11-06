using FluentAssertions;
using SentenceStudio.Services;
using Xunit;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// A Korean verb or adjective in dictionary form is found conjugated, including where the stem's
/// last syllable merges with the ending, and not in words that only begin like it (B72 review).
/// </summary>
public class KoreanPredicateMatcherTests
{
    [Theory]
    [InlineData("먹다", "밥을 먹었어요.")]
    [InlineData("만나다", "어제 친구를 만났어요.")]
    [InlineData("보다", "영화를 봤어요.")]
    [InlineData("가다", "학교에 갔어요.")]
    [InlineData("오다", "친구가 와요.")]
    [InlineData("마시다", "물을 마셔요.")]
    [InlineData("공부하다", "도서관에서 공부했어요.")]
    [InlineData("공부하다", "매일 공부해요.")]
    [InlineData("공부하다", "열심히 공부합니다.")]
    [InlineData("듣다", "음악을 들어요.")]
    [InlineData("덥다", "오늘 너무 더워요.")]
    [InlineData("모르다", "저는 잘 몰라요.")]
    [InlineData("부르다", "노래를 불러요.")]
    [InlineData("쓰다", "편지를 써요.")]
    [InlineData("되다", "선생님이 됐어요.")]
    [InlineData("살다", "서울에 사세요?")]
    [InlineData("맛있다", "정말 맛있었어요!")]
    [InlineData("가다", "내일 갈 거예요.")]
    [InlineData("가고 싶다", "다음 주에 또 가고 싶어요.")]
    [InlineData("밥 먹다", "친구랑 밥을 먹었어요.")]
    public void AConjugatedPredicate_IsFound(string dictionaryForm, string sentence)
    {
        KoreanPredicateMatcher.Contains(sentence, dictionaryForm).Should().BeTrue();
    }

    [Theory]
    [InlineData("가다", "고양이가 있어요.")]
    [InlineData("가다", "가방을 샀어요.")]
    [InlineData("보다", "보통 일찍 자요.")]
    [InlineData("오다", "오늘은 맑아요.")]
    [InlineData("공부하다", "공부방이 넓어요.")]
    [InlineData("먹다", "먹")]
    [InlineData("마시다", "마스크를 써요.")]
    [InlineData("밥 먹다", "고기를 먹었어요.")]
    public void AWordThatOnlyBeginsLikeTheStem_IsNotThePredicate(string dictionaryForm, string sentence)
    {
        KoreanPredicateMatcher.Contains(sentence, dictionaryForm).Should().BeFalse();
    }

    [Fact]
    public void ANounOrANonKoreanTerm_IsNotADictionaryForm()
    {
        KoreanPredicateMatcher.IsDictionaryForm("사과").Should().BeFalse();
        KoreanPredicateMatcher.IsDictionaryForm("comer").Should().BeFalse();
        KoreanPredicateMatcher.IsDictionaryForm("먹다").Should().BeTrue();
        KoreanPredicateMatcher.Contains("Ayer comí arroz.", "comer").Should().BeFalse();
    }
}
