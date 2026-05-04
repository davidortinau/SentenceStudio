using SentenceStudio.Services.Numbers;
using SentenceStudio.Shared.Models.Numbers;
using Xunit;

namespace SentenceStudio.AppLib.Tests.Services.Numbers;

public class KoreanNumberAnswerGraderTests
{
    private readonly KoreanNumberAnswerGrader _grader = new();
    private readonly KoreanNumberItemGenerator _generator = new();

    #region Exact Match Tests

    [Fact]
    public void Grade_ExactMatch_ReturnsCorrect()
    {
        var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: 42));
        var result = _grader.Grade(item, item.CanonicalAnswer, latencyMs: 1000);

        Assert.True(result.IsCorrect);
        Assert.Null(result.ErrorClass);
        Assert.Null(result.Tip);
    }

    [Fact]
    public void Grade_AcceptableAlternate_ReturnsCorrect()
    {
        var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: 42));
        
        // Try first acceptable alternate (no space version)
        if (item.AcceptableAlternates.Count > 0)
        {
            var result = _grader.Grade(item, item.AcceptableAlternates[0], latencyMs: 1000);
            Assert.True(result.IsCorrect);
        }
    }

    [Fact]
    public void Grade_WithExtraWhitespace_NormalizesAndAccepts()
    {
        var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: 42));
        var result = _grader.Grade(item, $"  {item.CanonicalAnswer}  ", latencyMs: 1000);

        Assert.True(result.IsCorrect);
    }

    #endregion

    #region Sound Change Error Tests

    [Fact]
    public void Grade_SoundChangeMissed_둘Instead두()
    {
        // Create an item that should have "두 명"
        for (int seed = 0; seed < 1000; seed++)
        {
            var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "명", RandomSeed: seed));
            if (item.CanonicalAnswer == "두 명")
            {
                var result = _grader.Grade(item, "둘 명", latencyMs: 1000);

                Assert.False(result.IsCorrect);
                Assert.Equal("SoundChangeMissed", result.ErrorClass);
                Assert.Contains("둘 → 두", result.Tip);
                return;
            }
        }
        
        Assert.Fail("Could not find item with '두 명'");
    }

    [Fact]
    public void Grade_SoundChangeMissed_하나Instead한()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: seed));
            if (item.CanonicalAnswer == "한 개")
            {
                var result = _grader.Grade(item, "하나 개", latencyMs: 1000);

                Assert.False(result.IsCorrect);
                Assert.Equal("SoundChangeMissed", result.ErrorClass);
                Assert.Contains("하나 → 한", result.Tip);
                return;
            }
        }
        
        Assert.Fail("Could not find item with '한 개'");
    }

    [Fact]
    public void Grade_SoundChangeMissed_스물Instead스무For20()
    {
        for (int seed = 0; seed < 2000; seed++)
        {
            var item = _generator.GenerateItem(new NumberItemRequest("Age", "ReadAndProduce", RandomSeed: seed));
            if (item.DigitValue == 20)
            {
                var result = _grader.Grade(item, "스물 살", latencyMs: 1000);

                Assert.False(result.IsCorrect);
                Assert.Equal("SoundChangeMissed", result.ErrorClass);
                Assert.Contains("스무", result.Tip);
                return;
            }
        }
        
        Assert.Fail("Could not find age 20 item");
    }

    #endregion

    #region Sino/Native Swap Tests

    [Fact]
    public void Grade_SinoNativeSwap_SinoUsedWithNativeCounter()
    {
        var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: 42));
        // User incorrectly uses Sino numbers
        var result = _grader.Grade(item, "이 개", latencyMs: 1000);

        Assert.False(result.IsCorrect);
        Assert.Equal("SinoNativeSwap", result.ErrorClass);
        Assert.Contains("Native", result.Tip);
        Assert.Contains("Sino", result.Tip);
    }

    #endregion

    #region Counter Mismatch Tests

    [Fact]
    public void Grade_CounterMismatch_WrongCounter()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: seed));
            if (item.CounterText == "개")
            {
                // Extract the native number part and use wrong counter
                var parts = item.CanonicalAnswer.Split(' ');
                var wrongAnswer = $"{parts[0]} 명"; // Use 명 instead of 개
                
                var result = _grader.Grade(item, wrongAnswer, latencyMs: 1000);

                Assert.False(result.IsCorrect);
                Assert.Equal("CounterMismatch", result.ErrorClass);
                Assert.Contains("개", result.Tip);
                return;
            }
        }
    }

    #endregion

    #region Wrong Format Tests

    [Fact]
    public void Grade_WrongFormat_HangulWhenDigitsExpected()
    {
        var item = _generator.GenerateItem(new NumberItemRequest("Time", "ListenAndType", RandomSeed: 42));
        var result = _grader.Grade(item, "세 시", latencyMs: 1000);

        Assert.False(result.IsCorrect);
        Assert.Equal("WrongFormat", result.ErrorClass);
        Assert.Contains("digits", result.Tip);
    }

    [Fact]
    public void Grade_WrongFormat_DigitsWhenHangulExpected()
    {
        var item = _generator.GenerateItem(new NumberItemRequest("Age", "ReadAndProduce", RandomSeed: 42));
        var result = _grader.Grade(item, "27", latencyMs: 1000);

        Assert.False(result.IsCorrect);
        Assert.Equal("WrongFormat", result.ErrorClass);
        Assert.Contains("Hangul", result.Tip);
    }

    #endregion

    #region Typo Tests

    [Fact]
    public void Grade_SingleCharacterTypo_DetectsAsTypo()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: seed));
            if (item.CanonicalAnswer == "한 개")
            {
                // Single character error
                var result = _grader.Grade(item, "한 게", latencyMs: 1000);

                Assert.False(result.IsCorrect);
                Assert.Equal("Typo", result.ErrorClass);
                Assert.Contains("spelling", result.Tip);
                return;
            }
        }
    }

    #endregion

    #region Normalization Tests

    [Fact]
    public void Grade_FullWidthDigits_NormalizesToHalfWidth()
    {
        var item = _generator.GenerateItem(new NumberItemRequest("Time", "ListenAndType", RandomSeed: 42));
        var expected = item.AcceptableAlternates.FirstOrDefault(a => a.Contains(":"));
        
        if (expected != null)
        {
            // Convert to full-width
            var fullWidth = expected.Replace("0", "０").Replace("1", "１").Replace("2", "２")
                                   .Replace("3", "３").Replace("4", "４").Replace("5", "５");
            
            var result = _grader.Grade(item, fullWidth, latencyMs: 1000);

            // Should accept full-width digits after normalization
            Assert.True(result.IsCorrect || result.ErrorClass != "WrongFormat");
        }
    }

    #endregion

    #region Unknown Error Tests

    [Fact]
    public void Grade_CompletelyWrongAnswer_ReturnsUnknownError()
    {
        var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: 42));
        var result = _grader.Grade(item, "완전히 틀린 답", latencyMs: 1000);

        Assert.False(result.IsCorrect);
        Assert.Equal("Unknown", result.ErrorClass);
        Assert.Contains(item.CanonicalAnswer, result.Tip);
    }

    #endregion

    #region Time-Specific Tests

    [Fact]
    public void Grade_TimeFormat_AcceptsColonFormat()
    {
        var item = _generator.GenerateItem(new NumberItemRequest("Time", "ListenAndType", RandomSeed: 42));
        var hour = (int)(item.DigitValue / 100);
        var minute = (int)(item.DigitValue % 100);
        
        var result = _grader.Grade(item, $"{hour}:{minute:D2}", latencyMs: 1000);

        Assert.True(result.IsCorrect);
    }

    #endregion
}
