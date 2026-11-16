using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// The import and part-of-speech reads on PostgreSQL, the provider they run on: the grouping and
/// the status and null filters translate. Skips without a scratch server.
/// </summary>
public sealed class AdminUsagePostgresTests
{
    [ApplicationOperationPostgresFact]
    public async Task Imports_and_part_of_speech_counts_translate_and_match_on_PostgreSQL()
    {
        await using var harness = await ApplicationOperationPostgresHarness.CreateAsync("usagepg");
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var learner = Guid.NewGuid().ToString();

        await using (var seed = harness.NewContext())
        {
            seed.UserProfiles.Add(new UserProfile { Id = learner, Name = "One" });
            seed.VideoImports.AddRange(
                Import("failed", learner, VideoImportStatus.Failed, "Import was cancelled.", now.AddDays(-1)),
                Import("stuck", learner, VideoImportStatus.SavingResource, null, now.AddMinutes(-90)),
                Import("recent", learner, VideoImportStatus.Pending, null, now.AddMinutes(-1)),
                Import("done", learner, VideoImportStatus.Completed, null, now.AddDays(-2)));
            seed.VocabularyWords.AddRange(
                Word(learner, VocabularyPartOfSpeech.Noun), Word(learner, null), Word(learner, null));
            await seed.SaveChangesAsync();
        }

        await using var db = harness.NewContext();
        var time = new FixedTime(now);

        var imports = ((Ok<AdminVideoImports>)await AdminUsageEndpoints.GetVideoImportsAsync(
            null!, db, time, CancellationToken.None)).Value!;
        imports.FailedCount.Should().Be(1);
        imports.StuckCount.Should().Be(1);
        imports.Items.Select(i => i.Id).Should().BeEquivalentTo(["failed", "stuck"]);
        imports.Items.Single(i => i.Id == "failed").ReasonCode.Should().Be(AdminVideoImportReasons.Cancelled);

        var pos = ((Ok<AdminPartOfSpeech>)await AdminUsageEndpoints.GetPartOfSpeechAsync(
            db, CancellationToken.None)).Value!;
        (pos.TotalWords, pos.Classified, pos.Unclassified, pos.UnownedWords).Should().Be((3L, 1L, 2L, 0L));
        pos.Learners.Should().ContainSingle().Which.Learner.ProfileId.Should().Be(learner);
    }

    private static VideoImport Import(string id, string learner, VideoImportStatus status, string? error, DateTime created) => new()
    {
        Id = id, UserProfileId = learner, Status = status, ErrorMessage = error, CreatedAt = created
    };

    private static VocabularyWord Word(string learner, VocabularyPartOfSpeech? partOfSpeech) => new()
    {
        Id = Guid.NewGuid().ToString(), UserProfileId = learner, TargetLanguageTerm = "t", NativeLanguageTerm = "n",
        PartOfSpeech = partOfSpeech
    };

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }
}
