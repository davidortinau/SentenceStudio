using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

/// <summary>
/// Phase 3 of docs/sync-all-tables-plan.md: custom scenarios and scene images belong to the
/// learner who made them (the web showed every learner's to everyone). Built-in scenarios have
/// fixed IDs and are seeded once. On a device database, rows saved before they had an owner stay
/// visible there (D3).
/// </summary>
public sealed class Phase3OwnerScopeTests : IDisposable
{
    private const string LearnerA = "learner-a";
    private const string LearnerB = "learner-b";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ActiveLearner _learner = new();
    private readonly ServiceProvider _provider;

    public Phase3OwnerScopeTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddSingleton<IUserScopeProvider>(_learner);
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private ScenarioRepository Scenarios() =>
        new(_provider, NullLogger<ScenarioRepository>.Instance);

    private static ConversationScenario Custom(string name) => new()
    {
        Name = name, PersonaName = "P", PersonaDescription = "D", SituationDescription = "S"
    };

    [Fact]
    public async Task BuiltInScenarios_AreAddedOnceUnderFixedIds_AndVisibleToEveryLearner()
    {
        _learner.Id = LearnerA;
        ConversationScenario[] BuiltIns() =>
        [
            new() { Id = SyncIds.BuiltInScenario("First Meeting"), Name = "First Meeting", PersonaName = "P",
                PersonaDescription = "D", SituationDescription = "S", IsPredefined = true },
            new() { Id = SyncIds.BuiltInScenario("Ordering Coffee"), Name = "Ordering Coffee", PersonaName = "P",
                PersonaDescription = "D", SituationDescription = "S", IsPredefined = true },
        ];
        (await Scenarios().AddMissingBuiltInsAsync(BuiltIns())).Should().Be(2);
        (await Scenarios().AddMissingBuiltInsAsync(BuiltIns())).Should().Be(0);

        var builtIns = await Scenarios().GetPredefinedAsync();
        builtIns.Should().HaveCount(2);
        builtIns.Should().OnlyContain(s => SyncIds.IsBuiltInScenario(s.Id) && s.UserProfileId == null);
        (await Scenarios().GetDefaultAsync())!.Name.Should().Be("First Meeting");
        // A built-in is never saved as a learner's scenario.
        (await Scenarios().SaveAsync(builtIns[0])).Should().BeNull();

        _learner.Id = LearnerB;
        (await Scenarios().GetAllAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task CustomScenarios_BelongToTheirCreator_AndAreInvisibleToAnotherLearner()
    {
        _learner.Id = LearnerA;
        var id = await Scenarios().SaveAsync(Custom("My interview"));
        id.Should().NotBeNull();
        (await Scenarios().GetByIdAsync(id!))!.UserProfileId.Should().Be(LearnerA);

        _learner.Id = LearnerB;
        (await Scenarios().GetAllAsync()).Should().BeEmpty();
        (await Scenarios().GetByIdAsync(id!)).Should().BeNull();
        (await Scenarios().FindByNameAsync("interview")).Should().BeNull();
        (await Scenarios().DeleteAsync(id!)).Should().BeFalse();
        var hijack = Custom("Taken over");
        hijack.Id = id!;
        (await Scenarios().SaveAsync(hijack)).Should().BeNull();

        _learner.Id = null;
        (await Scenarios().GetUserScenariosAsync()).Should().BeEmpty();
        (await Scenarios().SaveAsync(Custom("Nobody's"))).Should().BeNull();

        _learner.Id = LearnerA;
        (await Scenarios().GetByIdAsync(id!))!.Name.Should().Be("My interview");
    }

    [Fact]
    public async Task SceneImages_BelongToTheirCreator_AndOlderUnownedImagesStayVisibleOnTheDevice()
    {
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SceneImages.Add(new SceneImage { Id = "older", Url = "https://example.test/old.jpg" });
            await db.SaveChangesAsync();
        }
        var service = new SceneImageService(_provider, NullLogger<SceneImageService>.Instance);

        _learner.Id = LearnerA;
        (await service.SaveAsync(new SceneImage { Url = "https://example.test/a.jpg" })).Should().Be(1);
        var mine = await service.ListAsync();
        mine.Select(i => i.Url).Should().BeEquivalentTo(
            ["https://example.test/a.jpg", "https://example.test/old.jpg"]);
        mine.Single(i => i.Url!.EndsWith("a.jpg")).UserProfileId.Should().Be(LearnerA);

        _learner.Id = LearnerB;
        (await service.ListAsync()).Select(i => i.Url).Should().Equal("https://example.test/old.jpg");
        (await service.DeleteAsync(mine.Single(i => i.Url!.EndsWith("a.jpg")))).Should().Be(0);

        _learner.Id = null;
        (await service.ListAsync()).Should().BeEmpty();
        (await service.SaveAsync(new SceneImage { Url = "https://example.test/x.jpg" })).Should().Be(-1);
    }

    private sealed class ActiveLearner : IUserScopeProvider
    {
        public string? Id { get; set; }
        public string UserProfileId => Id ?? throw new UnauthorizedAccessException();
        public bool TryGetUserProfileId(out string userProfileId)
        {
            userProfileId = Id ?? string.Empty;
            return !string.IsNullOrEmpty(Id);
        }
    }
}
