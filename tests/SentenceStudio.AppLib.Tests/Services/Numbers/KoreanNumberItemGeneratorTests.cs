using SentenceStudio.Services.Numbers;
using SentenceStudio.Shared.Models.Numbers;
using Xunit;

namespace SentenceStudio.AppLib.Tests.Services.Numbers;

public class KoreanNumberItemGeneratorTests
{
    private readonly KoreanNumberItemGenerator _generator = new();

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
}
