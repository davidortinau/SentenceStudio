using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services.Numbers;
using SentenceStudio.Shared.Models.Numbers;
using Xunit;

namespace SentenceStudio.AppLib.Tests.Services.Numbers;

/// <summary>
/// The session summary's median time and patterns to review come from the
/// session's stored attempts (docs/native-port-backports.md B22), and an
/// absent owner never writes an attempt.
/// </summary>
public sealed class NumberSessionServiceSummaryTests : IDisposable
{
    private const string User = "summary-user";

    private readonly ApplicationDbContext _db;
    private readonly NumberSessionService _service;
    private readonly KoreanNumberItemGenerator _generator =
        new(NullLogger<KoreanNumberItemGenerator>.Instance);

    public NumberSessionServiceSummaryTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _service = new NumberSessionService(_generator, new KoreanNumberAnswerGrader(), _db,
            NullLogger<NumberSessionService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [Fact]
    public async Task Summary_HasTheMedianTimeAndTheWrongAnswersPatterns()
    {
        await SeedAsync();
        var session = await _service.StartSessionAsync(User, "ko",
            new NumberSessionRequest("Age", "ReadAndProduce", ItemCount: 3));
        var items = session.Items;

        await _service.SubmitAnswerAsync(User, session.SessionId, items[0], items[0].CanonicalAnswer, 3000);
        var wrong1 = await _service.SubmitAnswerAsync(User, session.SessionId, items[1], "틀림", 5000);
        var wrong2 = await _service.SubmitAnswerAsync(User, session.SessionId, items[2], "아니", 9000);

        var summary = await _service.EndSessionAsync(session.SessionId);

        Assert.Equal(5000, summary.MedianLatencyMs);
        Assert.Equal(1, summary.CorrectCount);
        Assert.Equal(2, summary.ErrorClassBreakdown.Values.Sum());
        Assert.Contains(wrong1.Grade.ErrorClass!, summary.ErrorClassBreakdown.Keys);
        Assert.Contains(wrong2.Grade.ErrorClass!, summary.ErrorClassBreakdown.Keys);
    }

    [Fact]
    public async Task OverriddenAttempt_LeavesThePatternsAndCountsAsCorrect()
    {
        await SeedAsync();
        var session = await _service.StartSessionAsync(User, "ko",
            new NumberSessionRequest("Age", "ReadAndProduce", ItemCount: 2));
        // "I was correct" is pressed on the item's feedback, before the next answer.
        var wrong = await _service.SubmitAnswerAsync(User, session.SessionId, session.Items[0], "틀림", 4000);
        var result = await _service.OverrideAttemptAsync(User, wrong.Receipt!);
        await _service.SubmitAnswerAsync(User, session.SessionId, session.Items[1], "아니", 6000);

        var summary = await _service.EndSessionAsync(session.SessionId);

        Assert.Equal(NumberOverrideOutcome.Applied, result.Outcome);
        Assert.Equal(1, summary.CorrectCount);
        Assert.Equal(1, summary.ErrorClassBreakdown.Values.Sum());
        Assert.Equal(5000, summary.MedianLatencyMs);
    }

    [Fact]
    public async Task DisambiguateMisses_CountAsSystemConfusion()
    {
        await SeedAsync();
        var session = await _service.StartSessionAsync(User, "ko",
            new NumberSessionRequest(null, "Disambiguate", ItemCount: 1));
        var item = session.Items[0];

        // A wrong choice for the second prompt; each choice is graded exactly.
        var wrongB = item.ChoicesB!.First(choice => choice != item.CorrectAnswerB);
        var result = await _service.SubmitAnswerAsync(User, session.SessionId, item,
            $"{item.CorrectAnswerA} | {wrongB}", 7000);
        var summary = await _service.EndSessionAsync(session.SessionId);

        Assert.False(result.Grade.IsCorrect);
        Assert.Equal(new Dictionary<string, int> { ["system_confusion"] = 1 }, summary.ErrorClassBreakdown);
    }

    [Fact]
    public async Task AttemptsOfAnotherSession_AreNotInThisSummary()
    {
        await SeedAsync();
        var first = await _service.StartSessionAsync(User, "ko",
            new NumberSessionRequest("Age", "ReadAndProduce", ItemCount: 1));
        var second = await _service.StartSessionAsync(User, "ko",
            new NumberSessionRequest("Age", "ReadAndProduce", ItemCount: 1));
        await _service.SubmitAnswerAsync(User, first.SessionId, first.Items[0], "틀림", 2000);
        await _service.SubmitAnswerAsync(User, second.SessionId, second.Items[0], second.Items[0].CanonicalAnswer, 8000);

        var summary = await _service.EndSessionAsync(second.SessionId);

        Assert.Equal(8000, summary.MedianLatencyMs);
        Assert.Empty(summary.ErrorClassBreakdown);
        Assert.Equal(1, summary.CorrectCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SubmitWithoutAnOwner_IsRefused_AndWritesNothing(string owner)
    {
        await SeedAsync();
        var item = _generator.GenerateItem(new NumberItemRequest("Age", "ReadAndProduce", RandomSeed: 7));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.SubmitAnswerAsync(owner, Guid.NewGuid(), item, item.CanonicalAnswer, 1000));

        Assert.Equal(0, await _db.NumberAttempts.CountAsync());
        Assert.Equal(0, await _db.NumberMasteryProgresses.CountAsync());
    }

    [Fact]
    public void SubModes_AreListedInSeedOrder_WhateverTheirStoredOrder()
    {
        var stored = new[]
        {
            new NumberSubMode { Code = "ListenAndPlace", Phase = 2 },
            new NumberSubMode { Code = "ReadAndProduce", Phase = 1 },
            new NumberSubMode { Code = "Disambiguate", Phase = 2 },
            new NumberSubMode { Code = "ListenAndType", Phase = 1 },
            new NumberSubMode { Code = "TapTheCounter", Phase = 2 },
        };

        Assert.Equal(
            ["ListenAndType", "ReadAndProduce", "TapTheCounter", "Disambiguate", "ListenAndPlace"],
            NumberSubModeOrder.InSeedOrder(stored).Select(mode => mode.Code));
    }

    private async Task SeedAsync() =>
        await new NumberContentSeeder(_db, NullLogger<NumberContentSeeder>.Instance).SeedAsync("ko");
}
