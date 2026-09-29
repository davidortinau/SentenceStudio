using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeQuizProductionAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerChoicesHaveOneCorrectAndOnlyDistinctReachableAnswers(
        bool promptUsesNativeLanguage)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 5);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        fixture.Activate(NativeSliceFixture.UserA);

        using var scope = fixture.Scope();
        var repo = scope.ServiceProvider.GetRequiredService<LearningResourceRepository>();
        var ownerWords = await repo.GetAllVocabularyWordsWithResourcesAsync(NativeSliceFixture.UserA);
        var foreign = Assert.Single(await repo.GetAllVocabularyWordsWithResourcesAsync(
            NativeSliceFixture.UserB));
        var question = ownerWords[0];
        var answer = promptUsesNativeLanguage
            ? question.TargetLanguageTerm : question.NativeLanguageTerm;
        var reachableAnswers = ownerWords.Select(word => promptUsesNativeLanguage
            ? word.TargetLanguageTerm : word.NativeLanguageTerm).ToHashSet(StringComparer.Ordinal);

        var choices = await NewChoices(scope.ServiceProvider).CreateAsync(
            NativeSliceFixture.UserA, question, ownerWords,
            promptUsesNativeLanguage, CancellationToken.None);

        Assert.Equal(4, choices.Length);
        Assert.Equal(1, choices.Count(choice => choice.Text == answer));
        Assert.All(choices, choice => Assert.Contains(choice.Text, reachableAnswers));
        Assert.Equal(choices.Length,
            choices.Select(choice => choice.Text.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(choices, choice => choice.Text ==
            (promptUsesNativeLanguage ? foreign.TargetLanguageTerm : foreign.NativeLanguageTerm));
    }

    [Fact]
    public async Task ChoicesRejectForeignWordsSpoofedQuestionAndSingleOption()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        fixture.Activate(NativeSliceFixture.UserA);
        using var scope = fixture.Scope();
        var repo = scope.ServiceProvider.GetRequiredService<LearningResourceRepository>();
        var owned = await repo.GetAllVocabularyWordsWithResourcesAsync(NativeSliceFixture.UserA);
        var foreign = Assert.Single(await repo.GetAllVocabularyWordsWithResourcesAsync(
            NativeSliceFixture.UserB));
        var adapter = NewChoices(scope.ServiceProvider);

        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.CreateAsync(
            NativeSliceFixture.UserA, owned[0], [owned[0], foreign],
            false, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.CreateAsync(
            NativeSliceFixture.UserA, foreign, [foreign],
            false, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.CreateAsync(
            NativeSliceFixture.UserA, owned[0], [owned[0]],
            false, CancellationToken.None));

        var spoofed = new VocabularyWord
        {
            Id = owned[0].Id,
            TargetLanguageTerm = "not-the-owned-target",
            NativeLanguageTerm = owned[0].NativeLanguageTerm
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.CreateAsync(
            NativeSliceFixture.UserA, spoofed, owned,
            false, CancellationToken.None));
    }

    [Fact]
    public async Task EligibilityRejectsForeignWordsAndProgress_WithoutLeakingABatch()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        fixture.Activate(NativeSliceFixture.UserA);
        using var scope = fixture.Scope();
        var repo = scope.ServiceProvider.GetRequiredService<LearningResourceRepository>();
        var owned = await repo.GetAllVocabularyWordsWithResourcesAsync(NativeSliceFixture.UserA);
        var foreign = Assert.Single(await repo.GetAllVocabularyWordsWithResourcesAsync(
            NativeSliceFixture.UserB));
        var adapter = NewEligibility(scope.ServiceProvider);
        var ownedItem = Item(owned[0], NativeSliceFixture.UserA);

        var selected = await adapter.SelectAsync(
            NativeSliceFixture.UserA, false, [ownedItem],
            CancellationToken.None);
        Assert.False(selected.AllMastered);
        Assert.Same(ownedItem, Assert.Single(selected.Batch));

        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.SelectAsync(
            NativeSliceFixture.UserA, false,
            [ownedItem, Item(foreign, NativeSliceFixture.UserA)],
            CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.SelectAsync(
            NativeSliceFixture.UserA, false,
            [Item(owned[1], NativeSliceFixture.UserB)],
            CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.SelectAsync(
            NativeSliceFixture.UserA, false,
            [new VocabularyQuizItem { Word = owned[1] }],
            CancellationToken.None));
    }

    [Fact]
    public async Task GraderSharesTextNormalizationButMultipleChoiceRemainsExact()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        using var scope = fixture.Scope();
        var adapter = NewGrader(scope.ServiceProvider);

        var text = await adapter.EvaluateAsync(
            NativeSliceFixture.UserA, "Text", "leftover", "remaining/leftover",
            CancellationToken.None);
        Assert.True(text.IsCorrect);
        Assert.Equal("remaining/leftover", text.CompleteForm);
        Assert.False((await adapter.EvaluateAsync(
            NativeSliceFixture.UserA, "MultipleChoice", "leftover",
            "remaining/leftover", CancellationToken.None)).IsCorrect);
        Assert.True((await adapter.EvaluateAsync(
            NativeSliceFixture.UserA, "MultipleChoice", " HELLO ",
            "hello", CancellationToken.None)).IsCorrect);
        Assert.False((await adapter.EvaluateAsync(
            NativeSliceFixture.UserA, "MultipleChoice", "helo",
            "hello", CancellationToken.None)).IsCorrect);

        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.EvaluateAsync(
            NativeSliceFixture.UserA, "Sentence", "a sentence using hello",
            "hello", CancellationToken.None));
    }

    [Fact]
    public async Task AllThreePortsRejectMissingMismatchedOrCanceledOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        fixture.Activate(NativeSliceFixture.UserA);
        using var scope = fixture.Scope();
        var repo = scope.ServiceProvider.GetRequiredService<LearningResourceRepository>();
        var owned = await repo.GetAllVocabularyWordsWithResourcesAsync(NativeSliceFixture.UserA);
        var candidates = new[] { Item(owned[0], NativeSliceFixture.UserA) };
        var choices = NewChoices(scope.ServiceProvider);
        var eligibility = NewEligibility(scope.ServiceProvider);
        var grader = NewGrader(scope.ServiceProvider);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => choices.CreateAsync(
            NativeSliceFixture.UserB, owned[0], owned, false, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => eligibility.SelectAsync(
            NativeSliceFixture.UserB, false, candidates, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => grader.EvaluateAsync(
            NativeSliceFixture.UserB, "Text", "hello", "hello", CancellationToken.None));

        fixture.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, NativeSliceFixture.UserB);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => choices.CreateAsync(
            NativeSliceFixture.UserA, owned[0], owned, false, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => eligibility.SelectAsync(
            NativeSliceFixture.UserA, false, candidates, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => grader.EvaluateAsync(
            NativeSliceFixture.UserA, "Text", "hello", "hello", CancellationToken.None));
        fixture.Activate(NativeSliceFixture.UserA);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => choices.CreateAsync(
            NativeSliceFixture.UserA, owned[0], owned, false, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => eligibility.SelectAsync(
            NativeSliceFixture.UserA, false, candidates, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grader.EvaluateAsync(
            NativeSliceFixture.UserA, "Text", "hello", "hello", canceled.Token));

        fixture.LoseOwner();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => choices.CreateAsync(
            NativeSliceFixture.UserA, owned[0], owned, false, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => eligibility.SelectAsync(
            NativeSliceFixture.UserA, false, candidates, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => grader.EvaluateAsync(
            NativeSliceFixture.UserA, "Text", "hello", "hello", CancellationToken.None));
    }

    private static NativeQuizChoicesAdapter NewChoices(IServiceProvider provider) => new(
        provider.GetRequiredService<IUserScopeProvider>(),
        provider.GetRequiredService<IPreferencesService>(),
        provider.GetRequiredService<LearningResourceRepository>());

    private static NativeQuizEligibilityAdapter NewEligibility(IServiceProvider provider) => new(
        provider.GetRequiredService<IUserScopeProvider>(),
        provider.GetRequiredService<IPreferencesService>(),
        provider.GetRequiredService<LearningResourceRepository>());

    private static NativeQuizGraderAdapter NewGrader(IServiceProvider provider) => new(
        provider.GetRequiredService<IUserScopeProvider>(),
        provider.GetRequiredService<IPreferencesService>());

    private static VocabularyQuizItem Item(VocabularyWord word, string progressOwner) => new()
    {
        Word = word,
        Progress = new VocabularyProgress
        {
            UserId = progressOwner, VocabularyWordId = word.Id
        }
    };
}
