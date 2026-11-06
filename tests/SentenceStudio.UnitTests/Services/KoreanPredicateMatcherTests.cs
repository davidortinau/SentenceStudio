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
    [InlineData("밥 먹다", "밥을 먹었어요.")]
    [InlineData("먹다", "먹었어요.")]
    [InlineData("있다", "시간이 있어요.")]
    [InlineData("공부하다", "공부해요.")]
    [InlineData("공부하다", "공부했어요.")]
    [InlineData("공부하다", "공부합니다.")]
    [InlineData("살다", "부산에 삽니다.")]
    [InlineData("알다", "그 사람을 아는 것 같아요.")]
    [InlineData("만들다", "제가 만든 케이크예요.")]
    [InlineData("길다", "긴 머리가 예뻐요.")]
    [InlineData("낫다", "감기가 다 나았어요.")]
    [InlineData("짓다", "집을 지었어요.")]
    [InlineData("덥다", "더우면 문을 여세요.")]
    [InlineData("돕다", "친구를 도와요.")]
    [InlineData("걷다", "공원을 걸었어요.")]
    [InlineData("그렇다", "네, 그래요.")]
    [InlineData("그렇다", "그러면 내일 만나요.")]
    [InlineData("어떻다", "어떤 음식을 좋아해요?")]
    [InlineData("하얗다", "눈이 하얘요.")]
    [InlineData("바쁘다", "요즘 너무 바빠요.")]
    [InlineData("크다", "집이 커요.")]
    [InlineData("주다", "책을 줬어요.")]
    [InlineData("보내다", "편지를 보냈어요.")]
    [InlineData("켜다", "불을 켰어요.")]
    [InlineData("자다", "일찍 잘게요.")]
    [InlineData("가다", "같이 갑시다.")]
    [InlineData("보다", "매일 뉴스를 본다.")]
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

    /// <summary>
    /// The last stem syllable may differ only where conjugation changes it (a vowel stem's
    /// contraction or added ㅆ/ㅂ/ㄴ/ㄹ, the ㄷ ㅂ ㅅ ㅎ ㄹ irregulars): another verb or noun that
    /// only shares the initial consonant is not the predicate (B72 third review).
    /// </summary>
    [Theory]
    [InlineData("먹다", "우유를 마셨어요.")]
    [InlineData("자다", "이 영화가 좋아요.")]
    [InlineData("있다", "저는 학생이 아니에요.")]
    [InlineData("있다", "여기서 기다려요.")]
    [InlineData("높다", "노래를 불러요.")]
    [InlineData("가다", "갑자기 비가 와요.")]
    [InlineData("하다", "할아버지가 오셨어요.")]
    [InlineData("가다", "간을 조심해요.")]
    [InlineData("쓰다", "이 가방은 싸요.")]
    [InlineData("알다", "그건 아니에요.")]
    [InlineData("살다", "산에 갔어요.")]
    [InlineData("밀다", "미세먼지가 많아요.")]
    [InlineData("넣다", "내 친구예요.")]
    [InlineData("낫다", "나는 학생이에요.")]
    public void AnotherWordWithTheSameInitialConsonant_IsNotThePredicate(string dictionaryForm, string sentence)
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
