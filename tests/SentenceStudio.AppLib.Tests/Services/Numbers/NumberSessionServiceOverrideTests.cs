using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services.Numbers;
using SentenceStudio.Shared.Models.Numbers;
using Xunit;

namespace SentenceStudio.AppLib.Tests.Services.Numbers;

/// <summary>
/// "I was correct" re-grades the stored attempt and its mastery row
/// (Captain, 2026-11-02) against real in-memory SQLite.
/// </summary>
public sealed class NumberSessionServiceOverrideTests : IDisposable
{
    private const string UserA = "override-user-a";
    private const string UserB = "override-user-b";
    private const int Latency = 5000;

    private readonly ApplicationDbContext _db;
    private readonly NumberSessionService _service;
    private readonly KoreanNumberItemGenerator _generator =
        new(NullLogger<KoreanNumberItemGenerator>.Instance);

    public NumberSessionServiceOverrideTests()
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
    public async Task Override_MarksAttemptCorrectAndMasteryEqualsADirectCorrectGrade()
    {
        var item = Item();
        await SeedProgressAsync(UserA, item);
        await SeedProgressAsync(UserB, item);

        var wrong = await _service.SubmitAnswerAsync(UserA, Guid.NewGuid(), item, "틀림", Latency);
        Assert.False(wrong.Grade.IsCorrect);
        Assert.NotNull(wrong.Receipt);
        Assert.Equal(new NumberSm2State(2.3, 6, 2, wrong.Receipt.Prior.DueDate), wrong.Receipt.Prior);

        var result = await _service.OverrideAttemptAsync(UserA, wrong.Receipt);

        Assert.Equal(NumberOverrideOutcome.Applied, result.Outcome);
        var direct = await _service.SubmitAnswerAsync(UserB, Guid.NewGuid(), item, item.CanonicalAnswer, Latency);
        Assert.True(direct.Grade.IsCorrect);

        _db.ChangeTracker.Clear();
        var attempt = await _db.NumberAttempts.AsNoTracking().SingleAsync(a => a.Id == wrong.Receipt.AttemptId);
        Assert.True(attempt.IsCorrect);
        Assert.Equal(UserA, attempt.UserProfileId);
        // The grader's error class stays for grader-miss diagnostics.
        Assert.Equal(wrong.Grade.ErrorClass, attempt.ErrorClass);

        var overridden = await Progress(UserA);
        var graded = await Progress(UserB);
        Assert.Equal(graded.CorrectCount, overridden.CorrectCount);
        Assert.Equal(graded.TotalCount, overridden.TotalCount);
        Assert.Equal(graded.EaseFactor, overridden.EaseFactor);
        Assert.Equal(graded.Interval, overridden.Interval);
        Assert.Equal(graded.Repetitions, overridden.Repetitions);
        Assert.Equal(graded.MedianLatencyMs, overridden.MedianLatencyMs);
        Assert.Equal(13, overridden.Interval);
        Assert.Equal(2.4, overridden.EaseFactor, 10);
        Assert.InRange((graded.DueDate - overridden.DueDate).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(1));
        Assert.Equal(overridden.CorrectCount, result.Snapshot!.CorrectCount);
        Assert.Equal(overridden.DueDate, result.Snapshot.NextDueDate, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Override_FirstAttemptOnANewRowStartsFromTheDefaultSchedule()
    {
        var item = Item();
        var wrong = await _service.SubmitAnswerAsync(UserA, Guid.NewGuid(), item, "틀림", Latency);

        var result = await _service.OverrideAttemptAsync(UserA, wrong.Receipt!);

        Assert.Equal(NumberOverrideOutcome.Applied, result.Outcome);
        _db.ChangeTracker.Clear();
        var row = await Progress(UserA);
        Assert.Equal(1, row.CorrectCount);
        Assert.Equal(1, row.TotalCount);
        Assert.Equal(6, row.Interval);
        Assert.Equal(2.5, row.EaseFactor, 10);
    }

    [Fact]
    public async Task Override_SecondCallIsANoOp()
    {
        var item = Item();
        await SeedProgressAsync(UserA, item);
        var wrong = await _service.SubmitAnswerAsync(UserA, Guid.NewGuid(), item, "틀림", Latency);

        Assert.Equal(NumberOverrideOutcome.Applied,
            (await _service.OverrideAttemptAsync(UserA, wrong.Receipt!)).Outcome);
        _db.ChangeTracker.Clear();
        var after = await Progress(UserA);

        var again = await _service.OverrideAttemptAsync(UserA, wrong.Receipt!);

        Assert.Equal(NumberOverrideOutcome.AlreadyCorrect, again.Outcome);
        _db.ChangeTracker.Clear();
        var final = await Progress(UserA);
        Assert.Equal(after.CorrectCount, final.CorrectCount);
        Assert.Equal(after.Interval, final.Interval);
        Assert.Equal(after.EaseFactor, final.EaseFactor);
        Assert.Equal(after.DueDate, final.DueDate);
    }

    [Theory]
    [InlineData(UserB)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Override_ForeignOrAbsentOwnerIsRefusedWithoutWriting(string caller)
    {
        var item = Item();
        await SeedProgressAsync(UserA, item);
        await SeedProgressAsync(UserB, item);
        var wrong = await _service.SubmitAnswerAsync(UserA, Guid.NewGuid(), item, "틀림", Latency);
        _db.ChangeTracker.Clear();
        var before = await Progress(UserA);
        var beforeB = await Progress(UserB);

        var result = await _service.OverrideAttemptAsync(caller, wrong.Receipt!);

        Assert.Equal(NumberOverrideOutcome.Refused, result.Outcome);
        Assert.Null(result.Snapshot);
        _db.ChangeTracker.Clear();
        Assert.False((await _db.NumberAttempts.AsNoTracking().SingleAsync()).IsCorrect);
        AssertSame(before, await Progress(UserA));
        AssertSame(beforeB, await Progress(UserB));
    }

    [Fact]
    public async Task Override_RefusedWhenTheMasteryRowChangedSinceTheAttempt()
    {
        var item = Item();
        await SeedProgressAsync(UserA, item);
        var first = await _service.SubmitAnswerAsync(UserA, Guid.NewGuid(), item, "틀림", Latency);
        var second = await _service.SubmitAnswerAsync(UserA, Guid.NewGuid(), item, "또 틀림", Latency);
        _db.ChangeTracker.Clear();
        var before = await Progress(UserA);

        var stale = await _service.OverrideAttemptAsync(UserA, first.Receipt!);

        Assert.Equal(NumberOverrideOutcome.Refused, stale.Outcome);
        _db.ChangeTracker.Clear();
        Assert.False((await _db.NumberAttempts.AsNoTracking().SingleAsync(a => a.Id == first.Receipt!.AttemptId)).IsCorrect);
        AssertSame(before, await Progress(UserA));

        // The latest attempt still matches the row and can be corrected.
        Assert.Equal(NumberOverrideOutcome.Applied,
            (await _service.OverrideAttemptAsync(UserA, second.Receipt!)).Outcome);
    }

    [Fact]
    public async Task Override_RefusedWhenTheRowWasChangedByAnotherContext()
    {
        var item = Item();
        await SeedProgressAsync(UserA, item);
        var wrong = await _service.SubmitAnswerAsync(UserA, Guid.NewGuid(), item, "틀림", Latency);
        // The service's context still tracks the row; another writer changes it.
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE NumberMasteryProgress SET Interval = 9 WHERE UserProfileId = {0}", UserA);

        var result = await _service.OverrideAttemptAsync(UserA, wrong.Receipt!);

        Assert.Equal(NumberOverrideOutcome.Refused, result.Outcome);
        _db.ChangeTracker.Clear();
        Assert.Equal(9, (await Progress(UserA)).Interval);
        Assert.False((await _db.NumberAttempts.AsNoTracking().SingleAsync()).IsCorrect);
    }

    [Fact]
    public async Task Override_RefusedForAReceiptThatIsNotTheWrongGradeOfThatRow()
    {
        var item = Item();
        await SeedProgressAsync(UserA, item);
        var wrong = await _service.SubmitAnswerAsync(UserA, Guid.NewGuid(), item, "틀림", Latency);
        var forged = wrong.Receipt! with { Prior = wrong.Receipt.Prior with { Interval = 300, EaseFactor = 2.5 } };

        var result = await _service.OverrideAttemptAsync(UserA, forged);

        Assert.Equal(NumberOverrideOutcome.Refused, result.Outcome);
        _db.ChangeTracker.Clear();
        Assert.False((await _db.NumberAttempts.AsNoTracking().SingleAsync()).IsCorrect);
    }

    private NumberItem Item() => _generator.GenerateItem(new NumberItemRequest(
        ContextCode: "Age", SubModeCode: "ReadAndProduce", Bucket: "1-10",
        CounterId: null, Difficulty: 1, RandomSeed: 123));

    private async Task SeedProgressAsync(string user, NumberItem item)
    {
        _db.NumberMasteryProgresses.Add(new NumberMasteryProgress
        {
            UserProfileId = user,
            LanguageCode = "ko",
            ContextCode = item.ContextCode,
            CounterId = item.CounterId,
            System = item.System,
            Bucket = item.Bucket,
            CorrectCount = 3,
            TotalCount = 4,
            MedianLatencyMs = 4000,
            EaseFactor = 2.3,
            Interval = 6,
            Repetitions = 2,
            DueDate = DateTime.UtcNow.AddDays(-1),
            LastReviewed = DateTime.UtcNow.AddDays(-7),
        });
        await _db.SaveChangesAsync();
    }

    private Task<NumberMasteryProgress> Progress(string user) =>
        _db.NumberMasteryProgresses.AsNoTracking().SingleAsync(p => p.UserProfileId == user);

    private static void AssertSame(NumberMasteryProgress expected, NumberMasteryProgress actual)
    {
        Assert.Equal(expected.CorrectCount, actual.CorrectCount);
        Assert.Equal(expected.TotalCount, actual.TotalCount);
        Assert.Equal(expected.EaseFactor, actual.EaseFactor);
        Assert.Equal(expected.Interval, actual.Interval);
        Assert.Equal(expected.Repetitions, actual.Repetitions);
        Assert.Equal(expected.DueDate, actual.DueDate);
        Assert.Equal(expected.LastReviewed, actual.LastReviewed);
    }
}
