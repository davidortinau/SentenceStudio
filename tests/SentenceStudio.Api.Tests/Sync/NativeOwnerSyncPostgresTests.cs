using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoreSync;
using CoreSync.Http;
using CoreSync.PostgreSQL;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio;
using SentenceStudio.Api.Sync;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Sync;

public sealed class NativeOwnerSyncPostgresTests : IClassFixture<NativeOwnerSyncPostgresFixture>
{
    private readonly NativeOwnerSyncPostgresFixture _fixture;

    public NativeOwnerSyncPostgresTests(NativeOwnerSyncPostgresFixture fixture) =>
        _fixture = fixture;

    [ApplicationOperationPostgresFact]
    public async Task ExistingForeignId_CannotBeUpdatedOrReparented_RegardlessOfChangeType()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var foreignId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = foreignId, UserProfileId = foreign, Title = "Foreign original"
            });
            await db.SaveChangesAsync();
        }

        foreach (var changeType in new[] { ChangeType.Update, ChangeType.Insert })
        {
            using var response = await _fixture.UploadAsync(owner, new SyncItem(
                "LearningResource", changeType, new Dictionary<string, object?>
                {
                    ["Id"] = foreignId, ["UserProfileId"] = owner, ["Title"] = "Stolen"
                }));
            await using var check = _fixture.NewContext();
            var storedAfterAttempt = await check.LearningResources.AsNoTracking()
                .SingleAsync(resource => resource.Id == foreignId);
            Assert.Equal(foreign, storedAfterAttempt.UserProfileId);
            Assert.Equal("Foreign original", storedAfterAttempt.Title);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await using var verified = _fixture.NewContext();
        var stored = await verified.LearningResources.AsNoTracking()
            .SingleAsync(resource => resource.Id == foreignId);
        Assert.Equal(foreign, stored.UserProfileId);
        Assert.Equal("Foreign original", stored.Title);
    }

    [ApplicationOperationPostgresFact]
    public async Task OwnExistingId_CanUpdate_AndNewOwnedId_CanInsert()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var existingId = Guid.NewGuid().ToString();
        var newId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = existingId, UserProfileId = owner, Title = "Before"
            });
            await db.SaveChangesAsync();
        }

        using var updated = await _fixture.UploadAsync(owner, new SyncItem(
            "LearningResource", ChangeType.Update, new Dictionary<string, object?>
            {
                ["Id"] = existingId, ["UserProfileId"] = owner, ["Title"] = "After"
            }));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        using var inserted = await _fixture.UploadAsync(owner, new SyncItem(
            "LearningResource", ChangeType.Insert, new Dictionary<string, object?>
            {
                ["Id"] = newId, ["UserProfileId"] = owner, ["Title"] = "New",
                ["CreatedAt"] = DateTime.UtcNow, ["UpdatedAt"] = DateTime.UtcNow,
                ["IsSmartResource"] = false
            }));
        Assert.Equal(HttpStatusCode.OK, inserted.StatusCode);

        await using var verified = _fixture.NewContext();
        var rows = await verified.LearningResources.AsNoTracking()
            .Where(resource => resource.Id == existingId || resource.Id == newId)
            .OrderBy(resource => resource.Id)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, resource => Assert.Equal(owner, resource.UserProfileId));
        Assert.Equal("After", rows.Single(resource => resource.Id == existingId).Title);
        Assert.Equal("New", rows.Single(resource => resource.Id == newId).Title);
    }

    [ApplicationOperationPostgresFact]
    public async Task SharedWord_AllowsOwnedProgressAndNewContext_WithoutChangingOtherOwner()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var wordId = Guid.NewGuid().ToString();
        var progressId = Guid.NewGuid().ToString();
        var foreignProgressId = Guid.NewGuid().ToString();
        var contextId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            var ownResourceId = Guid.NewGuid().ToString();
            var foreignResourceId = Guid.NewGuid().ToString();
            db.LearningResources.AddRange(
                new LearningResource { Id = ownResourceId, UserProfileId = owner },
                new LearningResource { Id = foreignResourceId, UserProfileId = foreign });
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = wordId, TargetLanguageTerm = "Shared original"
            });
            db.ResourceVocabularyMappings.AddRange(
                new ResourceVocabularyMapping
                {
                    Id = Guid.NewGuid().ToString(),
                    ResourceId = ownResourceId, VocabularyWordId = wordId
                },
                new ResourceVocabularyMapping
                {
                    Id = Guid.NewGuid().ToString(),
                    ResourceId = foreignResourceId, VocabularyWordId = wordId
                });
            db.VocabularyProgresses.AddRange(
                new VocabularyProgress
                {
                    Id = progressId, UserId = owner, VocabularyWordId = wordId
                },
                new VocabularyProgress
                {
                    Id = foreignProgressId, UserId = foreign, VocabularyWordId = wordId
                });
            await db.SaveChangesAsync();
        }

        var now = DateTime.UtcNow;
        using var response = await _fixture.UploadAsync(owner,
            new SyncItem("VocabularyProgress", ChangeType.Update,
                new Dictionary<string, object?>
                {
                    ["Id"] = progressId, ["UserId"] = owner,
                    ["VocabularyWordId"] = wordId, ["MasteryScore"] = 0.6f
                }),
            new SyncItem("VocabularyLearningContext", ChangeType.Insert,
                new Dictionary<string, object?>
                {
                    ["Id"] = contextId, ["VocabularyProgressId"] = progressId,
                    ["Activity"] = "VocabularyQuiz", ["InputMode"] = "MultipleChoice",
                    ["WasCorrect"] = true, ["DifficultyScore"] = 0.5f,
                    ["ResponseTimeMs"] = 200, ["LearnedAt"] = now,
                    ["CreatedAt"] = now, ["UpdatedAt"] = now,
                    ["CorrectAnswersInContext"] = 1
                }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.Equal(0.6f, (await verified.VocabularyProgresses.AsNoTracking()
            .SingleAsync(progress => progress.Id == progressId)).MasteryScore);
        Assert.Equal(0f, (await verified.VocabularyProgresses.AsNoTracking()
            .SingleAsync(progress => progress.Id == foreignProgressId)).MasteryScore);
        Assert.Equal(progressId, (await verified.VocabularyLearningContexts.AsNoTracking()
            .SingleAsync(context => context.Id == contextId)).VocabularyProgressId);

        using var wordUpdate = await _fixture.UploadAsync(owner,
            new SyncItem("VocabularyWord", ChangeType.Update,
                new Dictionary<string, object?>
                {
                    ["Id"] = wordId, ["TargetLanguageTerm"] = "Overwritten"
                }));
        Assert.Equal(HttpStatusCode.Forbidden, wordUpdate.StatusCode);
        Assert.Equal("Shared original", (await verified.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.Id == wordId)).TargetLanguageTerm);
    }

    [ApplicationOperationPostgresFact]
    public async Task NewOwnedMappings_AllowSameUploadProgressAfterReferencesAreVerified()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var resourceId = Guid.NewGuid().ToString();
        var foreignResourceId = Guid.NewGuid().ToString();
        var existingWordId = Guid.NewGuid().ToString();
        var newWordId = Guid.NewGuid().ToString();
        var progressId = Guid.NewGuid().ToString();
        var existingMappingId = Guid.NewGuid().ToString();
        var newMappingId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.AddRange(
                new LearningResource { Id = resourceId, UserProfileId = owner },
                new LearningResource { Id = foreignResourceId, UserProfileId = foreign });
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = existingWordId, TargetLanguageTerm = "unmapped"
            });
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = progressId, VocabularyWordId = existingWordId, UserId = owner
            });
            await db.SaveChangesAsync();
        }

        var now = DateTime.UtcNow;
        using var response = await _fixture.UploadAsync(owner,
            new SyncItem("VocabularyWord", ChangeType.Insert,
                new Dictionary<string, object?>
                {
                    ["Id"] = newWordId, ["TargetLanguageTerm"] = "new",
                    ["CreatedAt"] = now, ["UpdatedAt"] = now
                }),
            new SyncItem("ResourceVocabularyMapping", ChangeType.Insert,
                new Dictionary<string, object?>
                {
                    ["Id"] = existingMappingId,
                    ["ResourceId"] = resourceId, ["VocabularyWordId"] = existingWordId
                }),
            new SyncItem("ResourceVocabularyMapping", ChangeType.Insert,
                new Dictionary<string, object?>
                {
                    ["Id"] = newMappingId,
                    ["ResourceId"] = resourceId, ["VocabularyWordId"] = newWordId
                }),
            new SyncItem("VocabularyProgress", ChangeType.Update,
                new Dictionary<string, object?>
                {
                    ["Id"] = progressId, ["UserId"] = owner,
                    ["VocabularyWordId"] = existingWordId, ["MasteryScore"] = 0.7f
                }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.True(await verified.VocabularyWords.AsNoTracking()
            .AnyAsync(word => word.Id == newWordId && word.TargetLanguageTerm == "new"));
        Assert.Equal(2, await verified.ResourceVocabularyMappings.AsNoTracking()
            .CountAsync(mapping => mapping.Id == existingMappingId || mapping.Id == newMappingId));
        Assert.Equal(0.7f, (await verified.VocabularyProgresses.AsNoTracking()
            .SingleAsync(progress => progress.Id == progressId)).MasteryScore);
        Assert.Equal(foreign, (await verified.LearningResources.AsNoTracking()
            .SingleAsync(resource => resource.Id == foreignResourceId)).UserProfileId);
    }

    [ApplicationOperationPostgresFact]
    public async Task MissingOwnerAndSameOwnerReparentAttempt_AreRejected()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var resourceId = Guid.NewGuid().ToString();
        var unownedId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.AddRange(
                new LearningResource
                {
                    Id = resourceId, UserProfileId = owner, Title = "Unchanged"
                },
                new LearningResource
                {
                    Id = unownedId, Title = "Ownerless unchanged"
                });
            await db.SaveChangesAsync();
        }

        foreach (var assertedOwner in new string?[] { null, "", foreign })
        {
            var values = new Dictionary<string, object?>
            {
                ["Id"] = resourceId, ["Title"] = "Changed"
            };
            if (assertedOwner is not null)
                values["UserProfileId"] = assertedOwner;

            using var response = await _fixture.UploadAsync(
                owner, new SyncItem("LearningResource", ChangeType.Update, values));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var unowned = await _fixture.UploadAsync(owner, new SyncItem(
            "LearningResource", ChangeType.Update, new Dictionary<string, object?>
            {
                ["Id"] = unownedId, ["UserProfileId"] = owner, ["Title"] = "Claimed"
            }));
        Assert.Equal(HttpStatusCode.Forbidden, unowned.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.Equal("Unchanged", (await verified.LearningResources.AsNoTracking()
            .SingleAsync(resource => resource.Id == resourceId)).Title);
        var storedUnowned = await verified.LearningResources.AsNoTracking()
            .SingleAsync(resource => resource.Id == unownedId);
        Assert.Null(storedUnowned.UserProfileId);
        Assert.Equal("Ownerless unchanged", storedUnowned.Title);
    }

    [ApplicationOperationPostgresFact]
    public async Task ExistingForeignTransitiveIds_CannotBeReparentedInUploadOrder()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var ownResourceId = Guid.NewGuid().ToString();
        var foreignResourceId = Guid.NewGuid().ToString();
        var ownWordId = Guid.NewGuid().ToString();
        var foreignWordId = Guid.NewGuid().ToString();
        var foreignMappingId = Guid.NewGuid().ToString();
        var ownProgressId = Guid.NewGuid().ToString();
        var foreignProgressId = Guid.NewGuid().ToString();
        var foreignContextId = Guid.NewGuid().ToString();
        var foreignPhraseId = Guid.NewGuid().ToString();

        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.AddRange(
                new LearningResource { Id = ownResourceId, UserProfileId = owner },
                new LearningResource { Id = foreignResourceId, UserProfileId = foreign });
            db.VocabularyWords.AddRange(
                new VocabularyWord { Id = ownWordId, TargetLanguageTerm = "own" },
                new VocabularyWord { Id = foreignWordId, TargetLanguageTerm = "foreign" });
            db.ResourceVocabularyMappings.AddRange(
                new ResourceVocabularyMapping
                {
                    Id = Guid.NewGuid().ToString(),
                    ResourceId = ownResourceId, VocabularyWordId = ownWordId
                },
                new ResourceVocabularyMapping
                {
                    Id = foreignMappingId,
                    ResourceId = foreignResourceId, VocabularyWordId = foreignWordId
                });
            db.VocabularyProgresses.AddRange(
                new VocabularyProgress
                {
                    Id = ownProgressId, UserId = owner, VocabularyWordId = ownWordId
                },
                new VocabularyProgress
                {
                    Id = foreignProgressId, UserId = foreign, VocabularyWordId = foreignWordId
                });
            db.VocabularyLearningContexts.Add(new VocabularyLearningContext
            {
                Id = foreignContextId, VocabularyProgressId = foreignProgressId
            });
            db.PhraseConstituents.Add(new PhraseConstituent
            {
                Id = foreignPhraseId, PhraseWordId = foreignWordId
            });
            await db.SaveChangesAsync();
        }

        var attemptedReparents = new[]
        {
            new SyncItem("ResourceVocabularyMapping", ChangeType.Update,
                new Dictionary<string, object?>
                {
                    ["Id"] = foreignMappingId,
                    ["ResourceId"] = ownResourceId, ["VocabularyWordId"] = ownWordId
                }),
            new SyncItem("VocabularyLearningContext", ChangeType.Update,
                new Dictionary<string, object?>
                {
                    ["Id"] = foreignContextId,
                    ["VocabularyProgressId"] = ownProgressId,
                    ["Activity"] = "Stolen"
                }),
            new SyncItem("VocabularyProgress", ChangeType.Update,
                new Dictionary<string, object?>
                {
                    ["Id"] = foreignProgressId, ["UserId"] = owner,
                    ["VocabularyWordId"] = ownWordId
                }),
            new SyncItem("PhraseConstituent", ChangeType.Update,
                new Dictionary<string, object?>
                {
                    ["Id"] = foreignPhraseId, ["PhraseWordId"] = ownWordId
                }),
            new SyncItem("VocabularyWord", ChangeType.Update,
                new Dictionary<string, object?>
                {
                    ["Id"] = foreignWordId, ["TargetLanguageTerm"] = "Stolen"
                })
        };

        foreach (var attempted in attemptedReparents)
        {
            using var response = await _fixture.UploadAsync(owner, attempted);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // A forged first mapping must not make B's existing word writable, even
        // when that mapping appears before the word update in the same change set.
        var forgedMappingId = Guid.NewGuid().ToString();
        var forgedMapping = new SyncItem("ResourceVocabularyMapping", ChangeType.Insert,
            new Dictionary<string, object?>
            {
                ["Id"] = forgedMappingId,
                ["ResourceId"] = ownResourceId,
                ["VocabularyWordId"] = foreignWordId
            });
        var forgedWordUpdate = new SyncItem("VocabularyWord", ChangeType.Update,
            new Dictionary<string, object?>
            {
                ["Id"] = foreignWordId, ["TargetLanguageTerm"] = "Stolen"
            });
        using (var graft = await _fixture.UploadAsync(owner, forgedMapping))
            Assert.Equal(HttpStatusCode.Forbidden, graft.StatusCode);
        foreach (var orderedItems in new[]
        {
            new[] { forgedMapping, forgedWordUpdate },
            new[] { forgedWordUpdate, forgedMapping }
        })
        {
            using var ordered = await _fixture.UploadAsync(owner, orderedItems);
            Assert.Equal(HttpStatusCode.Forbidden, ordered.StatusCode);
        }

        await using var verified = _fixture.NewContext();
        Assert.Equal(foreignResourceId, (await verified.ResourceVocabularyMappings.AsNoTracking()
            .SingleAsync(mapping => mapping.Id == foreignMappingId)).ResourceId);
        Assert.False(await verified.ResourceVocabularyMappings.AsNoTracking()
            .AnyAsync(mapping => mapping.Id == forgedMappingId));
        Assert.Equal(foreignProgressId, (await verified.VocabularyLearningContexts.AsNoTracking()
            .SingleAsync(context => context.Id == foreignContextId)).VocabularyProgressId);
        Assert.Equal(foreignWordId, (await verified.PhraseConstituents.AsNoTracking()
            .SingleAsync(phrase => phrase.Id == foreignPhraseId)).PhraseWordId);
        Assert.Equal("foreign", (await verified.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.Id == foreignWordId)).TargetLanguageTerm);
    }

    [ApplicationOperationPostgresFact]
    public async Task ExistingTransitiveRowsWithMissingParent_FailClosed()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var resourceId = Guid.NewGuid().ToString();
        var wordId = Guid.NewGuid().ToString();
        var progressId = Guid.NewGuid().ToString();
        var orphanMappingId = Guid.NewGuid().ToString();
        var orphanContextId = Guid.NewGuid().ToString();
        var missingResourceId = Guid.NewGuid().ToString();
        var missingProgressId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = resourceId, UserProfileId = owner
            });
            db.VocabularyWords.Add(new VocabularyWord { Id = wordId });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resourceId, VocabularyWordId = wordId
            });
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = progressId, VocabularyWordId = wordId, UserId = owner
            });
            await db.SaveChangesAsync();

            // Only this disposable transaction bypasses FK triggers to model legacy orphans.
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync(
                "SET LOCAL session_replication_role = replica");
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = orphanMappingId,
                ResourceId = missingResourceId, VocabularyWordId = wordId
            });
            db.VocabularyLearningContexts.Add(new VocabularyLearningContext
            {
                Id = orphanContextId, VocabularyProgressId = missingProgressId
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        using var mapping = await _fixture.UploadAsync(owner, new SyncItem(
            "ResourceVocabularyMapping", ChangeType.Update, new Dictionary<string, object?>
            {
                ["Id"] = orphanMappingId,
                ["ResourceId"] = resourceId, ["VocabularyWordId"] = wordId
            }));
        Assert.Equal(HttpStatusCode.Forbidden, mapping.StatusCode);
        using var context = await _fixture.UploadAsync(owner, new SyncItem(
            "VocabularyLearningContext", ChangeType.Update, new Dictionary<string, object?>
            {
                ["Id"] = orphanContextId,
                ["VocabularyProgressId"] = progressId, ["Activity"] = "Stolen"
            }));
        Assert.Equal(HttpStatusCode.Forbidden, context.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.Equal(missingResourceId, (await verified.ResourceVocabularyMappings.AsNoTracking()
            .SingleAsync(row => row.Id == orphanMappingId)).ResourceId);
        Assert.Equal(missingProgressId, (await verified.VocabularyLearningContexts.AsNoTracking()
            .SingleAsync(row => row.Id == orphanContextId)).VocabularyProgressId);
    }

    [ApplicationOperationPostgresFact]
    public async Task NativeUploadSession_RejectsDuplicateBeginsAndForeignJsonBinaryOrHeaderlessReplay()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        using var client = _fixture.CreateClient();
        var sessionId = Guid.NewGuid();
        var storeId = Guid.NewGuid();
        var ownResourceId = Guid.NewGuid().ToString();
        var foreignResourceId = Guid.NewGuid().ToString();

        using (var begin = await _fixture.BeginUploadAsync(client, owner, sessionId, storeId, 1))
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using (var duplicate = await _fixture.BeginUploadAsync(client, foreign, sessionId, Guid.NewGuid(), 1))
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using (var duplicate = await _fixture.BeginUploadAsync(client, owner, sessionId, storeId, 1))
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var foreignRow = Resource(foreignResourceId, foreign);
        using (var json = await _fixture.UploadChunkAsync(client, foreign, sessionId, [foreignRow]))
            Assert.Equal(HttpStatusCode.Forbidden, json.StatusCode);
        using (var binary = await _fixture.UploadBinaryChunkAsync(client, foreign, sessionId, [foreignRow]))
            Assert.Equal(HttpStatusCode.Forbidden, binary.StatusCode);
        using (var legacy = await _fixture.UploadChunkAsync(
            client, foreign, sessionId, [foreignRow], nativeHeader: false))
            Assert.Equal(HttpStatusCode.Forbidden, legacy.StatusCode);
        using (var legacyBinary = await _fixture.UploadBinaryChunkAsync(
            client, foreign, sessionId, [foreignRow], nativeHeader: false))
            Assert.Equal(HttpStatusCode.Forbidden, legacyBinary.StatusCode);
        foreach (var binary in new[] { false, true })
        {
            using var wrongOwner = await _fixture.CompleteUploadAsync(
                client, foreign, sessionId, binary);
            Assert.Equal(HttpStatusCode.Forbidden, wrongOwner.StatusCode);
            using var headerless = await _fixture.CompleteUploadAsync(
                client, foreign, sessionId, binary, nativeHeader: false);
            Assert.Equal(HttpStatusCode.Forbidden, headerless.StatusCode);
        }

        using (var chunk = await _fixture.UploadChunkAsync(
            client, owner, sessionId, [Resource(ownResourceId, owner)]))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        using (var complete = await _fixture.CompleteUploadAsync(client, owner, sessionId))
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using (var replay = await _fixture.CompleteUploadAsync(client, owner, sessionId))
            Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        using (var reuse = await _fixture.BeginUploadAsync(
            client, owner, sessionId, storeId, 0))
            Assert.Equal(HttpStatusCode.OK, reuse.StatusCode);
        using (var reusedComplete = await _fixture.CompleteUploadAsync(client, owner, sessionId))
            Assert.Equal(HttpStatusCode.OK, reusedComplete.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.True(await verified.LearningResources.AnyAsync(row => row.Id == ownResourceId));
        Assert.False(await verified.LearningResources.AnyAsync(row => row.Id == foreignResourceId));
    }

    [ApplicationOperationPostgresFact]
    public async Task NativeUploadSession_BinaryPayloadCannotBeCompletedByAnotherOwner()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        using var client = _fixture.CreateClient();
        var sessionId = Guid.NewGuid();
        var foreignResourceId = Guid.NewGuid().ToString();

        using (var begin = await _fixture.BeginUploadAsync(
            client, foreign, sessionId, Guid.NewGuid(), 1))
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using (var chunk = await _fixture.UploadBinaryChunkAsync(
            client, foreign, sessionId, [Resource(foreignResourceId, foreign)]))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        foreach (var binary in new[] { false, true })
        {
            using var stolen = await _fixture.CompleteUploadAsync(client, owner, sessionId, binary);
            Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);
        }
        using (var complete = await _fixture.CompleteUploadAsync(
            client, foreign, sessionId, binary: true))
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.Equal(foreign, (await verified.LearningResources.AsNoTracking()
            .SingleAsync(row => row.Id == foreignResourceId)).UserProfileId);
    }

    [ApplicationOperationPostgresFact]
    public async Task OwnerCannotCompleteOtherOwnersSession_EvenWhenStagedRowClaimsOwnOwner()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        using var client = _fixture.CreateClient();
        var sessionId = Guid.NewGuid();
        var ownResourceId = Guid.NewGuid().ToString();
        using (var begin = await _fixture.BeginUploadAsync(
            client, foreign, sessionId, Guid.NewGuid(), 1))
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using (var chunk = await _fixture.UploadChunkAsync(
            client, foreign, sessionId, [Resource(ownResourceId, owner)]))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        foreach (var binary in new[] { false, true })
        {
            using var stolen = await _fixture.CompleteUploadAsync(
                client, owner, sessionId, binary);
            Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);
        }
        using (var foreignComplete = await _fixture.CompleteUploadAsync(
            client, foreign, sessionId))
            Assert.Equal(HttpStatusCode.Forbidden, foreignComplete.StatusCode);
        await using var verified = _fixture.NewContext();
        Assert.False(await verified.LearningResources.AsNoTracking()
            .AnyAsync(row => row.Id == ownResourceId));
    }

    [ApplicationOperationPostgresFact]
    public async Task HeaderlessLegacyUpload_RemainsUsable_WithoutOpeningNativeSessions()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        using var client = _fixture.CreateClient();
        var nativeId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();
        var resourceId = Guid.NewGuid().ToString();

        using (var native = await _fixture.BeginUploadAsync(
            client, owner, nativeId, Guid.NewGuid(), 0))
            Assert.Equal(HttpStatusCode.OK, native.StatusCode);
        using (var replay = await _fixture.BeginUploadAsync(
            client, foreign, nativeId, Guid.NewGuid(), 0, nativeHeader: false))
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        using (var legacy = await _fixture.BeginUploadAsync(
            client, foreign, legacyId, Guid.NewGuid(), 1, nativeHeader: false))
            Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        using (var replay = await _fixture.UploadChunkAsync(
            client, owner, legacyId, [Resource(resourceId, owner)]))
            Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        using (var chunk = await _fixture.UploadChunkAsync(
            client, foreign, legacyId, [Resource(resourceId, foreign)], nativeHeader: false))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        using (var complete = await _fixture.CompleteUploadAsync(
            client, foreign, legacyId, nativeHeader: false))
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using (var complete = await _fixture.CompleteUploadAsync(client, owner, nativeId))
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.Equal(foreign, (await verified.LearningResources.AsNoTracking()
            .SingleAsync(row => row.Id == resourceId)).UserProfileId);
    }

    [ApplicationOperationPostgresFact]
    public async Task NativeStoreCursor_RejectsForeignOwnerAcrossApiInstances_AndPreservesPendingRows()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var storeId = Guid.NewGuid();
        using var client = _fixture.CreateClient();
        var initial = await _fixture.StartDownloadAsync(client, owner, storeId: storeId);
        using (var saved = await _fixture.SaveVersionAsync(
            client, owner, storeId, initial.SourceAnchor.Version))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var resourceId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = resourceId, UserProfileId = owner, Title = "Pending after the first cursor"
            });
            await db.SaveChangesAsync();
        }

        using (var replica = _fixture.CreateReplica())
        using (var replicaClient = replica.CreateClient())
        {
            using var stolenWrite = await _fixture.SaveVersionAsync(
                replicaClient, foreign, storeId, initial.SourceAnchor.Version + 1000);
            Assert.Equal(HttpStatusCode.Forbidden, stolenWrite.StatusCode);
            using var stolenRead = await _fixture.GetAsync(
                replicaClient, $"/api/sync-agent/changes-bulk/{storeId}", foreign);
            Assert.Equal(HttpStatusCode.Forbidden, stolenRead.StatusCode);
        }

        using var restartedReplica = _fixture.CreateReplica();
        using var restartedClient = restartedReplica.CreateClient();
        using (var stolenAfterRestart = await _fixture.SaveVersionAsync(
            restartedClient, foreign, storeId, initial.SourceAnchor.Version + 1000))
            Assert.Equal(HttpStatusCode.Forbidden, stolenAfterRestart.StatusCode);
        var pending = await _fixture.StartDownloadAsync(restartedClient, owner, storeId: storeId);
        using var chunk = await _fixture.GetAsync(restartedClient,
            $"/api/sync-agent/changes-bulk-item/{pending.SessionId}/0/1000", owner);
        Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        var items = await chunk.Content.ReadFromJsonAsync<SyncItem[]>();
        Assert.Contains(items!, item => item.TableName == "LearningResource"
            && item.Values["Id"].Value?.ToString() == resourceId);
    }

    [ApplicationOperationPostgresFact]
    public async Task NativeStore_BindsUploadSourceAndRemapsTarget_WithoutChangingLegacyCursor()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var storeId = Guid.NewGuid();
        using var client = _fixture.CreateClient();
        using (var uploaded = await _fixture.UploadLegacyAsync(
            client, owner, storeId, Resource(Guid.NewGuid().ToString(), owner)))
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var legacy = await _fixture.StartDownloadAsync(
            client, owner, nativeHeader: false, storeId: storeId);
        using (var savedLegacy = await _fixture.SaveVersionAsync(
            client, owner, storeId, legacy.SourceAnchor.Version, nativeHeader: false))
            Assert.Equal(HttpStatusCode.OK, savedLegacy.StatusCode);

        var native = await _fixture.StartDownloadAsync(client, owner, storeId: storeId);
        Assert.Equal(0, native.TotalChanges);
        var sessionId = Guid.NewGuid();
        using (var begin = await _fixture.BeginUploadAsync(client, owner, sessionId, storeId, 0))
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using (var complete = await _fixture.CompleteUploadAsync(client, owner, sessionId))
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var nextNative = await _fixture.StartDownloadAsync(client, owner, storeId: storeId);
        Assert.Equal(storeId, nextNative.TargetAnchor.StoreId);
        using (var foreignBegin = await _fixture.BeginUploadAsync(
            client, foreign, Guid.NewGuid(), storeId, 0))
            Assert.Equal(HttpStatusCode.Forbidden, foreignBegin.StatusCode);

        var legacyAfterNative = await _fixture.StartDownloadAsync(
            client, owner, nativeHeader: false, storeId: storeId);
        Assert.Equal(0, legacyAfterNative.TotalChanges);
        using var legacyVersion = await _fixture.SaveVersionAsync(
            client, owner, storeId, legacy.SourceAnchor.Version, nativeHeader: false);
        Assert.Equal(HttpStatusCode.OK, legacyVersion.StatusCode);
    }

    [ApplicationOperationPostgresFact]
    public async Task ExistingStoreWithOwnedChanges_DeniesForeignFirstUseBeforeBinding()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        using var client = _fixture.CreateClient();
        var storeId = Guid.NewGuid();
        var resourceId = Guid.NewGuid().ToString();
        using (var uploaded = await _fixture.UploadLegacyAsync(
            client, owner, storeId, Resource(resourceId, owner)))
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);

        using (var foreignRead = await _fixture.GetAsync(
            client, $"/api/sync-agent/changes-bulk/{storeId}", foreign))
            Assert.Equal(HttpStatusCode.Forbidden, foreignRead.StatusCode);
        using (var foreignWrite = await _fixture.SaveVersionAsync(
            client, foreign, storeId, 5000))
            Assert.Equal(HttpStatusCode.Forbidden, foreignWrite.StatusCode);
        using (var ownerRead = await _fixture.GetAsync(
            client, $"/api/sync-agent/changes-bulk/{storeId}", owner))
            Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        await using var verified = _fixture.NewContext();
        Assert.Equal(owner, (await verified.LearningResources.AsNoTracking()
            .SingleAsync(row => row.Id == resourceId)).UserProfileId);
    }

    [ApplicationOperationPostgresFact]
    public async Task ExistingReadOnlyLegacyCursor_RejectsUnprovableNativeClaim()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        using var client = _fixture.CreateClient();
        var storeId = Guid.NewGuid();
        var legacy = await _fixture.StartDownloadAsync(
            client, owner, nativeHeader: false, storeId: storeId);
        using (var saved = await _fixture.SaveVersionAsync(
            client, owner, storeId, legacy.SourceAnchor.Version, nativeHeader: false))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        foreach (var attemptedOwner in new[] { owner, foreign })
        {
            using var native = await _fixture.GetAsync(
                client, $"/api/sync-agent/changes-bulk/{storeId}", attemptedOwner);
            Assert.Equal(HttpStatusCode.Forbidden, native.StatusCode);
        }
        using var legacyAfter = await _fixture.GetAsync(
            client, $"/api/sync-agent/changes-bulk/{storeId}",
            owner, nativeHeader: false);
        Assert.Equal(HttpStatusCode.OK, legacyAfter.StatusCode);
    }

    [ApplicationOperationPostgresFact]
    public async Task NativeDownload_EmitsUtcTimestampValues_WithoutChangingLegacyFormat()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync(
            "native_utc", migrate: false);
        await using (var schema = new UtcTimestampFixtureDbContext(database.ConnectionString))
            await schema.Database.EnsureCreatedAsync();
        var provider = new PostgreSQLSyncProvider(
            new PostgreSQLSyncConfigurationBuilder(database.ConnectionString)
                .ConfigureSyncTables().Build(), ProviderMode.Remote);
        await provider.ApplyProvisionAsync();
        var owner = Guid.NewGuid().ToString();
        var foreign = Guid.NewGuid().ToString();
        var rowId = Guid.NewGuid().ToString();
        var completedAt = new DateTime(2026, 9, 29, 2, 47, 18, DateTimeKind.Utc)
            .AddTicks(1_290_000);
        await using (var db = new UtcTimestampFixtureDbContext(database.ConnectionString))
        {
            db.UserProfiles.AddRange(
                new UserProfile { Id = owner, Email = $"{owner}@example.test" },
                new UserProfile { Id = foreign, Email = $"{foreign}@example.test" });
            db.DailyPlanCompletions.Add(new DailyPlanCompletion
            {
                Id = rowId, UserProfileId = owner,
                Date = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc),
                PlanItemId = Guid.NewGuid().ToString(), ActivityType = "VocabularyReview",
                IsCompleted = true, CompletedAt = completedAt, MinutesSpent = 3,
                CreatedAt = completedAt.AddHours(-1), UpdatedAt = completedAt
            });
            await db.SaveChangesAsync();
        }

        using var api = new NativeOwnerSyncPostgresApiFactory(database.ConnectionString);
        using var client = api.CreateClient();
        var native = await _fixture.StartDownloadAsync(client, owner);
        using var nativeChunk = await _fixture.GetAsync(client,
            $"/api/sync-agent/changes-bulk-item/{native.SessionId}/0/1000", owner);
        Assert.Equal(HttpStatusCode.OK, nativeChunk.StatusCode);
        var nativeItems = await nativeChunk.Content.ReadFromJsonAsync<SyncItem[]>();
        var nativeRow = Assert.Single(nativeItems!, item =>
            item.TableName == "DailyPlanCompletion"
            && item.Values["Id"].Value?.ToString() == rowId);
        var nativeTimestamp = Timestamp(nativeRow, "CompletedAt");
        Assert.Equal(DateTimeKind.Utc, nativeTimestamp.Kind);
        Assert.Equal(completedAt, nativeTimestamp);
        Assert.Equal(new DateTime(2026, 9, 29), Timestamp(nativeRow, "Date").Date);

        var legacy = await _fixture.StartDownloadAsync(client, owner, nativeHeader: false);
        using var legacyChunk = await _fixture.GetAsync(client,
            $"/api/sync-agent/changes-bulk-item/{legacy.SessionId}/0/1000",
            owner, nativeHeader: false);
        var legacyItems = await legacyChunk.Content.ReadFromJsonAsync<SyncItem[]>();
        var legacyRow = Assert.Single(legacyItems!, item =>
            item.TableName == "DailyPlanCompletion"
            && item.Values["Id"].Value?.ToString() == rowId);
        var legacyTimestamp = Timestamp(legacyRow, "CompletedAt");
        Assert.Equal(DateTimeKind.Local, legacyTimestamp.Kind);
        Assert.Equal(completedAt, legacyTimestamp.ToUniversalTime());

        var binary = await _fixture.StartDownloadAsync(client, owner);
        using var binaryChunk = await _fixture.GetAsync(client,
            $"/api/sync-agent/changes-bulk-item-binary/{binary.SessionId}/0/1000", owner);
        Assert.Equal(HttpStatusCode.OK, binaryChunk.StatusCode);
        await using var body = await binaryChunk.Content.ReadAsStreamAsync();
        var binaryItems = Assert.IsType<List<SyncItem>>(
            await CoreSyncMessagePackSerializer.DeserializeAsync<object>(body));
        var binaryRow = Assert.Single(binaryItems, item =>
            item.TableName == "DailyPlanCompletion"
            && item.Values["Id"].Value?.ToString() == rowId);
        Assert.Equal(completedAt, Timestamp(binaryRow, "CompletedAt").ToUniversalTime());
    }

    [ApplicationOperationPostgresFact]
    public async Task TwoApiInstances_RacingFirstNativeStoreUse_CommitOnlyOneOwner()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var storeId = Guid.NewGuid();
        using (var first = _fixture.CreateReplica())
        using (var second = _fixture.CreateReplica())
        using (var firstClient = first.CreateClient())
        using (var secondClient = second.CreateClient())
        {
            var firstRead = _fixture.GetAsync(
                firstClient, $"/api/sync-agent/changes-bulk/{storeId}", owner);
            var secondRead = _fixture.GetAsync(
                secondClient, $"/api/sync-agent/changes-bulk/{storeId}", foreign);
            using var ownerResult = await firstRead;
            using var foreignResult = await secondRead;
            Assert.Contains(HttpStatusCode.OK, new[]
            {
                ownerResult.StatusCode, foreignResult.StatusCode
            });
            Assert.Contains(HttpStatusCode.Forbidden, new[]
            {
                ownerResult.StatusCode, foreignResult.StatusCode
            });
            var winner = ownerResult.StatusCode == HttpStatusCode.OK ? owner : foreign;
            var loser = winner == owner ? foreign : owner;

            using var restarted = _fixture.CreateReplica();
            using var restartedClient = restarted.CreateClient();
            using var rejected = await _fixture.SaveVersionAsync(
                restartedClient, loser, storeId, 5000);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            using var allowed = await _fixture.GetAsync(
                restartedClient, $"/api/sync-agent/changes-bulk/{storeId}", winner);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
    }

    [ApplicationOperationPostgresFact]
    public async Task HeaderlessLegacyBinaryUpload_StillReadsItsSingleBodyAndCompletes()
    {
        var (_, foreign) = await _fixture.SeedOwnersAsync();
        using var client = _fixture.CreateClient();
        var sessionId = Guid.NewGuid();
        var resourceId = Guid.NewGuid().ToString();
        using (var begin = await _fixture.BeginUploadAsync(
            client, foreign, sessionId, Guid.NewGuid(), 1, nativeHeader: false))
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using (var chunk = await _fixture.UploadBinaryChunkAsync(
            client, foreign, sessionId, [Resource(resourceId, foreign)],
            nativeHeader: false))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        using (var complete = await _fixture.CompleteUploadAsync(
            client, foreign, sessionId, binary: true, nativeHeader: false))
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        await using var verified = _fixture.NewContext();
        Assert.Equal(foreign, (await verified.LearningResources.AsNoTracking()
            .SingleAsync(row => row.Id == resourceId)).UserProfileId);
    }

    [ApplicationOperationPostgresFact]
    public async Task ExpiredNativeUpload_EvictsStagedRowsWithoutTouchingHeaderlessSession()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var clock = new ControlledUploadTime();
        using var replica = _fixture.CreateReplicaWithClock(clock);
        using var client = replica.CreateClient();
        var sessions = replica.Services.GetRequiredService<NativeSyncUploadSessions>();
        var cache = replica.Services.GetRequiredService<IMemoryCache>();
        var nativeId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();
        using (var native = await _fixture.BeginUploadAsync(
            client, owner, nativeId, Guid.NewGuid(), 0))
            Assert.Equal(HttpStatusCode.OK, native.StatusCode);
        using (var legacy = await _fixture.BeginUploadAsync(
            client, foreign, legacyId, Guid.NewGuid(), 0, nativeHeader: false))
            Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.True(cache.TryGetValue(nativeId, out _));
        Assert.True(cache.TryGetValue(legacyId, out _));

        clock.Advance(TimeSpan.FromMinutes(21));
        sessions.PruneExpired();
        Assert.False(cache.TryGetValue(nativeId, out _));
        Assert.True(cache.TryGetValue(legacyId, out _));
        using (var expired = await _fixture.CompleteUploadAsync(client, owner, nativeId))
            Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        using (var legacy = await _fixture.CompleteUploadAsync(
            client, foreign, legacyId, nativeHeader: false))
            Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
    }

    [ApplicationOperationPostgresFact]
    public async Task NativeChildRows_RejectForeignOwnerLinkedParents()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var conversationId = Guid.NewGuid().ToString();
        var channelId = Guid.NewGuid().ToString();
        var resourceId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.Conversations.Add(new Conversation
            {
                Id = conversationId, UserProfileId = foreign
            });
            db.MonitoredChannels.Add(new MonitoredChannel
            {
                Id = channelId, UserProfileId = foreign
            });
            db.LearningResources.Add(new LearningResource
            {
                Id = resourceId, UserProfileId = foreign
            });
            await db.SaveChangesAsync();
        }

        using (var chunk = await _fixture.UploadAsync(owner, new SyncItem(
            "ConversationChunk", ChangeType.Insert, new Dictionary<string, object?>
            {
                ["Id"] = Guid.NewGuid().ToString(), ["UserProfileId"] = owner,
                ["ConversationId"] = conversationId, ["SentTime"] = DateTime.UtcNow,
                ["Role"] = 0
            })))
            Assert.Equal(HttpStatusCode.Forbidden, chunk.StatusCode);
        using (var video = await _fixture.UploadAsync(owner, new SyncItem(
            "VideoImport", ChangeType.Insert, new Dictionary<string, object?>
            {
                ["Id"] = Guid.NewGuid().ToString(), ["UserProfileId"] = owner,
                ["MonitoredChannelId"] = channelId, ["LearningResourceId"] = resourceId,
                ["Status"] = 0
            })))
            Assert.Equal(HttpStatusCode.Forbidden, video.StatusCode);
    }

    [ApplicationOperationPostgresFact]
    public async Task EveryOwnerLinkedParent_RejectsForeignAndOwnerlessReferences()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var ownConversation = Guid.NewGuid().ToString();
        var foreignConversation = Guid.NewGuid().ToString();
        var ownerlessConversation = Guid.NewGuid().ToString();
        var ownChannel = Guid.NewGuid().ToString();
        var foreignChannel = Guid.NewGuid().ToString();
        var ownerlessChannel = Guid.NewGuid().ToString();
        var ownResource = Guid.NewGuid().ToString();
        var foreignResource = Guid.NewGuid().ToString();
        var ownerlessResource = Guid.NewGuid().ToString();
        var ownSkill = Guid.NewGuid().ToString();
        var foreignSkill = Guid.NewGuid().ToString();
        var ownerlessSkill = Guid.NewGuid().ToString();
        var wordId = Guid.NewGuid().ToString();
        var foreignWord = Guid.NewGuid().ToString();
        var ownerlessWord = Guid.NewGuid().ToString();
        var progressId = Guid.NewGuid().ToString();
        var foreignProgress = Guid.NewGuid().ToString();
        var ownerlessProgress = Guid.NewGuid().ToString();

        await using (var db = _fixture.NewContext())
        {
            db.Conversations.AddRange(
                new Conversation { Id = ownConversation, UserProfileId = owner },
                new Conversation { Id = foreignConversation, UserProfileId = foreign },
                new Conversation { Id = ownerlessConversation });
            db.MonitoredChannels.AddRange(
                new MonitoredChannel { Id = ownChannel, UserProfileId = owner },
                new MonitoredChannel { Id = foreignChannel, UserProfileId = foreign },
                new MonitoredChannel { Id = ownerlessChannel });
            db.LearningResources.AddRange(
                new LearningResource { Id = ownResource, UserProfileId = owner },
                new LearningResource { Id = foreignResource, UserProfileId = foreign },
                new LearningResource { Id = ownerlessResource });
            db.SkillProfiles.AddRange(
                new SkillProfile { Id = ownSkill, UserProfileId = owner },
                new SkillProfile { Id = foreignSkill, UserProfileId = foreign },
                new SkillProfile { Id = ownerlessSkill });
            db.VocabularyWords.AddRange(
                new VocabularyWord { Id = wordId },
                new VocabularyWord { Id = foreignWord },
                new VocabularyWord { Id = ownerlessWord });
            db.ResourceVocabularyMappings.AddRange(
                new ResourceVocabularyMapping
                {
                    Id = Guid.NewGuid().ToString(),
                    ResourceId = ownResource, VocabularyWordId = wordId
                },
                new ResourceVocabularyMapping
                {
                    Id = Guid.NewGuid().ToString(),
                    ResourceId = foreignResource, VocabularyWordId = foreignWord
                });
            db.VocabularyProgresses.AddRange(
                new VocabularyProgress
                {
                    Id = progressId, UserId = owner, VocabularyWordId = wordId
                },
                new VocabularyProgress
                {
                    Id = foreignProgress, UserId = foreign, VocabularyWordId = foreignWord
                },
                new VocabularyProgress
                {
                    Id = ownerlessProgress, UserId = "", VocabularyWordId = ownerlessWord
                });
            await db.SaveChangesAsync();
        }

        var attemptedIds = new List<(string Table, string Id)>();
        foreach (var useForeign in new[] { true, false })
        {
            var conversation = useForeign ? foreignConversation : ownerlessConversation;
            var channel = useForeign ? foreignChannel : ownerlessChannel;
            var resource = useForeign ? foreignResource : ownerlessResource;
            var skill = useForeign ? foreignSkill : ownerlessSkill;
            var word = useForeign ? foreignWord : ownerlessWord;
            var progress = useForeign ? foreignProgress : ownerlessProgress;
            var cases = new (string Reference, SyncItem Item)[]
            {
                ("ConversationChunk.ConversationId",
                    Chunk(Guid.NewGuid().ToString(), owner, conversation)),
                ("VideoImport.MonitoredChannelId",
                    Video(Guid.NewGuid().ToString(), owner, channel, ownResource)),
                ("VideoImport.LearningResourceId",
                    Video(Guid.NewGuid().ToString(), owner, ownChannel, resource)),
                ("DailyPlanCompletion.ResourceId",
                    Completion(Guid.NewGuid().ToString(), owner, resource, ownSkill)),
                ("DailyPlanCompletion.SkillId",
                    Completion(Guid.NewGuid().ToString(), owner, ownResource, skill)),
                ("LearningResource.SkillID",
                    Resource(Guid.NewGuid().ToString(), owner, skill)),
                ("VocabularyLearningContext.LearningResourceId",
                    Context(Guid.NewGuid().ToString(), progressId, resource)),
                ("VocabularyLearningContext.VocabularyProgressId",
                    Context(Guid.NewGuid().ToString(), progress, ownResource)),
                ("ResourceVocabularyMapping.ResourceId",
                    new SyncItem("ResourceVocabularyMapping", ChangeType.Insert,
                        new Dictionary<string, object?>
                        {
                            ["Id"] = Guid.NewGuid().ToString(),
                            ["ResourceId"] = resource, ["VocabularyWordId"] = wordId
                        })),
                ("VocabularyProgress.VocabularyWordId",
                    new SyncItem("VocabularyProgress", ChangeType.Insert,
                        new Dictionary<string, object?>
                        {
                            ["Id"] = Guid.NewGuid().ToString(),
                            ["UserId"] = owner, ["VocabularyWordId"] = word
                        })),
                ("PhraseConstituent.PhraseWordId",
                    new SyncItem("PhraseConstituent", ChangeType.Insert,
                        new Dictionary<string, object?>
                        {
                            ["Id"] = Guid.NewGuid().ToString(),
                            ["PhraseWordId"] = word, ["ConstituentWordId"] = wordId
                        })),
                ("PhraseConstituent.ConstituentWordId",
                    new SyncItem("PhraseConstituent", ChangeType.Insert,
                        new Dictionary<string, object?>
                        {
                            ["Id"] = Guid.NewGuid().ToString(),
                            ["PhraseWordId"] = wordId, ["ConstituentWordId"] = word
                        }))
            };
            foreach (var (reference, item) in cases)
            {
                using var response = await _fixture.UploadAsync(owner, item);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                    $"{reference} with {(useForeign ? "foreign" : "ownerless")} parent returned {response.StatusCode}");
                attemptedIds.Add((item.TableName, item.Values["Id"].Value!.ToString()!));
            }
        }

        await using var verified = _fixture.NewContext();
        var rejectedChunks = attemptedIds.Where(entry => entry.Table == "ConversationChunk")
            .Select(entry => entry.Id).ToArray();
        var rejectedVideos = attemptedIds.Where(entry => entry.Table == "VideoImport")
            .Select(entry => entry.Id).ToArray();
        var rejectedCompletions = attemptedIds.Where(entry => entry.Table == "DailyPlanCompletion")
            .Select(entry => entry.Id).ToArray();
        var rejectedResources = attemptedIds.Where(entry => entry.Table == "LearningResource")
            .Select(entry => entry.Id).ToArray();
        var rejectedContexts = attemptedIds.Where(entry => entry.Table == "VocabularyLearningContext")
            .Select(entry => entry.Id).ToArray();
        var rejectedMappings = attemptedIds.Where(entry => entry.Table == "ResourceVocabularyMapping")
            .Select(entry => entry.Id).ToArray();
        var rejectedProgress = attemptedIds.Where(entry => entry.Table == "VocabularyProgress")
            .Select(entry => entry.Id).ToArray();
        var rejectedPhrases = attemptedIds.Where(entry => entry.Table == "PhraseConstituent")
            .Select(entry => entry.Id).ToArray();
        Assert.False(await verified.ConversationChunks.AnyAsync(row => rejectedChunks.Contains(row.Id)));
        Assert.False(await verified.VideoImports.AnyAsync(row => rejectedVideos.Contains(row.Id)));
        Assert.False(await verified.DailyPlanCompletions.AnyAsync(row => rejectedCompletions.Contains(row.Id)));
        Assert.False(await verified.LearningResources.AnyAsync(row => rejectedResources.Contains(row.Id)));
        Assert.False(await verified.VocabularyLearningContexts.AnyAsync(row => rejectedContexts.Contains(row.Id)));
        Assert.False(await verified.ResourceVocabularyMappings.AnyAsync(row => rejectedMappings.Contains(row.Id)));
        Assert.False(await verified.VocabularyProgresses.AnyAsync(row => rejectedProgress.Contains(row.Id)));
        Assert.False(await verified.PhraseConstituents.AnyAsync(row => rejectedPhrases.Contains(row.Id)));
    }

    [ApplicationOperationPostgresFact]
    public async Task ExistingOwnedProgress_CanReceiveSameUploadMappingAndContextInEitherOrder()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var resourceId = Guid.NewGuid().ToString();
        var wordId = Guid.NewGuid().ToString();
        var progressId = Guid.NewGuid().ToString();
        var mappingId = Guid.NewGuid().ToString();
        var contextId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.Add(new LearningResource
            {
                Id = resourceId, UserProfileId = owner
            });
            db.VocabularyWords.Add(new VocabularyWord { Id = wordId });
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = progressId, UserId = owner, VocabularyWordId = wordId
            });
            await db.SaveChangesAsync();
        }

        using var response = await _fixture.UploadAsync(owner,
            Context(contextId, progressId, resourceId),
            new SyncItem("ResourceVocabularyMapping", ChangeType.Insert,
                new Dictionary<string, object?>
                {
                    ["Id"] = mappingId, ["ResourceId"] = resourceId,
                    ["VocabularyWordId"] = wordId
                }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verified = _fixture.NewContext();
        Assert.True(await verified.ResourceVocabularyMappings.AsNoTracking()
            .AnyAsync(row => row.Id == mappingId));
        Assert.Equal(progressId, (await verified.VocabularyLearningContexts.AsNoTracking()
            .SingleAsync(row => row.Id == contextId)).VocabularyProgressId);
    }

    [ApplicationOperationPostgresFact]
    public async Task NonSyncedScenarioAndLegacyVocabularyList_RemainSharedReferences()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var listId = Guid.NewGuid().ToString();
        int scenarioId;
        await using (var db = _fixture.NewContext())
        {
            var scenario = new ConversationScenario
            {
                Name = "Shared scenario", PersonaName = "Partner",
                PersonaDescription = "Conversation partner",
                SituationDescription = "Learning conversation",
                IsPredefined = true
            };
            db.ConversationScenarios.Add(scenario);
            db.VocabularyLists.Add(new VocabularyList
            {
                Id = listId, Name = "Legacy global vocabulary list"
            });
            await db.SaveChangesAsync();
            scenarioId = scenario.Id;
        }

        var conversationId = Guid.NewGuid().ToString();
        var resourceId = Guid.NewGuid().ToString();
        using var response = await _fixture.UploadAsync(owner,
            new SyncItem("Conversation", ChangeType.Insert, new Dictionary<string, object?>
            {
                ["Id"] = conversationId, ["UserProfileId"] = owner,
                ["ScenarioId"] = scenarioId, ["Language"] = "Korean",
                ["CreatedAt"] = DateTime.UtcNow
            }),
            new SyncItem("LearningResource", ChangeType.Insert, new Dictionary<string, object?>
            {
                ["Id"] = resourceId, ["UserProfileId"] = owner,
                ["OldVocabularyListID"] = listId, ["Title"] = "Shared reference",
                ["CreatedAt"] = DateTime.UtcNow, ["UpdatedAt"] = DateTime.UtcNow,
                ["IsSmartResource"] = false
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using (var verified = _fixture.NewContext())
        {
            Assert.Equal(scenarioId, (await verified.Conversations.AsNoTracking()
                .SingleAsync(row => row.Id == conversationId)).ScenarioId);
            Assert.Equal(listId, (await verified.LearningResources.AsNoTracking()
                .SingleAsync(row => row.Id == resourceId)).OldVocabularyListID);
        }

        foreach (var table in new[] { "Challenge", "VocabularyList" })
        {
            using var ownerless = await _fixture.UploadAsync(owner, new SyncItem(
                table, ChangeType.Insert, new Dictionary<string, object?>
                {
                    ["Id"] = Guid.NewGuid().ToString()
                }));
            Assert.Equal(HttpStatusCode.Forbidden, ownerless.StatusCode);
        }
    }

    [ApplicationOperationPostgresFact]
    public async Task OwnedPhrase_CanReferenceExistingWordSharedWithAnotherOwner()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var ownResource = Guid.NewGuid().ToString();
        var foreignResource = Guid.NewGuid().ToString();
        var phraseWord = Guid.NewGuid().ToString();
        var sharedWord = Guid.NewGuid().ToString();
        var phraseId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.AddRange(
                new LearningResource { Id = ownResource, UserProfileId = owner },
                new LearningResource { Id = foreignResource, UserProfileId = foreign });
            db.VocabularyWords.AddRange(
                new VocabularyWord { Id = phraseWord },
                new VocabularyWord { Id = sharedWord });
            db.ResourceVocabularyMappings.AddRange(
                new ResourceVocabularyMapping
                {
                    Id = Guid.NewGuid().ToString(),
                    ResourceId = ownResource, VocabularyWordId = phraseWord
                },
                new ResourceVocabularyMapping
                {
                    Id = Guid.NewGuid().ToString(),
                    ResourceId = ownResource, VocabularyWordId = sharedWord
                },
                new ResourceVocabularyMapping
                {
                    Id = Guid.NewGuid().ToString(),
                    ResourceId = foreignResource, VocabularyWordId = sharedWord
                });
            await db.SaveChangesAsync();
        }

        using var response = await _fixture.UploadAsync(owner, new SyncItem(
            "PhraseConstituent", ChangeType.Insert, new Dictionary<string, object?>
            {
                ["Id"] = phraseId, ["PhraseWordId"] = phraseWord,
                ["ConstituentWordId"] = sharedWord, ["CreatedAt"] = DateTime.UtcNow
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verified = _fixture.NewContext();
        Assert.Equal(sharedWord, (await verified.PhraseConstituents.AsNoTracking()
            .SingleAsync(row => row.Id == phraseId)).ConstituentWordId);
        Assert.True(await verified.ResourceVocabularyMappings.AsNoTracking()
            .AnyAsync(mapping => mapping.ResourceId == foreignResource
                && mapping.VocabularyWordId == sharedWord));
    }

    [ApplicationOperationPostgresFact]
    public async Task ChildFirstOwnerGraph_InsertsSameUploadConversationsImportsResourcesSkillsAndPlanItems()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var conversationId = Guid.NewGuid().ToString();
        var chunkId = Guid.NewGuid().ToString();
        var channelId = Guid.NewGuid().ToString();
        var videoId = Guid.NewGuid().ToString();
        var skillId = Guid.NewGuid().ToString();
        var resourceId = Guid.NewGuid().ToString();
        var completionId = Guid.NewGuid().ToString();

        using var response = await _fixture.UploadAsync(owner,
            Chunk(chunkId, owner, conversationId),
            Video(videoId, owner, channelId, resourceId),
            Completion(completionId, owner, resourceId, skillId),
            Resource(resourceId, owner, skillId),
            new SyncItem("MonitoredChannel", ChangeType.Insert, new Dictionary<string, object?>
            {
                ["Id"] = channelId, ["UserProfileId"] = owner,
                ["CreatedAt"] = DateTime.UtcNow, ["UpdatedAt"] = DateTime.UtcNow,
                ["IsActive"] = true, ["CheckIntervalHours"] = 6
            }),
            new SyncItem("Conversation", ChangeType.Insert, new Dictionary<string, object?>
            {
                ["Id"] = conversationId, ["UserProfileId"] = owner,
                ["CreatedAt"] = DateTime.UtcNow, ["Language"] = "Korean"
            }),
            new SyncItem("SkillProfile", ChangeType.Insert, new Dictionary<string, object?>
            {
                ["Id"] = skillId, ["UserProfileId"] = owner,
                ["Language"] = "Korean", ["IsArchived"] = false,
                ["CreatedAt"] = DateTime.UtcNow, ["UpdatedAt"] = DateTime.UtcNow
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verified = _fixture.NewContext();
        Assert.True(await verified.Conversations.AsNoTracking()
            .AnyAsync(row => row.Id == conversationId), "Same-upload conversation was not persisted.");
        Assert.True(await verified.MonitoredChannels.AsNoTracking()
            .AnyAsync(row => row.Id == channelId), "Same-upload channel was not persisted.");
        Assert.True(await verified.SkillProfiles.AsNoTracking()
            .AnyAsync(row => row.Id == skillId), "Same-upload skill was not persisted.");
        Assert.True(await verified.LearningResources.AsNoTracking()
            .AnyAsync(row => row.Id == resourceId), "Same-upload resource was not persisted.");
        Assert.True(await verified.ConversationChunks.AsNoTracking()
            .AnyAsync(row => row.Id == chunkId), "Same-upload chunk was not persisted.");
        Assert.True(await verified.VideoImports.AsNoTracking()
            .AnyAsync(row => row.Id == videoId), "Same-upload video was not persisted.");
        Assert.True(await verified.DailyPlanCompletions.AsNoTracking()
            .AnyAsync(row => row.Id == completionId), "Same-upload plan item was not persisted.");
        Assert.Equal(conversationId, (await verified.ConversationChunks.AsNoTracking()
            .SingleAsync(row => row.Id == chunkId)).ConversationId);
        var storedVideo = await verified.VideoImports.AsNoTracking()
            .SingleAsync(row => row.Id == videoId);
        Assert.Equal(channelId, storedVideo.MonitoredChannelId);
        Assert.Equal(resourceId, storedVideo.LearningResourceId);
        Assert.Equal(skillId, (await verified.LearningResources.AsNoTracking()
            .SingleAsync(row => row.Id == resourceId)).SkillID);
        Assert.Equal(resourceId, (await verified.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(row => row.Id == completionId)).ResourceId);
    }

    [ApplicationOperationPostgresFact]
    public async Task OwnedChunk_WithPersistedConversation_IsInserted()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var conversationId = Guid.NewGuid().ToString();
        var chunkId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.Conversations.Add(new Conversation
            {
                Id = conversationId, UserProfileId = owner
            });
            await db.SaveChangesAsync();
        }

        using var upload = await _fixture.UploadAsync(owner,
            Chunk(chunkId, owner, conversationId));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        await using var verified = _fixture.NewContext();
        Assert.True(await verified.ConversationChunks.AsNoTracking()
            .AnyAsync(row => row.Id == chunkId), "Owned child upload was not persisted.");
    }

    [ApplicationOperationPostgresFact]
    public async Task ExistingOwnedChildren_CannotReparentToForeignConversationChannelResourceOrSkill()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var ownConversation = Guid.NewGuid().ToString();
        var foreignConversation = Guid.NewGuid().ToString();
        var ownChunk = Guid.NewGuid().ToString();
        var ownChannel = Guid.NewGuid().ToString();
        var foreignChannel = Guid.NewGuid().ToString();
        var ownResource = Guid.NewGuid().ToString();
        var foreignResource = Guid.NewGuid().ToString();
        var ownSkill = Guid.NewGuid().ToString();
        var foreignSkill = Guid.NewGuid().ToString();
        var ownVideo = Guid.NewGuid().ToString();
        var ownCompletion = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.Conversations.AddRange(
                new Conversation { Id = ownConversation, UserProfileId = owner },
                new Conversation { Id = foreignConversation, UserProfileId = foreign });
            db.ConversationChunks.Add(new ConversationChunk
            {
                Id = ownChunk, UserProfileId = owner, ConversationId = ownConversation
            });
            db.MonitoredChannels.AddRange(
                new MonitoredChannel { Id = ownChannel, UserProfileId = owner },
                new MonitoredChannel { Id = foreignChannel, UserProfileId = foreign });
            db.SkillProfiles.AddRange(
                new SkillProfile { Id = ownSkill, UserProfileId = owner },
                new SkillProfile { Id = foreignSkill, UserProfileId = foreign });
            db.LearningResources.AddRange(
                new LearningResource
                {
                    Id = ownResource, UserProfileId = owner, SkillID = ownSkill
                },
                new LearningResource
                {
                    Id = foreignResource, UserProfileId = foreign
                });
            db.VideoImports.Add(new VideoImport
            {
                Id = ownVideo, UserProfileId = owner,
                MonitoredChannelId = ownChannel, LearningResourceId = ownResource
            });
            db.DailyPlanCompletions.Add(new DailyPlanCompletion
            {
                Id = ownCompletion, UserProfileId = owner,
                Date = DateTime.UtcNow.Date, PlanItemId = Guid.NewGuid().ToString(),
                ResourceId = ownResource, SkillId = ownSkill
            });
            await db.SaveChangesAsync();
        }

        var cases = new (string Reference, SyncItem Item)[]
        {
            ("ConversationChunk.ConversationId", OwnedUpdate(
                "ConversationChunk", ownChunk, owner, "ConversationId", foreignConversation)),
            ("VideoImport.MonitoredChannelId", OwnedUpdate(
                "VideoImport", ownVideo, owner, "MonitoredChannelId", foreignChannel)),
            ("VideoImport.LearningResourceId", OwnedUpdate(
                "VideoImport", ownVideo, owner, "LearningResourceId", foreignResource)),
            ("DailyPlanCompletion.ResourceId", OwnedUpdate(
                "DailyPlanCompletion", ownCompletion, owner, "ResourceId", foreignResource)),
            ("DailyPlanCompletion.SkillId", OwnedUpdate(
                "DailyPlanCompletion", ownCompletion, owner, "SkillId", foreignSkill)),
            ("LearningResource.SkillID", OwnedUpdate(
                "LearningResource", ownResource, owner, "SkillID", foreignSkill))
        };
        foreach (var (reference, item) in cases)
        {
            using var response = await _fixture.UploadAsync(owner, item);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                $"{reference} foreign reparent returned {response.StatusCode}");
        }

        await using var verified = _fixture.NewContext();
        Assert.Equal(ownConversation, (await verified.ConversationChunks.AsNoTracking()
            .SingleAsync(row => row.Id == ownChunk)).ConversationId);
        var video = await verified.VideoImports.AsNoTracking().SingleAsync(row => row.Id == ownVideo);
        Assert.Equal(ownChannel, video.MonitoredChannelId);
        Assert.Equal(ownResource, video.LearningResourceId);
        var completion = await verified.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(row => row.Id == ownCompletion);
        Assert.Equal(ownResource, completion.ResourceId);
        Assert.Equal(ownSkill, completion.SkillId);
        Assert.Equal(ownSkill, (await verified.LearningResources.AsNoTracking()
            .SingleAsync(row => row.Id == ownResource)).SkillID);
    }

    [ApplicationOperationPostgresFact]
    public async Task ExistingOwnedChildren_CanUpdateAndReparentToOtherOwnedParents()
    {
        var (owner, _) = await _fixture.SeedOwnersAsync();
        var conversations = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var channels = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var resources = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var skills = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var chunkId = Guid.NewGuid().ToString();
        var importId = Guid.NewGuid().ToString();
        var completionId = Guid.NewGuid().ToString();
        var wordId = Guid.NewGuid().ToString();
        var progressId = Guid.NewGuid().ToString();
        var contextId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.Conversations.AddRange(conversations.Select(id => new Conversation
            {
                Id = id, UserProfileId = owner
            }));
            db.ConversationChunks.Add(new ConversationChunk
            {
                Id = chunkId, UserProfileId = owner, ConversationId = conversations[0]
            });
            db.MonitoredChannels.AddRange(channels.Select(id => new MonitoredChannel
            {
                Id = id, UserProfileId = owner
            }));
            db.SkillProfiles.AddRange(skills.Select(id => new SkillProfile
            {
                Id = id, UserProfileId = owner
            }));
            db.LearningResources.AddRange(resources.Select(id => new LearningResource
            {
                Id = id, UserProfileId = owner, SkillID = skills[0]
            }));
            db.VideoImports.Add(new VideoImport
            {
                Id = importId, UserProfileId = owner,
                MonitoredChannelId = channels[0], LearningResourceId = resources[0]
            });
            db.DailyPlanCompletions.Add(new DailyPlanCompletion
            {
                Id = completionId, UserProfileId = owner,
                Date = DateTime.UtcNow.Date, PlanItemId = Guid.NewGuid().ToString(),
                ResourceId = resources[0], SkillId = skills[0]
            });
            db.VocabularyWords.Add(new VocabularyWord { Id = wordId });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resources[0], VocabularyWordId = wordId
            });
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = progressId, UserId = owner, VocabularyWordId = wordId
            });
            db.VocabularyLearningContexts.Add(new VocabularyLearningContext
            {
                Id = contextId, VocabularyProgressId = progressId,
                LearningResourceId = resources[0]
            });
            await db.SaveChangesAsync();
        }

        var updates = new[]
        {
            OwnedUpdate("ConversationChunk", chunkId, owner, "ConversationId", conversations[1]),
            OwnedUpdate("VideoImport", importId, owner, "MonitoredChannelId", channels[1]),
            OwnedUpdate("VideoImport", importId, owner, "LearningResourceId", resources[1]),
            OwnedUpdate("DailyPlanCompletion", completionId, owner, "ResourceId", resources[1]),
            OwnedUpdate("DailyPlanCompletion", completionId, owner, "SkillId", skills[1]),
            OwnedUpdate("LearningResource", resources[0], owner, "SkillID", skills[1]),
            new SyncItem("VocabularyLearningContext", ChangeType.Update,
                new Dictionary<string, object?>
                {
                    ["Id"] = contextId, ["VocabularyProgressId"] = progressId,
                    ["LearningResourceId"] = resources[1]
                })
        };
        foreach (var update in updates)
        {
            using var response = await _fixture.UploadAsync(owner, update);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await using var verified = _fixture.NewContext();
        Assert.Equal(conversations[1], (await verified.ConversationChunks.AsNoTracking()
            .SingleAsync(row => row.Id == chunkId)).ConversationId);
        var import = await verified.VideoImports.AsNoTracking()
            .SingleAsync(row => row.Id == importId);
        Assert.Equal(channels[1], import.MonitoredChannelId);
        Assert.Equal(resources[1], import.LearningResourceId);
        var completion = await verified.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(row => row.Id == completionId);
        Assert.Equal(resources[1], completion.ResourceId);
        Assert.Equal(skills[1], completion.SkillId);
        Assert.Equal(skills[1], (await verified.LearningResources.AsNoTracking()
            .SingleAsync(row => row.Id == resources[0])).SkillID);
        Assert.Equal(resources[1], (await verified.VocabularyLearningContexts.AsNoTracking()
            .SingleAsync(row => row.Id == contextId)).LearningResourceId);
    }

    [ApplicationOperationPostgresFact]
    public async Task ForgedSameUploadParentOwner_CannotAuthorizeForeignStoredParent()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var conversationId = Guid.NewGuid().ToString();
        var channelId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.Conversations.Add(new Conversation
            {
                Id = conversationId, UserProfileId = foreign
            });
            db.MonitoredChannels.Add(new MonitoredChannel
            {
                Id = channelId, UserProfileId = foreign
            });
            await db.SaveChangesAsync();
        }

        var forgedConversation = OwnedUpdate(
            "Conversation", conversationId, owner, "Language", "Korean");
        var child = Chunk(Guid.NewGuid().ToString(), owner, conversationId);
        var forgedChannel = OwnedUpdate(
            "MonitoredChannel", channelId, owner, "ChannelName", "Claimed");
        var import = Video(Guid.NewGuid().ToString(), owner, channelId);
        foreach (var ordered in new[]
        {
            new[] { child, forgedConversation },
            new[] { forgedConversation, child },
            new[] { import, forgedChannel },
            new[] { forgedChannel, import }
        })
        {
            using var response = await _fixture.UploadAsync(owner, ordered);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await using var verified = _fixture.NewContext();
        Assert.False(await verified.ConversationChunks.AnyAsync(row => row.Id
            == child.Values["Id"].Value!.ToString()));
        Assert.False(await verified.VideoImports.AnyAsync(row => row.Id
            == import.Values["Id"].Value!.ToString()));
        Assert.Equal(foreign, (await verified.Conversations.AsNoTracking()
            .SingleAsync(row => row.Id == conversationId)).UserProfileId);
        Assert.Equal(foreign, (await verified.MonitoredChannels.AsNoTracking()
            .SingleAsync(row => row.Id == channelId)).UserProfileId);
    }

    [ApplicationOperationPostgresFact]
    public async Task NativeDeletes_FailClosedEvenWhenAParentHasAnOwnedId()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        var ownId = Guid.NewGuid().ToString();
        var foreignId = Guid.NewGuid().ToString();
        await using (var db = _fixture.NewContext())
        {
            db.LearningResources.AddRange(
                new LearningResource { Id = ownId, UserProfileId = owner },
                new LearningResource { Id = foreignId, UserProfileId = foreign });
            await db.SaveChangesAsync();
        }

        foreach (var id in new[] { ownId, foreignId })
        {
            using var response = await _fixture.UploadAsync(owner, new SyncItem(
                "LearningResource", ChangeType.Delete, new Dictionary<string, object?>
                {
                    ["Id"] = id, ["UserProfileId"] = owner
                }));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        await using var verified = _fixture.NewContext();
        Assert.Equal(2, await verified.LearningResources.AsNoTracking()
            .CountAsync(row => row.Id == ownId || row.Id == foreignId));
    }

    [ApplicationOperationPostgresFact]
    public async Task NativePagedDownload_DeniesForeignAndHeaderlessReplay_ButPreservesLegacy()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        using var client = _fixture.CreateClient();

        var nativeSession = await _fixture.StartDownloadAsync(client, owner);
        Assert.True(nativeSession.TotalChanges > 0);
        var chunkPath = $"/api/sync-agent/changes-bulk-item/{nativeSession.SessionId}/0/1000";
        var binaryPath = $"/api/sync-agent/changes-bulk-item-binary/{nativeSession.SessionId}/0/1000";

        using var wrongOwner = await _fixture.GetAsync(client, chunkPath, foreign);
        Assert.Contains(wrongOwner.StatusCode,
            new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.DoesNotContain(owner, await wrongOwner.Content.ReadAsStringAsync());
        using var wrongBinary = await _fixture.GetAsync(client, binaryPath, foreign);
        Assert.Contains(wrongBinary.StatusCode,
            new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Empty(await wrongBinary.Content.ReadAsByteArrayAsync());
        using var noHeader = await _fixture.GetAsync(client, chunkPath, foreign, nativeHeader: false);
        Assert.Contains(noHeader.StatusCode,
            new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        using var ownChunk = await _fixture.GetAsync(client, chunkPath, owner);
        Assert.Equal(HttpStatusCode.OK, ownChunk.StatusCode);
        var ownedRows = await ownChunk.Content.ReadFromJsonAsync<SyncItem[]>();
        Assert.NotNull(ownedRows);
        Assert.Contains(ownedRows, item => item.TableName == "UserProfile"
            && item.Values["Id"].Value?.ToString() == owner);
        Assert.DoesNotContain(ownedRows, item => item.TableName == "UserProfile"
            && item.Values["Id"].Value?.ToString() == foreign);

        var legacySession = await _fixture.StartDownloadAsync(client, owner, nativeHeader: false);
        using var legacyChunk = await _fixture.GetAsync(client,
            $"/api/sync-agent/changes-bulk-item/{legacySession.SessionId}/0/1000",
            foreign, nativeHeader: false);
        Assert.Equal(HttpStatusCode.OK, legacyChunk.StatusCode);
        var legacyRows = await legacyChunk.Content.ReadFromJsonAsync<SyncItem[]>();
        Assert.NotNull(legacyRows);
        Assert.Contains(legacyRows, item => item.TableName == "UserProfile"
            && item.Values["Id"].Value?.ToString() == owner);
        Assert.Contains(legacyRows, item => item.TableName == "UserProfile"
            && item.Values["Id"].Value?.ToString() == foreign);
    }

    [ApplicationOperationPostgresFact]
    public async Task NativeBinaryChunk_StillDownloadsForSessionOwner()
    {
        var (owner, foreign) = await _fixture.SeedOwnersAsync();
        using var client = _fixture.CreateClient();
        var session = await _fixture.StartDownloadAsync(client, owner);

        using var rejected = await _fixture.GetAsync(client,
            $"/api/sync-agent/changes-bulk-item-binary/{session.SessionId}/0/1000",
            foreign);
        Assert.Contains(rejected.StatusCode,
            new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        using var ownChunk = await _fixture.GetAsync(client,
            $"/api/sync-agent/changes-bulk-item-binary/{session.SessionId}/0/1000",
            owner);
        Assert.Equal(HttpStatusCode.OK, ownChunk.StatusCode);
        Assert.Equal("application/x-msgpack", ownChunk.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await ownChunk.Content.ReadAsByteArrayAsync());
    }

    private static SyncItem Resource(string id, string owner, string? skillId = null) =>
        new("LearningResource", ChangeType.Insert, new Dictionary<string, object?>
        {
            ["Id"] = id, ["UserProfileId"] = owner, ["Title"] = "Upload session row",
            ["SkillID"] = skillId,
            ["CreatedAt"] = DateTime.UtcNow, ["UpdatedAt"] = DateTime.UtcNow,
            ["IsSmartResource"] = false
        });

    private static SyncItem Chunk(string id, string owner, string conversationId) =>
        new("ConversationChunk", ChangeType.Insert, new Dictionary<string, object?>
        {
            ["Id"] = id, ["UserProfileId"] = owner,
            ["ConversationId"] = conversationId, ["SentTime"] = DateTime.UtcNow,
            ["Role"] = 0, ["Comprehension"] = 0d
        });

    private static SyncItem Video(
        string id, string owner, string? channelId = null, string? resourceId = null) =>
        new("VideoImport", ChangeType.Insert, new Dictionary<string, object?>
        {
            ["Id"] = id, ["UserProfileId"] = owner,
            ["MonitoredChannelId"] = channelId, ["LearningResourceId"] = resourceId,
            ["VideoId"] = Guid.NewGuid().ToString("N")[..11],
            ["Status"] = 0, ["CreatedAt"] = DateTime.UtcNow
        });

    private static SyncItem Completion(
        string id, string owner, string? resourceId = null, string? skillId = null) =>
        new("DailyPlanCompletion", ChangeType.Insert, new Dictionary<string, object?>
        {
            ["Id"] = id, ["UserProfileId"] = owner,
            ["Date"] = DateTime.UtcNow.Date, ["PlanItemId"] = Guid.NewGuid().ToString(),
            ["ActivityType"] = "VocabularyReview",
            ["ResourceId"] = resourceId, ["SkillId"] = skillId,
            ["IsCompleted"] = false, ["IsArchived"] = false,
            ["TitleKey"] = "Test", ["DescriptionKey"] = "Test",
            ["Rationale"] = "", ["MinutesSpent"] = 0,
            ["EstimatedMinutes"] = 5, ["Priority"] = 1,
            ["CreatedAt"] = DateTime.UtcNow, ["UpdatedAt"] = DateTime.UtcNow
        });

    private static SyncItem Context(string id, string progressId, string? resourceId) =>
        new("VocabularyLearningContext", ChangeType.Insert, new Dictionary<string, object?>
        {
            ["Id"] = id, ["VocabularyProgressId"] = progressId,
            ["LearningResourceId"] = resourceId,
            ["Activity"] = "VocabularyQuiz", ["InputMode"] = "MultipleChoice",
            ["WasCorrect"] = true, ["DifficultyScore"] = 0.5f,
            ["ResponseTimeMs"] = 200, ["LearnedAt"] = DateTime.UtcNow,
            ["CreatedAt"] = DateTime.UtcNow, ["UpdatedAt"] = DateTime.UtcNow,
            ["CorrectAnswersInContext"] = 1
        });

    private static SyncItem OwnedUpdate(
        string table, string id, string owner, string column, string value) =>
        new(table, ChangeType.Update, new Dictionary<string, object?>
        {
            ["Id"] = id, ["UserProfileId"] = owner, [column] = value
        });

    private static DateTime Timestamp(SyncItem item, string column) =>
        item.Values[column].Value switch
        {
            DateTime value => value,
            JsonElement value => value.GetDateTime(),
            _ => throw new InvalidOperationException($"Expected a {column} timestamp.")
        };

    private sealed class UtcTimestampFixtureDbContext(string connectionString)
        : ApplicationDbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseNpgsql(connectionString);
            base.OnConfiguring(optionsBuilder);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // EnsureCreated otherwise maps this DateTime to timestamp without time zone
            // under the API's legacy Npgsql switch, unlike the production migration.
            modelBuilder.Entity<DailyPlanCompletion>()
                .Property(item => item.CompletedAt)
                .HasColumnType("timestamp with time zone");
        }
    }

    private sealed class ControlledUploadTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }
}

public sealed class NativeOwnerSyncPostgresFixture : IAsyncLifetime
{
    private ApplicationOperationPostgresHarness? _database;
    private NativeOwnerSyncPostgresApiFactory? _api;

    public async Task InitializeAsync()
    {
        if (ApplicationOperationPostgresServer.SkipReason is not null)
            return;

        _database = await ApplicationOperationPostgresHarness.CreateAsync("native_sync", migrate: false);
        try
        {
            await using (var db = _database.NewContext())
                await db.Database.EnsureCreatedAsync();
            var coreSync = new PostgreSQLSyncProvider(
                new PostgreSQLSyncConfigurationBuilder(_database.ConnectionString)
                    .ConfigureSyncTables().Build(), ProviderMode.Remote);
            await coreSync.ApplyProvisionAsync();
            _api = new NativeOwnerSyncPostgresApiFactory(_database.ConnectionString);
        }
        catch (Exception initializationError)
        {
            try
            {
                await _database.DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Native sync test database initialization and cleanup both failed.",
                    initializationError, cleanupError);
            }

            _database = null;
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        _api?.Dispose();
        if (_database is not null)
            await _database.DisposeAsync();
    }

    public ApplicationDbContext NewContext() => Database.NewContext();
    public HttpClient CreateClient() => Api.CreateClient();

    public async Task<(string Owner, string Foreign)> SeedOwnersAsync()
    {
        var owner = Guid.NewGuid().ToString();
        var foreign = Guid.NewGuid().ToString();
        await using var db = NewContext();
        db.UserProfiles.AddRange(
            new UserProfile { Id = owner, Email = $"{owner}@example.test" },
            new UserProfile { Id = foreign, Email = $"{foreign}@example.test" });
        await db.SaveChangesAsync();
        return (owner, foreign);
    }

    public async Task<HttpResponseMessage> UploadAsync(string owner, params SyncItem[] items)
    {
        using var client = CreateClient();
        var sessionId = Guid.NewGuid();
        using (var begin = await BeginUploadAsync(
            client, owner, sessionId, Guid.NewGuid(), items.Length))
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using (var chunk = await UploadChunkAsync(client, owner, sessionId, items))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);

        return await CompleteUploadAsync(client, owner, sessionId);
    }

    public async Task<HttpResponseMessage> UploadLegacyAsync(
        HttpClient client, string owner, Guid storeId, params SyncItem[] items)
    {
        var sessionId = Guid.NewGuid();
        using (var begin = await BeginUploadAsync(
            client, owner, sessionId, storeId, items.Length, nativeHeader: false))
            Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using (var chunk = await UploadChunkAsync(
            client, owner, sessionId, items, nativeHeader: false))
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        return await CompleteUploadAsync(client, owner, sessionId, nativeHeader: false);
    }

    public async Task<BulkSyncChangeSet> StartDownloadAsync(
        HttpClient client, string owner, bool nativeHeader = true, Guid? storeId = null)
    {
        using var response = await GetAsync(client,
            $"/api/sync-agent/changes-bulk/{storeId ?? Guid.NewGuid()}", owner, nativeHeader);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BulkSyncChangeSet>())!;
    }

    internal NativeOwnerSyncPostgresApiFactory CreateReplica() =>
        new(Database.ConnectionString);

    internal NativeOwnerSyncPostgresApiFactory CreateReplicaWithClock(TimeProvider clock) =>
        new(Database.ConnectionString, clock);

    public Task<HttpResponseMessage> BeginUploadAsync(
        HttpClient client, string owner, Guid sessionId, Guid storeId, int totalChanges,
        bool nativeHeader = true) =>
        SendJsonAsync(client, "/api/sync-agent/changes-bulk-begin", owner,
            new BulkSyncChangeSet
            {
                SessionId = sessionId,
                TotalChanges = totalChanges,
                SourceAnchor = new SyncAnchor(storeId, 1),
                TargetAnchor = SyncAnchor.Null
            }, nativeHeader);

    public Task<HttpResponseMessage> UploadChunkAsync(
        HttpClient client, string owner, Guid sessionId, IReadOnlyList<SyncItem> items,
        bool nativeHeader = true) =>
        SendJsonAsync(client, "/api/sync-agent/changes-bulk-item", owner,
            new BulkChangeSetUploadItem { SessionId = sessionId, Items = items }, nativeHeader);

    public async Task<HttpResponseMessage> UploadBinaryChunkAsync(
        HttpClient client, string owner, Guid sessionId, IReadOnlyList<SyncItem> items,
        bool nativeHeader = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/sync-agent/changes-bulk-item-binary")
        {
            Content = new ByteArrayContent(CoreSyncMessagePackSerializer.Serialize<object>(
                new BulkChangeSetUploadItem { SessionId = sessionId, Items = items }))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-msgpack");
        SetAuth(request, owner, nativeHeader);
        return await client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> CompleteUploadAsync(
        HttpClient client, string owner, Guid sessionId, bool binary = false,
        bool nativeHeader = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/sync-agent/changes-bulk-complete{(binary ? "-binary" : "")}/{sessionId}");
        SetAuth(request, owner, nativeHeader);
        return await client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> SaveVersionAsync(
        HttpClient client, string owner, Guid storeId, long version, bool nativeHeader = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/sync-agent/save-version/{storeId}/{version}");
        SetAuth(request, owner, nativeHeader);
        return await client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> GetAsync(
        HttpClient client, string path, string owner, bool nativeHeader = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        SetAuth(request, owner, nativeHeader);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync<T>(
        HttpClient client, string path, string owner, T payload, bool nativeHeader = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        SetAuth(request, owner, nativeHeader);
        return await client.SendAsync(request);
    }

    private static void SetAuth(
        HttpRequestMessage request, string owner, bool nativeHeader = true)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtGenerator.GenerateToken(userProfileId: owner));
        if (nativeHeader)
            request.Headers.Add(SharedSyncRegistration.NativeOwnerHeader, owner);
    }

    private ApplicationOperationPostgresHarness Database =>
        _database ?? throw new InvalidOperationException("Disposable PostgreSQL fixture was not initialized.");

    private NativeOwnerSyncPostgresApiFactory Api =>
        _api ?? throw new InvalidOperationException("Disposable PostgreSQL API was not initialized.");
}

internal sealed class NativeOwnerSyncPostgresApiFactory(
    string connectionString, TimeProvider? uploadClock = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:sentencestudio", connectionString);
        builder.UseSetting("Database:SkipMigrateOnStartup", "true");
        builder.UseSetting("Auth:SeedDevTestAccounts", "false");
        builder.UseSetting("Auth:EnableDevAuthFallback", "false");
        builder.UseSetting("Jwt:SigningKey", TestJwtGenerator.TestSigningKeyValue);
        builder.UseSetting("Jwt:Issuer", TestJwtGenerator.TestIssuer);
        builder.UseSetting("Jwt:Audience", TestJwtGenerator.TestAudience);
        builder.UseSetting("AI:OpenAI:ApiKey", "test-openai-key");
        builder.UseSetting("ElevenLabsKey", "test-elevenlabs-key");
        builder.ConfigureServices((context, services) =>
        {
            if (uploadClock is not null)
            {
                services.AddSingleton(sp => new NativeSyncUploadSessions(
                    sp.GetRequiredService<IMemoryCache>(),
                    sp.GetRequiredService<NativeSyncDownloadSessions>(),
                    sp.GetRequiredService<ILogger<NativeSyncUploadSessions>>(),
                    uploadClock));
            }
            TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(
                services, context.Configuration);
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            });
        });
    }
}
