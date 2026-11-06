namespace SentenceStudio.Services;

/// <summary>
/// Finds a Korean verb or adjective, given in dictionary form (먹다, 공부하다, 가고 싶다), in a
/// sentence where it is conjugated (먹었어요, 공부했어요, 가고 싶어요).
/// </summary>
/// <remarks>
/// <para>
/// The stem (the form without 다) must start a word of the sentence. All its syllables but the
/// last must match exactly; the last may merge with the ending, so it only has to share its
/// initial consonant (가/갔, 보/봐, 오/와, 시/셔, 하/해/합, 쓰/써, 되/돼, and the ㄷ, ㅂ, ㅅ, ㅎ
/// irregulars 듣/들, 덥/더, 짓/지, 그렇/그래). 르 verbs double their ㄹ (모르/몰라, 부르/불러).
/// What follows must begin a verb or adjective ending, or the stem syllable itself carries the
/// ending (봐, 갈): a word that only begins like the stem (고양이 for 가다, 가방, 오늘) is not
/// the verb. Words of a phrase before its verb (밥 먹다, 가고 싶다) must begin the sentence's
/// words just before it, particles allowed (밥을 먹었어요).
/// </para>
/// <para>
/// Not found: honorific replacement verbs (드시다 for 먹다, 주무시다 for 자다, 계시다 for 있다)
/// and a stem written without a space after the word before it (잘먹었어요).
/// </para>
/// </remarks>
public static class KoreanPredicateMatcher
{
    private const char FirstSyllable = '가';
    private const char LastSyllable = '힣';
    private const int SyllablesPerInitial = 21 * 28;
    private const int FinalRieul = 8;

    /// <summary>First syllables of verb and adjective endings (and of the 르 doubling's second half).</summary>
    private static readonly HashSet<char> EndingStarts =
    [
        '어', '아', '여', '요', '서', '도', '야', '었', '았', '였', '고', '는', '은', '을', '게', '지',
        '면', '며', '니', '세', '시', '셨', '셔', '십', '습', '다', '기', '자', '네', '죠', '나', '냐',
        '라', '러', '워', '와', '려', '겠', '거', '던', '든', '으', '음', '군', '구', '잖', '래', '대',
        '더', '데', '까'
    ];

    /// <summary>True when <paramref name="term"/> ends in a Korean dictionary-form predicate (…다).</summary>
    public static bool IsDictionaryForm(string? term) =>
        DictionaryStem(term) is not null;

    /// <summary>True when <paramref name="sentence"/> contains <paramref name="dictionaryForm"/>, conjugated or not.</summary>
    public static bool Contains(string? sentence, string? dictionaryForm)
    {
        if (string.IsNullOrWhiteSpace(sentence) || DictionaryStem(dictionaryForm) is not { } parts)
            return false;
        var (leading, stem) = parts;
        var words = Words(sentence);
        for (var i = leading.Length; i < words.Count; i++)
        {
            if (!MatchesStem(words[i], stem))
                continue;
            var start = i - leading.Length;
            var phrase = true;
            for (var j = 0; j < leading.Length && phrase; j++)
                phrase = words[start + j].StartsWith(leading[j], StringComparison.Ordinal);
            if (phrase)
                return true;
        }
        return false;
    }

    private static (string[] Leading, string Stem)? DictionaryStem(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return null;
        var parts = term.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var last = parts[^1];
        if (last.Length < 2 || last[^1] != '다' || !last.All(IsSyllable))
            return null;
        return (parts[..^1], last[..^1]);
    }

    private static bool MatchesStem(string word, string stem)
    {
        var n = stem.Length;
        // 르 irregular: 모르 + 아요 = 몰라요 (the syllable before 르 takes ㄹ, and 르 becomes 라/러).
        if (n >= 2 && stem[^1] == '르' && word.Length >= n
            && word.AsSpan(0, n - 2).SequenceEqual(stem.AsSpan(0, n - 2))
            && WithFinal(stem[n - 2], FinalRieul) is { } doubled && word[n - 2] == doubled
            && IsSyllable(word[n - 1]) && Initial(word[n - 1]) == Initial('르')
            && EndsAsPredicate(word, n, merged: true))
            return true;
        if (word.Length < n || !word.AsSpan(0, n - 1).SequenceEqual(stem.AsSpan(0, n - 1)))
            return false;
        var aligned = word[n - 1];
        if (aligned == stem[n - 1])
            return EndsAsPredicate(word, n, merged: false);
        return IsSyllable(aligned) && Initial(aligned) == Initial(stem[n - 1])
            && EndsAsPredicate(word, n, merged: true);
    }

    /// <summary>
    /// What follows the stem begins an ending; with nothing after it, the stem syllable must have
    /// taken the ending into itself (봐, 갈), because a bare stem is not a conjugated form.
    /// </summary>
    private static bool EndsAsPredicate(string word, int stemLength, bool merged) =>
        word.Length == stemLength ? merged : EndingStarts.Contains(word[stemLength]);

    private static bool IsSyllable(char c) => c is >= FirstSyllable and <= LastSyllable;

    private static int Initial(char syllable) => (syllable - FirstSyllable) / SyllablesPerInitial;

    /// <summary>The syllable with <paramref name="final"/> added; null when it already has a final.</summary>
    private static char? WithFinal(char syllable, int final) =>
        IsSyllable(syllable) && (syllable - FirstSyllable) % 28 == 0
            ? (char)(syllable + final)
            : null;

    private static List<string> Words(string sentence)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= sentence.Length; i++)
        {
            var letter = i < sentence.Length && char.IsLetterOrDigit(sentence[i]);
            if (letter && start < 0)
                start = i;
            else if (!letter && start >= 0)
            {
                words.Add(sentence[start..i]);
                start = -1;
            }
        }
        return words;
    }
}
