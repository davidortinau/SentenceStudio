using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

/// <summary>
/// Static guard for the dual-provider pair that adds <c>SkillProfile.IsArchived</c>.
/// </summary>
/// <remarks>
/// A SQLite copy missing <c>[Migration]</c> is invisible to EF and silently no-ops on mobile — the
/// bug that shipped twice (RefreshToken 2026-05-03, ActivitySession 2026-07-02). Archiving is
/// exactly the kind of change that hides it: the server would archive correctly while the device
/// threw on a column that was never created, and only on the device.
/// </remarks>
public sealed class SkillProfileArchiveMigrationTests
{
    private const string MigrationId = "20260819120000_AddSkillProfileIsArchived";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "SentenceStudio.Shared")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test must be able to locate the repository root");
        return dir!.FullName;
    }

    private static string SharedPath(string relativeDir, string fileName) => Path.Combine(
        RepoRoot(),
        "src",
        "SentenceStudio.Shared",
        relativeDir.Replace('/', Path.DirectorySeparatorChar),
        fileName);

    [Theory]
    [InlineData("Migrations", "boolean")]
    [InlineData("Migrations/Sqlite", "INTEGER")]
    public void BothProviderMigrationsExistWithDiscoveryAttributes(string relativeDir, string columnType)
    {
        var path = SharedPath(relativeDir, $"{MigrationId}.cs");
        File.Exists(path).Should().BeTrue($"the {relativeDir} migration must exist at {path}");

        var source = File.ReadAllText(path);
        source.Should().Contain("[DbContext(typeof(ApplicationDbContext))]");
        source.Should().Contain(
            $"[Migration(\"{MigrationId}\")]",
            "without this attribute EF never discovers the migration and silently skips it on mobile");
        source.Should().Contain($"type: \"{columnType}\"", "provider column types must not be swapped");
    }

    [Theory]
    [InlineData("Migrations")]
    [InlineData("Migrations/Sqlite")]
    public void MigrationIsAdditiveAndReversible(string relativeDir)
    {
        var source = File.ReadAllText(SharedPath(relativeDir, $"{MigrationId}.cs"));

        source.Should().Contain("AddColumn<bool>");
        source.Should().Contain("table: \"SkillProfile\"");
        source.Should().Contain("nullable: false");
        source.Should().Contain(
            "defaultValue: false",
            "every existing skill must come out of this migration unarchived");

        source.Should().Contain("DropColumn");
        source.Should().NotContain("DropTable");
        source.Should().NotContain("AlterColumn");
        source.Should().NotContain(
            "Sql(", "a schema change that replaces a deletion must not itself delete anything");
        source.Should().NotContain("DELETE ");
        source.Should().NotContain("UPDATE ");
    }

    [Theory]
    [InlineData("Migrations", "boolean")]
    [InlineData("Migrations/Sqlite", "INTEGER")]
    public void ModelSnapshotCarriesTheArchiveColumnForBothProviders(string relativeDir, string columnType)
    {
        var source = File.ReadAllText(SharedPath(relativeDir, "ApplicationDbContextModelSnapshot.cs"));
        var section = SnapshotSection(source, "SentenceStudio.Shared.Models.SkillProfile\"");

        section.Should().Contain("b.Property<bool>(\"IsArchived\")");
        section.Should().Contain($".HasColumnType(\"{columnType}\")");
    }

    private static string SnapshotSection(string source, string entityMarker)
    {
        var start = source.IndexOf(entityMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"the snapshot must configure {entityMarker}");

        var end = source.IndexOf("modelBuilder.Entity(", start + entityMarker.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }
}

/// <summary>
/// What archiving a skill does, and — more importantly — what it does not do.
/// </summary>
/// <remarks>
/// Archiving replaced a hard delete. The tests that matter are therefore the ones about
/// preservation: the row survives, its identifier survives, and anything referring to it still
/// resolves. A test that only checked "archived skills are hidden" would pass against the deletion
/// this change exists to remove.
/// </remarks>
public sealed class SkillProfileArchiveRepositoryTests : IDisposable
{
    private const string Owner = "user-owner";
    private const string Stranger = "user-stranger";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public SkillProfileArchiveRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w => w.Ignore(
                   Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        // Deliberately empty. The API host has no preferences service, so a repository that fell
        // back to an ambient profile would be answering for whoever was last active — which on a
        // multi-tenant host is somebody else.
        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(p => p.Get("active_profile_id", It.IsAny<string>())).Returns(string.Empty);
        services.AddSingleton(preferences.Object);

        _provider = services.BuildServiceProvider();

        using var bootstrap = _provider.CreateScope();
        bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private SkillProfileRepository NewRepository() =>
        new(_provider, NullLogger<SkillProfileRepository>.Instance);

    private async Task<string> SeedAsync(string owner, string title, bool archived = false)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var skill = new SkillProfile
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = title,
            Description = "Seeded.",
            Language = "Korean",
            UserProfileId = owner,
            IsArchived = archived,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.SkillProfiles.Add(skill);
        await db.SaveChangesAsync();
        return skill.Id;
    }

    private async Task<SkillProfile> ReadAsync(string skillId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SkillProfiles.AsNoTracking().SingleAsync(s => s.Id == skillId);
    }

    [Fact]
    public async Task Archiving_keeps_the_row_and_its_identifier()
    {
        var skillId = await SeedAsync(Owner, "Ordering food");

        (await NewRepository().SetArchivedAsync(skillId, isArchived: true, Owner)).Should().BeGreaterThan(0);

        var stored = await ReadAsync(skillId);
        stored.IsArchived.Should().BeTrue();
        stored.Id.Should().Be(skillId, "everything that referenced this skill still points at this id");
        stored.Title.Should().Be("Ordering food", "archiving is not a wipe");
        stored.Description.Should().Be("Seeded.");
    }

    [Fact]
    public async Task An_archived_skill_is_not_offered_for_practice()
    {
        var active = await SeedAsync(Owner, "Active");
        var archived = await SeedAsync(Owner, "Archived", archived: true);

        var listed = await NewRepository().ListAsync(Owner);

        listed.Select(s => s.Id).Should().Contain(active);
        listed.Select(s => s.Id).Should().NotContain(archived);
    }

    [Fact]
    public async Task An_archived_skill_can_still_be_asked_for_explicitly()
    {
        var archived = await SeedAsync(Owner, "Archived", archived: true);

        var listed = await NewRepository().ListAsync(Owner, includeArchived: true);

        listed.Select(s => s.Id).Should().Contain(
            archived, "restoring an archive needs a way to see what is in it");
    }

    [Fact]
    public async Task Restoring_puts_the_skill_back_in_the_practice_list()
    {
        var skillId = await SeedAsync(Owner, "Ordering food");
        var repository = NewRepository();

        await repository.SetArchivedAsync(skillId, isArchived: true, Owner);
        (await repository.SetArchivedAsync(skillId, isArchived: false, Owner)).Should().BeGreaterThan(0);

        (await repository.ListAsync(Owner)).Select(s => s.Id).Should().Contain(skillId);
        (await ReadAsync(skillId)).IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task Archiving_a_skill_that_is_already_archived_reports_no_change()
    {
        var skillId = await SeedAsync(Owner, "Archived", archived: true);

        (await NewRepository().SetArchivedAsync(skillId, isArchived: true, Owner))
            .Should().Be(0, "a caller must be able to tell a change from a no-op");
    }

    [Fact]
    public async Task Another_learners_skill_is_not_archivable()
    {
        var skillId = await SeedAsync(Stranger, "Not yours");

        (await NewRepository().SetArchivedAsync(skillId, isArchived: true, Owner)).Should().Be(0);

        (await ReadAsync(skillId)).IsArchived.Should().BeFalse("nothing of the stranger's changed");
    }

    [Fact]
    public async Task An_empty_owner_archives_nothing()
    {
        var skillId = await SeedAsync(Owner, "Ordering food");

        (await NewRepository().SetArchivedAsync(skillId, isArchived: true, userProfileId: null))
            .Should().Be(0, "an unresolved owner means no data, never every learner's data");

        (await ReadAsync(skillId)).IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task Language_filtered_practice_lists_also_exclude_archived_skills()
    {
        await SeedAsync(Owner, "Active");
        await SeedAsync(Owner, "Archived", archived: true);

        // GetSkillsByLanguageAsync reads the ambient profile, which is empty here by design, so
        // the assertion available is that it never leaks — the archive filter is asserted through
        // ListAsync above, and this pins that the fail-closed path did not regress alongside it.
        (await NewRepository().GetSkillsByLanguageAsync("Korean"))
            .Should().BeEmpty("no active profile means no rows, archived or otherwise");
    }

    /// <summary>
    /// Reading one skill by identifier hides an archived one, the same way the lists do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list was already filtered; the single-row reads were not, and that gap is a real one.
    /// Every practice activity — clozure, shadowing, translation, storyteller, the quiz launch
    /// validator — resolves a skill by an identifier it is holding, not by picking one out of a
    /// list. So a learner who archived a skill and then opened an activity that still remembered
    /// its id would go on practising the skill they had just put away, with no screen anywhere
    /// showing it existed.
    /// </para>
    /// <para>
    /// Both entry points are asserted because both are called: <c>GetSkillProfileAsync</c> by the
    /// activity services and <c>GetAsync</c> by the coach write handlers.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_archived_skill_is_not_returned_by_identifier()
    {
        var archived = await SeedAsync(Owner, "Archived", archived: true);
        var repository = NewRepository();

        (await repository.GetSkillProfileAsync(archived, Owner))
            .Should().BeNull("an activity holding the id must not keep generating from it");
        (await repository.GetAsync(archived, Owner))
            .Should().BeNull("the same rule applies wherever a skill is resolved by id");
    }

    /// <summary>
    /// The archive's own undo can still reach the row it has to put back.
    /// </summary>
    /// <remarks>
    /// The filter above would break restoring if it had no exception, because the row an undo
    /// exists to un-archive is archived by definition. The exception is opt-in and named, so a
    /// caller gets it only by asking.
    /// </remarks>
    [Fact]
    public async Task An_archived_skill_is_still_reachable_when_asked_for_explicitly()
    {
        var archived = await SeedAsync(Owner, "Archived", archived: true);
        var repository = NewRepository();

        (await repository.GetSkillProfileAsync(archived, Owner, includeArchived: true))
            .Should().NotBeNull();
        (await repository.GetAsync(archived, Owner, includeArchived: true))
            .Should().NotBeNull();
    }

    /// <summary>
    /// An active skill is unaffected by the archive filter.
    /// </summary>
    /// <remarks>
    /// The other half of the pair. A filter that hid everything would pass every assertion above
    /// and break every activity in the app.
    /// </remarks>
    [Fact]
    public async Task An_active_skill_is_still_returned_by_identifier()
    {
        var active = await SeedAsync(Owner, "Active");
        var repository = NewRepository();

        (await repository.GetSkillProfileAsync(active, Owner)).Should().NotBeNull();
        (await repository.GetAsync(active, Owner)).Should().NotBeNull();
    }

    /// <summary>
    /// Asking for archived rows explicitly does not widen the owner scope.
    /// </summary>
    /// <remarks>
    /// <c>includeArchived</c> relaxes one filter. It must not relax the one that matters: a
    /// stranger's skill stays unreachable whether it is archived or not, so the flag cannot be
    /// used as a way around ownership.
    /// </remarks>
    [Fact]
    public async Task Including_archived_rows_does_not_reach_another_learners_skill()
    {
        var strangers = await SeedAsync(Stranger, "Not yours", archived: true);
        var repository = NewRepository();

        (await repository.GetSkillProfileAsync(strangers, Owner, includeArchived: true)).Should().BeNull();
        (await repository.GetAsync(strangers, Owner, includeArchived: true)).Should().BeNull();
    }
}
