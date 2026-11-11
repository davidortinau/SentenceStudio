using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.Vocabulary;
using SentenceStudio.Services;
using SentenceStudio.Services.DTOs;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// The word editor's Reference Sentences (source ReferenceSentencesSection, restored on native
/// at the Captain's request 2026-11-10), through the shared browser and the real store on
/// isolated SQLite: every write is the owner's own sentence on the owner's own word.
/// </summary>
public sealed class VocabularyExampleSentencesTests
{
    private const string A = NativeSliceFixture.UserA;
    private const string B = NativeSliceFixture.UserB;

    [Fact]
    public async Task Browser_AddsSetsCoreFlagsEditsAndDeletes_EachSavedAtOnce()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        fixture.Activate(A);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(A));
        var wordId = fixture.WordId(A, 0);
        await browser.OpenWordAsync(wordId);
        var examples = browser.EditState.Examples!;
        Assert.Empty(examples.Sentences);
        Assert.False(examples.CanAdd);

        browser.ChangeNewExample(VocabularyExampleField.Target, "  병을 열어요.  ");
        browser.ChangeNewExample(VocabularyExampleField.Native, "I open the bottle.");
        browser.SetNewExampleRegister(SpeechRegister.InformalPolite);
        Assert.True(browser.EditState.Examples!.CanAdd);
        await browser.AddExampleAsync();
        examples = browser.EditState.Examples!;
        var first = Assert.Single(examples.Sentences);
        // Source AddManualAsync: trimmed, the learner's own, the word's first is its core one.
        Assert.Equal("병을 열어요.", first.TargetSentence);
        Assert.True(first.IsCore);
        Assert.Equal(ExampleSentenceSource.UserAuthored, first.Source);
        Assert.Equal(SpeechRegister.InformalPolite, first.Register);
        Assert.Equal(string.Empty, examples.NewTarget);
        Assert.Equal(SpeechRegister.Unspecified, examples.NewRegister);
        Assert.Equal("RefSentences_Saved", examples.Message);
        Assert.False(examples.MessageIsError);
        Assert.True(fixture.Provider.GetRequiredService<NativePendingWrites>().IsPending(A));

        browser.ChangeNewExample(VocabularyExampleField.Target, "병이 깨졌다.");
        await browser.AddExampleAsync();
        var second = browser.EditState.Examples!.Sentences.Single(s => s.Id != first.Id);
        Assert.False(second.IsCore);

        // One core example per word, listed first.
        await browser.ToggleExampleCoreAsync(second.Id);
        var sentences = browser.EditState.Examples!.Sentences;
        Assert.Equal(new[] { second.Id, first.Id }, sentences.Select(s => s.Id));
        Assert.True(sentences[0].IsCore);
        Assert.False(sentences[1].IsCore);
        await browser.ToggleExampleFlagAsync(first.Id);
        Assert.True(browser.EditState.Examples!.Sentences.Single(s => s.Id == first.Id).IsFlagged);

        browser.StartEditExample(first.Id);
        browser.ChangeEditedExample(VocabularyExampleField.Target, " ");
        Assert.False(browser.EditState.Examples!.CanSaveEdit);
        await browser.SaveEditedExampleAsync();
        Assert.Equal(first.Id, browser.EditState.Examples!.EditingId);
        browser.ChangeEditedExample(VocabularyExampleField.Target, "병을 열었어요.");
        browser.ChangeEditedExample(VocabularyExampleField.Native, "  ");
        browser.SetEditedExampleRegister(SpeechRegister.Casual);
        await browser.SaveEditedExampleAsync();
        examples = browser.EditState.Examples!;
        Assert.Null(examples.EditingId);
        var edited = examples.Sentences.Single(s => s.Id == first.Id);
        Assert.Equal(("병을 열었어요.", (string?)null, SpeechRegister.Casual),
            (edited.TargetSentence, edited.NativeSentence, edited.Register));

        await browser.DeleteExampleAsync(second.Id);
        examples = browser.EditState.Examples!;
        Assert.Equal(first.Id, Assert.Single(examples.Sentences).Id);
        Assert.Equal("RefSentences_Deleted", examples.Message);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = Assert.Single(await db.ExampleSentences
                .Where(s => s.VocabularyWordId == wordId).ToListAsync());
            Assert.Equal((A, ExampleSentenceStatus.Curated, true, false),
                (row.UserProfileId, row.Status, row.IsFlagged, row.IsCore));
            var word = await db.VocabularyWords.SingleAsync(w => w.Id == wordId);
            // The strength label counts the owner's sentences.
            Assert.Equal(EncodingScoreHelper.CalculateWithLabel(word, 1).Label,
                browser.EditState.Detail!.Word.EncodingLabel);
        }

        // The sentences never touch the word, so its own Save still applies.
        browser.ChangeDraft(nameof(NativeVocabularyEditDraft.NativeTerm), "bottle");
        await browser.SaveAsync();
        Assert.False(browser.IsFormOpen);
        Assert.Equal("VocabWordEdit_UpdatedSuccess", browser.ListState.Feedback);
    }

    [Fact]
    public async Task Browser_LeavingAfterASentenceChange_ReloadsTheListsStrengthBadge()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        var wordId = fixture.WordId(A, 0);
        using (var scope = fixture.Scope())
        {
            // Terms only (2 of 6): Basic; one sentence makes it Good (3 of 6).
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.VocabularyWords.SingleAsync(w => w.Id == wordId)).MnemonicImageUri = null;
            await db.SaveChangesAsync();
        }
        fixture.Activate(A);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(A));
        Assert.Equal("Basic", browser.ListState.Catalog.Words.Single(w => w.Id == wordId).EncodingLabel);
        await browser.OpenWordAsync(wordId);
        browser.ChangeNewExample(VocabularyExampleField.Target, "문장");
        await browser.AddExampleAsync();
        var expected = browser.EditState.Detail!.Word.EncodingLabel;
        Assert.Equal("Good", expected);
        Assert.Equal("Basic", browser.ListState.Catalog.Words.Single(w => w.Id == wordId).EncodingLabel);

        // Review 2026-11-10: moving to another word in the open editor keeps the reload.
        await browser.OpenWordAsync(fixture.WordId(A, 1));
        browser.CloseWord();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (browser.ListState.Catalog.Words.SingleOrDefault(w => w.Id == wordId)?.EncodingLabel != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, "The list did not read the word again.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Browser_ASentenceTooLongForItsColumn_SaysHowLongItMayBe()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        fixture.Activate(A);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(A));
        await browser.OpenWordAsync(fixture.WordId(A, 0));

        browser.ChangeNewExample(VocabularyExampleField.Target, new string('가', 501));
        await browser.AddExampleAsync();
        var examples = browser.EditState.Examples!;
        Assert.Empty(examples.Sentences);
        Assert.Equal(("RefSentences_TooLong", true), (examples.Message, examples.MessageIsError));
        // The typed text stays, so it can be shortened.
        Assert.Equal(501, examples.NewTarget.Length);
        browser.ChangeNewExample(VocabularyExampleField.Target, new string('가', 500));
        await browser.AddExampleAsync();
        Assert.Single(browser.EditState.Examples!.Sentences);
    }

    [Fact]
    public async Task Store_ChangesOnlyTheOwnersSentencesOnTheOwnersWord()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        await fixture.SeedAsync(B);
        var wordA = fixture.WordId(A, 0);
        var wordB = fixture.WordId(B, 0);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Another learner's core sentence, and one recorded to no one, on A's word.
            db.ExampleSentences.AddRange(
                new ExampleSentence { Id = "b-core", VocabularyWordId = wordA, UserProfileId = B,
                    TargetSentence = "B's sentence", IsCore = true },
                new ExampleSentence { Id = "nobody", VocabularyWordId = wordA,
                    TargetSentence = "No one's sentence" });
            await db.SaveChangesAsync();
        }
        fixture.Activate(A);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var draft = new NativeExampleSentenceDraft("병을 열어요.", null);

        var added = await store.AddOwnedExampleAsync(A, wordA, draft,
            ExampleSentenceSource.UserAuthored, coreWhenFirst: true, default);
        Assert.True(added.Saved);
        var own = Assert.Single(added.Examples);
        // The first of A's own sentences is A's core one; the others are not A's to count.
        Assert.True(own.IsCore);
        Assert.True((await store.SetOwnedExampleCoreAsync(A, wordA, own.Id, true, default)).Saved);

        foreach (var foreign in new[] { "b-core", "nobody" })
        {
            Assert.Equal(NativeExampleSentenceStatus.NotFoundOrForbidden,
                (await store.EditOwnedExampleAsync(A, wordA, foreign, draft, default)).Status);
            Assert.Equal(NativeExampleSentenceStatus.NotFoundOrForbidden,
                (await store.SetOwnedExampleFlagAsync(A, wordA, foreign, true, default)).Status);
            Assert.Equal(NativeExampleSentenceStatus.NotFoundOrForbidden,
                (await store.SetOwnedExampleCoreAsync(A, wordA, foreign, true, default)).Status);
            Assert.Equal(NativeExampleSentenceStatus.NotFoundOrForbidden,
                (await store.DeleteOwnedExampleAsync(A, wordA, foreign, default)).Status);
        }
        // Not A's word; not the active owner.
        Assert.Equal(NativeExampleSentenceStatus.NotFoundOrForbidden,
            (await store.AddOwnedExampleAsync(A, wordB, draft, ExampleSentenceSource.UserAuthored,
                true, default)).Status);
        Assert.Equal(NativeExampleSentenceStatus.NotFoundOrForbidden,
            (await store.AddOwnedExampleAsync(B, wordB, draft, ExampleSentenceSource.UserAuthored,
                true, default)).Status);
        // Blank, too long (the 500-character columns), or an unknown register.
        foreach (var invalid in new[] {
                     draft with { TargetSentence = "  " },
                     draft with { TargetSentence = new string('가', 501) },
                     draft with { NativeSentence = new string('a', 501) },
                     draft with { Register = (SpeechRegister)99 } })
            Assert.Equal(NativeExampleSentenceStatus.Invalid,
                (await store.AddOwnedExampleAsync(A, wordA, invalid,
                    ExampleSentenceSource.UserAuthored, true, default)).Status);

        var detail = (await store.GetOwnedVocabularyAsync(A, wordA, default))!;
        Assert.Equal(own.Id, Assert.Single(detail.Examples).Id);
        using var verify = fixture.Scope();
        var rows = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var others = await rows.ExampleSentences.AsNoTracking()
            .Where(s => s.Id == "b-core" || s.Id == "nobody").ToListAsync();
        Assert.Equal(2, others.Count);
        Assert.True(others.Single(s => s.Id == "b-core").IsCore);
        Assert.All(others, s => Assert.False(s.IsFlagged));
        Assert.Equal(0, await rows.ExampleSentences.CountAsync(s => s.VocabularyWordId == wordB));
    }

    [Fact]
    public async Task Browser_GenerateSuggestsForTheStoredWord_KeepSavesAsAi_DiscardDrops()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedAsync(A);
        fixture.Activate(A);
        fixture.ResourceAi.ExampleResponse = new GeneratedExampleSentencesDto
        {
            Sentences = [
                new() { TargetSentence = " 물 한 병 주세요. ", NativeSentence = "A bottle of water, please.",
                    Register = SpeechRegister.InformalPolite, DifficultyLevel = 1 },
                new() { TargetSentence = "  ", NativeSentence = "blank" },
                new() { TargetSentence = "병이 있어요.", NativeSentence = "", DifficultyLevel = 9 }]
        };
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(A));
        var wordId = fixture.WordId(A, 0);
        await browser.OpenWordAsync(wordId);

        await browser.GenerateExamplesAsync();
        var examples = browser.EditState.Examples!;
        Assert.False(examples.Generating);
        Assert.Equal(2, examples.Suggestions.Count);
        Assert.Equal("물 한 병 주세요.", examples.Suggestions[0].TargetSentence);
        Assert.Null(examples.Suggestions[1].DifficultyLevel);
        Assert.Equal(A, fixture.ResourceAi.LastOwner);
        Assert.Contains($"\"target-{A}-0\"", fixture.ResourceAi.LastPrompt);
        Assert.Contains("Korean", fixture.ResourceAi.LastPrompt);
        Assert.Empty(examples.Sentences);

        var kept = examples.Suggestions[1].Key;
        browser.ChangeSuggestion(kept, VocabularyExampleField.Target, "병이 두 개 있어요.");
        await browser.KeepSuggestionAsync(kept);
        examples = browser.EditState.Examples!;
        var saved = Assert.Single(examples.Sentences);
        Assert.Equal(("병이 두 개 있어요.", ExampleSentenceSource.AiGenerated, false),
            (saved.TargetSentence, saved.Source, saved.IsCore));
        Assert.Equal("RefSentences_Kept", examples.Message);
        var discarded = Assert.Single(examples.Suggestions).Key;
        browser.DiscardSuggestion(discarded);
        Assert.Empty(browser.EditState.Examples!.Suggestions);
        using (var scope = fixture.Scope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .ExampleSentences.SingleAsync(s => s.VocabularyWordId == wordId);
            // Kept: curated, so quizzes may use it (source KeepAsync).
            Assert.Equal((A, ExampleSentenceStatus.Curated), (row.UserProfileId, row.Status));
        }

        fixture.ResourceAi.ExampleResponse = null;
        await browser.GenerateExamplesAsync();
        examples = browser.EditState.Examples!;
        Assert.Empty(examples.Suggestions);
        Assert.Equal(("RefSentences_GenerateFailed", true), (examples.Message, examples.MessageIsError));
    }

    [Fact]
    public async Task Browser_WithoutAnAiGateway_GenerateSaysItFailed()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        fixture.Activate(A);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(A));
        await browser.OpenWordAsync(fixture.WordId(A, 0));

        await browser.GenerateExamplesAsync();
        var examples = browser.EditState.Examples!;
        Assert.False(examples.Generating);
        Assert.Equal(("RefSentences_GenerateFailed", true), (examples.Message, examples.MessageIsError));
    }

    [Fact]
    public async Task Browser_AfterTheOwnerChanged_WritesNoSentence()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        await fixture.SeedAsync(B);
        fixture.Activate(A);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(A));
        var wordId = fixture.WordId(A, 0);
        await browser.OpenWordAsync(wordId);
        browser.ChangeNewExample(VocabularyExampleField.Target, "병을 열어요.");

        fixture.Activate(B);
        await browser.AddExampleAsync();

        Assert.True(browser.ListState.Blocked);
        Assert.False(browser.IsFormOpen);
        using var scope = fixture.Scope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .ExampleSentences.CountAsync());
    }

    private static VocabularyBrowser Create(NativeSliceFixture fixture) =>
        new(fixture.Provider.GetRequiredService<INativeResourceStore>(),
            fixture.UserScope,
            fixture.Preferences, new ScratchQuizMedia(),
            NullLogger<VocabularyBrowser>.Instance, key => key);
}
