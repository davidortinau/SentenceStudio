using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

/// <summary>
/// The legacy Conversation activity used to query <c>Conversation</c> and
/// <c>ConversationChunk</c> with no owner predicate at all, which leaked whole
/// transcripts between accounts on the multi-tenant web head. These tests pin
/// the corrected behavior: every path is owner-scoped, an unresolved owner
/// means "no data", and ownerless legacy rows stay hidden and untouched rather
/// than being claimed by whoever logs in next.
/// </summary>
public sealed class ConversationOwnerScopingTests : IDisposable
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly CollectingLoggerProvider _logs;
    private readonly SwitchablePreferencesService _preferences;

    public ConversationOwnerScopingTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _preferences = new SwitchablePreferencesService();
        _logs = new CollectingLoggerProvider();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(_logs);
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        services.AddSingleton<IPreferencesService>(_preferences);
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddSingleton<ConversationRepository>();

        _provider = services.BuildServiceProvider();

        using var bootstrap = _provider.CreateScope();
        var db = bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
    }

    private ConversationRepository Repository => _provider.GetRequiredService<ConversationRepository>();

    private void SignIn(string? userId) => _preferences.ActiveProfileId = userId ?? string.Empty;

    // --- 1. No active user means no data, never all data ----------------------

    [Fact]
    public async Task Reads_WithNoActiveUser_ReturnEmptyAndWarn()
    {
        SeedConversation("owned-by-a", UserA);
        SignIn(null);

        (await Repository.GetAllConversationsAsync()).Should().BeEmpty();
        (await Repository.GetMostRecentConversationAsync()).Should().BeNull();
        (await Repository.GetConversationAsync("owned-by-a")).Should().BeNull(
            "an unresolved owner must never fall through to an unfiltered query");
        (await Repository.GetConversationChunksAsync("owned-by-a")).Should().BeEmpty();

        _logs.HasWarningContaining("without an active user").Should().BeTrue();
    }

    [Fact]
    public async Task Writes_WithNoActiveUser_AreRefused()
    {
        SignIn(null);

        var saved = await Repository.SaveConversationAsync(new Conversation { Language = "ko" });
        saved.Should().BeNull("a write with no owner would create an unattributable row");

        var chunkSaved = await Repository.SaveConversationChunkAsync(new ConversationChunk
        {
            ConversationId = "anything",
            Text = "hello"
        });
        chunkSaved.Should().BeFalse();

        (await Repository.DeleteConversationAsync("anything")).Should().BeFalse();

        await using var db = NewContext();
        (await db.Conversations.CountAsync()).Should().Be(0);
        (await db.ConversationChunks.CountAsync()).Should().Be(0);
    }

    // --- 2. Writes stamp the owner -------------------------------------------

    [Fact]
    public async Task SaveConversation_StampsActiveOwner()
    {
        SignIn(UserA);

        var id = await Repository.SaveConversationAsync(new Conversation { Language = "ko" });

        id.Should().NotBeNullOrWhiteSpace();
        await using var db = NewContext();
        var stored = await db.Conversations.SingleAsync();
        stored.UserProfileId.Should().Be(UserA);
        stored.CreatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task SaveConversationChunk_StampsActiveOwner()
    {
        SignIn(UserA);
        var conversationId = await Repository.SaveConversationAsync(new Conversation { Language = "ko" });

        var saved = await Repository.SaveConversationChunkAsync(new ConversationChunk
        {
            ConversationId = conversationId!,
            Text = "안녕하세요"
        });

        saved.Should().BeTrue();
        await using var db = NewContext();
        (await db.ConversationChunks.SingleAsync()).UserProfileId.Should().Be(UserA);
    }

    // --- 3. Cross-account isolation ------------------------------------------

    [Fact]
    public async Task Reads_AreIsolatedBetweenAccounts()
    {
        SeedConversation("a-1", UserA, chunkText: "a secret");
        SeedConversation("b-1", UserB, chunkText: "b secret");

        SignIn(UserA);
        var forA = await Repository.GetAllConversationsAsync();
        forA.Should().ContainSingle().Which.Id.Should().Be("a-1");
        (await Repository.GetConversationAsync("b-1")).Should().BeNull();
        (await Repository.GetConversationChunksAsync("b-1")).Should().BeEmpty(
            "chunks are filtered on owner as well as parent, so a known id is not enough");

        SignIn(UserB);
        var forB = await Repository.GetAllConversationsAsync();
        forB.Should().ContainSingle().Which.Id.Should().Be("b-1");
        (await Repository.GetConversationAsync("a-1")).Should().BeNull();
    }

    [Fact]
    public async Task Update_CannotTargetAnotherAccountsConversation()
    {
        SeedConversation("b-1", UserB);
        SignIn(UserA);

        var result = await Repository.SaveConversationAsync(new Conversation
        {
            Id = "b-1",
            Language = "hijacked",
            CreatedAt = DateTime.UtcNow
        });

        result.Should().BeNull();
        await using var db = NewContext();
        var stored = await db.Conversations.SingleAsync(c => c.Id == "b-1");
        stored.Language.Should().NotBe("hijacked");
        stored.UserProfileId.Should().Be(UserB, "an update must never re-own a row");
    }

    [Fact]
    public async Task Chunk_CannotBeAttachedToAnotherAccountsConversation()
    {
        SeedConversation("b-1", UserB);
        SignIn(UserA);

        var saved = await Repository.SaveConversationChunkAsync(new ConversationChunk
        {
            ConversationId = "b-1",
            Text = "injected"
        });

        saved.Should().BeFalse();
        await using var db = NewContext();
        (await db.ConversationChunks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Delete_CannotTargetAnotherAccountsConversation()
    {
        SeedConversation("b-1", UserB, chunkText: "b secret");
        SignIn(UserA);

        (await Repository.DeleteConversationAsync("b-1")).Should().BeFalse();

        await using var db = NewContext();
        (await db.Conversations.CountAsync(c => c.Id == "b-1")).Should().Be(1);
        (await db.ConversationChunks.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Delete_RemovesOwnConversationAndItsChunks()
    {
        SeedConversation("a-1", UserA, chunkText: "mine");
        SeedConversation("b-1", UserB, chunkText: "theirs");
        SignIn(UserA);

        (await Repository.DeleteConversationAsync("a-1")).Should().BeTrue();

        await using var db = NewContext();
        (await db.Conversations.Select(c => c.Id).ToListAsync()).Should().BeEquivalentTo(new[] { "b-1" });
        (await db.ConversationChunks.CountAsync()).Should().Be(1, "only the owner's chunks are removed");
    }

    /// <summary>
    /// Two accounts can hold the same conversation id (ids are client-generated
    /// GUIDs and sync can carry them anywhere). Owner filtering, not id
    /// uniqueness, is what keeps them apart.
    /// </summary>
    [Fact]
    public async Task SameConversationId_DoesNotCrossAccounts()
    {
        const string sharedId = "shared-id";
        SeedConversation(sharedId, UserB, chunkText: "b private");
        SignIn(UserA);

        (await Repository.GetConversationAsync(sharedId)).Should().BeNull();
        (await Repository.GetConversationChunksAsync(sharedId)).Should().BeEmpty();
        (await Repository.GetAllConversationsAsync()).Should().BeEmpty();
    }

    // --- 4. Ownerless legacy rows: hidden, preserved, never claimed -----------

    [Fact]
    public async Task LegacyOwnerlessRows_AreHiddenFromEveryUser()
    {
        SeedConversation("legacy-1", userProfileId: null, chunkText: "legacy text");

        SignIn(UserA);
        (await Repository.GetAllConversationsAsync()).Should().BeEmpty();
        (await Repository.GetConversationAsync("legacy-1")).Should().BeNull();
        (await Repository.GetConversationChunksAsync("legacy-1")).Should().BeEmpty();
        (await Repository.GetMostRecentConversationAsync()).Should().BeNull();

        SignIn(UserB);
        (await Repository.GetAllConversationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task LegacyOwnerlessRows_AreNotClaimedByUpdateOrDelete()
    {
        SeedConversation("legacy-1", userProfileId: null, chunkText: "legacy text");
        SignIn(UserA);

        var updated = await Repository.SaveConversationAsync(new Conversation
        {
            Id = "legacy-1",
            Language = "ko",
            CreatedAt = DateTime.UtcNow
        });
        updated.Should().BeNull("an update is not a claim mechanism");

        (await Repository.DeleteConversationAsync("legacy-1")).Should().BeFalse();

        await using var db = NewContext();
        var stored = await db.Conversations.SingleAsync(c => c.Id == "legacy-1");
        stored.UserProfileId.Should().BeNull("legacy rows keep their unknown ownership");
        (await db.ConversationChunks.CountAsync(cc => cc.UserProfileId == null)).Should().Be(1);
    }

    // --- 5. Export / delete contributor --------------------------------------

    [Fact]
    public async Task ExportOwned_ReturnsOnlyThatUsersConversations()
    {
        SeedConversation("a-1", UserA, chunkText: "mine");
        SeedConversation("b-1", UserB, chunkText: "theirs");
        SeedConversation("legacy-1", userProfileId: null, chunkText: "legacy");

        var export = await ((IConversationOwnerDataService)Repository).ExportOwnedAsync(UserA);

        export.Conversations.Should().ContainSingle().Which.Id.Should().Be("a-1");
        export.Conversations[0].Chunks.Should().ContainSingle().Which.Text.Should().Be("mine");
    }

    [Fact]
    public async Task ExportOwned_WithEmptyUserId_ReturnsNothingAndWarns()
    {
        SeedConversation("a-1", UserA);

        var export = await ((IConversationOwnerDataService)Repository).ExportOwnedAsync(string.Empty);

        export.Conversations.Should().BeEmpty();
        _logs.HasWarningContaining("ExportOwnedAsync").Should().BeTrue();
    }

    [Fact]
    public async Task DeleteOwned_RemovesOnlyThatUsersRowsAndLeavesLegacyRows()
    {
        SeedConversation("a-1", UserA, chunkText: "mine");
        SeedConversation("b-1", UserB, chunkText: "theirs");
        SeedConversation("legacy-1", userProfileId: null, chunkText: "legacy");

        var result = await ((IConversationOwnerDataService)Repository).DeleteOwnedAsync(UserA);

        result.ConversationsDeleted.Should().Be(1);
        result.ChunksDeleted.Should().Be(1);

        await using var db = NewContext();
        (await db.Conversations.Select(c => c.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { "b-1", "legacy-1" },
                "account deletion removes owned rows only; unattributed rows are not this user's to delete");
    }

    [Fact]
    public async Task DeleteOwned_WithEmptyUserId_DeletesNothingAndWarns()
    {
        SeedConversation("a-1", UserA, chunkText: "mine");

        var result = await ((IConversationOwnerDataService)Repository).DeleteOwnedAsync("   ");

        result.ConversationsDeleted.Should().Be(0);
        _logs.HasWarningContaining("DeleteOwnedAsync").Should().BeTrue();

        await using var db = NewContext();
        (await db.Conversations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UnownedDiagnostics_CountsLegacyRowsWithoutExposingThem()
    {
        SeedConversation("a-1", UserA, chunkText: "mine");
        SeedConversation("legacy-1", userProfileId: null, chunkText: "legacy");
        SeedConversation("legacy-2", userProfileId: null);

        var diagnostics = await ((IConversationOwnerDataService)Repository).GetUnownedDiagnosticsAsync();

        diagnostics.UnownedConversations.Should().Be(2);
        diagnostics.UnownedChunks.Should().Be(1);
    }

    // --- 6. Logs carry no user ids and no conversation content ---------------

    [Fact]
    public async Task Warnings_ContainNeitherUserIdsNorConversationContent()
    {
        SeedConversation("b-1", UserB, chunkText: "sensitive transcript text");

        SignIn(null);
        await Repository.GetAllConversationsAsync();

        SignIn(UserA);
        await Repository.SaveConversationAsync(new Conversation { Id = "b-1", Language = "ko" });
        await Repository.SaveConversationChunkAsync(new ConversationChunk
        {
            ConversationId = "b-1",
            Text = "sensitive transcript text"
        });
        await Repository.DeleteConversationAsync("b-1");

        var warnings = _logs.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        warnings.Should().NotBeEmpty();

        foreach (var entry in warnings)
        {
            entry.Message.Should().NotContain("sensitive transcript text");
            entry.Message.Should().NotContain(UserA);
            entry.Message.Should().NotContain(UserB);
            entry.Message.Should().NotContain("b-1");
        }
    }

    // --- helpers -------------------------------------------------------------

    private ApplicationDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new ApplicationDbContext(options);
    }

    private void SeedConversation(string id, string? userProfileId, string? chunkText = null)
    {
        using var db = NewContext();
        db.Conversations.Add(new Conversation
        {
            Id = id,
            Language = "ko",
            CreatedAt = DateTime.UtcNow,
            UserProfileId = userProfileId
        });

        if (chunkText is not null)
        {
            db.ConversationChunks.Add(new ConversationChunk
            {
                Id = $"{id}-chunk",
                ConversationId = id,
                Text = chunkText,
                SentTime = DateTime.UtcNow,
                UserProfileId = userProfileId
            });
        }

        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Stands in for the host's claim-derived <c>active_profile_id</c>
    /// preference so a test can switch accounts mid-run.
    /// </summary>
    private sealed class SwitchablePreferencesService : IPreferencesService
    {
        public string ActiveProfileId { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue)
        {
            if (key == "active_profile_id" && ActiveProfileId is T typed)
            {
                return typed;
            }

            return defaultValue;
        }

        public void Set<T>(string key, T value)
        {
        }

        public void Remove(string key)
        {
        }

        public void Clear()
        {
        }
    }
}

/// <summary>
/// On hosts that register <see cref="SentenceStudio.Services.Plans.IUserScopeProvider"/>
/// (the API and the MAUI device session) it is the authoritative owner source
/// and must win over the preference fallback.
/// </summary>
public sealed class ConversationOwnerScopeProviderPrecedenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public ConversationOwnerScopeProviderPrecedenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddLogging();
        services.AddSingleton<SentenceStudio.Services.Plans.IUserScopeProvider>(
            new StubUserScopeProvider("scope-user"));
        services.AddSingleton<IPreferencesService>(new StalePreferencesService("stale-user"));
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddSingleton<ConversationRepository>();

        _provider = services.BuildServiceProvider();

        using var bootstrap = _provider.CreateScope();
        bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task ScopeProviderWinsOverPreferenceFallback()
    {
        var repository = _provider.GetRequiredService<ConversationRepository>();

        var id = await repository.SaveConversationAsync(new Conversation { Language = "ko" });

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ApplicationDbContext(options);
        (await db.Conversations.SingleAsync(c => c.Id == id)).UserProfileId
            .Should().Be("scope-user", "a stale preference must never outrank the request scope");
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private sealed class StubUserScopeProvider : SentenceStudio.Services.Plans.IUserScopeProvider
    {
        private readonly string _userProfileId;

        public StubUserScopeProvider(string userProfileId) => _userProfileId = userProfileId;

        public string UserProfileId => _userProfileId;

        public bool TryGetUserProfileId(out string userProfileId)
        {
            userProfileId = _userProfileId;
            return true;
        }
    }

    private sealed class StalePreferencesService : IPreferencesService
    {
        private readonly string _value;

        public StalePreferencesService(string value) => _value = value;

        public T Get<T>(string key, T defaultValue) =>
            key == "active_profile_id" && _value is T typed ? typed : defaultValue;

        public void Set<T>(string key, T value)
        {
        }

        public void Remove(string key)
        {
        }

        public void Clear()
        {
        }
    }
}
