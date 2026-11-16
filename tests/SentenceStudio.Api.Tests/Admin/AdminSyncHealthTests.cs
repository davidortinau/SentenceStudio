using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using CoreSync;
using CoreSync.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// Sync and data health: the numbers match the database for two learners with different data,
/// one learner's numbers never include the other's, and no content leaves.
/// </summary>
public sealed class AdminSyncHealthTests : IDisposable
{
    private const string SecretText = "SECRET-LEARNER-CONTENT";
    private static readonly DateTime Monday = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);

    private readonly AdminApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly string[] LearnerRoutes =
    [
        "/api/admin/sync-health/app",
        "/api/admin/sync-health/learners",
        "/api/admin/sync-health/learners/some-profile-id"
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_learner_and_a_caller_with_no_token_get_404_on_every_sync_health_route(int index)
    {
        var learner = await _factory.SeedAccountAsync("learner@sync-health.local");
        using var learnerClient = _factory.CreateClientFor(learner);
        using var anonymous = _factory.CreateClient();

        (await learnerClient.GetAsync(LearnerRoutes[index])).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync(LearnerRoutes[index])).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _factory.ReadAuditAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_numbers_match_the_database_for_two_learners_with_different_data()
    {
        var (admin, a, b) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        var healthA = await GetLearnerAsync(client, a.UserProfileId!);
        var healthB = await GetLearnerAsync(client, b.UserProfileId!);

        Count(healthA, "LearningResource").Should().Be(2);
        Count(healthA, "VocabularyWord").Should().Be(3);
        Count(healthA, "ResourceVocabularyMapping").Should().Be(3);
        Count(healthA, "VocabularyProgress").Should().Be(2);
        Count(healthA, "VocabularyLearningContext").Should().Be(1);
        Count(healthA, "UserProfile").Should().Be(1);

        Count(healthB, "LearningResource").Should().Be(1);
        Count(healthB, "VocabularyWord").Should().Be(1);
        Count(healthB, "ResourceVocabularyMapping").Should().Be(1);
        Count(healthB, "VocabularyProgress").Should().Be(0);
        Count(healthB, "VocabularyLearningContext").Should().Be(0);
        Count(healthB, "UserProfile").Should().Be(1);

        healthA.Tables.Single(t => t.Table == "VocabularyWord").NewestChangeUtc
            .Should().Be(Monday.AddDays(2));
        healthB.Tables.Single(t => t.Table == "VocabularyWord").NewestChangeUtc
            .Should().Be(Monday.AddDays(5));
        healthA.Tables.Single(t => t.Table == "LearningResource").NewestChangeUtc.Should().Be(Monday.AddHours(3));
        healthB.Tables.Single(t => t.Table == "VocabularyProgress").NewestChangeUtc.Should().BeNull();
        healthA.NewestChangeUtc.Should().Be(Monday.AddDays(2));
        healthB.NewestChangeUtc.Should().Be(Monday.AddDays(5));

        // The same numbers straight from the database, by the owner columns the sync rules use.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var (health, learner) in new[] { (healthA, a), (healthB, b) })
        {
            var id = learner.UserProfileId!;
            Count(health, "VocabularyWord").Should().Be(await db.VocabularyWords.CountAsync(w => w.UserProfileId == id));
            Count(health, "LearningResource").Should().Be(await db.LearningResources.CountAsync(r => r.UserProfileId == id));
            Count(health, "VocabularyProgress").Should().Be(await db.VocabularyProgresses.CountAsync(p => p.UserId == id));
        }

        // Together they are everything the database holds: nothing counted twice, nothing in both.
        (Count(healthA, "VocabularyWord") + Count(healthB, "VocabularyWord"))
            .Should().Be(await db.VocabularyWords.CountAsync());
    }

    [Fact]
    public async Task Tables_with_no_owner_say_so_instead_of_a_count()
    {
        var (admin, a, _) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        var health = await GetLearnerAsync(client, a.UserProfileId!);

        foreach (var table in new[] { "Challenge", "VocabularyList" })
        {
            var row = health.Tables.Single(t => t.Table == table);
            row.Status.Should().Be(AdminSyncTableStatus.NoOwner);
            row.RowCount.Should().BeNull();
        }

        health.Tables.Where(t => t.Status == AdminSyncTableStatus.Counted).Should().OnlyContain(t => t.RowCount != null);
    }

    [Fact]
    public async Task The_table_list_is_exactly_the_tables_the_sync_registration_syncs()
    {
        var registered = new SqliteSyncConfigurationBuilder("Data Source=:memory:")
            .ConfigureSyncTables().Build().Tables.Select(t => t.Name).ToList();

        AdminSyncHealthReader.Tables.Select(t => t.Name)
            .Should().BeEquivalentTo(registered, o => o.WithStrictOrdering(),
                "a table added to SharedSyncRegistration needs a line in AdminSyncHealthReader.Tables");
    }

    [Fact]
    public async Task No_learner_content_is_in_the_response()
    {
        var (admin, a, _) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        var body = await client.GetStringAsync($"/api/admin/sync-health/learners/{a.UserProfileId}");

        body.Should().NotContain(SecretText);
    }

    [Fact]
    public async Task A_learner_read_leaves_one_audit_row_naming_the_learner_and_the_numbers_route_does_not()
    {
        var (admin, a, _) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        (await client.GetAsync($"/api/admin/sync-health/learners/{a.UserProfileId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var row = (await _factory.ReadAuditAsync()).Should().ContainSingle().Subject;
        row.Action.Should().Be(AdminActions.SyncHealthLearnerRead);
        row.TargetKind.Should().Be(AdminTargetKinds.Learner);
        row.TargetId.Should().Be(a.UserProfileId);
        row.ActorId.Should().Be(admin.Id);
        row.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task The_app_and_list_reads_leave_audit_rows_with_no_target()
    {
        var (admin, _, _) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        await client.GetAsync("/api/admin/sync-health/app");
        await client.GetAsync("/api/admin/sync-health/learners");

        var rows = await _factory.ReadAuditAsync();
        rows.Select(r => r.Action).Should().Equal(AdminActions.SyncHealthAppRead, AdminActions.SyncHealthLearnersRead);
        rows.Should().OnlyContain(r => r.TargetId == null && r.TargetKind == null);
    }

    [Fact]
    public async Task An_unknown_learner_is_404_for_an_admin()
    {
        var (admin, _, _) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        (await client.GetAsync($"/api/admin/sync-health/learners/{Guid.NewGuid()}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/admin/sync-health/learners/{new string('x', 200)}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_learner_list_names_each_learner_by_profile_id_name_and_email_and_pages()
    {
        var (admin, a, b) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        var all = (await client.GetFromJsonAsync<AdminLearnerPage>("/api/admin/sync-health/learners"))!;
        all.Items.Select(i => i.ProfileId).Should().Contain([a.UserProfileId!, b.UserProfileId!]);
        all.Items.Single(i => i.ProfileId == a.UserProfileId).Email.Should().Be(a.Email);
        all.NextAfter.Should().BeNull();

        var first = (await client.GetFromJsonAsync<AdminLearnerPage>("/api/admin/sync-health/learners?take=1"))!;
        first.Items.Should().HaveCount(1);
        first.NextAfter.Should().Be(first.Items[0].ProfileId);
        var second = (await client.GetFromJsonAsync<AdminLearnerPage>(
            $"/api/admin/sync-health/learners?take=1&after={first.NextAfter}"))!;
        second.Items.Should().HaveCount(1);
        second.Items[0].ProfileId.Should().NotBe(first.Items[0].ProfileId);

        var found = (await (await client.PostAsJsonAsync(
                "/api/admin/sync-health/learners/search", new AdminLearnerSearchRequest(a.Email!.ToUpperInvariant())))
            .Content.ReadFromJsonAsync<AdminLearnerPage>())!;
        found.Items.Should().ContainSingle().Which.ProfileId.Should().Be(a.UserProfileId);
    }

    [Fact]
    public async Task A_search_travels_in_the_body_and_text_in_the_URL_is_not_a_search()
    {
        // A host logs the URL of every request it starts. A learner's name or email in a query
        // string would be in those logs, so the list route does not read one.
        var (admin, a, _) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        var byUrl = (await client.GetFromJsonAsync<AdminLearnerPage>(
            $"/api/admin/sync-health/learners?q={a.Email}"))!;
        byUrl.Items.Count.Should().BeGreaterThan(1, "q in the URL is ignored, not applied");

        var paged = (await (await client.PostAsJsonAsync(
                "/api/admin/sync-health/learners/search?take=1", new AdminLearnerSearchRequest("local")))
            .Content.ReadFromJsonAsync<AdminLearnerPage>())!;
        paged.Items.Should().HaveCount(1);
        paged.NextAfter.Should().NotBeNull("a search pages like the list does");

        (await client.PostAsJsonAsync("/api/admin/sync-health/learners/search", new AdminLearnerSearchRequest(new string('x', 101))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var rows = await _factory.ReadAuditAsync();
        rows.Select(r => r.Action).Should().Equal(
            AdminActions.SyncHealthLearnersRead,
            AdminActions.SyncHealthLearnersSearch,
            AdminActions.SyncHealthLearnersSearch);
        rows.Should().OnlyContain(r => r.TargetId == null, "what was searched for is not written to the audit log either");
    }

    [Theory]
    [InlineData("null", "application/json")]
    [InlineData("{ \"q\": ", "application/json")]
    [InlineData("{ \"q\": 5 }", "application/json")]
    [InlineData("[]", "application/json")]
    [InlineData("q=alpha", "application/x-www-form-urlencoded")]
    public async Task A_search_body_that_is_not_the_expected_JSON_is_the_routes_own_400(string body, string contentType)
    {
        var (admin, _, _) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        var response = await client.PostAsync("/api/admin/sync-health/learners/search",
            new StringContent(body, System.Text.Encoding.UTF8, contentType));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var row = (await _factory.ReadAuditAsync()).Should().ContainSingle().Subject;
        row.Action.Should().Be(AdminActions.SyncHealthLearnersSearch);
        row.Outcome.Should().Be(AdminAuditOutcome.Failed);
        row.FailureCode.Should().Be(AdminFailureCodes.InvalidRequest);
    }

    [Fact]
    public async Task A_search_for_nothing_or_for_wildcards_is_not_a_search_for_everything()
    {
        var (admin, _, _) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        async Task<int> FoundAsync(string? q) =>
            (await (await client.PostAsJsonAsync("/api/admin/sync-health/learners/search", new AdminLearnerSearchRequest(q)))
                .Content.ReadFromJsonAsync<AdminLearnerPage>())!.Items.Count;

        (await FoundAsync("%")).Should().Be(0, "a wildcard is looked for as itself");
        (await FoundAsync("_")).Should().Be(0);
        (await FoundAsync(null)).Should().BeGreaterThan(1, "no text is the whole list, as the unfiltered route gives");
        (await FoundAsync("   ")).Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task A_learner_and_a_caller_with_no_token_get_404_on_the_search_too()
    {
        var (_, a, _) = await SeedAdminAndTwoLearnersAsync();
        using var learner = _factory.CreateClientFor(a);
        using var anonymous = _factory.CreateClient();

        (await learner.PostAsJsonAsync("/api/admin/sync-health/learners/search", new AdminLearnerSearchRequest("x")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.PostAsJsonAsync("/api/admin/sync-health/learners/search", new AdminLearnerSearchRequest("x")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _factory.ReadAuditAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("take=0")]
    [InlineData("take=201")]
    [InlineData("take=abc")]
    [InlineData("take=-1")]
    [InlineData("after=")]
    public async Task A_bad_list_parameter_is_the_routes_own_400(string query)
    {
        var (admin, _, _) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        (await client.GetAsync($"/api/admin/sync-health/learners?{query}")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_app_read_names_the_build_and_the_change_log_range()
    {
        var (admin, _, _) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        var app = (await client.GetFromJsonAsync<AdminSyncHealthApp>("/api/admin/sync-health/app"))!;

        app.ApiVersion.Should().NotBeNullOrWhiteSpace();
        app.AppliedMigrations.Should().BeGreaterThanOrEqualTo(0);
        app.PendingMigrations.Should().BeGreaterThanOrEqualTo(0);
        app.ServerChangeVersion.Should().Be(await MaxChangeAsync());
        app.OldestChangeVersion.Should().BeGreaterThan(0);
        app.OldestChangeVersion.Should().BeLessThanOrEqualTo(app.ServerChangeVersion!.Value);
    }

    [Fact]
    public async Task Devices_are_the_stores_bound_to_the_learner_with_their_versions_and_no_times()
    {
        var (admin, a, b) = await SeedAdminAndTwoLearnersAsync();
        var storeOne = Guid.NewGuid();
        var storeTwo = Guid.NewGuid();
        var otherStore = Guid.NewGuid();
        var head = await MaxChangeAsync();
        await SeedStoreAsync(a, storeOne, local: head - 2, remote: 3);
        await SeedStoreAsync(a, storeTwo, local: head, remote: null);
        await SeedStoreAsync(b, otherStore, local: 1, remote: 1);
        using var client = _factory.CreateClientFor(admin);

        var healthA = await GetLearnerAsync(client, a.UserProfileId!);
        var healthB = await GetLearnerAsync(client, b.UserProfileId!);

        healthA.Devices.Select(d => d.StoreId).Should().BeEquivalentTo(
            [storeOne.ToString("N"), storeTwo.ToString("N")]);
        var one = healthA.Devices.Single(d => d.StoreId == storeOne.ToString("N"));
        one.DownloadedThroughVersion.Should().Be(head - 2);
        one.UploadedThroughVersion.Should().Be(3);
        one.ChangesBehind.Should().Be(2);
        var two = healthA.Devices.Single(d => d.StoreId == storeTwo.ToString("N"));
        two.ChangesBehind.Should().Be(0);
        two.UploadedThroughVersion.Should().BeNull();

        healthB.Devices.Should().ContainSingle().Which.StoreId.Should().Be(otherStore.ToString("N"));
        healthA.ServerChangeVersion.Should().Be(head);
        healthA.NotRecorded.Should().BeEquivalentTo(
            [AdminSyncNotRecorded.LastSyncTime, AdminSyncNotRecorded.RefusedRows]);
    }

    [Fact]
    public async Task A_learner_with_no_stores_has_no_devices()
    {
        var (admin, a, _) = await SeedAdminAndTwoLearnersAsync();
        using var client = _factory.CreateClientFor(admin);

        (await GetLearnerAsync(client, a.UserProfileId!)).Devices.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ helpers

    private static long Count(AdminSyncHealthLearner health, string table) =>
        health.Tables.Single(t => t.Table == table).RowCount!.Value;

    private static async Task<AdminSyncHealthLearner> GetLearnerAsync(HttpClient client, string profileId) =>
        (await client.GetFromJsonAsync<AdminSyncHealthLearner>($"/api/admin/sync-health/learners/{profileId}"))!;

    private async Task<long?> MaxChangeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(id) FROM __core_sync_ct";
            var value = await command.ExecuteScalarAsync();
            return value is null or DBNull ? null : Convert.ToInt64(value);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    /// <summary>Writes the two rows the API writes when a store first syncs: its owner marker and its cursor.</summary>
    private async Task SeedStoreAsync(ApplicationUser learner, Guid store, long? local, long? remote)
    {
        var owner = Guid.Parse(learner.UserProfileId!);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"SentenceStudio.Native.Store.v3:{owner:N}:{store:N}"));
        var cursor = new Guid(digest.AsSpan(0, 16)).ToString();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO __core_sync_remote_anchor (id) VALUES ({0})",
            $"sentencestudio:native-store:v1:{store:N}:{owner:N}");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO __core_sync_remote_anchor (id, local_version, remote_version) VALUES ({0}, {1}, {2})",
            cursor, local, remote);
    }

    private async Task<(ApplicationUser Admin, ApplicationUser A, ApplicationUser B)> SeedAdminAndTwoLearnersAsync()
    {
        var admin = await _factory.SeedAdminAsync("captain@sync-health.local");
        var a = await _factory.SeedAccountAsync("alpha@sync-health.local");
        var b = await _factory.SeedAccountAsync("bravo@sync-health.local");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.UserProfiles.Add(new UserProfile { Id = a.UserProfileId!, Name = "Alpha", Email = a.Email });
        db.UserProfiles.Add(new UserProfile { Id = b.UserProfileId!, Name = "Bravo", Email = b.Email });
        await db.SaveChangesAsync();

        // Learner A: two resources, three words and links, progress on two words, one learning record.
        var aWords = Enumerable.Range(0, 3).Select(i => new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = a.UserProfileId,
            TargetLanguageTerm = $"{SecretText}-a{i}",
            NativeLanguageTerm = $"{SecretText}-na{i}",
            CreatedAt = Monday,
            UpdatedAt = Monday.AddDays(i)
        }).ToList();
        var aResources = new[]
        {
            Resource(a, "ra1", Monday.AddHours(1)),
            Resource(a, "ra2", Monday.AddHours(3))
        };
        db.VocabularyWords.AddRange(aWords);
        db.LearningResources.AddRange(aResources);
        await db.SaveChangesAsync();
        db.ResourceVocabularyMappings.AddRange(
            Link(aResources[0], aWords[0]), Link(aResources[0], aWords[1]), Link(aResources[1], aWords[2]));
        var progress = aWords.Take(2).Select(w => new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = w.Id,
            UserId = a.UserProfileId!,
            UpdatedAt = Monday.AddDays(1)
        }).ToList();
        db.VocabularyProgresses.AddRange(progress);
        await db.SaveChangesAsync();
        db.VocabularyLearningContexts.Add(new VocabularyLearningContext
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyProgressId = progress[0].Id,
            UpdatedAt = Monday.AddDays(1)
        });

        // Learner B: one resource, one word, one link, no progress.
        var bWord = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = b.UserProfileId,
            TargetLanguageTerm = $"{SecretText}-b",
            NativeLanguageTerm = $"{SecretText}-nb",
            CreatedAt = Monday,
            UpdatedAt = Monday.AddDays(5)
        };
        var bResource = Resource(b, "rb1", Monday.AddHours(9));
        db.VocabularyWords.Add(bWord);
        db.LearningResources.Add(bResource);
        await db.SaveChangesAsync();
        db.ResourceVocabularyMappings.Add(Link(bResource, bWord));
        await db.SaveChangesAsync();

        return (admin, a, b);
    }

    private static LearningResource Resource(ApplicationUser owner, string key, DateTime updated) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Title = $"{SecretText}-{key}",
        UserProfileId = owner.UserProfileId,
        CreatedAt = Monday,
        UpdatedAt = updated
    };

    private static ResourceVocabularyMapping Link(LearningResource resource, VocabularyWord word) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ResourceId = resource.Id,
        VocabularyWordId = word.Id
    };
}
