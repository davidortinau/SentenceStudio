using CoreSync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Phase 3 of docs/sync-all-tables-plan.md on the device: which rows leave it, and the owner a
/// per-learner database gives rows saved before these tables had one (Captain 2026-11-03).
/// </summary>
public sealed class NativePhase3SyncTests
{
    private static SyncItem Item(string table, ChangeType change, params (string Column, object? Value)[] values) =>
        new(table, change, values.ToDictionary(value => value.Column, value => value.Value));

    [Fact]
    public void Upload_KeepsDevicePhotosBuiltInsUnownedRowsAndDeletesOnTheDevice()
    {
        static bool Local(SyncItem item) => NativeLocalOnlyMappingExportProvider.IsDeviceOnlyRow(item);

        Assert.False(Local(Item("SceneImage", ChangeType.Insert,
            ("Id", "a"), ("UserProfileId", "owner"), ("Url", "https://example.test/a.jpg"))));
        Assert.True(Local(Item("SceneImage", ChangeType.Insert,
            ("Id", "b"), ("UserProfileId", "owner"), ("Url", SceneImage.DevicePhotoScheme + "b.jpg"))));
        Assert.True(Local(Item("SceneImage", ChangeType.Update,
            ("Id", "c"), ("UserProfileId", null), ("Url", "https://example.test/c.jpg"))));

        Assert.False(Local(Item("ConversationScenario", ChangeType.Insert,
            ("Id", "custom"), ("UserProfileId", "owner"))));
        Assert.True(Local(Item("ConversationScenario", ChangeType.Insert,
            ("Id", SyncIds.BuiltInScenario("First Meeting")), ("UserProfileId", null))));
        Assert.True(Local(Item("ConversationScenario", ChangeType.Insert,
            ("Id", "unowned"), ("UserProfileId", null))));

        Assert.False(Local(Item("ExampleSentence", ChangeType.Insert,
            ("Id", "mine"), ("UserProfileId", "owner"), ("VocabularyWordId", "w1"))));
        Assert.True(Local(Item("ExampleSentence", ChangeType.Insert,
            ("Id", "old"), ("UserProfileId", ""), ("VocabularyWordId", "w1"))));

        // The server refuses every delete from a device; one would block the whole upload.
        Assert.True(Local(Item("MinimalPair", ChangeType.Delete, ("Id", "pair"))));
        Assert.True(Local(Item("SceneImage", ChangeType.Delete, ("Id", "a"))));
        // Word-link deletes have their own proven undo handling.
        Assert.False(Local(Item("ResourceVocabularyMapping", ChangeType.Delete, ("Id", Guid.NewGuid().ToString()))));
        Assert.False(Local(Item("VocabularyWord", ChangeType.Insert, ("Id", "w1"))));
    }

    [Fact]
    public async Task PerLearnerDatabase_AdoptsItsUnownedRows_ButNeverBuiltInScenarios()
    {
        using var fixture = new NativeSliceFixture();
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        db.VocabularyWords.Add(new VocabularyWord { Id = "w-adopt", TargetLanguageTerm = "물" });
        db.SceneImages.Add(new SceneImage { Id = "img", Url = "https://example.test/a.jpg" });
        db.ConversationScenarios.AddRange(
            new ConversationScenario
            {
                Id = "custom", Name = "Mine", PersonaName = "P", PersonaDescription = "D",
                SituationDescription = "S", CreatedAt = now, UpdatedAt = now
            },
            new ConversationScenario
            {
                Id = SyncIds.BuiltInScenario("First Meeting"), Name = "First Meeting", PersonaName = "P",
                PersonaDescription = "D", SituationDescription = "S", IsPredefined = true,
                CreatedAt = now, UpdatedAt = now
            });
        db.ExampleSentences.Add(new ExampleSentence
        {
            Id = "sentence", VocabularyWordId = "w-adopt", TargetSentence = "물을 마셔요.",
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();

        await NativeOwnerStores.AdoptUnownedRowsAsync(db, NativeSliceFixture.UserA, default);

        Assert.Equal(NativeSliceFixture.UserA,
            (await db.SceneImages.AsNoTracking().SingleAsync(row => row.Id == "img")).UserProfileId);
        Assert.Equal(NativeSliceFixture.UserA,
            (await db.ConversationScenarios.AsNoTracking().SingleAsync(row => row.Id == "custom")).UserProfileId);
        Assert.Null((await db.ConversationScenarios.AsNoTracking()
            .SingleAsync(row => row.IsPredefined)).UserProfileId);
        Assert.Equal(NativeSliceFixture.UserA,
            (await db.ExampleSentences.AsNoTracking().SingleAsync(row => row.Id == "sentence")).UserProfileId);
    }
}
