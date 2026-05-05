using SentenceStudio.Services.Numbers;
using SentenceStudio.Shared.Models.Numbers;
using Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SentenceStudio.AppLib.Tests.Services.Numbers;

public class KoreanNumberItemGeneratorTests
{
    private readonly KoreanNumberItemGenerator _generator = new(NullLogger<KoreanNumberItemGenerator>.Instance);

    [Fact]
    public void LanguageCode_IsKorean()
    {
        Assert.Equal("ko", _generator.LanguageCode);
    }

    #region Counting Context Tests

    [Fact]
    public void GenerateCountingItem_WithFixedSeed_ProducesDeterministicResult()
    {
        var request = new NumberItemRequest("Counting", "ReadAndProduce", RandomSeed: 42);
        var item1 = _generator.GenerateItem(request);
        var item2 = _generator.GenerateItem(request);

        Assert.Equal(item1.DigitValue, item2.DigitValue);
        Assert.Equal(item1.CanonicalAnswer, item2.CanonicalAnswer);
        Assert.Equal(item1.CounterId, item2.CounterId);
    }

    [Fact]
    public void GenerateCountingItem_AppliesSoundChange_하나To한BeforeCounter()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: seed);
            var item = _generator.GenerateItem(request);
            
            if (item.DigitValue == 1)
            {
                Assert.Equal("한 개", item.CanonicalAnswer);
                Assert.Equal(NumberSystem.Native, item.System);
                return;
            }
        }
        
        Assert.Fail("Could not find seed that generates value 1");
    }

    [Fact]
    public void GenerateCountingItem_AppliesSoundChange_둘To두BeforeCounter()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "명", RandomSeed: seed);
            var item = _generator.GenerateItem(request);
            
            if (item.DigitValue == 2)
            {
                Assert.Equal("두 명", item.CanonicalAnswer);
                return;
            }
        }
        
        Assert.Fail("Could not find seed that generates value 2");
    }

    [Fact]
    public void GenerateCountingItem_AppliesSoundChange_셋To세BeforeCounter()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "잔", RandomSeed: seed);
            var item = _generator.GenerateItem(request);
            
            if (item.DigitValue == 3)
            {
                Assert.Equal("세 잔", item.CanonicalAnswer);
                return;
            }
        }
        
        Assert.Fail("Could not find seed that generates value 3");
    }

    [Fact]
    public void GenerateCountingItem_AppliesSoundChange_넷To네BeforeCounter()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "마리", RandomSeed: seed);
            var item = _generator.GenerateItem(request);
            
            if (item.DigitValue == 4)
            {
                Assert.Equal("네 마리", item.CanonicalAnswer);
                return;
            }
        }
        
        Assert.Fail("Could not find seed that generates value 4");
    }

    [Fact]
    public void GenerateCountingItem_NoSoundChangeFor다섯()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "권", RandomSeed: seed);
            var item = _generator.GenerateItem(request);
            
            if (item.DigitValue == 5)
            {
                Assert.Equal("다섯 권", item.CanonicalAnswer);
                return;
            }
        }
        
        Assert.Fail("Could not find seed that generates value 5");
    }

    [Fact]
    public void GenerateCountingItem_Phase1Range_IsOneToNinetyNine()
    {
        for (int i = 0; i < 100; i++)
        {
            var request = new NumberItemRequest("Counting", "ReadAndProduce", RandomSeed: i);
            var item = _generator.GenerateItem(request);
            
            Assert.InRange(item.DigitValue, 1, 99);
        }
    }

    [Fact]
    public void GenerateCountingItem_IncludesHints()
    {
        var request = new NumberItemRequest("Counting", "ReadAndProduce", RandomSeed: 42);
        var item = _generator.GenerateItem(request);

        Assert.NotEmpty(item.Hints);
        Assert.Contains(item.Hints, h => h.Contains("Native"));
    }

    #endregion

    #region Time Context Tests

    [Fact]
    public void GenerateTimeItem_HoursAreNative()
    {
        var request = new NumberItemRequest("Time", "ReadAndProduce", RandomSeed: 42);
        var item = _generator.GenerateItem(request);

        Assert.Equal(NumberSystem.Mixed, item.System);
        Assert.Contains("시", item.CanonicalAnswer);
    }

    [Fact]
    public void GenerateTimeItem_MinutesAreSino()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var request = new NumberItemRequest("Time", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);
            
            var minute = (int)(item.DigitValue % 100);
            if (minute > 0)
            {
                Assert.Contains("분", item.CanonicalAnswer);
                if (minute == 15) Assert.Contains("십오 분", item.CanonicalAnswer);
                if (minute == 30) Assert.Contains("삼십 분", item.CanonicalAnswer);
                if (minute == 45) Assert.Contains("사십오 분", item.CanonicalAnswer);
                return;
            }
        }
    }

    [Fact]
    public void GenerateTimeItem_TwelveOClock_Uses열둘()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Time", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);
            
            var hour = (int)(item.DigitValue / 100);
            if (hour == 12)
            {
                // 12 = 열둘 (no sound change when compounded with 열)
                Assert.StartsWith("열둘 시", item.CanonicalAnswer);
                return;
            }
        }
        
        Assert.Fail("Could not find seed that generates 12 o'clock");
    }

    [Fact]
    public void GenerateTimeItem_Phase1Minutes_AreZeroOrQuarterHours()
    {
        for (int i = 0; i < 100; i++)
        {
            var request = new NumberItemRequest("Time", "ReadAndProduce", RandomSeed: i);
            var item = _generator.GenerateItem(request);
            
            var minute = (int)(item.DigitValue % 100);
            Assert.Contains(minute, new[] { 0, 15, 30, 45 });
        }
    }

    #endregion

    #region Age Context Tests

    [Fact]
    public void GenerateAgeItem_UsesNativeWithSound()
    {
        var request = new NumberItemRequest("Age", "ReadAndProduce", RandomSeed: 42);
        var item = _generator.GenerateItem(request);

        Assert.Equal(NumberSystem.Native, item.System);
        Assert.Contains("살", item.CanonicalAnswer);
        Assert.Equal("살", item.CounterId);
    }

    [Fact]
    public void GenerateAgeItem_TwentyYearsOld_Uses스무()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Age", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);
            
            if (item.DigitValue == 20)
            {
                Assert.Equal("스무 살", item.CanonicalAnswer);
                return;
            }
        }
        
        Assert.Fail("Could not find seed that generates age 20");
    }

    [Fact]
    public void GenerateAgeItem_TwentyOneYearsOld_Uses스물하나()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Age", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);
            
            if (item.DigitValue == 21)
            {
                Assert.Equal("스물하나 살", item.CanonicalAnswer);
                return;
            }
        }
        
        Assert.Fail("Could not find seed that generates age 21");
    }

    [Fact]
    public void GenerateAgeItem_TwentySeven_Uses스물일곱()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Age", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);
            
            if (item.DigitValue == 27)
            {
                Assert.Equal("스물일곱 살", item.CanonicalAnswer);
                return;
            }
        }
        
        Assert.Fail("Could not find seed that generates age 27");
    }

    #endregion

    #region Bucket Tests

    [Fact]
    public void Bucket_OneToTen()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var request = new NumberItemRequest("Counting", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);
            
            if (item.DigitValue >= 1 && item.DigitValue <= 10)
            {
                Assert.Equal("1-10", item.Bucket);
            }
        }
    }

    [Fact]
    public void Bucket_ElevenToNinetyNine()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var request = new NumberItemRequest("Counting", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);
            
            if (item.DigitValue >= 11 && item.DigitValue <= 99)
            {
                Assert.Equal("11-99", item.Bucket);
            }
        }
    }

    #endregion

    #region Money Context Tests

    [Fact]
    public void GenerateMoneyItem_UsesSinoSystem()
    {
        var request = new NumberItemRequest("Money", "ReadAndProduce", RandomSeed: 42);
        var item = _generator.GenerateItem(request);

        Assert.Equal(NumberSystem.Sino, item.System);
        Assert.Contains("원", item.CanonicalAnswer);
        Assert.Equal("원", item.CounterId);
    }

    [Fact]
    public void GenerateMoneyItem_SmallPrice_ThousandWon()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Money", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);

            if (item.DigitValue == 1000)
            {
                Assert.Equal("천 원", item.CanonicalAnswer);
                return;
            }
        }

        Assert.Fail("Could not find seed that generates 1000 won");
    }

    [Fact]
    public void GenerateMoneyItem_MediumPrice_TenThousandWon()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Money", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);

            if (item.DigitValue == 10000)
            {
                Assert.Equal("만 원", item.CanonicalAnswer);
                return;
            }
        }

        Assert.Fail("Could not find seed that generates 10000 won");
    }

    [Fact]
    public void GenerateMoneyItem_LargePrice_HundredThousandWon()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Money", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);

            if (item.DigitValue == 100000)
            {
                Assert.Equal("십만 원", item.CanonicalAnswer);
                return;
            }
        }

        Assert.Fail("Could not find seed that generates 100000 won");
    }

    [Fact]
    public void GenerateMoneyItem_ManBoundary_HasPlaceValueErrorHint()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var request = new NumberItemRequest("Money", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);

            if (item.DigitValue >= 10000 && item.DigitValue < 100000000)
            {
                Assert.NotNull(item.ErrorClassHints);
                Assert.True(item.ErrorClassHints.ContainsKey("likely_error"));
                Assert.Equal("place_value_grouping_4digit_vs_3digit", item.ErrorClassHints["likely_error"]);
                return;
            }
        }
    }

    #endregion

    #region Date Context Tests

    [Fact]
    public void GenerateDateItem_UsesSinoSystem()
    {
        var request = new NumberItemRequest("Date", "ReadAndProduce", RandomSeed: 42);
        var item = _generator.GenerateItem(request);

        Assert.Equal(NumberSystem.Sino, item.System);
        Assert.Contains("월", item.CanonicalAnswer);
        Assert.Contains("일", item.CanonicalAnswer);
    }

    [Fact]
    public void GenerateDateItem_RegularMonth_January()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Date", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);

            var month = (int)(item.DigitValue / 100);
            if (month == 1)
            {
                Assert.Contains("일월", item.CanonicalAnswer);
                return;
            }
        }

        Assert.Fail("Could not find seed that generates January date");
    }

    [Fact]
    public void GenerateDateItem_IrregularMonth_June()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Date", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);

            var month = (int)(item.DigitValue / 100);
            if (month == 6)
            {
                Assert.Contains("유월", item.CanonicalAnswer);
                Assert.DoesNotContain("육월", item.CanonicalAnswer);
                Assert.NotNull(item.ErrorClassHints);
                Assert.Equal("wrong_form_for_month_6_or_10", item.ErrorClassHints["likely_error"]);
                return;
            }
        }

        Assert.Fail("Could not find seed that generates June date");
    }

    [Fact]
    public void GenerateDateItem_IrregularMonth_October()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Date", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);

            var month = (int)(item.DigitValue / 100);
            if (month == 10)
            {
                Assert.Contains("시월", item.CanonicalAnswer);
                Assert.DoesNotContain("십월", item.CanonicalAnswer);
                Assert.NotNull(item.ErrorClassHints);
                Assert.Equal("wrong_form_for_month_6_or_10", item.ErrorClassHints["likely_error"]);
                return;
            }
        }

        Assert.Fail("Could not find seed that generates October date");
    }

    [Fact]
    public void GenerateDateItem_ValidDates_NoFebruary30()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var request = new NumberItemRequest("Date", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);

            var month = (int)(item.DigitValue / 100);
            var day = (int)(item.DigitValue % 100);

            if (month == 2)
            {
                Assert.InRange(day, 1, 28);
            }
            else if (month == 4 || month == 6 || month == 9 || month == 11)
            {
                Assert.InRange(day, 1, 30);
            }
            else
            {
                Assert.InRange(day, 1, 31);
            }
        }
    }

    #endregion

    #region Ordinal Context Tests

    [Fact]
    public void GenerateOrdinalItem_UsesNativeSystem()
    {
        var request = new NumberItemRequest("Ordinal", "ReadAndProduce", RandomSeed: 42);
        var item = _generator.GenerateItem(request);

        Assert.Equal(NumberSystem.Native, item.System);
        Assert.True(item.CanonicalAnswer.Contains("째") || item.CanonicalAnswer.Contains("번째"));
    }

    [Fact]
    public void GenerateOrdinalItem_RankPattern_첫째()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Ordinal", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);

            if (item.DigitValue == 1 && item.Bucket == "rank")
            {
                Assert.Equal("첫째", item.CanonicalAnswer);
                Assert.DoesNotContain("하나째", item.CanonicalAnswer);
                Assert.Equal("째", item.CounterId);
                return;
            }
        }

        Assert.Fail("Could not find seed that generates 첫째 rank pattern");
    }

    [Fact]
    public void GenerateOrdinalItem_RankPattern_둘째()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Ordinal", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);

            if (item.DigitValue == 2 && item.Bucket == "rank")
            {
                Assert.Equal("둘째", item.CanonicalAnswer);
                Assert.Equal("째", item.CounterId);
                return;
            }
        }

        Assert.Fail("Could not find seed that generates 둘째 rank pattern");
    }

    [Fact]
    public void GenerateOrdinalItem_OccurrencePattern_첫번째()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Ordinal", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);

            if (item.DigitValue == 1 && item.Bucket == "occurrence")
            {
                Assert.Equal("첫 번째", item.CanonicalAnswer);
                Assert.Contains(" ", item.CanonicalAnswer); // Spaced
                Assert.Equal("번째", item.CounterId);
                return;
            }
        }

        Assert.Fail("Could not find seed that generates 첫 번째 occurrence pattern");
    }

    [Fact]
    public void GenerateOrdinalItem_OccurrencePattern_두번째()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var request = new NumberItemRequest("Ordinal", "ReadAndProduce", RandomSeed: seed);
            var item = _generator.GenerateItem(request);

            if (item.DigitValue == 2 && item.Bucket == "occurrence")
            {
                Assert.Equal("두 번째", item.CanonicalAnswer);
                Assert.Contains(" ", item.CanonicalAnswer); // Spaced
                Assert.Equal("번째", item.CounterId);
                return;
            }
        }

        Assert.Fail("Could not find seed that generates 두 번째 occurrence pattern");
    }

    [Fact]
    public void GenerateOrdinalItem_HasPatternConfusionErrorHint()
    {
        var request = new NumberItemRequest("Ordinal", "ReadAndProduce", RandomSeed: 42);
        var item = _generator.GenerateItem(request);

        Assert.NotNull(item.ErrorClassHints);
        Assert.Equal("rank_vs_occurrence_confusion", item.ErrorClassHints["likely_error"]);
        Assert.True(item.ErrorClassHints.ContainsKey("pattern"));
    }

    #endregion

    #region Disambiguate Sub-Mode Tests

    [Fact]
    public void GenerateDisambiguateItem_ProducesPairedPrompts()
    {
        var request = new NumberItemRequest("Any", "Disambiguate", RandomSeed: 42);
        var item = _generator.GenerateItem(request);

        Assert.Equal("Disambiguate", item.SubModeCode);
        Assert.NotNull(item.PromptA);
        Assert.NotNull(item.PromptB);
        Assert.NotNull(item.CorrectAnswerA);
        Assert.NotNull(item.CorrectAnswerB);
        Assert.NotNull(item.ChoicesA);
        Assert.NotNull(item.ChoicesB);
        Assert.True(item.ChoicesA.Count >= 3, "PromptA should have at least 3 choices");
        Assert.True(item.ChoicesB.Count >= 3, "PromptB should have at least 3 choices");
    }

    [Fact]
    public void GenerateDisambiguateItem_BothHalvesPopulated()
    {
        var request = new NumberItemRequest("Any", "Disambiguate", RandomSeed: 99);
        var item = _generator.GenerateItem(request);

        Assert.False(string.IsNullOrEmpty(item.PromptA), "PromptA should not be empty");
        Assert.False(string.IsNullOrEmpty(item.PromptB), "PromptB should not be empty");
        Assert.False(string.IsNullOrEmpty(item.CorrectAnswerA), "CorrectAnswerA should not be empty");
        Assert.False(string.IsNullOrEmpty(item.CorrectAnswerB), "CorrectAnswerB should not be empty");
        Assert.False(string.IsNullOrEmpty(item.HintA), "HintA should not be empty");
        Assert.False(string.IsNullOrEmpty(item.HintB), "HintB should not be empty");
    }

    [Fact]
    public void GenerateDisambiguateItem_HasPatternDisambiguationHint()
    {
        var request = new NumberItemRequest("Any", "Disambiguate", RandomSeed: 42);
        var item = _generator.GenerateItem(request);

        Assert.NotNull(item.ErrorClassHints);
        Assert.Equal("pattern_disambiguation", item.ErrorClassHints["pattern"]);
        Assert.Equal("system_confusion", item.ErrorClassHints["likely_error"]);
    }

    [Fact]
    public void GenerateDisambiguateItem_ChoicesContainCorrectAnswer()
    {
        var request = new NumberItemRequest("Any", "Disambiguate", RandomSeed: 123);
        var item = _generator.GenerateItem(request);

        Assert.Contains(item.CorrectAnswerA, item.ChoicesA);
        Assert.Contains(item.CorrectAnswerB, item.ChoicesB);
    }

    [Fact]
    public void GenerateDisambiguateItem_WithDifferentSeeds_ProducesDifferentPairs()
    {
        var item1 = _generator.GenerateItem(new NumberItemRequest("Any", "Disambiguate", RandomSeed: 1));
        var item2 = _generator.GenerateItem(new NumberItemRequest("Any", "Disambiguate", RandomSeed: 999));

        // With 8 pairs and different seeds, we should eventually get different pairs
        // (This may occasionally fail with same pair; run multiple times to verify randomness)
        bool differentPairs = item1.PromptA != item2.PromptA || item1.PromptB != item2.PromptB;
        bool differentDigits = item1.DigitValue != item2.DigitValue;
        
        // At least one should differ due to random selection
        Assert.True(differentPairs || differentDigits, "Different seeds should produce variety");
    }

    #endregion
}
