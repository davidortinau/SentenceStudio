using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeResourceSourceWorkflowTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    public void NativeErrorAndOwnerOutcomeKeysAreCompleteWithoutFormatPlaceholders(
        string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        foreach (var key in new[] {
            "NativeResources_FileTooLarge", "NativeResources_UnsupportedFile",
            "NativeResources_FileReadError",
            "NativeResources_SetTargetLanguage",
            "NativeResources_SaveLanguageBeforeQuickAdd",
            "NativeResources_LexicalIdiom", "NativeResources_DeleteFailed",
            "NativeResources_GenerationPartial",
            "NativeDashboard_ActivityNotAvailable",
            "NativeAuth_RegistrationAccepted", "NativeAuth_RecoveryUnavailable",
            "NativeAuth_RecoveryFailed"
        })
        {
            var value = LocalizationManager.Instance.GetString(key, culture);
            Assert.NotEqual(key, value);
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.DoesNotContain("{0}", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CreateFullResource_StagesVocabularyAndPersistsOwnedRowsAfterRestart()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        Assert.Equal(NativeResourceListPhase.Empty, (await presenter.LoadListAsync()).Phase);
        Assert.Equal(NativeResourceDetailPhase.Creating, presenter.BeginCreate().Phase);
        Assert.Equal("Video", presenter.Detail.Draft!.MediaType);
        Assert.Equal("English", presenter.Detail.Draft.Language);
        presenter.ChangeDraft(NativeResourceField.Title, "  Video lesson  ");
        presenter.ChangeDraft(NativeResourceField.Description, "description");
        presenter.ChangeDraft(NativeResourceField.MediaType, "Article");
        presenter.ChangeDraft(NativeResourceField.Language, "Korean");
        presenter.ChangeDraft(NativeResourceField.MediaUrl, "https://example.invalid/lesson");
        presenter.ChangeDraft(NativeResourceField.Tags, "study, video");
        presenter.ChangeDraft(NativeResourceField.Transcript, new string('T', 20_000));
        presenter.ChangeDraft(NativeResourceField.Translation, new string('E', 18_000));
        var staged = presenter.StageVocabularyFromText(
            "단어,word\n집,house\n단어,duplicate", "comma");
        Assert.Equal(NativeResourceMutationResult.Success, staged.Result);
        Assert.Equal(2, staged.Added);
        Assert.Equal(1, staged.Skipped);
        var wordId = presenter.Detail.Draft!.StagedVocabulary[0].Id;
        Assert.True(presenter.SetStagedLexicalUnitType(wordId, LexicalUnitType.Phrase));

        Assert.True(await presenter.SaveAsync());
        var created = presenter.Detail.Resource!;
        Assert.NotNull(created);
        Assert.Equal("  Video lesson  ", created.Title);
        Assert.Equal("Article", created.MediaType);
        Assert.Equal("Korean", created.Language);
        Assert.Equal(20_000, created.Transcript!.Length);
        Assert.Equal(18_000, created.Translation!.Length);
        Assert.Equal(2, created.VocabularyCount);
        Assert.Equal(LexicalUnitType.Phrase,
            Assert.Single(created.Vocabulary, row => row.Id == wordId).LexicalUnitType);
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.Detail.Sync);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.LearningResources.AsNoTracking().SingleAsync(
                item => item.Id == created.Id);
            Assert.Equal(NativeSliceFixture.UserA, row.UserProfileId);
            Assert.Equal(2, await db.ResourceVocabularyMappings.CountAsync(
                mapping => mapping.ResourceId == created.Id));
            Assert.Equal(2, await db.VocabularyWords.CountAsync(
                word => db.ResourceVocabularyMappings.Any(mapping =>
                    mapping.ResourceId == created.Id && mapping.VocabularyWordId == word.Id)));
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        Assert.Equal(NativeResourceSyncPhase.Pending,
            (await fixture.Resources.LoadListAsync()).Sync);
        Assert.Equal(created.Transcript,
            (await fixture.Resources.OpenAsync(created.Id)).Resource!.Transcript);
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(NativeResourceDetailPhase.NotFound,
            (await fixture.Resources.OpenAsync(created.Id)).Phase);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, fixture.Resources.List.Sync);
    }

    [Fact]
    public async Task AddDraft_ChangingLanguageAfterPasteKeepsAllStagedWords()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        presenter.BeginCreate();
        presenter.ChangeDraft(NativeResourceField.Title, "Learner words");
        Assert.Equal(1, presenter.StageVocabularyFromText("そら,sky").Added);
        presenter.ChangeDraft(NativeResourceField.Language, "Japanese");
        Assert.Equal("Japanese",
            Assert.Single(presenter.Detail.Draft!.StagedVocabulary).Language);

        Assert.True(await presenter.SaveAsync());
        var resource = presenter.Detail.Resource!;
        Assert.Equal("Japanese", resource.Language);
        var word = Assert.Single(resource.Vocabulary);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("Japanese", (await db.VocabularyWords.SingleAsync(
            row => row.Id == word.Id)).Language);
    }

    [Fact]
    public async Task SingletonNativeStoreUsesScopedRepositoryWithoutCaptiveAndroidDependency()
    {
        using var fixture = new NativeSliceFixture(scopedResourceRepository: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var created = await store.CreateAsync(NativeSliceFixture.UserA,
            new NativeResourceDraft("Owned", null, null,
                MediaType: "Video", Language: "Korean"), default);
        Assert.Equal(NativeResourceMutationResult.Success, created.Result);
        var loaded = await store.GetAsync(
            NativeSliceFixture.UserA, created.ResourceId!, default);
        Assert.NotNull(loaded?.OriginalContent);
        var saved = await store.SaveContentAsync(
            NativeSliceFixture.UserA, loaded.Id, loaded.OriginalContent,
            new ResourceContentUpdate("Edited", null, null, "Article",
                "Korean", null, "full text", null), default);
        Assert.Equal(ResourceMetadataUpdateResult.Success, saved);
        Assert.Equal("full text", (await store.GetAsync(
            NativeSliceFixture.UserA, loaded.Id, default))!.Transcript);
        Assert.Equal("Edited", Assert.Single(await store.ListAsync(
            NativeSliceFixture.UserA, default)).Title);
    }

    [Fact]
    public async Task FullEdit_ChangesEveryFieldWithoutErasingMappingsOrOtherOwners()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        await fixture.SeedAsync(NativeSliceFixture.UserB, known: true);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        var id = fixture.ResourceId(NativeSliceFixture.UserA);
        var opened = await presenter.OpenAsync(id);
        Assert.Equal(4, opened.Resource!.Vocabulary.Length);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Changed");
        presenter.ChangeDraft(NativeResourceField.Description, string.Empty);
        presenter.ChangeDraft(NativeResourceField.Tags, "visible");
        presenter.ChangeDraft(NativeResourceField.MediaType, "Podcast");
        presenter.ChangeDraft(NativeResourceField.Language, "Japanese");
        presenter.ChangeDraft(NativeResourceField.MediaUrl, "https://example.invalid/audio");
        presenter.ChangeDraft(NativeResourceField.Transcript, new string('X', 25_000));
        presenter.ChangeDraft(NativeResourceField.Translation, new string('Y', 22_000));

        Assert.True(await presenter.SaveAsync());
        Assert.Equal(4, presenter.Detail.Resource!.Vocabulary.Length);
        Assert.Equal(25_000, presenter.Detail.Resource.Transcript!.Length);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owned = await db.LearningResources.AsNoTracking()
            .SingleAsync(resource => resource.Id == id);
        Assert.Equal("Changed", owned.Title);
        Assert.Equal(string.Empty, owned.Description);
        Assert.Equal("visible", owned.Tags);
        Assert.Equal("Podcast", owned.MediaType);
        Assert.Equal("Japanese", owned.Language);
        Assert.Equal("https://example.invalid/audio", owned.MediaUrl);
        Assert.Equal(25_000, owned.Transcript!.Length);
        Assert.Equal(22_000, owned.Translation!.Length);
        Assert.Equal(4, await db.ResourceVocabularyMappings
            .CountAsync(mapping => mapping.ResourceId == id));
        Assert.Equal(4, await db.ResourceVocabularyMappings.CountAsync(mapping =>
            mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserB)));
        Assert.Equal(4, await db.VocabularyProgresses.CountAsync(progress =>
            progress.UserId == NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task FullEdit_ConcurrentLongTextChangeConflictsWithoutClearingDraftOrMappings()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        var id = fixture.ResourceId(NativeSliceFixture.UserA);
        await presenter.OpenAsync(id);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "unsent");
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.LearningResources.SingleAsync(resource => resource.Id == id))
                .Transcript = "newer downloaded text";
            await db.SaveChangesAsync();
        }

        Assert.False(await presenter.SaveAsync());
        Assert.Equal(NativeResourceDetailPhase.Conflict, presenter.Detail.Phase);
        Assert.Equal("unsent", presenter.Detail.Draft!.Title);
        Assert.True(await presenter.ReloadDraftAsync());
        Assert.Equal("newer downloaded text", presenter.Detail.Draft!.Transcript);
        Assert.Equal(4, presenter.Detail.Resource!.Vocabulary.Length);
    }

    [Fact]
    public async Task FileStaging_EnforcesOneMegabyteCapAndCancelIsNonDestructive()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        presenter.BeginCreate();
        var content = Encoding.UTF8.GetBytes("가\tword\n나\tother");
        using var file = new MemoryStream(content);
        var result = await presenter.StageVocabularyFromFileAsync(
            file, content.Length, "terms.CSV", "tab");
        Assert.Equal(2, result.Added);
        Assert.Equal(2, presenter.Detail.Draft!.StagedVocabulary.Length);
        Assert.True(presenter.RemoveStagedVocabulary(
            presenter.Detail.Draft.StagedVocabulary[0].Id));

        using var oversized = new MemoryStream(new byte[
            NativeResourcePresenter.MaxVocabularyFileBytes + 1]);
        var tooLarge = await presenter.StageVocabularyFromFileAsync(
            oversized, oversized.Length, "too-big.csv");
        Assert.Equal(NativeResourceMutationResult.Invalid, tooLarge.Result);
        Assert.Equal("NativeResources_FileTooLarge", tooLarge.ErrorKey);
        using var unsupported = new MemoryStream(Encoding.UTF8.GetBytes("말,word"));
        Assert.Equal("NativeResources_UnsupportedFile",
            (await presenter.StageVocabularyFromFileAsync(
                unsupported, unsupported.Length, "terms.pdf")).ErrorKey);
        using var empty = new MemoryStream();
        Assert.Equal("ResourceAdd_FileEmpty",
            (await presenter.StageVocabularyFromFileAsync(
                empty, 0, "empty.txt")).ErrorKey);
        using var underestimated = new MemoryStream(new byte[
            NativeResourcePresenter.MaxVocabularyFileBytes + 1]);
        Assert.Equal("NativeResources_FileTooLarge",
            (await presenter.StageVocabularyFromFileAsync(
                underestimated, 1, "terms.txt")).ErrorKey);
        var unreadable = new MemoryStream(Encoding.UTF8.GetBytes("말,word"));
        unreadable.Dispose();
        Assert.Equal("NativeResources_FileReadError",
            (await presenter.StageVocabularyFromFileAsync(
                unreadable, 8, "private/location/terms.csv")).ErrorKey);
        Assert.Single(presenter.Detail.Draft!.StagedVocabulary);

        presenter.CancelEdit();
        Assert.Equal(NativeResourceDetailPhase.Idle, presenter.Detail.Phase);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.LearningResources.ToListAsync());
        Assert.Empty(await db.VocabularyWords.ToListAsync());
    }

    [Fact]
    public async Task CreateWithoutOwnedProfileFailsClosedAndDoesNotMarkPending()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        presenter.BeginCreate();
        presenter.ChangeDraft(NativeResourceField.Title, "unsaved");

        Assert.False(await presenter.SaveAsync());
        Assert.Equal(NativeResourceDetailPhase.SaveError, presenter.Detail.Phase);
        Assert.Equal("ResourceEdit_NotFound", presenter.Detail.ErrorKey);
        Assert.Equal("unsaved", presenter.Detail.Draft!.Title);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, presenter.Detail.Sync);
        using var scope = fixture.Scope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .LearningResources.ToListAsync());
    }

    [Fact]
    public async Task QuickAddExistingAndNewWords_UnlinkOnlyTheOwnedMapping()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        presenter.BeginCreate();
        presenter.ChangeDraft(NativeResourceField.Title, "Extra vocabulary");
        presenter.ChangeDraft(NativeResourceField.MediaType, "Vocabulary List");
        presenter.ChangeDraft(NativeResourceField.Language, "Korean");
        Assert.True(await presenter.SaveAsync());
        var extraId = presenter.Detail.Resource!.Id;
        Assert.True(presenter.CanQuickAdd);

        var search = await presenter.SearchVocabularyAsync(
            $"target-{NativeSliceFixture.UserA}-0");
        Assert.Equal(NativeResourceVocabularySearchPhase.Ready, search.Phase);
        var existing = Assert.Single(search.Results);
        Assert.Equal(fixture.WordId(NativeSliceFixture.UserA, 0), existing.Id);
        Assert.Equal(NativeResourceMutationResult.Success,
            await presenter.AttachExistingVocabularyAsync(existing.Id));
        Assert.Single(presenter.Detail.Resource!.Vocabulary);
        Assert.Empty((await presenter.SearchVocabularyAsync(existing.TargetTerm)).Results);
        Assert.Empty((await presenter.SearchVocabularyAsync(
            $"target-{NativeSliceFixture.UserB}-0")).Results);
        Assert.Equal(NativeResourceMutationResult.AlreadyPresent,
            await presenter.CreateNewVocabularyAsync(existing.TargetTerm, "duplicate"));

        Assert.Equal(NativeResourceMutationResult.Success,
            await presenter.CreateNewVocabularyAsync("지갑", "wallet"));
        Assert.Equal(2, presenter.Detail.Resource!.VocabularyCount);
        Assert.Equal(NativeResourceMutationResult.Success,
            await presenter.RemoveVocabularyAsync(existing.Id));
        Assert.Equal(1, presenter.Detail.Resource!.VocabularyCount);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.ResourceVocabularyMappings.AnyAsync(
            mapping => mapping.ResourceId == extraId && mapping.VocabularyWordId == existing.Id));
        Assert.True(await db.ResourceVocabularyMappings.AnyAsync(mapping =>
            mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserA)
            && mapping.VocabularyWordId == existing.Id));
        Assert.True(await db.VocabularyWords.AnyAsync(word => word.Id == existing.Id));
        Assert.Equal(4, await db.ResourceVocabularyMappings.CountAsync(mapping =>
            mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserB)));
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.Detail.Sync);
    }

    [Fact]
    public async Task EditedDraftLanguage_SearchesOwnerWords_StagesExactSelection_AndCommitsOnSave()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var candidate = new NativeResourceVocabularyRow(
            Guid.NewGuid().ToString(), "そら", "sky", Language: "Japanese");
        fixture.Activate(NativeSliceFixture.UserA);
        var sourceA = await store.CreateAsync(NativeSliceFixture.UserA,
            new NativeResourceDraft("Japanese owned", null, null,
                MediaType: "Vocabulary List", Language: "Japanese",
                StagedVocabulary: [candidate]), default);
        Assert.Equal(NativeResourceMutationResult.Success, sourceA.Result);
        fixture.Activate(NativeSliceFixture.UserB);
        var sourceB = await store.CreateAsync(NativeSliceFixture.UserB,
            new NativeResourceDraft("Japanese foreign", null, null,
                MediaType: "Vocabulary List", Language: "Japanese",
                StagedVocabulary: [new NativeResourceVocabularyRow(
                    Guid.NewGuid().ToString(), "そらB", "sky", Language: "Japanese")]),
            default);
        Assert.Equal(NativeResourceMutationResult.Success, sourceB.Result);

        fixture.Activate(NativeSliceFixture.UserA);
        using (var guard = fixture.Scope())
        {
            var repository = guard.ServiceProvider
                .GetRequiredService<LearningResourceRepository>();
            Assert.Empty(await repository.SearchOwnedVocabularyForDraftAsync(
                "そら", "Japanese", sourceB.ResourceId!,
                NativeSliceFixture.UserA));
            Assert.Empty(await repository.SearchOwnedVocabularyForDraftAsync(
                "そら", "Japanese", sourceA.ResourceId!, string.Empty));
        }
        var presenter = fixture.Resources;
        var id = fixture.ResourceId(NativeSliceFixture.UserA);
        await presenter.OpenAsync(id);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Language, "Japanese");
        Assert.True(presenter.CanQuickAdd);
        var search = await presenter.SearchVocabularyAsync("そら");
        var selected = Assert.Single(search.Results);
        Assert.True(selected.IsExisting);
        Assert.Equal(candidate.Id, selected.Id);
        Assert.Equal(NativeResourceMutationResult.Success,
            await presenter.AttachExistingVocabularyAsync(selected.Id));
        Assert.Equal(candidate.Id,
            Assert.Single(presenter.Detail.Draft!.StagedVocabulary).Id);
        Assert.Equal(4, presenter.Detail.Resource!.Vocabulary.Length);
        Assert.Equal(NativeResourceMutationResult.Success,
            await presenter.CreateNewVocabularyAsync("ありがとう", string.Empty));
        Assert.Equal(2, presenter.Detail.Draft!.StagedVocabulary.Length);
        presenter.ChangeDraft(NativeResourceField.Language, "English");
        Assert.Equal("Japanese", presenter.Detail.Draft!.Language);
        Assert.Equal("NativeResources_SaveLanguageBeforeQuickAdd",
            presenter.Detail.ErrorKey);

        using (var before = fixture.Scope())
            Assert.Equal(4, await before.ServiceProvider
                .GetRequiredService<ApplicationDbContext>().ResourceVocabularyMappings
                .CountAsync(row => row.ResourceId == id));
        Assert.True(await presenter.SaveAsync());
        Assert.Equal("Japanese", presenter.Detail.Resource!.Language);
        Assert.Equal(6, presenter.Detail.Resource.Vocabulary.Length);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.True(await db.ResourceVocabularyMappings.AnyAsync(row =>
                row.ResourceId == id && row.VocabularyWordId == candidate.Id));
            Assert.Equal(6, await db.ResourceVocabularyMappings.CountAsync(
                row => row.ResourceId == id));
            Assert.Equal(1, await db.ResourceVocabularyMappings.CountAsync(
                row => row.ResourceId == sourceB.ResourceId));
        }
        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        Assert.Equal(6, (await fixture.Resources.OpenAsync(id))
            .Resource!.Vocabulary.Length);
    }

    [Fact]
    public async Task CancelWithUnsavedLanguageAndStagedWord_DoesNotWriteAnyMapping()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var source = await store.CreateAsync(NativeSliceFixture.UserA,
            new NativeResourceDraft("Japanese owned", null, null,
                MediaType: "Vocabulary List", Language: "Japanese",
                StagedVocabulary: [new NativeResourceVocabularyRow(
                    Guid.NewGuid().ToString(), "そら", "sky", Language: "Japanese")]),
            default);
        Assert.Equal(NativeResourceMutationResult.Success, source.Result);
        var presenter = fixture.Resources;
        var id = fixture.ResourceId(NativeSliceFixture.UserA);
        await presenter.OpenAsync(id);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Language, "Japanese");
        var selected = Assert.Single((await presenter.SearchVocabularyAsync("そら")).Results);
        Assert.Equal(NativeResourceMutationResult.Success,
            await presenter.AttachExistingVocabularyAsync(selected.Id));
        presenter.CancelEdit();

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("Korean", (await db.LearningResources.SingleAsync(
            resource => resource.Id == id)).Language);
        Assert.Equal(4, await db.ResourceVocabularyMappings.CountAsync(
            mapping => mapping.ResourceId == id));
    }

    [Fact]
    public async Task BlankNativeGlossAndOtherLanguageBulkImportRoundTripWithoutEnablingQuickAdd()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var created = await fixture.Provider.GetRequiredService<INativeResourceStore>()
            .CreateAsync(NativeSliceFixture.UserA,
                new NativeResourceDraft("Other language glossary", null, null,
                    MediaType: "Vocabulary List", Language: null,
                    StagedVocabulary: [
                        new NativeResourceVocabularyRow(Guid.NewGuid().ToString(),
                            "first", string.Empty)
                    ]), default);
        Assert.Equal(NativeResourceMutationResult.Success, created.Result);
        var presenter = fixture.Resources;
        var id = created.ResourceId!;
        await presenter.OpenAsync(id);
        Assert.Equal("Other", presenter.Detail.Resource.Language);
        Assert.False(presenter.CanQuickAdd);
        Assert.Equal(NativeResourceMutationResult.Invalid,
            await presenter.CreateNewVocabularyAsync("quick-add blocked", ""));

        var pasted = await presenter.ImportVocabularyFromTextAsync(
            "second,\nfirst,duplicate", "comma");
        Assert.Equal(NativeResourceMutationResult.Success, pasted.Result);
        Assert.Equal(1, pasted.Added);
        Assert.Equal(1, pasted.CreatedCount);
        Assert.Equal(1, pasted.Skipped);
        var fileBytes = Encoding.UTF8.GetBytes("third\t");
        using var file = new MemoryStream(fileBytes);
        var fromFile = await presenter.ImportVocabularyFromFileAsync(
            file, fileBytes.Length, "glossary.txt", "tab");
        Assert.Equal(NativeResourceMutationResult.Success, fromFile.Result);
        Assert.Equal(1, fromFile.Added);
        var pendingRevision = fixture.Provider.GetRequiredService<NativePendingWrites>()
            .Revision(NativeSliceFixture.UserA);
        var repeated = await presenter.ImportVocabularyFromTextAsync("second,");
        Assert.Equal(0, repeated.Added);
        Assert.Equal(1, repeated.Skipped);
        Assert.Equal(pendingRevision,
            fixture.Provider.GetRequiredService<NativePendingWrites>()
                .Revision(NativeSliceFixture.UserA));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var reloaded = await fixture.Resources.OpenAsync(id);
        Assert.Equal(3, reloaded.Resource!.Vocabulary.Length);
        Assert.All(reloaded.Resource.Vocabulary,
            row => Assert.Equal(string.Empty, row.NativeTerm));
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(3, await db.ResourceVocabularyMappings.CountAsync(
                row => row.ResourceId == id));
            Assert.Equal(3, await db.VocabularyWords.CountAsync(
                row => db.ResourceVocabularyMappings.Any(mapping =>
                    mapping.ResourceId == id && mapping.VocabularyWordId == row.Id
                    && row.Language == "Other")));
        }
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(NativeResourceDetailPhase.NotFound,
            (await fixture.Resources.OpenAsync(id)).Phase);
        var foreign = await fixture.Provider.GetRequiredService<LearningResourceRepository>()
            .ImportOwnedVocabularyAsync(id, [new VocabularyWord {
                TargetLanguageTerm = "foreign", NativeLanguageTerm = ""
            }], "Other", NativeSliceFixture.UserB);
        Assert.Equal(ResourceVocabularyWriteStatus.NotFoundOrForbidden, foreign.Status);
    }

    [Fact]
    public async Task QuickCreateAcceptsEmptyNativeGlossOnOwnedKnownLanguage()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        presenter.BeginCreate();
        presenter.ChangeDraft(NativeResourceField.Title, "Blank gloss glossary");
        presenter.ChangeDraft(NativeResourceField.Language, "Korean");
        Assert.Equal(1, presenter.StageVocabularyFromText("첫째,").Added);
        Assert.Equal(string.Empty,
            Assert.Single(presenter.Detail.Draft!.StagedVocabulary).NativeTerm);
        Assert.True(await presenter.SaveAsync());
        var id = presenter.Detail.Resource!.Id;
        Assert.Equal(NativeResourceMutationResult.Success,
            await presenter.CreateNewVocabularyAsync("소금", string.Empty));
        var reloaded = await presenter.OpenAsync(id);
        Assert.Equal(string.Empty,
            Assert.Single(reloaded.Resource!.Vocabulary, row =>
                row.TargetTerm == "소금").NativeTerm);
        Assert.Equal(string.Empty,
            Assert.Single(reloaded.Resource.Vocabulary, row =>
                row.TargetTerm == "첫째").NativeTerm);
        using var scope = fixture.Scope();
        Assert.Equal(string.Empty,
            (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .VocabularyWords.SingleAsync(word =>
                    word.TargetLanguageTerm == "소금")).NativeLanguageTerm);
    }

    [Fact]
    public async Task NativeWritesNeverDispatchAmbientSyncAndOwnerReceiptsStaySeparate()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var profiles = fixture.Provider.GetRequiredService<UserProfileRepository>();
        Assert.True(await profiles.SaveOnboardingProfileForOwnerAsync(
            NativeSliceFixture.UserA, "Owned name", "English", "Korean",
            20, "A2", NativeSliceFixture.EmailA) > 0);
        Assert.Equal(0, fixture.AmbientSync.Calls);

        var presenter = fixture.Resources;
        presenter.BeginCreate();
        presenter.ChangeDraft(NativeResourceField.Title, "Owner A glossary");
        Assert.True(await presenter.SaveAsync());
        var resourceA = presenter.Detail.Resource!.Id;
        Assert.Equal(0, fixture.AmbientSync.Calls);
        var pending = fixture.Provider.GetRequiredService<NativePendingWrites>();
        Assert.True(pending.IsPending(NativeSliceFixture.UserA));
        Assert.False(pending.IsPending(NativeSliceFixture.UserB));

        fixture.Activate(NativeSliceFixture.UserB);
        presenter.BeginCreate();
        presenter.ChangeDraft(NativeResourceField.Title, "Owner B glossary");
        Assert.True(await presenter.SaveAsync());
        var resourceB = presenter.Detail.Resource!.Id;
        Assert.Equal(0, fixture.AmbientSync.Calls);
        Assert.True(pending.IsPending(NativeSliceFixture.UserA));
        Assert.True(pending.IsPending(NativeSliceFixture.UserB));
        Assert.Equal(NativeResourceSyncPhase.Confirmed,
            await presenter.SynchronizeAsync());
        Assert.Equal([NativeSliceFixture.UserB], fixture.Sync.RequestedOwners);
        Assert.True(pending.IsPending(NativeSliceFixture.UserA));
        Assert.False(pending.IsPending(NativeSliceFixture.UserB));

        fixture.Activate(NativeSliceFixture.UserA);
        await presenter.LoadListAsync();
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.List.Sync);
        Assert.Equal(NativeResourceSyncPhase.Confirmed,
            await presenter.SynchronizeAsync());
        Assert.Equal([NativeSliceFixture.UserB, NativeSliceFixture.UserA],
            fixture.Sync.RequestedOwners);
        Assert.False(pending.IsPending(NativeSliceFixture.UserA));

        var skills = fixture.Provider.GetRequiredService<SkillProfileRepository>();
        Assert.NotEmpty(await skills.SaveAsync(new SkillProfile {
            Title = "Native skill", Language = "Korean"
        }, NativeSliceFixture.UserA, triggerSync: false));
        Assert.Equal(0, fixture.AmbientSync.Calls);
        Assert.NotEmpty(await skills.SaveAsync(new SkillProfile {
            Title = "Established caller skill", Language = "Korean"
        }, NativeSliceFixture.UserA));
        Assert.Equal(1, fixture.AmbientSync.Calls);

        await presenter.OpenAsync(resourceA);
        Assert.Equal(NativeResourceMutationResult.Success,
            await presenter.CreateNewVocabularyAsync("단어", string.Empty));
        var createdWord = Assert.Single(presenter.Detail.Resource!.Vocabulary);
        Assert.Equal(1, fixture.AmbientSync.Calls);
        Assert.Equal(1, (await presenter.ImportVocabularyFromTextAsync("둘,")).Added);
        Assert.Equal(1, fixture.AmbientSync.Calls);
        Assert.Equal(NativeResourceMutationResult.Success,
            await presenter.RemoveVocabularyAsync(createdWord.Id));
        Assert.Equal(1, fixture.AmbientSync.Calls);
        Assert.True(presenter.RequestDelete());
        Assert.Equal(NativeResourceMutationResult.Success,
            await presenter.ConfirmDeleteAsync());
        Assert.Equal(1, fixture.AmbientSync.Calls);
        using var verify = fixture.Scope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.LearningResources.AnyAsync(row => row.Id == resourceA));
        Assert.True(await db.LearningResources.AnyAsync(row =>
            row.Id == resourceB && row.UserProfileId == NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task ExistingRepositoryDeleteCallerStillTriggersItsDefaultAmbientSync()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var id = fixture.ResourceId(NativeSliceFixture.UserA);
        var repository = fixture.Provider.GetRequiredService<LearningResourceRepository>();

        Assert.True(await repository.DeleteResourceAsync(
            new LearningResource { Id = id }, NativeSliceFixture.UserA) > 0);
        Assert.Equal(1, fixture.AmbientSync.Calls);
        using var check = fixture.Scope();
        Assert.True(await check.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .LearningResources.AnyAsync(row =>
                row.Id == fixture.ResourceId(NativeSliceFixture.UserB)
                && row.UserProfileId == NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task ImportExistingResource_ParsesPasteAndFileDedupesAndDoesNotClearMappings()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        var id = fixture.ResourceId(NativeSliceFixture.UserA);
        await presenter.OpenAsync(id);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Description, "Keep unsaved description");
        var paste = await presenter.ImportVocabularyFromTextAsync(
            "하늘,sky\n하늘,duplicate\n바다,sea", "comma");
        Assert.Equal(NativeResourceMutationResult.Success, paste.Result);
        Assert.Equal(2, paste.Added);
        Assert.Equal(1, paste.Skipped);
        Assert.Equal("Keep unsaved description", presenter.Detail.Draft!.Description);
        Assert.Equal(6, presenter.Detail.Resource!.VocabularyCount);

        var fileBytes = Encoding.UTF8.GetBytes("바다\tduplicate\n달\tmoon");
        using var file = new MemoryStream(fileBytes);
        var imported = await presenter.ImportVocabularyFromFileAsync(
            file, fileBytes.Length, "more.txt", "tab");
        Assert.Equal(1, imported.Added);
        Assert.Equal(1, imported.Skipped);
        Assert.Equal(7, presenter.Detail.Resource!.VocabularyCount);
        Assert.True(await presenter.SaveAsync());
        Assert.Equal("Keep unsaved description",
            presenter.Detail.Resource!.Description);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(7, await db.ResourceVocabularyMappings
            .CountAsync(mapping => mapping.ResourceId == id));
        Assert.Equal(7, await db.VocabularyWords.CountAsync(word =>
            db.ResourceVocabularyMappings.Any(mapping =>
                mapping.ResourceId == id && mapping.VocabularyWordId == word.Id)));
    }

    [Fact]
    public async Task DuplicateOnlyImportDoesNotClaimAWriteOrPendingSync()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        var id = fixture.ResourceId(NativeSliceFixture.UserA);
        await presenter.OpenAsync(id);
        var repeated = await presenter.ImportVocabularyFromTextAsync(
            $"target-{NativeSliceFixture.UserA}-0,native-{NativeSliceFixture.UserA}-0");

        Assert.Equal(NativeResourceMutationResult.Success, repeated.Result);
        Assert.Equal(0, repeated.Added);
        Assert.Equal(1, repeated.Skipped);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, presenter.Detail.Sync);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, presenter.List.Sync);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(4, await db.ResourceVocabularyMappings.CountAsync(
            mapping => mapping.ResourceId == id));
    }

    [Fact]
    public async Task ForeignSmartAndUnsetLanguageRefuseVocabularyMutations()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var repository = fixture.Provider.GetRequiredService<LearningResourceRepository>();
        var ownedId = fixture.ResourceId(NativeSliceFixture.UserA);
        var foreignId = fixture.ResourceId(NativeSliceFixture.UserB);
        var ownedWord = fixture.WordId(NativeSliceFixture.UserA, 0);
        Assert.Equal(ResourceVocabularyWriteStatus.NotFoundOrForbidden,
            await repository.AttachOwnedVocabularyAsync(
                foreignId, ownedWord, "Korean", NativeSliceFixture.UserA));
        Assert.Equal(ResourceVocabularyWriteStatus.Invalid,
            await repository.AttachOwnedVocabularyAsync(
                ownedId, ownedWord, "Other", NativeSliceFixture.UserA));
        Assert.Equal(ResourceVocabularyWriteStatus.NotFoundOrForbidden,
            await repository.RemoveOwnedVocabularyAsync(
                foreignId, ownedWord, NativeSliceFixture.UserA));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var smart = await db.LearningResources.SingleAsync(row => row.Id == ownedId);
            smart.IsSmartResource = true;
            await db.SaveChangesAsync();
        }
        Assert.Equal(ResourceVocabularyWriteStatus.SmartReadOnly,
            await repository.CreateAndAttachOwnedVocabularyAsync(ownedId,
                new VocabularyWord {
                    TargetLanguageTerm = "new", NativeLanguageTerm = "new"
                }, "Korean", NativeSliceFixture.UserA));
        Assert.Equal(ResourceVocabularyWriteStatus.SmartReadOnly,
            await repository.RemoveOwnedVocabularyAsync(
                ownedId, ownedWord, NativeSliceFixture.UserA));
        using var check = fixture.Scope();
        var checkDb = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(4, await checkDb.ResourceVocabularyMappings
            .CountAsync(row => row.ResourceId == ownedId));
        Assert.Equal(4, await checkDb.ResourceVocabularyMappings
            .CountAsync(row => row.ResourceId == foreignId));
    }

    [Fact]
    public async Task DeleteRequiresConfirmation_CancelPreservesRows_ConfirmDeletesOnlyDisposableOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        await fixture.SeedAsync(NativeSliceFixture.UserB, known: true);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        var owned = fixture.ResourceId(NativeSliceFixture.UserA);
        var foreign = fixture.ResourceId(NativeSliceFixture.UserB);
        await presenter.OpenAsync(owned);
        Assert.True(presenter.RequestDelete());
        Assert.True(presenter.Detail.ShowDeleteConfirmation);
        presenter.CancelDelete();
        Assert.False(presenter.Detail.ShowDeleteConfirmation);
        Assert.Equal(NativeResourceMutationResult.Invalid,
            await presenter.ConfirmDeleteAsync());
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.True(await db.LearningResources.AnyAsync(row => row.Id == owned));
            Assert.Equal(4, await db.ResourceVocabularyMappings.CountAsync(
                mapping => mapping.ResourceId == owned));
        }

        Assert.True(presenter.RequestDelete());
        Assert.Equal(NativeResourceMutationResult.Success,
            await presenter.ConfirmDeleteAsync());
        Assert.Equal(NativeResourceDetailPhase.Idle, presenter.Detail.Phase);
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.List.Sync);
        using var check = fixture.Scope();
        var checkDb = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await checkDb.LearningResources.AnyAsync(row => row.Id == owned));
        Assert.Equal(0, await checkDb.ResourceVocabularyMappings.CountAsync(
            mapping => mapping.ResourceId == owned));
        Assert.Equal(0, await checkDb.VocabularyProgresses.CountAsync(row =>
            row.UserId == NativeSliceFixture.UserA));
        Assert.Equal(4, await checkDb.ResourceVocabularyMappings.CountAsync(
            mapping => mapping.ResourceId == foreign));
        Assert.Equal(4, await checkDb.VocabularyProgresses.CountAsync(row =>
            row.UserId == NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task StarterUsesAuthenticatedOwnerProfileAndReturnsExistingWithoutAnotherAiRequest()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        Assert.Equal(NativeResourceListPhase.Empty, (await presenter.LoadListAsync()).Phase);
        var created = await presenter.CreateStarterAsync();
        Assert.Equal(NativeResourceMutationResult.Success, created.Result);
        Assert.NotNull(created.ResourceId);
        Assert.False(presenter.List.StarterBusy);
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.List.Sync);
        Assert.Contains("learning Korean", fixture.ResourceAi.LastPrompt);
        Assert.Equal(2,
            (await presenter.OpenAsync(created.ResourceId)).Resource!.Vocabulary.Length);
        Assert.Equal(1, fixture.ResourceAi.Calls);
        Assert.Equal(NativeResourceMutationResult.AlreadyPresent,
            (await presenter.CreateStarterAsync()).Result);
        Assert.Equal(1, fixture.ResourceAi.Calls);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(1, await db.LearningResources.CountAsync(resource =>
                resource.UserProfileId == NativeSliceFixture.UserA
                && resource.Tags != null && resource.Tags.Contains("starter")));
        }

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await db.UserProfiles.SingleAsync(user =>
                user.Id == NativeSliceFixture.UserB);
            profile.NativeLanguage = "";
            await db.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserB);
        var missingLanguage = await presenter.CreateStarterAsync();
        Assert.Equal(NativeResourceMutationResult.Invalid, missingLanguage.Result);
        Assert.Equal("Resources_SetNativeLanguage", presenter.List.ErrorKey);
        Assert.Equal(1, fixture.ResourceAi.Calls);
    }

    [Fact]
    public async Task StarterWithBlankTargetLanguage_FailsClosedWithoutAiOrAnyResourceWrite()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        using (var scope = fixture.Scope())
        {
            var profileDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await profileDb.UserProfiles.SingleAsync(user =>
                user.Id == NativeSliceFixture.UserA);
            profile.TargetLanguage = string.Empty;
            await profileDb.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        var created = await presenter.CreateStarterAsync();
        Assert.Equal(NativeResourceMutationResult.Invalid, created.Result);
        Assert.Equal("NativeResources_SetTargetLanguage", created.ErrorKey);
        Assert.Equal("NativeResources_SetTargetLanguage", presenter.List.ErrorKey);
        Assert.Equal(0, fixture.ResourceAi.Calls);
        using var verify = fixture.Scope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.LearningResources.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.VocabularyWords.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task DashboardAndResourcesStarterCallers_ReuseOneOwnerResourceAndOneAiRequest()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var firstPresenter = fixture.Resources;
        var secondPresenter = fixture.Resources;
        fixture.ResourceAi.StarterGate =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = firstPresenter.CreateStarterAsync();
        var second = secondPresenter.CreateStarterAsync();
        Assert.False(second.IsCompleted);
        fixture.ResourceAi.StarterGate.SetResult(true);

        var outcomes = await Task.WhenAll(first, second);
        Assert.Equal(1, outcomes.Count(result =>
            result.Result == NativeResourceMutationResult.Success));
        Assert.Equal(1, outcomes.Count(result =>
            result.Result == NativeResourceMutationResult.AlreadyPresent));
        Assert.Equal(1, fixture.ResourceAi.Calls);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.LearningResources.CountAsync(resource =>
            resource.UserProfileId == NativeSliceFixture.UserA
            && resource.Tags != null && resource.Tags.Contains("starter")));
    }

    [Fact]
    public async Task TranscriptGenerationUsesAllowedGatewayDtoAndPersistsMappedWords()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var id = fixture.ResourceId(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.LearningResources.SingleAsync(row => row.Id == id))
                .Transcript = "오늘 하늘을 봐요";
            await db.SaveChangesAsync();
        }
        fixture.ResourceAi.TranscriptResponse = new VocabularyExtractionResponse
        {
            Vocabulary =
            [
                new ExtractedVocabularyItem {
                    TargetLanguageTerm = "하늘", NativeLanguageTerm = "sky",
                    PartOfSpeech = "noun", LexicalUnitType = LexicalUnitType.Word
                },
                new ExtractedVocabularyItem {
                    TargetLanguageTerm = "home", NativeLanguageTerm = "집",
                    LexicalUnitType = LexicalUnitType.Word
                },
                new ExtractedVocabularyItem {
                    TargetLanguageTerm = "하늘", NativeLanguageTerm = "duplicate"
                },
                new ExtractedVocabularyItem {
                    TargetLanguageTerm = "coffee", NativeLanguageTerm = "drink"
                }
            ]
        };
        var presenter = fixture.Resources;
        await presenter.OpenAsync(id);
        var generation = await presenter.GenerateVocabularyAsync();
        Assert.Equal(NativeResourceMutationResult.Success, generation.Result);
        Assert.Equal(2, generation.Added);
        Assert.Equal(4, generation.GeneratedTotal);
        Assert.Equal(2, generation.CreatedCount);
        Assert.Equal(0, generation.ExistingLinkedCount);
        Assert.Equal(2, generation.Skipped);
        Assert.False(presenter.Detail.VocabularyBusy);
        Assert.Contains("오늘 하늘을 봐요", fixture.ResourceAi.LastPrompt);
        Assert.Contains("English", fixture.ResourceAi.LastPrompt);
        Assert.Equal(NativeSliceFixture.UserA, fixture.ResourceAi.LastOwner);
        Assert.Equal(6, presenter.Detail.Resource!.Vocabulary.Length);
        Assert.Contains(presenter.Detail.Resource.Vocabulary,
            word => word.TargetTerm == "집" && word.NativeTerm == "home");
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.Detail.Sync);
        using var check = fixture.Scope();
        var dbCheck = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(6, await dbCheck.ResourceVocabularyMappings
            .CountAsync(mapping => mapping.ResourceId == id));
        Assert.Equal(4, await dbCheck.ResourceVocabularyMappings.CountAsync(mapping =>
            mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserB)));
    }

    [Fact]
    public async Task GenerationUsesCurrentUnsavedDraftAndPersistsItsFullContent()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var resourceId = fixture.ResourceId(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.LearningResources.SingleAsync(row => row.Id == resourceId))
                .Transcript = "old persisted transcript";
            await db.SaveChangesAsync();
        }
        fixture.ResourceAi.TranscriptResponse = new VocabularyExtractionResponse
        {
            Vocabulary =
            [
                new ExtractedVocabularyItem {
                    TargetLanguageTerm = "東京", NativeLanguageTerm = "Tokyo"
                }
            ]
        };

        var presenter = fixture.Resources;
        await presenter.OpenAsync(resourceId);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Unsaved title");
        presenter.ChangeDraft(NativeResourceField.Description, string.Empty);
        presenter.ChangeDraft(NativeResourceField.Tags, "draft-tag");
        presenter.ChangeDraft(NativeResourceField.MediaType, "Podcast");
        presenter.ChangeDraft(NativeResourceField.Language, "Japanese");
        presenter.ChangeDraft(NativeResourceField.MediaUrl, "https://example.invalid/draft");
        presenter.ChangeDraft(NativeResourceField.Transcript, "東京で話しましょう");
        presenter.ChangeDraft(NativeResourceField.Translation, string.Empty);

        var generated = await presenter.GenerateVocabularyAsync();
        Assert.Equal(NativeResourceMutationResult.Success, generated.Result);
        Assert.Equal(1, generated.GeneratedTotal);
        Assert.Equal(1, generated.Added);
        Assert.Equal(1, generated.CreatedCount);
        Assert.Equal(0, generated.ExistingLinkedCount);
        Assert.Contains("東京で話しましょう", fixture.ResourceAi.LastPrompt);
        Assert.Contains("Japanese", fixture.ResourceAi.LastPrompt);
        Assert.Equal(NativeSliceFixture.UserA, fixture.ResourceAi.LastOwner);
        Assert.DoesNotContain("old persisted transcript", fixture.ResourceAi.LastPrompt);
        Assert.Equal(NativeResourceDetailPhase.Ready, presenter.Detail.Phase);
        Assert.False(presenter.Detail.VocabularyBusy);
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.Detail.Sync);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await db.LearningResources.AsNoTracking().SingleAsync(
                row => row.Id == resourceId);
            Assert.Equal("Unsaved title", saved.Title);
            Assert.Equal(string.Empty, saved.Description);
            Assert.Equal("draft-tag", saved.Tags);
            Assert.Equal("Podcast", saved.MediaType);
            Assert.Equal("Japanese", saved.Language);
            Assert.Equal("https://example.invalid/draft", saved.MediaUrl);
            Assert.Equal("東京で話しましょう", saved.Transcript);
            Assert.Equal(string.Empty, saved.Translation);
            Assert.Equal(5, await db.ResourceVocabularyMappings.CountAsync(
                row => row.ResourceId == resourceId));
            Assert.Equal(4, await db.ResourceVocabularyMappings.CountAsync(
                row => row.ResourceId == fixture.ResourceId(NativeSliceFixture.UserB)));
        }
        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var reloaded = await fixture.Resources.OpenAsync(resourceId);
        Assert.Equal("東京で話しましょう", reloaded.Resource!.Transcript);
        Assert.Contains(reloaded.Resource.Vocabulary,
            word => word.TargetTerm == "東京" && word.NativeTerm == "Tokyo"
                && word.Language == "Japanese");
    }

    [Fact]
    public async Task GenerationCountsCreatedLinkedAndAlreadyMappedWordsFromActualRows()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var resourceId = fixture.ResourceId(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.LearningResources.SingleAsync(row => row.Id == resourceId))
                .Transcript = "단어와 하늘과 강";
            db.LearningResources.Add(new LearningResource {
                Id = "resource-existing-word", UserProfileId = NativeSliceFixture.UserA,
                Title = "Existing word", Language = "Korean"
            });
            db.VocabularyWords.AddRange(
                new VocabularyWord {
                    Id = "already-mapped", TargetLanguageTerm = "단어",
                    NativeLanguageTerm = "word", Language = "Korean"
                },
                new VocabularyWord {
                    Id = "existing-linked", TargetLanguageTerm = "하늘",
                    NativeLanguageTerm = "sky", Language = "Korean"
                });
            db.ResourceVocabularyMappings.AddRange(
                new ResourceVocabularyMapping {
                    ResourceId = resourceId, VocabularyWordId = "already-mapped"
                },
                new ResourceVocabularyMapping {
                    ResourceId = "resource-existing-word",
                    VocabularyWordId = "existing-linked"
                });
            await db.SaveChangesAsync();
        }
        fixture.ResourceAi.TranscriptResponse = new VocabularyExtractionResponse
        {
            Vocabulary =
            [
                new ExtractedVocabularyItem {
                    TargetLanguageTerm = "단어", NativeLanguageTerm = "word"
                },
                new ExtractedVocabularyItem {
                    TargetLanguageTerm = "하늘", NativeLanguageTerm = "sky"
                },
                new ExtractedVocabularyItem {
                    TargetLanguageTerm = "강", NativeLanguageTerm = "river"
                }
            ]
        };
        var presenter = fixture.Resources;
        await presenter.OpenAsync(resourceId);
        Assert.True(presenter.BeginEdit());
        var result = await presenter.GenerateVocabularyAsync();
        Assert.Equal(NativeResourceMutationResult.Success, result.Result);
        Assert.Equal(3, result.GeneratedTotal);
        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.ExistingLinkedCount);
        using var verify = fixture.Scope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await dbVerify.VocabularyWords.CountAsync(
            word => word.TargetLanguageTerm == "하늘"));
        Assert.Equal(1, await dbVerify.VocabularyWords.CountAsync(
            word => word.TargetLanguageTerm == "강"));
        Assert.Equal(7, await dbVerify.ResourceVocabularyMappings.CountAsync(
            mapping => mapping.ResourceId == resourceId));
        Assert.True(await dbVerify.ResourceVocabularyMappings.AnyAsync(
            mapping => mapping.ResourceId == resourceId
                && mapping.VocabularyWordId == "existing-linked"));
        Assert.Equal(4, await dbVerify.ResourceVocabularyMappings.CountAsync(
            mapping => mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserB)));
    }

    [Fact]
    public async Task GenerationPreservesTheActualLanguageOfBorrowedTerms()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var resourceId = fixture.ResourceId(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.UserProfiles.SingleAsync(row =>
                row.Id == NativeSliceFixture.UserA)).TargetLanguage = "Spanish";
            var resource = await db.LearningResources.SingleAsync(
                row => row.Id == resourceId);
            resource.Language = "Spanish";
            resource.Transcript = "bonjour en español";
            await db.SaveChangesAsync();
        }
        fixture.ResourceAi.TranscriptResponse = new VocabularyExtractionResponse
        {
            Vocabulary =
            [
                new ExtractedVocabularyItem {
                    TargetLanguageTerm = "bonjour", NativeLanguageTerm = "hello",
                    Language = "French"
                }
            ]
        };

        var presenter = fixture.Resources;
        await presenter.OpenAsync(resourceId);
        Assert.True(presenter.BeginEdit());
        var result = await presenter.GenerateVocabularyAsync();
        Assert.Equal(NativeResourceMutationResult.Success, result.Result);
        Assert.Equal(1, result.CreatedCount);
        Assert.Contains("Spanish", fixture.ResourceAi.LastPrompt);
        Assert.Equal("French", Assert.Single(presenter.Detail.Resource!.Vocabulary,
            row => row.TargetTerm == "bonjour").Language);
        using var verify = fixture.Scope();
        Assert.Equal("French",
            (await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .VocabularyWords.SingleAsync(row =>
                    row.TargetLanguageTerm == "bonjour")).Language);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<VocabularyExtractionResponse>(
            """{"vocabulary":[{"targetLanguageTerm":"bonjour","nativeLanguageTerm":"hello","language":"French"}]}""",
            new System.Text.Json.JsonSerializerOptions {
                PropertyNameCaseInsensitive = true
            });
        Assert.Equal("French", Assert.Single(parsed!.Vocabulary).Language);
    }

    [Fact]
    public async Task GeneratedTargetTermMayHaveAnEmptyNativeGloss()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var resourceId = fixture.ResourceId(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        await presenter.OpenAsync(resourceId);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Transcript, "사과를 먹어요");
        fixture.ResourceAi.TranscriptResponse = new VocabularyExtractionResponse
        {
            Vocabulary =
            [
                new ExtractedVocabularyItem {
                    TargetLanguageTerm = "사과",
                    NativeLanguageTerm = string.Empty
                }
            ]
        };

        var result = await presenter.GenerateVocabularyAsync();
        Assert.Equal(NativeResourceMutationResult.Success, result.Result);
        Assert.Equal(1, result.GeneratedTotal);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(string.Empty,
            Assert.Single(presenter.Detail.Resource!.Vocabulary,
                word => word.TargetTerm == "사과").NativeTerm);
        using var verify = fixture.Scope();
        Assert.Equal(string.Empty,
            (await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .VocabularyWords.SingleAsync(word =>
                    word.TargetLanguageTerm == "사과")).NativeLanguageTerm);
    }

    [Fact]
    public async Task GenerationWithOnlyFilteredWordsStillSavesCurrentDraftAndReportsActualCounts()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var resourceId = fixture.ResourceId(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        await presenter.OpenAsync(resourceId);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Transcript, "new unsaved transcript");
        fixture.ResourceAi.TranscriptResponse = new VocabularyExtractionResponse
        {
            Vocabulary =
            [
                new ExtractedVocabularyItem {
                    TargetLanguageTerm = "coffee", NativeLanguageTerm = "drink"
                }
            ]
        };

        var result = await presenter.GenerateVocabularyAsync();
        Assert.Equal(NativeResourceMutationResult.Success, result.Result);
        Assert.Equal(1, result.GeneratedTotal);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(0, result.ExistingLinkedCount);
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.Detail.Sync);
        using var verify = fixture.Scope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("new unsaved transcript",
            (await db.LearningResources.SingleAsync(row =>
                row.Id == resourceId)).Transcript);
        Assert.Equal(4, await db.ResourceVocabularyMappings.CountAsync(row =>
            row.ResourceId == resourceId));
    }

    [Fact]
    public async Task GenerationRefusesConcurrentContentChangeAfterUsingCurrentDraft()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var resourceId = fixture.ResourceId(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        await presenter.OpenAsync(resourceId);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Transcript, "current unsaved speech");
        var gate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ResourceAi.TranscriptGate = gate;
        var pending = presenter.GenerateVocabularyAsync();
        Assert.True(presenter.Detail.VocabularyBusy);
        Assert.Throws<InvalidOperationException>(() =>
            presenter.ChangeDraft(NativeResourceField.Title, "late stale edit"));
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var concurrent = await db.LearningResources.SingleAsync(
                row => row.Id == resourceId);
            concurrent.Transcript = "concurrent database edit";
            concurrent.UpdatedAt = concurrent.UpdatedAt.AddMinutes(1);
            await db.SaveChangesAsync();
        }
        gate.SetResult(true);
        var result = await pending;
        Assert.Equal(NativeResourceMutationResult.Failed, result.Result);
        Assert.Equal("NativeResources_Conflict", result.ErrorKey);
        Assert.Equal(NativeResourceDetailPhase.Conflict, presenter.Detail.Phase);
        Assert.Equal("current unsaved speech", presenter.Detail.Draft!.Transcript);
        Assert.False(presenter.Detail.VocabularyBusy);
        Assert.Contains("current unsaved speech", fixture.ResourceAi.LastPrompt);
        using var verify = fixture.Scope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("concurrent database edit",
            (await dbVerify.LearningResources.AsNoTracking().SingleAsync(
                row => row.Id == resourceId)).Transcript);
        Assert.Equal(4, await dbVerify.ResourceVocabularyMappings.CountAsync(
            row => row.ResourceId == resourceId));
    }

    [Fact]
    public async Task ResourceAiUnavailableAndOwnerSwitchDoNotReportSuccessOrWriteWords()
    {
        using (var unavailable = new NativeSliceFixture())
        {
            await unavailable.SeedAsync(NativeSliceFixture.UserA);
            unavailable.Activate(NativeSliceFixture.UserA);
            var id = unavailable.ResourceId(NativeSliceFixture.UserA);
            using (var scope = unavailable.Scope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                (await db.LearningResources.SingleAsync(row => row.Id == id))
                    .Transcript = "spoken content";
                await db.SaveChangesAsync();
            }
            var unavailablePresenter = unavailable.Resources;
            await unavailablePresenter.OpenAsync(id);
            Assert.Equal(NativeResourceMutationResult.Failed,
                (await unavailablePresenter.GenerateVocabularyAsync()).Result);
            Assert.Equal("ResourceEdit_GenerateFailed", unavailablePresenter.Detail.ErrorKey);
            Assert.Equal(NativeResourceMutationResult.Failed,
                (await unavailablePresenter.CreateStarterAsync()).Result);
            using var check = unavailable.Scope();
            Assert.Equal(4, await check.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .ResourceVocabularyMappings.CountAsync(mapping => mapping.ResourceId == id));
        }

        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var resourceId = fixture.ResourceId(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.LearningResources.SingleAsync(row => row.Id == resourceId))
                .Transcript = "queued transcript";
            await db.SaveChangesAsync();
        }
        var presenter = fixture.Resources;
        await presenter.OpenAsync(resourceId);
        var gate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ResourceAi.TranscriptGate = gate;
        var inFlight = presenter.GenerateVocabularyAsync();
        Assert.True(presenter.Detail.VocabularyBusy);
        fixture.Activate(NativeSliceFixture.UserB);
        gate.SetResult(true);
        Assert.NotEqual(NativeResourceMutationResult.Success, (await inFlight).Result);
        using var verify = fixture.Scope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(4, await dbVerify.ResourceVocabularyMappings.CountAsync(
            mapping => mapping.ResourceId == resourceId));
        Assert.Equal(4, await dbVerify.ResourceVocabularyMappings.CountAsync(
            mapping => mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserB)));
    }
}
