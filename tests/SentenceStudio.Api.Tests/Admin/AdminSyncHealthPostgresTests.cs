using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// The sync health reads on PostgreSQL, the provider they run on: the owner counts translate, and
/// the CoreSync tables are read with the names and columns CoreSync creates there. Skips without a
/// scratch server (<c>COACH_PG_TEST_CONNECTION</c>).
/// </summary>
public sealed class AdminSyncHealthPostgresTests
{
    private static readonly DateTime Monday = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);

    [ApplicationOperationPostgresFact]
    public async Task Counts_devices_and_change_log_read_correctly_on_PostgreSQL()
    {
        await using var harness = await ApplicationOperationPostgresHarness.CreateAsync("synchealth");
        var learner = Guid.NewGuid().ToString();
        var other = Guid.NewGuid().ToString();
        var store = Guid.NewGuid();

        await using (var seed = harness.NewContext())
        {
            // The two CoreSync tables as CoreSync.PostgreSQL creates them.
            await seed.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS __core_sync_ct (id BIGSERIAL PRIMARY KEY, tbl TEXT NOT NULL, op CHAR(1) NOT NULL,
                    pk_integer BIGINT NULL, pk_text TEXT NULL, pk_bytea BYTEA NULL, src TEXT NULL);
                CREATE TABLE IF NOT EXISTS __core_sync_remote_anchor (id TEXT NOT NULL PRIMARY KEY, local_version BIGINT NULL, remote_version BIGINT NULL);
                """);
            seed.UserProfiles.Add(new UserProfile { Id = learner, Name = "One" });
            seed.UserProfiles.Add(new UserProfile { Id = other, Name = "Two" });
            var resource = new LearningResource { Id = Guid.NewGuid().ToString(), Title = "r", UserProfileId = learner, CreatedAt = Monday, UpdatedAt = Monday.AddHours(2) };
            var word = new VocabularyWord { Id = Guid.NewGuid().ToString(), UserProfileId = learner, TargetLanguageTerm = "t", NativeLanguageTerm = "n", CreatedAt = Monday, UpdatedAt = Monday.AddDays(1) };
            var otherWord = new VocabularyWord { Id = Guid.NewGuid().ToString(), UserProfileId = other, TargetLanguageTerm = "t2", NativeLanguageTerm = "n2", CreatedAt = Monday, UpdatedAt = Monday.AddDays(4) };
            seed.LearningResources.Add(resource);
            seed.VocabularyWords.AddRange(word, otherWord);
            await seed.SaveChangesAsync();
            seed.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = Guid.NewGuid().ToString(), ResourceId = resource.Id, VocabularyWordId = word.Id
            });
            var progress = new VocabularyProgress { Id = Guid.NewGuid().ToString(), VocabularyWordId = word.Id, UserId = learner, UpdatedAt = Monday };
            seed.VocabularyProgresses.Add(progress);
            await seed.SaveChangesAsync();
            seed.VocabularyLearningContexts.Add(new VocabularyLearningContext
            {
                Id = Guid.NewGuid().ToString(), VocabularyProgressId = progress.Id, UpdatedAt = Monday.AddDays(2)
            });
            await seed.SaveChangesAsync();

            var owner = Guid.Parse(learner);
            var cursor = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"SentenceStudio.Native.Store.v3:{owner:N}:{store:N}")).AsSpan(0, 16)).ToString();
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO __core_sync_ct (tbl, op, pk_text) VALUES ('VocabularyWord', 'I', 'a'), ('VocabularyWord', 'I', 'b'), ('VocabularyWord', 'U', 'a')");
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO __core_sync_remote_anchor (id) VALUES ({0})", $"sentencestudio:native-store:v1:{store:N}:{owner:N}");
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO __core_sync_remote_anchor (id, local_version, remote_version) VALUES ({0}, 2, 9)", cursor);
        }

        await using var db = harness.NewContext();
        var tables = await AdminSyncHealthReader.ReadTablesAsync(db, learner, CancellationToken.None);
        long Count(string t) => tables.Single(x => x.Table == t).RowCount!.Value;
        Count("LearningResource").Should().Be(1);
        Count("VocabularyWord").Should().Be(1);
        Count("ResourceVocabularyMapping").Should().Be(1);
        Count("VocabularyProgress").Should().Be(1);
        Count("VocabularyLearningContext").Should().Be(1);
        tables.Single(x => x.Table == "VocabularyWord").NewestChangeUtc.Should().Be(Monday.AddDays(1));
        tables.Single(x => x.Table == "VocabularyLearningContext").NewestChangeUtc.Should().Be(Monday.AddDays(2));

        var (newest, oldest) = await AdminSyncHealthReader.ReadChangeLogRangeAsync(db, CancellationToken.None);
        (newest, oldest).Should().Be((3L, 1L));

        var devices = await AdminSyncHealthReader.ReadDevicesAsync(db, learner, newest, CancellationToken.None);
        var device = devices.Should().ContainSingle().Subject;
        device.StoreId.Should().Be(store.ToString("N"));
        device.DownloadedThroughVersion.Should().Be(2);
        device.UploadedThroughVersion.Should().Be(9);
        device.ChangesBehind.Should().Be(1);
        (await AdminSyncHealthReader.ReadDevicesAsync(db, other, newest, CancellationToken.None)).Should().BeEmpty();
    }
}
