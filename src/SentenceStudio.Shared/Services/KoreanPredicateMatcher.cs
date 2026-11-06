namespace SentenceStudio.Services;

/// <summary>
/// Finds a Korean verb or adjective, given in dictionary form (먹다, 공부하다, 가고 싶다), in a
/// sentence where it is conjugated (먹었어요, 공부했어요, 가고 싶어요).
/// </summary>
/// <remarks>
/// <para>
/// The stem (the form without 다) must start a word of the sentence. All its syllables but the
/// last must match exactly. The last matches exactly too, unless conjugation changes it:
/// </para>
/// <list type="bullet">
/// <item>a stem ending in a vowel keeps its initial consonant and takes the ending's ㄴ, ㄹ, ㅂ or
/// past ㅆ as its final (간, 갈게요, 갑니다, 갔어요; 켰어요), or merges 아/어 into its vowel
/// (봐, 와, 줘, 마셔, 써, 바빠, 돼, and 하 to 해/했);</item>
/// <item>the irregular finals: ㄷ becomes ㄹ before a vowel ending (들어요), ㅂ and ㅅ drop before one
/// (더워요, 도와요, 나아요), ㄹ drops before ㄴ, ㅂ and ㅅ (사는, 삽니다, 사세요), and the ㅎ of a
/// 그렇다-type adjective drops (그래요, 그러면, 그런, 하얘요);</item>
/// <item>르 verbs double their ㄹ (모르/몰라, 부르/불러).</item>
/// </list>
/// <para>
/// A regular consonant stem (먹, 있, 높, 읽, 앉) matches only exactly, so 마셨어요 is not 먹다 and
/// 아니에요 is not 있다. What follows must begin a verb or adjective ending, or the changed syllable
/// itself carries the ending (봐, 갈): a word that only begins like the stem (고양이 for 가다, 가방,
/// 오늘) is not the verb. Words of a phrase before its verb (밥 먹다, 가고 싶다) must begin the
/// sentence's words just before it, particles allowed (밥을 먹었어요).
/// </para>
/// <para>
/// Not found: honorific replacement verbs (드시다 for 먹다, 주무시다 for 자다, 계시다 for 있다)
/// and a stem written without a space after the word before it (잘먹었어요). Still found: a word
/// spelled like a conjugated form (발을 for 받다, as 들을 for 듣다; 잘 for 자다; 안 for 알다; 한 for
/// 하다) and a word that is the exact stem followed by an ending's first syllable (보고서 for 보다,
/// 가게 and 가지 for 가다).
/// </para>
/// </remarks>
public static class KoreanPredicateMatcher
{
    private const char FirstSyllable = '가';
    private const char LastSyllable = '힣';
    private const int Vowels = 21;
    private const int Finals = 28;

    // Vowel (medial) and final indexes in Unicode Hangul syllable order.
    private const int VowelA = 0, VowelAe = 1, VowelYa = 2, VowelYae = 3, VowelEo = 4, VowelE = 5,
        VowelYeo = 6, VowelO = 8, VowelWa = 9, VowelWae = 10, VowelOe = 11, VowelU = 13, VowelWo = 14,
        VowelEu = 18, VowelI = 20;
    private const int NoFinal = 0, FinalNieun = 4, FinalDigeut = 7, FinalRieul = 8, FinalMieum = 16,
        FinalBieup = 17, FinalSiot = 19, FinalSsangSiot = 20, FinalHieut = 27;

    /// <summary>First syllables of verb and adjective endings (and of the 르 doubling's second half).</summary>
    private static readonly HashSet<char> EndingStarts =
    [
        '어', '아', '여', '요', '서', '도', '야', '었', '았', '였', '고', '는', '은', '을', '게', '지',
        '면', '며', '니', '세', '시', '셨', '셔', '십', '습', '다', '기', '자', '네', '죠', '나', '냐',
        '라', '러', '워', '와', '려', '겠', '거', '던', '든', '으', '음', '군', '구', '잖', '래', '대',
        '더', '데', '까'
    ];

    /// <summary>Vowel endings after a ㄷ or ㅅ irregular stem (들어요, 들었어요, 들으면, 들은, 들을; 나아요).</summary>
    private static readonly string[] VowelEndings = ["아", "았", "어", "었", "으", "은", "을"];

    /// <summary>What an ㅂ irregular stem takes once its ㅂ drops (더워요, 더웠어요, 더우면, 더운, 도와요).</summary>
    private static readonly string[] BieupEndings = ["우", "운", "울", "워", "웠", "와", "왔"];

    /// <summary>After a vowel stem's ㅂ final: ㅂ니다, ㅂ니까, ㅂ시다 (갑니다, 갑시다; not 갑자기).</summary>
    private static readonly string[] BieupFinalNext = ["니", "시"];

    /// <summary>After a modifier or plain ㄴ final (본다, 간대요, 큰데, 큰지, 큰가요; not 간을).</summary>
    private static readonly string[] NieunFinalNext = ["다", "대", "데", "지", "가"];

    /// <summary>After a future ㄹ final (갈게요, 할까요, 갈래요, 갈거예요, 갈지; not 할아버지).</summary>
    private static readonly string[] RieulFinalNext = ["게", "께", "까", "래", "거", "걸", "지", "수"];

    /// <summary>
    /// After an ㄹ stem whose ㄹ dropped (사는, 사니까, 사나요, 사네요, 사세요, 사셨어요, 사셔서,
    /// 사십니까; not 아니에요, 미세먼지, 사십 for forty). Honorific 시 is checked on its own.
    /// </summary>
    private static readonly string[] RieulDroppedNext = ["는", "니까", "나요", "네요", "세요", "셨", "셔", "십니", "십시"];

    private readonly record struct Syllable(int Initial, int Vowel, int Final);

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
            && EndsOrBeginsEnding(word.AsSpan(n)))
            return true;
        if (word.Length < n || !word.AsSpan(0, n - 1).SequenceEqual(stem.AsSpan(0, n - 1)))
            return false;
        var rest = word.AsSpan(n);
        // A bare stem is not a conjugated form, so an exact last syllable needs an ending after it.
        if (word[n - 1] == stem[n - 1])
            return BeginsEnding(rest);
        return Decompose(word[n - 1]) is { } changed && Decompose(stem[n - 1]) is { } last
            && changed.Initial == last.Initial
            && (last.Final == NoFinal
                ? VowelStemChanged(stem, last, changed, rest)
                : ConsonantStemChanged(stem, last, changed, rest));
    }

    /// <summary>
    /// A stem ending in a vowel (가, 보, 마시, 쓰, 되, 하): the ending's ㄴ, ㄹ, ㅂ or past ㅆ becomes
    /// its final (간, 갈, 갑니다, 갔어요), or 아/어 merges into its vowel (봐, 와, 줘, 셔, 써, 돼, 해).
    /// </summary>
    private static bool VowelStemChanged(string stem, Syllable last, Syllable changed, ReadOnlySpan<char> rest)
    {
        var contracted = ContractedVowel(stem, last);
        if (changed.Vowel == last.Vowel)
            return changed.Final switch
            {
                // 갔, 섰, 보냈, 켰 (아/어 absorbed), and 됬, the common spelling of 됐.
                FinalSsangSiot => (contracted is not null || last.Vowel is VowelA or VowelEo or VowelAe or VowelE or VowelYeo)
                    && BeginsEnding(rest),
                FinalBieup => StartsWithAny(rest, BieupFinalNext),
                FinalNieun => rest.IsEmpty || StartsWithAny(rest, NieunFinalNext),
                FinalRieul => rest.IsEmpty || StartsWithAny(rest, RieulFinalNext),
                _ => false,
            };
        return changed.Vowel == contracted && MergedEnding(changed, rest);
    }

    /// <summary>The vowel a vowel stem's last syllable takes when 아/어 merges into it; null when none.</summary>
    private static int? ContractedVowel(string stem, Syllable last)
    {
        if (stem[^1] == '하')
            return VowelAe;
        return last.Vowel switch
        {
            VowelO => VowelWa,
            VowelU => VowelWo,
            VowelI => VowelYeo,
            VowelOe => VowelWae,
            // ㅡ drops: 아 after a syllable with ㅏ or ㅗ (바빠, 아파), otherwise 어 (써, 커, 예뻐).
            VowelEu => stem.Length >= 2 && Decompose(stem[^2]) is { Vowel: VowelA or VowelO } ? VowelA : VowelEo,
            _ => null,
        };
    }

    /// <summary>
    /// A stem ending in a consonant changes only in the irregular classes; a regular stem (먹, 있,
    /// 높, 읽, 앉) matches only exactly.
    /// </summary>
    private static bool ConsonantStemChanged(string stem, Syllable last, Syllable changed, ReadOnlySpan<char> rest)
    {
        // ㅎ irregulars are 그렇다-type adjectives of two or more syllables with ㅏ, ㅓ or ㅑ
        // (그렇, 어떻, 빨갛, 하얗); one-syllable 좋, 넣, 놓, 낳 are regular.
        if (last.Final == FinalHieut)
            return stem.Length >= 2 && last.Vowel is VowelA or VowelEo or VowelYa && HieutDropped(last, changed, rest);
        if (changed.Vowel != last.Vowel)
            return false;
        return last.Final switch
        {
            FinalDigeut => changed.Final == FinalRieul && StartsWithAny(rest, VowelEndings),
            FinalBieup => changed.Final == NoFinal && StartsWithAny(rest, BieupEndings),
            FinalSiot => changed.Final == NoFinal && StartsWithAny(rest, VowelEndings),
            FinalRieul => RieulDropped(changed, rest),
            _ => false,
        };
    }

    /// <summary>그래요, 그랬어요, 하얘요; 그러면, 그러니까; 그런, 그럴까요, 그럼.</summary>
    private static bool HieutDropped(Syllable last, Syllable changed, ReadOnlySpan<char> rest)
    {
        if (changed.Vowel == (last.Vowel == VowelYa ? VowelYae : VowelAe))
            return MergedEnding(changed, rest);
        if (changed.Vowel != last.Vowel)
            return false;
        return changed.Final switch
        {
            NoFinal => StartsWithAny(rest, ["면", "니", "네", "며"]),
            FinalNieun or FinalRieul or FinalMieum => EndsOrBeginsEnding(rest),
            _ => false,
        };
    }

    /// <summary>사는, 사세요, 아시면; 삽니다, 만듭시다; 산다, 긴데, 만든.</summary>
    private static bool RieulDropped(Syllable changed, ReadOnlySpan<char> rest) => changed.Final switch
    {
        NoFinal => StartsWithAny(rest, RieulDroppedNext)
            // Honorific 시 before a consonant ending (사시는, 아시다시피); 시아 and 시어 are not (아시아).
            || (rest.Length >= 2 && rest[0] == '시' && rest[1] is not ('아' or '어') && EndingStarts.Contains(rest[1])),
        FinalBieup => StartsWithAny(rest, BieupFinalNext),
        FinalNieun => rest.IsEmpty || StartsWithAny(rest, NieunFinalNext),
        _ => false,
    };

    /// <summary>A syllable 아/어 merged into: alone or before an ending (봐, 봐요); with past ㅆ, before one (봤어요).</summary>
    private static bool MergedEnding(Syllable changed, ReadOnlySpan<char> rest) => changed.Final switch
    {
        NoFinal => EndsOrBeginsEnding(rest),
        FinalSsangSiot => BeginsEnding(rest),
        _ => false,
    };

    private static bool BeginsEnding(ReadOnlySpan<char> rest) => !rest.IsEmpty && EndingStarts.Contains(rest[0]);

    private static bool EndsOrBeginsEnding(ReadOnlySpan<char> rest) => rest.IsEmpty || EndingStarts.Contains(rest[0]);

    private static bool StartsWithAny(ReadOnlySpan<char> rest, ReadOnlySpan<string> prefixes)
    {
        foreach (var prefix in prefixes)
            if (rest.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static Syllable? Decompose(char c)
    {
        if (!IsSyllable(c))
            return null;
        var index = c - FirstSyllable;
        return new Syllable(index / (Vowels * Finals), index / Finals % Vowels, index % Finals);
    }

    private static bool IsSyllable(char c) => c is >= FirstSyllable and <= LastSyllable;

    private static int Initial(char syllable) => (syllable - FirstSyllable) / (Vowels * Finals);

    /// <summary>The syllable with <paramref name="final"/> added; null when it already has a final.</summary>
    private static char? WithFinal(char syllable, int final) =>
        IsSyllable(syllable) && (syllable - FirstSyllable) % Finals == 0
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
