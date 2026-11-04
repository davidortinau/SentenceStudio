using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

/// <summary>
/// SaveAsync wrote a row with an empty owner when neither the item nor the active profile
/// named one (docs/native-port-backports.md, found while backporting). No owner now means no write.
/// </summary>
public sealed class UserActivityRepositoryOwnerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _provider;
    private string _activeUserId = string.Empty;

    public UserActivityRepositoryOwnerTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(p => p.Get("active_profile_id", It.IsAny<string>())).Returns(() => _activeUserId);
        services.AddSingleton(preferences.Object);
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddLogging();
        services.AddScoped<UserActivityRepository>();
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task NoOwnerAndNoActiveProfile_WritesNothing()
    {
        var saved = await Repository().SaveAsync(new UserActivity { Activity = "Reading", Input = "Dictionary lookup: 바다" });

        saved.Should().BeNegative();
        (await Rows()).Should().BeEmpty();
    }

    [Fact]
    public async Task NoOwnerOnTheItem_UsesTheActiveProfile()
    {
        _activeUserId = "owner-a";

        var saved = await Repository().SaveAsync(new UserActivity { Activity = "Reading", Input = "Dictionary lookup: 바다" });

        saved.Should().Be(1);
        (await Rows()).Should().ContainSingle().Which.UserProfileId.Should().Be("owner-a");
    }

    [Fact]
    public async Task TheItemsOwner_IsKept()
    {
        _activeUserId = "owner-a";

        await Repository().SaveAsync(new UserActivity { Activity = "Reading", UserProfileId = "owner-b" });

        (await Rows()).Should().ContainSingle().Which.UserProfileId.Should().Be("owner-b");
    }

    private UserActivityRepository Repository() =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<UserActivityRepository>();

    private async Task<List<UserActivity>> Rows()
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .UserActivities.AsNoTracking().ToListAsync();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}
