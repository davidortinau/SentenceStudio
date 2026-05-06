using SentenceStudio.Services.Numbers;
using SentenceStudio.Shared.Models.Numbers;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace SentenceStudio.AppLib.Tests.Services.Numbers;

/// <summary>
/// Tests for the "I was right" override flow in NumberDrill grader.
/// 
/// Pattern mirrors VocabQuiz: when user overrides an incorrect result,
/// the system must:
/// 1. Flip result → correct
/// 2. Count toward streak/score
/// 3. Emit telemetry event with grader-miss context
/// 
/// These tests will FAIL until Kaylee implements the override mechanism.
/// They document the required behavior so we can't ship without it.
/// </summary>
public class KoreanNumberAnswerGrader_OverrideFlowTests
{
    private readonly KoreanNumberAnswerGrader _grader = new();
    private readonly KoreanNumberItemGenerator _generator = new(NullLogger<KoreanNumberItemGenerator>.Instance);

    #region Override Mechanism Tests

    [Fact(Skip = "Override flow not yet implemented - waiting for Kaylee's VocabQuiz pattern integration")]
    public void Override_OnIncorrectResult_FlipsToCorrect()
    {
        // Arrange: Create an item and get an incorrect result
        var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: 42));
        var userInput = "completely wrong answer";
        var result = _grader.Grade(item, userInput, latencyMs: 1000);
        
        Assert.False(result.IsCorrect, "Precondition: result should be incorrect");
        
        // Act: Apply user override (mechanism TBD - may be service method or result property)
        // var overriddenResult = _grader.ApplyUserOverride(result);
        // OR
        // result.ApplyUserOverride();
        
        // Assert: Result is now marked correct
        // Assert.True(overriddenResult.IsCorrect, "Override should flip result to correct");
        
        // This test documents the requirement. Implementation details TBD by Kaylee.
    }

    [Fact(Skip = "Override flow not yet implemented - waiting for Kaylee")]
    public void Override_IncrementsStreakCounter()
    {
        // After override, the result should count toward user's streak/score
        // exactly as if they had gotten it right on first attempt.
        
        // This will require testing with NumberSessionService or whatever
        // tracks streak state. Test pattern:
        // 1. Start session
        // 2. Get 2 correct answers (streak = 2)
        // 3. Get 1 incorrect answer
        // 4. Override it
        // 5. Assert streak = 3
        
        Assert.Fail("Override flow not yet implemented");
    }

    #endregion

    #region Telemetry Event Tests

    [Fact(Skip = "Telemetry event not yet implemented - waiting for Kaylee")]
    public void Override_EmitsTelemetryEvent_WithGraderMissContext()
    {
        // Arrange
        var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: 42));
        var userInput = "wrong answer";
        var result = _grader.Grade(item, userInput, latencyMs: 1000);
        
        // Mock or capture telemetry sink
        // var telemetrySink = new MockTelemetrySink();
        
        // Act: Override
        // result.ApplyUserOverride(telemetrySink);
        
        // Assert: Event emitted with required fields
        // var evt = telemetrySink.Events.Single();
        // Assert.Equal("NumberDrill.GraderMiss", evt.Name);
        // Assert.Equal(item.CanonicalAnswer, evt.Properties["canonical_answer"]);
        // Assert.Equal(userInput, evt.Properties["user_input"]);
        // Assert.Equal(item.System.ToString(), evt.Properties["number_system"]);
        // Assert.Equal(item.CounterText, evt.Properties["counter"]);
        // Assert.Equal(item.DigitValue, evt.Properties["target_value"]);
        
        Assert.Fail("Telemetry event not yet implemented");
    }

    [Fact(Skip = "Telemetry event not yet implemented - waiting for Kaylee")]
    public void Override_TelemetryEvent_CapturesErrorClass()
    {
        // When override happens, telemetry should capture the original ErrorClass
        // so we can analyze which grader errors are most often overridden.
        
        // Example: If grader said "SinoNativeSwap" but user was actually right,
        // the telemetry event should include error_class: "SinoNativeSwap"
        
        var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: 42));
        
        // Find a Native counter item
        NumberItem nativeItem = null!;
        for (int seed = 0; seed < 10000; seed++)
        {
            var candidate = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: seed));
            if (candidate.System == NumberSystem.Native && candidate.DigitValue == 46)
            {
                nativeItem = candidate;
                break;
            }
        }
        
        Assert.NotNull(nativeItem);
        
        var result = _grader.Grade(nativeItem, "사십육 개", latencyMs: 1000);
        Assert.Equal("SinoNativeSwap", result.ErrorClass);
        
        // Override
        // var telemetrySink = new MockTelemetrySink();
        // result.ApplyUserOverride(telemetrySink);
        
        // Assert: Telemetry includes original error class
        // var evt = telemetrySink.Events.Single();
        // Assert.Equal("SinoNativeSwap", evt.Properties["original_error_class"]);
        
        Assert.Fail("Telemetry event not yet implemented");
    }

    [Theory(Skip = "Telemetry event not yet implemented - waiting for Kaylee")]
    [InlineData("SinoNativeSwap")]
    [InlineData("CounterMismatch")]
    [InlineData("WrongFormat")]
    [InlineData("SoundChangeMissed")]
    public void Override_TelemetryEvent_WorksForAllErrorClasses(string errorClass)
    {
        // Each error class should be capturable in override telemetry
        // This helps identify which grader rules are too strict
        
        Assert.Fail("Telemetry event not yet implemented");
    }

    #endregion

    #region Edge Cases for Override

    [Fact(Skip = "Override flow not yet implemented - waiting for Kaylee")]
    public void Override_OnAlreadyCorrectResult_HasNoEffect()
    {
        // If user tries to override a result that was already correct,
        // it should be a no-op (don't double-count streak, don't emit telemetry)
        
        var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: 42));
        var result = _grader.Grade(item, item.CanonicalAnswer, latencyMs: 1000);
        
        Assert.True(result.IsCorrect, "Precondition: result is already correct");
        
        // var telemetrySink = new MockTelemetrySink();
        // result.ApplyUserOverride(telemetrySink);
        
        // Assert: No telemetry event emitted (or event has flag indicating already-correct)
        // Assert.Empty(telemetrySink.Events);
        
        Assert.Fail("Override flow not yet implemented");
    }

    [Fact(Skip = "Override flow not yet implemented - waiting for Kaylee")]
    public void Override_CanOnlyBeAppliedOnce()
    {
        // User shouldn't be able to override the same result multiple times
        // (which would duplicate telemetry events or inflate score)
        
        var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: 42));
        var result = _grader.Grade(item, "wrong", latencyMs: 1000);
        
        // var telemetrySink = new MockTelemetrySink();
        // result.ApplyUserOverride(telemetrySink);
        // result.ApplyUserOverride(telemetrySink); // Second override
        
        // Assert: Only one event, or second call throws/no-ops
        // Assert.Single(telemetrySink.Events);
        
        Assert.Fail("Override flow not yet implemented");
    }

    #endregion

    #region Integration Hints (for when implementation lands)

    // TODO for Kaylee: When implementing override flow, consider:
    // 
    // 1. Where does override state live?
    //    - On GradingResult object (immutable → new instance)?
    //    - In NumberSessionService (tracks overrides per item)?
    //    - In UI state (NumberDrillPage)?
    // 
    // 2. How is telemetry emitted?
    //    - Direct dependency on ITelemetryService in grader? (breaks separation)
    //    - Service-layer wrapper that handles override + telemetry?
    //    - Event/callback pattern where UI passes telemetry sink?
    // 
    // 3. VocabQuiz pattern reference:
    //    - Find "I was right" button in VocabQuiz UI
    //    - Trace to service method that flips result
    //    - Copy telemetry event emission pattern
    //    - Adapt to NumberDrill domain (different event properties)
    // 
    // 4. Test strategy:
    //    - If telemetry is injected, use mock/spy pattern
    //    - If telemetry is hard dependency, add test helper to capture events
    //    - If UI-driven, write Playwright E2E test (see e2e-testing skill)

    #endregion
}
