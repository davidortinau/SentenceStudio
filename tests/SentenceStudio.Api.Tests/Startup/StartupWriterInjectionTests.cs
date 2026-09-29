using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Auth;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Numbers;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Startup;

public sealed class StartupWriterInjectionTests
{
    [Fact]
    public async Task Classification_phrase_backfill_and_number_seed_use_the_supplied_context()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var word = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = "안녕 친구",
            LexicalUnitType = LexicalUnitType.Unknown
        };
        db.VocabularyWords.Add(word);
        await db.SaveChangesAsync();

        // No ApplicationDbContext is registered here: any hidden CreateScope/GetRequiredService
        // path would fail instead of silently writing to a second database.
        using var emptyServices = new ServiceCollection().BuildServiceProvider();
        var backfill = new VocabularyClassificationBackfillService(
            emptyServices, NullLogger<VocabularyClassificationBackfillService>.Instance);
        var guardCalls = 0;
        Action assertSession = () => guardCalls++;

        await backfill.BackfillLexicalUnitTypesAsync(db, assertSession);
        await backfill.BackfillPhraseConstituentsAsync(db, assertSession);
        var seeder = new NumberContentSeeder(db, NullLogger<NumberContentSeeder>.Instance);
        await seeder.SeedAsync("ko", assertSession);

        db.ChangeTracker.Clear();
        (await db.VocabularyWords.SingleAsync(w => w.Id == word.Id))
            .LexicalUnitType.Should().Be(LexicalUnitType.Phrase);
        (await db.NumberContexts.CountAsync()).Should().BeGreaterThan(0);
        guardCalls.Should().BeGreaterThan(3);
    }

    [Fact]
    public async Task Backfill_fence_loss_between_read_and_write_leaves_rows_unmodified()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var word = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = "안녕 친구",
            LexicalUnitType = LexicalUnitType.Unknown
        };
        db.VocabularyWords.Add(word);
        await db.SaveChangesAsync();

        using var emptyServices = new ServiceCollection().BuildServiceProvider();
        var backfill = new VocabularyClassificationBackfillService(
            emptyServices, NullLogger<VocabularyClassificationBackfillService>.Instance);
        var checks = 0;
        Func<Task> run = () => backfill.BackfillLexicalUnitTypesAsync(
            db,
            () =>
            {
                if (++checks == 4)
                {
                    throw new InvalidOperationException("lock lost");
                }
            });

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("lock lost");
        db.ChangeTracker.Clear();
        (await db.VocabularyWords.SingleAsync(w => w.Id == word.Id))
            .LexicalUnitType.Should().Be(LexicalUnitType.Unknown);
    }

    [Fact]
    public async Task Number_seed_fence_loss_before_save_does_not_write_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var seeder = new NumberContentSeeder(db, NullLogger<NumberContentSeeder>.Instance);
        Action assertSession = () =>
        {
            if (db.ChangeTracker.Entries().Any(entry => entry.State == EntityState.Added))
            {
                throw new InvalidOperationException("number seed backend lost");
            }
        };

        Func<Task> seed = () => seeder.SeedAsync("ko", assertSession);
        await seed.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("number seed backend lost");

        db.ChangeTracker.Clear();
        (await db.NumberContexts.CountAsync()).Should().Be(0);
        (await db.NumberSubModes.CountAsync()).Should().Be(0);
        (await db.NumberCounters.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Development_account_seeding_accepts_a_store_on_the_supplied_context()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        using var services = new ServiceCollection()
            .AddLogging()
            .AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .Services.BuildServiceProvider();
        using var store = new UserStore<ApplicationUser, IdentityRole, ApplicationDbContext>(db);
        using var userManager = ActivatorUtilities.CreateInstance<UserManager<ApplicationUser>>(
            services, store);
        var seeder = new DevTestAccountSeeder(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DevTestAccountSeeder>.Instance);
        var guardCalls = 0;

        await seeder.SeedAsync(userManager, db, () => guardCalls++, CancellationToken.None);

        (await db.Users.CountAsync()).Should().Be(DevTestAccountSeeder.Accounts.Length);
        (await db.UserProfiles.CountAsync()).Should().Be(DevTestAccountSeeder.Accounts.Length);
        guardCalls.Should().BeGreaterThan(DevTestAccountSeeder.Accounts.Length);
    }
}
