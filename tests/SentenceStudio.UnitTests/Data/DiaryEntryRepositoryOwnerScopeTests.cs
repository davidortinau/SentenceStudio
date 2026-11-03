using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

/// <summary>
/// Backport B5: the shared diary repository updated an entry and saved AI feedback by id
/// without checking who owned the row. Writes are now bound to the active profile and
/// refuse an absent owner or another owner's entry, leaving that entry unchanged.
/// </summary>
public sealed class DiaryEntryRepositoryOwnerScopeTests : IDisposable
{
    private const string UserA = "diary-user-a";
    private const string UserB = "diary-user-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private string _activeUserId = UserA;

    public DiaryEntryRepositoryOwnerScopeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(p => p.Get("active_profile_id", It.IsAny<string>())).Returns(() => _activeUserId);
        services.AddSingleton(preferences.Object);
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddSingleton<DiaryEntryRepository>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        db.UserProfiles.AddRange(
            new UserProfile { Id = UserA, Name = UserA, CreatedAt = DateTime.UtcNow },
            new UserProfile { Id = UserB, Name = UserB, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    private DiaryEntryRepository Repository => _provider.GetRequiredService<DiaryEntryRepository>();

    [Fact]
    public async Task Upsert_NewEntry_IsOwnedByTheActiveProfile()
    {
        var saved = await Repository.UpsertAsync(new DiaryEntry
        {
            EntryDate = DateTime.UtcNow, Language = "Korean", Content = "오늘은 좋아요"
        });

        var stored = await FindAsync(saved.Id);
        stored!.UserProfileId.Should().Be(UserA);
        stored.WordCount.Should().Be(2);
    }

    [Fact]
    public async Task Upsert_WithoutActiveProfile_ThrowsAndWritesNothing()
    {
        _activeUserId = string.Empty;

        var act = () => Repository.UpsertAsync(new DiaryEntry
        {
            EntryDate = DateTime.UtcNow, Language = "Korean", Content = "아무도"
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Upsert_OverAnotherOwnersEntryId_IsRefusedAndLeavesItUnchanged()
    {
        var foreign = await SeedAsync(UserB, "B의 일기");

        // The caller does not set an owner, as the editor page does for a new entry.
        var act = () => Repository.UpsertAsync(new DiaryEntry
        {
            Id = foreign.Id, EntryDate = DateTime.UtcNow, Language = "Korean", Content = "overwrite"
        });

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        var stored = await FindAsync(foreign.Id);
        stored!.UserProfileId.Should().Be(UserB);
        stored.Content.Should().Be("B의 일기");
    }

    [Fact]
    public async Task Upsert_EntryClaimingAnotherOwner_IsRefused()
    {
        var act = () => Repository.UpsertAsync(new DiaryEntry
        {
            UserProfileId = UserB, EntryDate = DateTime.UtcNow, Language = "Korean", Content = "x"
        });

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Upsert_OwnEntry_UpdatesIt()
    {
        var own = await SeedAsync(UserA, "처음");
        var loaded = await Repository.GetByIdAsync(own.Id);
        loaded!.Content = "두 번째 글";

        await Repository.UpsertAsync(loaded);

        (await FindAsync(own.Id))!.Content.Should().Be("두 번째 글");
    }

    [Fact]
    public async Task SaveFeedback_OnAnotherOwnersEntry_ReturnsNullAndLeavesItUnchanged()
    {
        var foreign = await SeedAsync(UserB, "B의 일기");

        var result = await Repository.SaveFeedbackAsync(foreign.Id, "rec", "notes", "strengths");

        result.Should().BeNull();
        var stored = await FindAsync(foreign.Id);
        stored!.FeedbackRecommended.Should().BeNull();
        stored.FeedbackAt.Should().BeNull();
    }

    [Fact]
    public async Task SaveFeedback_WithoutActiveProfile_ReturnsNull()
    {
        var own = await SeedAsync(UserA, "내 일기");
        _activeUserId = string.Empty;

        (await Repository.SaveFeedbackAsync(own.Id, "rec", "notes", "strengths")).Should().BeNull();
        (await FindAsync(own.Id))!.FeedbackAt.Should().BeNull();
    }

    [Fact]
    public async Task SaveFeedback_OnOwnEntry_StoresIt()
    {
        var own = await SeedAsync(UserA, "내 일기");

        var result = await Repository.SaveFeedbackAsync(own.Id, "rec", "notes", "strengths");

        result!.FeedbackRecommended.Should().Be("rec");
        (await FindAsync(own.Id))!.FeedbackNotes.Should().Be("notes");
    }

    [Fact]
    public async Task GetByDate_WithoutActiveProfile_DoesNotReturnAnUnownedRow()
    {
        await SeedAsync(string.Empty, "unowned");
        _activeUserId = string.Empty;

        (await Repository.GetByDateAsync(DateTime.UtcNow, "Korean")).Should().BeNull();
    }

    private async Task<DiaryEntry> SeedAsync(string owner, string content)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entry = new DiaryEntry
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = owner,
            EntryDate = DiaryEntryRepository.NormalizeEntryDate(DateTime.UtcNow),
            Language = "Korean",
            Content = content,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.DiaryEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    private async Task<DiaryEntry?> FindAsync(string id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DiaryEntries.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id);
    }

    private async Task<int> CountAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DiaryEntries.CountAsync();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}
