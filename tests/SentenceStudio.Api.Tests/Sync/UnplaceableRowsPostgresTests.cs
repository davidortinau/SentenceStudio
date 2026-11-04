using System.Net;
using CoreSync;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Sync;

/// <summary>
/// An owner-checked upload applies every row the server can place for its owner and leaves out
/// the rest (they stay on the device), so one old or unowned row never stops a device's sync
/// (Captain 2026-11-04, decision 1: the MAUI apps sync with the same owner checks). Only a row
/// that names another learner in its owner column refuses the whole upload.
/// </summary>
public sealed class UnplaceableRowsPostgresTests : IClassFixture<NativeOwnerSyncPostgresFixture>
{
    private readonly NativeOwnerSyncPostgresFixture _fixture;

    public UnplaceableRowsPostgresTests(NativeOwnerSyncPostgresFixture fixture) =>
        _fixture = fixture;

    [ApplicationOperationPostgresFact]
    public async Task MixedUpload_AppliesOwnRows_AndLeavesOutRowsTheServerCannotPlace()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var ownerlessSkill = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.SkillProfiles.Add(new SkillProfile { Id = ownerlessSkill });
            await db.SaveChangesAsync();
        }

        var keptResource = Guid.NewGuid().ToString();
        var keptWord = Guid.NewGuid().ToString();
        var keptMapping = Guid.NewGuid().ToString();
        var strandedResource = Guid.NewGuid().ToString();
        var strandedWord = Guid.NewGuid().ToString();
        var strandedMapping = Guid.NewGuid().ToString();
        var legacyList = Guid.NewGuid().ToString();
        var unownedImage = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;

        using var response = await _fixture.UploadAsync(owner,
            Resource(keptResource, owner, skill: null, now),
            Word(keptWord),
            Mapping(keptMapping, keptResource, keptWord),
            // Its skill has no owner, so the resource cannot be placed, and its link and word
            // would have no visible parent: all three stay on the device.
            Resource(strandedResource, owner, ownerlessSkill, now),
            Word(strandedWord),
            Mapping(strandedMapping, strandedResource, strandedWord),
            new SyncItem("VocabularyList", ChangeType.Insert,
                new Dictionary<string, object?> { ["Id"] = legacyList, ["Name"] = "Legacy" }),
            new SyncItem("SceneImage", ChangeType.Insert, new Dictionary<string, object?>
            {
                ["Id"] = unownedImage, ["UserProfileId"] = null,
                ["Url"] = "https://example.test/old.jpg"
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.True(await verified.LearningResources.AnyAsync(row =>
            row.Id == keptResource && row.UserProfileId == owner));
        Assert.True(await verified.VocabularyWords.AnyAsync(row => row.Id == keptWord));
        Assert.True(await verified.ResourceVocabularyMappings.AnyAsync(row => row.Id == keptMapping));
        Assert.False(await verified.LearningResources.AnyAsync(row => row.Id == strandedResource));
        Assert.False(await verified.VocabularyWords.AnyAsync(row => row.Id == strandedWord));
        Assert.False(await verified.ResourceVocabularyMappings.AnyAsync(row => row.Id == strandedMapping));
        Assert.False(await verified.VocabularyLists.AnyAsync(row => row.Id == legacyList));
        Assert.False(await verified.SceneImages.AnyAsync(row => row.Id == unownedImage));
        Assert.Null((await verified.SkillProfiles.AsNoTracking()
            .SingleAsync(row => row.Id == ownerlessSkill)).UserProfileId);
    }

    [ApplicationOperationPostgresFact]
    public async Task RowNamingAnotherLearner_RefusesTheUpload_ButAForeignStoredRowIsLeftOut()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var foreignResource = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = foreignResource, UserProfileId = foreign, Title = "Theirs"
            });
            await db.SaveChangesAsync();
        }
        var now = DateTime.UtcNow;

        // A row that names another learner: a correct client never sends one.
        var refusedOwn = Guid.NewGuid().ToString();
        var namedForeign = Guid.NewGuid().ToString();
        using (var response = await _fixture.UploadAsync(owner,
            Resource(refusedOwn, owner, skill: null, now), Resource(namedForeign, foreign, skill: null, now)))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // A row whose stored copy is another learner's, though the upload names this owner: the
        // client's copy can disagree with the server, so it is left out and the rest applies.
        var appliedOwn = Guid.NewGuid().ToString();
        using (var response = await _fixture.UploadAsync(owner,
            Resource(appliedOwn, owner, skill: null, now),
            new SyncItem("LearningResource", ChangeType.Update, new Dictionary<string, object?>
            {
                ["Id"] = foreignResource, ["UserProfileId"] = owner, ["Title"] = "Mine now"
            })))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.False(await verified.LearningResources.AnyAsync(row => row.Id == refusedOwn));
        Assert.False(await verified.LearningResources.AnyAsync(row => row.Id == namedForeign));
        Assert.True(await verified.LearningResources.AnyAsync(row => row.Id == appliedOwn));
        var theirs = await verified.LearningResources.AsNoTracking()
            .SingleAsync(row => row.Id == foreignResource);
        Assert.Equal(foreign, theirs.UserProfileId);
        Assert.Equal("Theirs", theirs.Title);
    }

    private static SyncItem Resource(string id, string owner, string? skill, DateTime now) =>
        new("LearningResource", ChangeType.Insert, new Dictionary<string, object?>
        {
            ["Id"] = id, ["UserProfileId"] = owner, ["SkillID"] = skill, ["Title"] = "Resource",
            ["CreatedAt"] = now, ["UpdatedAt"] = now, ["IsSmartResource"] = false
        });

    private static SyncItem Word(string id) =>
        new("VocabularyWord", ChangeType.Insert, new Dictionary<string, object?>
        {
            ["Id"] = id, ["TargetLanguageTerm"] = "물", ["NativeLanguageTerm"] = "water",
            ["CreatedAt"] = DateTime.UtcNow, ["UpdatedAt"] = DateTime.UtcNow
        });

    private static SyncItem Mapping(string id, string resource, string word) =>
        new("ResourceVocabularyMapping", ChangeType.Insert, new Dictionary<string, object?>
        {
            ["Id"] = id, ["ResourceId"] = resource, ["VocabularyWordId"] = word
        });
}
