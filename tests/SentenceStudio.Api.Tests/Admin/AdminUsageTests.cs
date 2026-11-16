using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// AI usage and jobs: the numbers match what is stored for two learners with different data, one
/// learner's numbers never include the other's, and no content leaves.
/// </summary>
/// <remarks>The factory's clock is 2026-11-15 12:00 UTC, a Sunday in ISO week 2026-W46.</remarks>
public sealed class AdminUsageTests : IDisposable
{
    private const string SecretText = "SECRET-LEARNER-CONTENT";

    private readonly AdminApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly string[] Routes =
    [
        "/api/admin/usage/sam",
        "/api/admin/usage/video-imports",
        "/api/admin/usage/part-of-speech"
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_learner_and_a_caller_with_no_token_get_404_on_every_usage_route(int index)
    {
        var learner = await _factory.SeedAccountAsync("learner@usage.local");
        using var learnerClient = _factory.CreateClientFor(learner);
        using var anonymous = _factory.CreateClient();

        (await learnerClient.GetAsync(Routes[index])).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync(Routes[index])).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _factory.ReadAuditAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Each_usage_read_leaves_one_audit_row_with_its_action_and_no_target()
    {
        var (admin, _, _) = await SeedAsync();
        using var client = _factory.CreateClientFor(admin);

        foreach (var route in Routes)
        {
            (await client.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var rows = await _factory.ReadAuditAsync();
        rows.Select(r => r.Action).Should().Equal(
            AdminActions.UsageSamRead, AdminActions.UsageVideoImportsRead, AdminActions.UsagePartOfSpeechRead);
        rows.Should().OnlyContain(r => r.ActorId == admin.Id && r.StatusCode == 200 && r.TargetId == null);
    }

    // ------------------------------------------------------------------ Sam

    [Fact]
    public async Task Sam_usage_matches_the_stored_rows_per_learner_per_day_and_week()
    {
        var (admin, a, b) = await SeedAsync();
        using var client = _factory.CreateClientFor(admin);

        var usage = (await client.GetFromJsonAsync<AdminSamUsage>("/api/admin/usage/sam?weeks=2"))!;

        usage.FromLocalDate.Should().Be("2026-11-02");
        usage.Truncated.Should().BeFalse();
        usage.NotRecorded.Should().Equal(AdminUsageNotRecorded.OtherAiCalls);
        usage.Days.Select(d => (d.ProfileId, d.LocalDate)).Should().Equal(
            (a.UserProfileId!, "2026-11-15"),
            (a.UserProfileId!, "2026-11-14"),
            (b.UserProfileId!, "2026-11-10"),
            (a.UserProfileId!, "2026-11-03"));
        usage.Days.Single(d => d.ProfileId == b.UserProfileId).Should().Be(
            new AdminSamUsageDay(b.UserProfileId!, "2026-11-10", 5, 1000, 500, 0.5m));

        var aThisWeek = usage.Weeks.Single(w => w.ProfileId == a.UserProfileId && w.WeekKey == "2026-W46");
        aThisWeek.Runs.Should().Be(3);
        aThisWeek.InputTokens.Should().Be(300);
        aThisWeek.OutputTokens.Should().Be(150);
        aThisWeek.EstimatedCostUsd.Should().Be(0.03m);
        aThisWeek.DaysWithRuns.Should().Be(2);
        usage.Weeks.Single(w => w.ProfileId == a.UserProfileId && w.WeekKey == "2026-W45").Runs.Should().Be(1);
        usage.Weeks.Single(w => w.ProfileId == b.UserProfileId).Runs.Should().Be(5);

        // One learner's weeks never include the other's.
        usage.Weeks.Where(w => w.ProfileId == a.UserProfileId).Sum(w => w.Runs).Should().Be(4);
        usage.Weeks.Sum(w => w.Runs).Should().Be(usage.Days.Sum(d => d.Runs));

        usage.Learners.Select(l => l.ProfileId).Should().BeEquivalentTo([a.UserProfileId!, b.UserProfileId!]);
        usage.Learners.Single(l => l.ProfileId == a.UserProfileId).Email.Should().Be(a.Email);
    }

    [Fact]
    public async Task A_longer_window_reaches_older_days_and_a_shorter_one_leaves_them_out()
    {
        var (admin, a, _) = await SeedAsync();
        using var client = _factory.CreateClientFor(admin);

        var four = (await client.GetFromJsonAsync<AdminSamUsage>("/api/admin/usage/sam"))!;
        var one = (await client.GetFromJsonAsync<AdminSamUsage>("/api/admin/usage/sam?weeks=1"))!;

        four.WeeksAsked.Should().Be(4);
        four.Days.Should().Contain(d => d.LocalDate == "2026-10-20" && d.ProfileId == a.UserProfileId);
        one.Days.Select(d => d.LocalDate).Should().Equal("2026-11-15", "2026-11-14", "2026-11-10");
    }

    [Fact]
    public async Task A_run_on_a_learners_Sunday_evening_is_still_shown_when_UTC_has_reached_Monday()
    {
        // Rows are dated by the learner's own calendar. Alpha's newest row is Sunday the 15th; an
        // admin looking a few hours later, on UTC's Monday, must still see it under "1 week".
        var (admin, a, _) = await SeedAsync();
        using var client = _factory.CreateClientFor(admin);
        _factory.Time.Advance(TimeSpan.FromHours(13));

        var one = (await client.GetFromJsonAsync<AdminSamUsage>("/api/admin/usage/sam?weeks=1"))!;

        one.FromLocalDate.Should().Be("2026-11-09");
        one.Days.Should().Contain(d => d.LocalDate == "2026-11-15" && d.ProfileId == a.UserProfileId);
        one.Weeks.Should().Contain(w => w.WeekKey == "2026-W46" && w.ProfileId == a.UserProfileId);
    }

    [Fact]
    public async Task A_long_day_list_is_cut_and_says_so_while_the_weeks_stay_whole()
    {
        var admin = await _factory.SeedAdminAsync("captain@usage.local");
        var today = new DateOnly(2026, 11, 15);
        using (var scope = _factory.Services.CreateScope())
        {
            var coach = scope.ServiceProvider.GetRequiredService<CoachDbContext>();
            for (var learner = 0; learner < 30; learner++)
            {
                for (var back = 0; back < 70; back++)
                {
                    var day = today.AddDays(-back);
                    coach.CoachUsages.Add(new CoachUsage
                    {
                        Id = Guid.NewGuid().ToString(),
                        UserProfileId = $"many-{learner:D2}",
                        LocalDate = day,
                        WeekKey = CoachNormalizedJson.WeekKey(day),
                        RunCount = 1,
                        InputTokens = 10,
                        OutputTokens = 5,
                        EstimatedCostUsd = 0.001m,
                        CreatedAt = _factory.Time.GetUtcNow().UtcDateTime,
                        UpdatedAt = _factory.Time.GetUtcNow().UtcDateTime
                    });
                }
            }

            await coach.SaveChangesAsync();
        }

        using var client = _factory.CreateClientFor(admin);
        var usage = (await client.GetFromJsonAsync<AdminSamUsage>("/api/admin/usage/sam?weeks=10"))!;

        // 30 learners over the 70 days from 2026-09-07 (a Monday) to today: 2,100 rows.
        usage.Truncated.Should().BeFalse("every row was read");
        usage.DaysTruncated.Should().BeTrue();
        usage.Days.Should().HaveCount(2000);
        usage.Weeks.Sum(w => w.Runs).Should().Be(2100, "the weeks are summed from every row read, not from the days listed");
    }

    [Theory]
    [InlineData("weeks=0")]
    [InlineData("weeks=14")]
    [InlineData("weeks=abc")]
    [InlineData("weeks=-1")]
    public async Task A_bad_weeks_value_is_the_routes_own_400(string query)
    {
        var (admin, _, _) = await SeedAsync();
        using var client = _factory.CreateClientFor(admin);

        (await client.GetAsync($"/api/admin/usage/sam?{query}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ video imports

    [Fact]
    public async Task Failed_and_stuck_imports_are_listed_with_a_reason_code_and_no_content()
    {
        var (admin, a, b) = await SeedAsync();
        using var client = _factory.CreateClientFor(admin);

        var body = await client.GetStringAsync("/api/admin/usage/video-imports");
        var imports = (await client.GetFromJsonAsync<AdminVideoImports>("/api/admin/usage/video-imports"))!;

        body.Should().NotContain(SecretText).And.NotContain("youtube.com").And.NotContain("exception said");
        imports.FailedCount.Should().Be(3);
        imports.StuckCount.Should().Be(1);
        imports.StuckAfterMinutes.Should().Be(10);
        imports.Items.Should().HaveCount(4);
        imports.Items.Select(i => i.Id).Should().BeEquivalentTo(["a-nt", "a-other", "a-stuck", "b-cancel"]);

        var noTranscript = imports.Items.Single(i => i.Id == "a-nt");
        noTranscript.Problem.Should().Be(AdminVideoImportProblems.Failed);
        noTranscript.ReasonCode.Should().Be(AdminVideoImportReasons.NoTranscript);
        noTranscript.Status.Should().Be(AdminVideoImportStatusCodes.Failed);
        noTranscript.ProfileId.Should().Be(a.UserProfileId);
        imports.Items.Single(i => i.Id == "a-other").ReasonCode.Should().Be(AdminVideoImportReasons.Other);
        imports.Items.Single(i => i.Id == "b-cancel").ReasonCode.Should().Be(AdminVideoImportReasons.Cancelled);
        imports.Items.Single(i => i.Id == "b-cancel").ProfileId.Should().Be(b.UserProfileId);

        var stuck = imports.Items.Single(i => i.Id == "a-stuck");
        stuck.Problem.Should().Be(AdminVideoImportProblems.Stuck);
        stuck.Status.Should().Be(AdminVideoImportStatusCodes.GeneratingVocabulary);
        stuck.ReasonCode.Should().BeNull();

        imports.Items.Select(i => i.Id).Should().NotContain(["a-recent", "a-done"],
            "a recent in-progress import and a completed one are neither failed nor stuck");
        imports.Reasons.Should().BeEquivalentTo(new[]
        {
            new AdminVideoImportReasonCount(AdminVideoImportReasons.NoTranscript, 1),
            new AdminVideoImportReasonCount(AdminVideoImportReasons.Other, 1),
            new AdminVideoImportReasonCount(AdminVideoImportReasons.Cancelled, 1)
        });
    }

    [Theory]
    [InlineData("No Korean transcript available for this video.", AdminVideoImportReasons.NoTranscript)]
    [InlineData("No Japanese transcript available for this video.", AdminVideoImportReasons.NoTranscript)]
    [InlineData("Transcript too short (12 chars). Minimum is 100 — this may be a YouTube Short.", AdminVideoImportReasons.TranscriptTooShort)]
    [InlineData("Import timed out after 30 minutes", AdminVideoImportReasons.TimedOut)]
    [InlineData("Import timed out — stuck in FetchingTranscript for over 10 minutes.", AdminVideoImportReasons.TimedOut)]
    [InlineData("Import was cancelled.", AdminVideoImportReasons.Cancelled)]
    [InlineData("An unexpected error occurred during import.", AdminVideoImportReasons.UnexpectedError)]
    [InlineData("An unexpected error occurred while fetching transcripts.", AdminVideoImportReasons.UnexpectedError)]
    [InlineData("No suitable audio stream found for this video.", AdminVideoImportReasons.NoAudioStream)]
    [InlineData("anything else", AdminVideoImportReasons.Other)]
    [InlineData("", AdminVideoImportReasons.NoneStored)]
    [InlineData(null, AdminVideoImportReasons.NoneStored)]
    public void Stored_failure_text_is_sorted_into_a_fixed_code(string? text, string expected)
    {
        AdminUsageEndpoints.ReasonCodeOf(text).Should().Be(expected);
    }

    [Fact]
    public void The_failures_the_YouTube_service_classifies_itself_each_get_a_code_of_their_own()
    {
        // Asked of the service's own classifiers, not of copied text: a change of wording there
        // fails here instead of quietly turning a known reason into "another reason".
        static string Written(string classifier, Exception? cause) =>
            ((Exception)typeof(YouTubeImportService)
                .GetMethod(classifier, BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [cause])!).Message;

        AdminUsageEndpoints.ReasonCodeOf(Written("ClassifyVideoUnavailable", null))
            .Should().Be(AdminVideoImportReasons.VideoUnavailable);
        AdminUsageEndpoints.ReasonCodeOf(Written("ClassifyRateLimited", null))
            .Should().Be(AdminVideoImportReasons.RateLimited);
        AdminUsageEndpoints.ReasonCodeOf(Written("ClassifyNetwork", new HttpRequestException("x", null, HttpStatusCode.TooManyRequests)))
            .Should().Be(AdminVideoImportReasons.NetworkError);
        AdminUsageEndpoints.ReasonCodeOf(Written("ClassifyNetwork", new HttpRequestException("x")))
            .Should().Be(AdminVideoImportReasons.NetworkError);
    }

    [Fact]
    public async Task A_bad_take_is_the_routes_own_400()
    {
        var (admin, _, _) = await SeedAsync();
        using var client = _factory.CreateClientFor(admin);

        foreach (var take in new[] { "0", "201", "x", "-3" })
        {
            (await client.GetAsync($"/api/admin/usage/video-imports?take={take}")).StatusCode
                .Should().Be(HttpStatusCode.BadRequest, take);
        }

        var one = (await client.GetFromJsonAsync<AdminVideoImports>("/api/admin/usage/video-imports?take=1"))!;
        one.Items.Should().ContainSingle();
        one.FailedCount.Should().Be(3, "the counts cover every import, not just the page");
    }

    // ------------------------------------------------------------------ part of speech

    [Fact]
    public async Task Part_of_speech_counts_match_the_words_per_learner_and_say_what_is_not_recorded()
    {
        var (admin, a, b) = await SeedAsync();
        using var client = _factory.CreateClientFor(admin);

        var body = await client.GetStringAsync("/api/admin/usage/part-of-speech");
        var pos = (await client.GetFromJsonAsync<AdminPartOfSpeech>("/api/admin/usage/part-of-speech"))!;

        body.Should().NotContain(SecretText);
        pos.TotalWords.Should().Be(6);
        pos.Classified.Should().Be(3);
        pos.Unclassified.Should().Be(3);
        pos.UnownedWords.Should().Be(2);
        pos.Truncated.Should().BeFalse();
        pos.NotRecorded.Should().BeEquivalentTo(
            [AdminUsageNotRecorded.BackfillLastRun, AdminUsageNotRecorded.BackfillEnabled]);

        pos.Learners.Select(l => l.Learner.ProfileId).Should().Equal(a.UserProfileId!, b.UserProfileId!);
        var forA = pos.Learners[0];
        (forA.Words, forA.Classified, forA.Unclassified).Should().Be((3L, 1L, 2L));
        forA.Learner.Email.Should().Be(a.Email);
        var forB = pos.Learners[1];
        (forB.Words, forB.Classified, forB.Unclassified).Should().Be((1L, 1L, 0L));
    }

    // ------------------------------------------------------------------ seed

    private async Task<(ApplicationUser Admin, ApplicationUser A, ApplicationUser B)> SeedAsync()
    {
        var admin = await _factory.SeedAdminAsync("captain@usage.local");
        var a = await _factory.SeedAccountAsync("alpha@usage.local");
        var b = await _factory.SeedAccountAsync("bravo@usage.local");
        var now = _factory.Time.GetUtcNow().UtcDateTime;

        using var scope = _factory.Services.CreateScope();
        var coach = scope.ServiceProvider.GetRequiredService<CoachDbContext>();
        coach.CoachUsages.AddRange(
            Usage(a, new DateOnly(2026, 11, 15), 1, 100, 50, 0.01m),
            Usage(a, new DateOnly(2026, 11, 14), 2, 200, 100, 0.02m),
            Usage(a, new DateOnly(2026, 11, 3), 1, 10, 5, 0.001m),
            Usage(a, new DateOnly(2026, 10, 20), 7, 70, 70, 0.07m),
            Usage(b, new DateOnly(2026, 11, 10), 5, 1000, 500, 0.5m));
        await coach.SaveChangesAsync();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.UserProfiles.Add(new UserProfile { Id = a.UserProfileId!, Name = "Alpha", Email = a.Email });
        db.UserProfiles.Add(new UserProfile { Id = b.UserProfileId!, Name = "Bravo", Email = b.Email });
        db.VideoImports.AddRange(
            Import("a-nt", a, VideoImportStatus.Failed, "No Korean transcript available for this video.", now.AddDays(-1)),
            Import("a-other", a, VideoImportStatus.Failed, "The exception said SECRET-LEARNER-CONTENT", now.AddDays(-2)),
            Import("a-stuck", a, VideoImportStatus.GeneratingVocabulary, null, now.AddMinutes(-60)),
            Import("a-recent", a, VideoImportStatus.FetchingTranscript, null, now.AddMinutes(-5)),
            Import("a-done", a, VideoImportStatus.Completed, null, now.AddDays(-3)),
            Import("b-cancel", b, VideoImportStatus.Failed, "Import was cancelled.", now.AddDays(-4)));

        // The context refuses a new word with no owner, so the two unowned (legacy) words are
        // recorded to someone and then cleared, as old rows were.
        var unowned = new[] { Word(a, null), Word(a, VocabularyPartOfSpeech.Verb) };
        db.VocabularyWords.AddRange(
            Word(a, VocabularyPartOfSpeech.Noun), Word(a, null), Word(a, null),
            Word(b, VocabularyPartOfSpeech.Unknown));
        db.VocabularyWords.AddRange(unowned);
        await db.SaveChangesAsync();
        var unownedIds = unowned.Select(w => w.Id).ToList();
        await db.VocabularyWords.Where(w => unownedIds.Contains(w.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.UserProfileId, (string?)null));

        return (admin, a, b);
    }

    private CoachUsage Usage(ApplicationUser learner, DateOnly day, int runs, long input, long output, decimal cost) => new()
    {
        Id = Guid.NewGuid().ToString(),
        UserProfileId = learner.UserProfileId!,
        LocalDate = day,
        WeekKey = CoachNormalizedJson.WeekKey(day),
        RunCount = runs,
        InputTokens = input,
        OutputTokens = output,
        EstimatedCostUsd = cost,
        CreatedAt = _factory.Time.GetUtcNow().UtcDateTime,
        UpdatedAt = _factory.Time.GetUtcNow().UtcDateTime
    };

    private static VideoImport Import(
        string id, ApplicationUser learner, VideoImportStatus status, string? error, DateTime created) => new()
    {
        Id = id,
        UserProfileId = learner.UserProfileId,
        VideoId = "AAAAAAAAAAA",
        VideoTitle = SecretText,
        VideoUrl = "https://www.youtube.com/watch?v=AAAAAAAAAAA",
        Status = status,
        ErrorMessage = error,
        CreatedAt = created,
        CompletedAt = status is VideoImportStatus.Failed or VideoImportStatus.Completed ? created.AddMinutes(1) : null
    };

    private static VocabularyWord Word(ApplicationUser? owner, VocabularyPartOfSpeech? partOfSpeech) => new()
    {
        Id = Guid.NewGuid().ToString(),
        UserProfileId = owner?.UserProfileId,
        TargetLanguageTerm = SecretText,
        NativeLanguageTerm = SecretText,
        PartOfSpeech = partOfSpeech
    };
}
