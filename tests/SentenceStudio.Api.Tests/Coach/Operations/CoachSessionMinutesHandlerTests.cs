using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Data;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// "Increase to 45 minutes from now on" (Captain, 2026-11-17): one approval saves the session
/// length and resizes today's plan; one undo puts both back. Only the profile's own lengths are
/// accepted, and only the signed-in learner's row is written.
/// </summary>
public sealed class CoachSessionMinutesHandlerTests : IDisposable
{
    private const string Owner = "user-owner";
    private const string Other = "user-other";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    private readonly FakePlanService _plans = new(DateOnly.FromDateTime(DateTime.UtcNow));

    public CoachSessionMinutesHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w => w.Ignore(
                   Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        _provider = services.BuildServiceProvider();

        using (var bootstrap = _provider.CreateScope())
        {
            var db = bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.EnsureCreated();
            db.UserProfiles.AddRange(
                new UserProfile { Id = Owner, Name = "Owner", PreferredSessionMinutes = 20, TargetLanguage = "Korean" },
                new UserProfile { Id = Other, Name = "Other", PreferredSessionMinutes = 20, TargetLanguage = "Korean" });
            db.SaveChanges();
        }

        _db = _provider.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    // A fresh context per handler, as each request (approve, then undo) gets its own scope.
    private CoachPreferenceChangeHandler NewHandler(IPlanService? plans) =>
        new(new UserProfileRepository(_provider, NullLogger<UserProfileRepository>.Instance),
            new CoachWriteOwnership(_provider.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>()), plans);

    private int MinutesOf(string userId)
    {
        using var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .UserProfiles.AsNoTracking().Single(p => p.Id == userId).PreferredSessionMinutes;
    }

    private static string Json(string setting, string value) =>
        System.Text.Json.JsonSerializer.Serialize(new CoachPreferenceChangeArgs(setting, value), CoachNormalizedJson.Options);

    [Fact]
    public async Task One_approval_saves_the_length_and_resizes_todays_plan()
    {
        var handler = NewHandler(_plans);

        var preview = await handler.PrepareAsync(Owner, Json("session_minutes", "45"), CancellationToken.None);
        preview.Lines.Should().Contain(l => l.Contains("resized to 45 minutes"));

        var execution = await handler.ExecuteAsync(Owner, Json("session_minutes", "45"), CancellationToken.None);

        MinutesOf(Owner).Should().Be(45);
        MinutesOf(Other).Should().Be(20, "only the signed-in learner's row is written");
        _plans.ApplyCallCount.Should().Be(1);
        _plans.LastAppliedConstraints!.AvailableMinutes.Should().Be(45);
        execution.Lines.Should().Contain(l => l.StartsWith("Today’s Plan:"));
    }

    [Fact]
    public async Task One_undo_puts_back_the_length_and_the_plan()
    {
        var handler = NewHandler(_plans);
        var args = Json("session_minutes", "45");
        var execution = await handler.ExecuteAsync(Owner, args, CancellationToken.None);

        var undo = await NewHandler(_plans).UndoAsync(Owner, args, execution.PriorStateJson!, CancellationToken.None);

        MinutesOf(Owner).Should().Be(20);
        _plans.UndoCallCount.Should().Be(1);
        undo.Lines.Should().Contain("Today’s Plan is back as it was.");
    }

    [Fact]
    public async Task With_no_plan_today_only_the_length_changes()
    {
        _plans.SetItems([]);
        var handler = NewHandler(_plans);

        var execution = await handler.ExecuteAsync(Owner, Json("session_minutes", "30"), CancellationToken.None);

        MinutesOf(Owner).Should().Be(30);
        _plans.ApplyCallCount.Should().Be(0);
        execution.Lines.Should().ContainSingle();
    }

    [Fact]
    public async Task A_plan_that_cannot_be_resized_keeps_the_saved_length_and_says_so()
    {
        var handler = NewHandler(new ThrowingPlans(_plans));

        var execution = await handler.ExecuteAsync(Owner, Json("session_minutes", "45"), CancellationToken.None);

        MinutesOf(Owner).Should().Be(45);
        execution.Lines.Should().Contain("Today\u2019s Plan is unchanged.");
        execution.PriorStateJson.Should().NotBeNull("undo stays offered for the saved setting");
    }

    [Fact]
    public async Task Undo_leaves_a_length_the_learner_changed_since()
    {
        var handler = NewHandler(_plans);
        var args = Json("session_minutes", "45");
        var execution = await handler.ExecuteAsync(Owner, args, CancellationToken.None);
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.UserProfiles.Single(p => p.Id == Owner).PreferredSessionMinutes = 30;
            db.SaveChanges();
        }

        var act = () => NewHandler(_plans).UndoAsync(Owner, args, execution.PriorStateJson!, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        MinutesOf(Owner).Should().Be(30, "the learner's own later choice stands");
        _plans.UndoCallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("45 minute sessions", CoachWriteChangeKind.SettingChange, true)]
    [InlineData("매일 30분", CoachWriteChangeKind.SettingChange, true)]
    [InlineData("I like K-dramas", CoachWriteChangeKind.SettingChange, false)]
    [InlineData("45 minute sessions", CoachWriteChangeKind.VocabularyAdd, false)]
    public void Only_a_memory_of_the_same_session_length_is_dropped(string goal, CoachWriteChangeKind kind, bool dropped)
    {
        var proposal = new CoachMemoryProposalIntent { Kind = CoachProposedMemoryKind.PersistentStudyGoal, StudyGoalText = goal };

        SentenceStudio.Api.Coach.Application.CoachSessionService.DuplicatesSessionLengthSetting(proposal, kind)
            .Should().Be(dropped);
    }

    [Theory]
    [InlineData("25")]
    [InlineData("180")]
    [InlineData("forty-five")]
    public async Task A_length_the_profile_screen_does_not_offer_is_refused(string value)
    {
        var act = () => NewHandler(_plans).PrepareAsync(Owner, Json("session_minutes", value), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        MinutesOf(Owner).Should().Be(20);
    }

    [Theory]
    [InlineData("target_language", "German")]
    [InlineData("cefr_level", "B2")]
    [InlineData("quiz_show_text_with_photo", "true")]
    public async Task Every_other_setting_is_still_refused(string setting, string value)
    {
        var act = () => NewHandler(_plans).ExecuteAsync(Owner, Json(setting, value), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    /// <summary>Today's plan as the fake has it, with every resize failing.</summary>
    private sealed class ThrowingPlans(FakePlanService inner) : IPlanService
    {
        public Task<PlanSnapshot> GetTodaySnapshotAsync(CancellationToken ct = default) => inner.GetTodaySnapshotAsync(ct);
        public Task<PlanRevisionResult> ApplyCoachConstraintsAsync(CoachPlanRevisionRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("planner unavailable");
        public Task<TodaysPlanDto?> GetTodayAsync(CancellationToken ct = default) => inner.GetTodayAsync(ct);
        public Task<TodaysPlanDto> GenerateTodayAsync(GenerateTodaysPlanRequest request, CancellationToken ct = default) => inner.GenerateTodayAsync(request, ct);
        public Task<bool> UpdateProgressAsync(DateOnly planDate, string planItemId, int minutesSpent, CancellationToken ct = default) => inner.UpdateProgressAsync(planDate, planItemId, minutesSpent, ct);
        public Task<PlanItemDto?> MarkCompleteAsync(DateOnly planDate, string planItemId, int minutesSpent, CancellationToken ct = default) => inner.MarkCompleteAsync(planDate, planItemId, minutesSpent, ct);
        public Task ResetTodayAsync(CancellationToken ct = default) => inner.ResetTodayAsync(ct);
        public Task<TodaysPlanDto?> RegenerateTodayAsync(CancellationToken ct = default) => inner.RegenerateTodayAsync(ct);
        public Task<PlanPreviewResult> PreviewPlanAsync(PlanConstraints? constraints, CancellationToken ct = default) => inner.PreviewPlanAsync(constraints, ct);
        public Task<PlanPreviewResult> PreviewPlanAsync(PlanConstraints? constraints, IReadOnlyList<string>? focusVocabularyWordIds, CancellationToken ct = default) => inner.PreviewPlanAsync(constraints, focusVocabularyWordIds, ct);
        public Task<PlanRevisionResult> UndoCoachRevisionAsync(CoachPlanUndoRequest request, CancellationToken ct = default) => inner.UndoCoachRevisionAsync(request, ct);
    }
}
