using System.Collections;
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Services;
using VocabularyPage = SentenceStudio.WebUI.Pages.Vocabulary;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// The web/MAUI Vocabulary page under the one ownership rule (Captain 2026-11-06), on the real
/// repository over scratch SQLite. Review findings of the e85d5dc3 ownership merge:
/// 4 (Fix Swapped reported a technical failure as an ownership refusal) and
/// 8 (bulk delete did not reload when the selected words were no longer found).
/// </summary>
public sealed class VocabularyPageOwnershipTests
{
    private static readonly BlazorLocalizationService Text = new();

    [Fact]
    public async Task FixSwapped_TechnicalFailure_IsReportedAsAnError_NotAsKept()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync(services =>
        {
            Register(services);
            services.ConfigureDbContext<ApplicationDbContext>(options =>
                options.AddInterceptors(new FailVocabularyWordUpdates()));
        });
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("apple", "사과"));
        var (_, page) = await fixture.Renderer.RenderAsync<VocabularyPage>();

        await fixture.Renderer.InvokeAsync(page, "FixSwappedLanguages");

        var results = Members.Get<List<string>>(page, "cleanupResults");
        results.Should().Contain(string.Format(Text["Vocabulary_FixSwappedFailed"], 1));
        results.Should().NotContain(string.Format(Text["Vocabulary_FixSwappedKept"], 1));
        fixture.Toasts.Should().Contain(t => t.Type == ToastType.Error
            && t.Text == string.Format(Text["Vocabulary_FixSwappedFailed"], 1));
        (await fixture.RowsAsync<VocabularyWord>()).Single().TargetLanguageTerm.Should().Be("apple",
            "the failed swap saved nothing");
    }

    [Fact]
    public async Task FixSwapped_WordNoLongerTheLearners_IsKept_WithoutAnError()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync(Register);
        var (_, words) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("apple", "사과"));
        var (_, page) = await fixture.Renderer.RenderAsync<VocabularyPage>();
        // Another device recorded it elsewhere after the list loaded (here: to another learner).
        await RecordToAsync(fixture, words[0].Id, ActivityPageFixture.OwnerB);

        await fixture.Renderer.InvokeAsync(page, "FixSwappedLanguages");

        var results = Members.Get<List<string>>(page, "cleanupResults");
        results.Should().Contain(string.Format(Text["Vocabulary_FixSwappedKept"], 1));
        results.Should().NotContain(string.Format(Text["Vocabulary_FixSwappedFailed"], 1));
        fixture.Toasts.Should().NotContain(t => t.Type == ToastType.Error);
        (await fixture.RowsAsync<VocabularyWord>()).Single().TargetLanguageTerm.Should().Be("apple");
    }

    [Fact]
    public async Task BulkDelete_OnlyNotFound_ReloadsTheList()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync(Register);
        var (_, words) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("하나", "one"), ("둘", "two"));
        var (_, page) = await fixture.Renderer.RenderAsync<VocabularyPage>();
        ShownWordIds(page).Should().BeEquivalentTo(words.Select(w => w.Id));

        // The selected word is gone from the learner's vocabulary before the delete runs.
        Members.Get<HashSet<string>>(page, "selectedWordIds").Add(words[0].Id);
        await RecordToAsync(fixture, words[0].Id, ActivityPageFixture.OwnerB);

        await fixture.Renderer.InvokeAsync(page, "ConfirmBulkDelete");

        fixture.Toasts.Should().Contain(t => t.Text == string.Format(Text["Vocabulary_BulkDeleteNotFound"], 1));
        ShownWordIds(page).Should().Equal(words[1].Id);
        (await fixture.RowsAsync<VocabularyWord>()).Should().HaveCount(2, "nothing was deleted");
    }

    private static void Register(IServiceCollection services)
    {
        services.AddSingleton<ISearchQueryParser, SearchQueryParser>();
        services.AddSingleton<IJSRuntime>(ThrowingStub<IJSRuntime>.Create());
    }

    private static async Task RecordToAsync(ActivityPageFixture fixture, string wordId, string owner)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().VocabularyWords
            .Where(w => w.Id == wordId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.UserProfileId, owner));
    }

    private static List<string> ShownWordIds(object page)
    {
        var ids = new List<string>();
        foreach (var item in (IEnumerable)Members.Get<object>(page, "allWords"))
        {
            var word = (VocabularyWord)item.GetType()
                .GetProperty("Word", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(item)!;
            ids.Add(word.Id);
        }
        return ids;
    }

    /// <summary>A database failure on any word update: the technical failure the page must report.</summary>
    private sealed class FailVocabularyWordUpdates : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result) =>
            IsWordUpdate(command) ? throw new InvalidOperationException("Simulated database failure.") : result;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            IsWordUpdate(command) ? throw new InvalidOperationException("Simulated database failure.") : ValueTask.FromResult(result);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            IsWordUpdate(command) ? throw new InvalidOperationException("Simulated database failure.") : ValueTask.FromResult(result);

        private static bool IsWordUpdate(DbCommand command) =>
            command.CommandText.Contains("UPDATE \"VocabularyWord\"", StringComparison.Ordinal);
    }
}
