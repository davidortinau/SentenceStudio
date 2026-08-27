using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// What the learner is told before and after a protected change, checked against what the app can
/// actually do.
/// </summary>
/// <remarks>
/// <para>
/// Consent copy is a promise, and a promise the product cannot keep is worse than no promise: the
/// learner confirms on the strength of a safety net, and the net is not there. The archive
/// preview said "Nothing is deleted, and you can restore it" and the receipt said "You can restore
/// it from your skills". Neither was true after the undo window closed. <c>Skills.razor</c> lists
/// through <c>SkillProfileRepository.ListAsync</c>, which excludes archived rows; <c>SkillEdit
/// .razor</c> offers delete and nothing else; there is no archived-skills view and no restore
/// control anywhere. The only reversal that exists is the ledger's own undo, and it is bounded.
/// </para>
/// <para>
/// So these tests assert two things: the words that promised the missing screen are gone, and the
/// words that describe the bounded reversal are present and agree with the window the ledger
/// actually enforces. They go through the real handler, so the strings under test are the ones the
/// learner reads rather than a copy of them.
/// </para>
/// </remarks>
public sealed class CoachSkillArchiveConsentCopyTests : IDisposable
{
    private const string Owner = "user-owner";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;

    public CoachSkillArchiveConsentCopyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w => w.Ignore(
                   Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        _provider = services.BuildServiceProvider();

        using var bootstrap = _provider.CreateScope();
        bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();

        _db = _provider.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private CoachSkillArchiveHandler NewHandler() =>
        new(
            new SkillProfileRepository(_provider, NullLogger<SkillProfileRepository>.Instance),
            new CoachWriteOwnership(_db));

    private async Task<string> SeedSkillAsync(string title = "Ordering food")
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var skill = new SkillProfile
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = title,
            Description = "Seeded.",
            Language = "Korean",
            UserProfileId = Owner,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.SkillProfiles.Add(skill);
        await db.SaveChangesAsync();
        return skill.Id;
    }

    private static string Json<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, CoachNormalizedJson.Options);

    private static string Text(string summary, IReadOnlyList<string> lines) =>
        summary + "\n" + string.Join("\n", lines);

    /// <summary>Every restore promise the product cannot keep.</summary>
    /// <remarks>
    /// The exact phrases that shipped, plus the fragment they share. Kept as literal strings
    /// rather than a looser pattern because the honest copy legitimately mentions the learner's
    /// skills list, and a pattern broad enough to catch "from your skills" would fail on the
    /// sentence that tells the truth.
    /// </remarks>
    public static TheoryData<string> BrokenPromises() =>
    [
        "Nothing is deleted, and you can restore it.",
        "You can restore it from your skills.",
        "restore it from your skills",
        "you can restore it"
    ];

    [Theory]
    [MemberData(nameof(BrokenPromises))]
    public async Task The_preview_does_not_promise_a_restore_screen(string promise)
    {
        var skillId = await SeedSkillAsync();

        var preview = await ((ICoachWriteHandler)NewHandler())
            .PrepareAsync(Owner, Json(new CoachSkillArchiveArgs(skillId)), CancellationToken.None);

        Text(preview.Summary, preview.Lines).Should().NotContain(
            promise, "the app has no archived-skills view to restore from");
    }

    [Theory]
    [MemberData(nameof(BrokenPromises))]
    public async Task The_receipt_does_not_promise_a_restore_screen(string promise)
    {
        var skillId = await SeedSkillAsync();
        var handler = (ICoachWriteHandler)NewHandler();

        var execution = await handler.ExecuteAsync(
            Owner, Json(new CoachSkillArchiveArgs(skillId)), CancellationToken.None);

        Text(execution.Summary, execution.Lines).Should().NotContain(promise);
    }

    /// <summary>
    /// The consent copy states the bound, and states the bound the ledger enforces.
    /// </summary>
    /// <remarks>
    /// The window is read from <see cref="CoachWriteLimits.UndoWindow"/> rather than typed into
    /// the assertion, so shortening the window cannot leave a prompt promising the old one and a
    /// test agreeing with the prompt.
    /// </remarks>
    [Fact]
    public async Task The_preview_states_the_bounded_undo_window()
    {
        var skillId = await SeedSkillAsync();

        var preview = await ((ICoachWriteHandler)NewHandler())
            .PrepareAsync(Owner, Json(new CoachSkillArchiveArgs(skillId)), CancellationToken.None);

        var text = Text(preview.Summary, preview.Lines);

        text.Should().Contain($"{(int)CoachWriteLimits.UndoWindow.TotalMinutes} minutes");
        text.Should().Contain("undo", "the learner is told the reversal exists");
        text.Should().Contain(
            "no way to bring it back", "the learner has to know the reversal is only temporary");
    }

    [Fact]
    public async Task The_receipt_states_the_bounded_undo_window()
    {
        var skillId = await SeedSkillAsync();

        var execution = await ((ICoachWriteHandler)NewHandler())
            .ExecuteAsync(Owner, Json(new CoachSkillArchiveArgs(skillId)), CancellationToken.None);

        var text = Text(execution.Summary, execution.Lines);

        text.Should().Contain($"{(int)CoachWriteLimits.UndoWindow.TotalMinutes} minutes");
        text.Should().Contain("no way to bring it back");
    }

    /// <summary>
    /// The copy still says the thing that is true: the skill is kept.
    /// </summary>
    /// <remarks>
    /// Correcting an over-promise must not turn into an under-promise. Archiving replaced a hard
    /// delete, and a learner who thought this deleted their skill would decline a change that is
    /// far safer than the one it replaced.
    /// </remarks>
    [Fact]
    public async Task The_preview_still_says_the_skill_is_kept()
    {
        var skillId = await SeedSkillAsync();

        var preview = await ((ICoachWriteHandler)NewHandler())
            .PrepareAsync(Owner, Json(new CoachSkillArchiveArgs(skillId)), CancellationToken.None);

        var text = Text(preview.Summary, preview.Lines);

        text.Should().Contain("kept, not deleted");
        text.Should().Contain("hidden");
    }

    [Fact]
    public async Task The_receipt_still_says_nothing_was_deleted()
    {
        var skillId = await SeedSkillAsync();

        var execution = await ((ICoachWriteHandler)NewHandler())
            .ExecuteAsync(Owner, Json(new CoachSkillArchiveArgs(skillId)), CancellationToken.None);

        Text(execution.Summary, execution.Lines).Should().Contain("Nothing was deleted");
    }
}

/// <summary>
/// The preference tool's own surface, with no setting approved for change.
/// </summary>
/// <remarks>
/// RFC §6.5 pins the V1 allow-list at the empty set. Emptying the list is only half the job: the
/// argument description and the registry description are read by the model, and a description that
/// still advertises six settable fields would have Sam offering a change it cannot make and
/// blaming the learner's phrasing when it fails.
/// </remarks>
public class CoachPreferenceAllowListTests
{
    [Fact]
    public void No_setting_is_approved_for_change()
    {
        CoachPreferenceChangeHandler.IsClosed.Should().BeTrue(
            "RFC 6.5 keeps the V1 allow-list empty until Captain approves a specific field");
    }

    /// <summary>
    /// The candidate list is the RFC's, so approving one is a one-line change to a reviewed set.
    /// </summary>
    [Fact]
    public void The_candidate_settings_are_the_documented_ones() =>
        CoachPreferenceChangeHandler.CandidateNames.Should().BeEquivalentTo(
        [
            "target_language", "native_language", "display_language",
            "session_minutes", "cefr_level", "quiz_show_text_with_photo"
        ]);

    /// <summary>
    /// The model is told the tool declines, rather than being given a menu.
    /// </summary>
    /// <remarks>
    /// The argument description used to name every candidate. A model reading that will keep
    /// choosing one and reporting the refusal as a mistake the learner made. Naming none of them
    /// and saying plainly that nothing is changeable is the truthful prompt.
    /// </remarks>
    [Theory]
    [InlineData("target_language")]
    [InlineData("native_language")]
    [InlineData("display_language")]
    [InlineData("session_minutes")]
    [InlineData("cefr_level")]
    [InlineData("quiz_show_text_with_photo")]
    public void The_argument_description_does_not_advertise_a_settable_field(string candidate)
    {
        var description = typeof(CoachPreferenceChangeArgs)
            .GetProperty(nameof(CoachPreferenceChangeArgs.Setting))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: true)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;

        description.Should().NotContain(candidate);
        description.Should().Contain("declines");
    }
}
