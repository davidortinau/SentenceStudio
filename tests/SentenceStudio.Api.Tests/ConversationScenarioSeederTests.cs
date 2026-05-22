using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Conversation;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests;

public class ConversationScenarioSeederTests : IDisposable
{
    private readonly ApplicationDbContext _db;

    public ConversationScenarioSeederTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    private ConversationScenarioSeeder CreateSeeder() =>
        new(_db, NullLogger<ConversationScenarioSeeder>.Instance);

    [Fact]
    public async Task SeedAsync_EmptyDatabase_InsertsAllPredefinedScenarios()
    {
        var expected = ConversationScenarioSeedData.GetPredefinedScenarios();

        await CreateSeeder().SeedAsync();

        var stored = await _db.ConversationScenarios.AsNoTracking().ToListAsync();
        Assert.Equal(expected.Count, stored.Count);
        Assert.All(stored, s => Assert.True(s.IsPredefined));
        foreach (var seed in expected)
        {
            Assert.Contains(stored, s => s.Name == seed.Name && s.PersonaName == seed.PersonaName);
        }
    }

    [Fact]
    public async Task SeedAsync_RunTwice_IsIdempotent_NoDuplicates()
    {
        await CreateSeeder().SeedAsync();
        var firstCount = await _db.ConversationScenarios.CountAsync();

        // Detach so the second seeder reads fresh from DB
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;

        await CreateSeeder().SeedAsync();

        var secondCount = await _db.ConversationScenarios.CountAsync();
        Assert.Equal(firstCount, secondCount);
        Assert.Equal(ConversationScenarioSeedData.GetPredefinedScenarios().Count, secondCount);
    }

    [Fact]
    public async Task SeedAsync_ExistingPredefinedWithStaleData_UpdatesInPlace()
    {
        var first = ConversationScenarioSeedData.GetPredefinedScenarios().First();

        var stale = new ConversationScenario
        {
            Name = first.Name,
            NameKorean = first.NameKorean,
            PersonaName = "STALE",
            PersonaDescription = "stale description",
            SituationDescription = "stale situation",
            ConversationType = first.ConversationType,
            QuestionBank = "stale",
            IsPredefined = true,
            CreatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _db.ConversationScenarios.Add(stale);
        await _db.SaveChangesAsync();
        var staleId = stale.Id;

        foreach (var entry in _db.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;

        await CreateSeeder().SeedAsync();

        var refreshed = await _db.ConversationScenarios.AsNoTracking().FirstAsync(s => s.Id == staleId);
        Assert.Equal(first.PersonaName, refreshed.PersonaName);
        Assert.Equal(first.PersonaDescription, refreshed.PersonaDescription);
        Assert.Equal(first.SituationDescription, refreshed.SituationDescription);
        Assert.Equal(first.QuestionBank, refreshed.QuestionBank);
        Assert.True(refreshed.UpdatedAt > new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var total = await _db.ConversationScenarios.CountAsync();
        Assert.Equal(ConversationScenarioSeedData.GetPredefinedScenarios().Count, total);
    }

    [Fact]
    public async Task SeedAsync_PartialState_FillsInMissingScenarios()
    {
        var seedList = ConversationScenarioSeedData.GetPredefinedScenarios();
        var existing = seedList.First();
        _db.ConversationScenarios.Add(new ConversationScenario
        {
            Name = existing.Name,
            NameKorean = existing.NameKorean,
            PersonaName = existing.PersonaName,
            PersonaDescription = existing.PersonaDescription,
            SituationDescription = existing.SituationDescription,
            ConversationType = existing.ConversationType,
            QuestionBank = existing.QuestionBank,
            IsPredefined = true
        });
        await _db.SaveChangesAsync();

        foreach (var entry in _db.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;

        await CreateSeeder().SeedAsync();

        var total = await _db.ConversationScenarios.CountAsync();
        Assert.Equal(seedList.Count, total);
    }

    [Fact]
    public async Task SeedAsync_DoesNotTouchUserCreatedScenarios()
    {
        _db.ConversationScenarios.Add(new ConversationScenario
        {
            Name = "My Custom Scenario",
            PersonaName = "Custom",
            PersonaDescription = "custom",
            SituationDescription = "custom",
            ConversationType = ConversationType.OpenEnded,
            IsPredefined = false
        });
        await _db.SaveChangesAsync();

        foreach (var entry in _db.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;

        await CreateSeeder().SeedAsync();

        var userCreated = await _db.ConversationScenarios
            .AsNoTracking()
            .Where(s => !s.IsPredefined)
            .ToListAsync();
        Assert.Single(userCreated);
        Assert.Equal("My Custom Scenario", userCreated[0].Name);

        var predefined = await _db.ConversationScenarios.CountAsync(s => s.IsPredefined);
        Assert.Equal(ConversationScenarioSeedData.GetPredefinedScenarios().Count, predefined);
    }
}
