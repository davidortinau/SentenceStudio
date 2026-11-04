using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Services;
using ReadingPage = SentenceStudio.WebUI.Pages.Reading;
using RecentLookupsPage = SentenceStudio.WebUI.Pages.RecentLookups;
using WritingPage = SentenceStudio.WebUI.Pages.Writing;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Lookup history on the web and MAUI page code (Captain 2026-11-04, decision 4 B): Writing's
/// translate helper and Reading's word lookup record each successful lookup with its translation
/// for the active learner; a failed lookup records nothing; a recording failure still shows the
/// translation. Recent lookups lists only the learner's own lookups and adds one to vocabulary.
/// </summary>
public sealed class LookupHistoryPageTests
{
    [Fact]
    public async Task Writing_SuccessfulTranslation_IsRecordedOnceWithItsTranslation()
    {
        await using var fixture = await CreateAsync();
        var page = await OpenWritingAsync(fixture);
        fixture.Ai.Respond = _ => "the sea is blue";
        await SetInputAsync(fixture, page, "바다가 파래요");

        await fixture.Renderer.InvokeAsync(page, "TranslateInput");
        await SetInputAsync(fixture, page, "  바다가   파래요 ");
        await fixture.Renderer.InvokeAsync(page, "TranslateInput");

        fixture.Toasts.Should().Contain(t => t.Text == "the sea is blue" && t.Type == ToastType.Info);
        var lookup = (await fixture.RowsAsync<VocabularyLookup>()).Should().ContainSingle(
            "a repeat of the same text updates its one row").Subject;
        lookup.UserProfileId.Should().Be(ActivityPageFixture.OwnerA);
        lookup.Text.Should().Be("바다가   파래요");
        lookup.Translation.Should().Be("the sea is blue");
        lookup.Activity.Should().Be(VocabularyLookupActivity.Writing);
        lookup.Direction.Should().Be(VocabularyLookupDirection.ToNative);
        lookup.Language.Should().Be("Korean");
        (await fixture.RowsAsync<VocabularyWord>()).Should().BeEmpty("a lookup is still not a word");
    }

    [Fact]
    public async Task Writing_OwnNativeTerm_IsRecordedAsALookupIntoTheTargetLanguage()
    {
        await using var fixture = await CreateAsync();
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        var page = await OpenWritingAsync(fixture);
        await SetInputAsync(fixture, page, "sea");

        await fixture.Renderer.InvokeAsync(page, "TranslateInput");

        var lookup = (await fixture.RowsAsync<VocabularyLookup>()).Should().ContainSingle().Subject;
        lookup.Direction.Should().Be(VocabularyLookupDirection.ToTarget);
        lookup.TargetTerm.Should().Be("바다");
        lookup.NativeTerm.Should().Be("sea");
        fixture.Ai.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Writing_FailedTranslation_RecordsNothing()
    {
        await using var fixture = await CreateAsync();
        var page = await OpenWritingAsync(fixture);
        fixture.Ai.Respond = _ => throw new HttpRequestException("AI unavailable");
        await SetInputAsync(fixture, page, "바다");
        await fixture.Renderer.InvokeAsync(page, "TranslateInput");

        fixture.Ai.Respond = _ => "  ";
        await fixture.Renderer.InvokeAsync(page, "TranslateInput");

        (await fixture.RowsAsync<VocabularyLookup>()).Should().BeEmpty("a failed lookup is not a lookup");
    }

    [Fact]
    public async Task Writing_RecordingFails_TheLearnerStillSeesTheTranslation()
    {
        await using var fixture = await CreateAsync();
        var page = await OpenWritingAsync(fixture);
        await BreakLookupTableAsync(fixture);
        fixture.Ai.Respond = _ => "the sea";
        await SetInputAsync(fixture, page, "바다");

        await fixture.Renderer.InvokeAsync(page, "TranslateInput");

        fixture.Toasts.Should().Contain(t => t.Text == "the sea" && t.Type == ToastType.Info);
        fixture.Toasts.Should().NotContain(t => t.Type == ToastType.Error);
        Members.Get<bool>(page, "isTranslating").Should().BeFalse();
        fixture.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task Writing_AnotherProfileBecameActive_RecordsNothing()
    {
        await using var fixture = await CreateAsync();
        var page = await OpenWritingAsync(fixture);
        fixture.Ai.Respond = _ => "the sea";
        fixture.Ai.Hold();
        await SetInputAsync(fixture, page, "바다");

        var translating = fixture.Renderer.InvokeAsync(page, "TranslateInput");
        await fixture.Ai.Started;
        fixture.Preferences.ActiveId = ActivityPageFixture.OwnerB;
        fixture.Ai.Release();
        await translating;

        (await fixture.RowsAsync<VocabularyLookup>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Reading_SuccessfulLookup_RecordsTheActivityRowAndTheLookupWithItsResource()
    {
        await using var fixture = await CreateAsync();
        var (resource, _) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("하늘", "sky"));
        var page = await OpenReadingAsync(fixture, resource);
        fixture.Ai.Respond = _ => "sea";

        await fixture.Renderer.InvokeAsync(page, "HandleWordClicked", "바다,");

        Members.Get<string>(page, "wordDefinition").Should().Be("sea");
        (await fixture.RowsAsync<UserActivity>()).Should().ContainSingle()
            .Which.Input.Should().Be("Dictionary lookup: 바다");
        var lookup = (await fixture.RowsAsync<VocabularyLookup>()).Should().ContainSingle().Subject;
        lookup.Text.Should().Be("바다");
        lookup.Translation.Should().Be("sea");
        lookup.Activity.Should().Be(VocabularyLookupActivity.Reading);
        lookup.ResourceId.Should().Be(resource.Id);
        lookup.UserProfileId.Should().Be(ActivityPageFixture.OwnerA);
    }

    [Fact]
    public async Task Reading_FailedLookup_RecordsNothing()
    {
        await using var fixture = await CreateAsync();
        var page = await OpenReadingAsync(fixture, resource: null);
        fixture.Ai.Respond = _ => throw new HttpRequestException("AI unavailable");

        await fixture.Renderer.InvokeAsync(page, "HandleWordClicked", "바다");

        Members.Get<bool>(page, "translationUnavailable").Should().BeTrue();
        (await fixture.RowsAsync<VocabularyLookup>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Reading_RecordingFails_TheDefinitionIsStillShown()
    {
        await using var fixture = await CreateAsync();
        var page = await OpenReadingAsync(fixture, resource: null);
        await BreakLookupTableAsync(fixture);
        fixture.Ai.Respond = _ => "sea";

        await fixture.Renderer.InvokeAsync(page, "HandleWordClicked", "바다");

        Members.Get<string>(page, "wordDefinition").Should().Be("sea");
        Members.Get<bool>(page, "translationUnavailable").Should().BeFalse();
        (await fixture.RowsAsync<UserActivity>()).Should().ContainSingle("the activity log row is independent");
        fixture.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task RecentLookups_ListsOnlyTheLearnersOwnLookupsNewestFirst()
    {
        await using var fixture = await CreateAsync();
        var (resource, _) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("하늘", "sky"));
        var lookups = fixture.Services.GetRequiredService<VocabularyLookupRepository>();
        await lookups.RecordAsync(ActivityPageFixture.OwnerA, new VocabularyLookupEntry(
            "바다", "sea", VocabularyLookupDirection.ToNative, VocabularyLookupActivity.Reading, resource.Id));
        await Task.Delay(5);
        await lookups.RecordAsync(ActivityPageFixture.OwnerA, new VocabularyLookupEntry(
            "산", "mountain", VocabularyLookupDirection.ToNative, VocabularyLookupActivity.Writing));
        fixture.Preferences.ActiveId = ActivityPageFixture.OwnerB;
        await lookups.RecordAsync(ActivityPageFixture.OwnerB, new VocabularyLookupEntry(
            "강", "B's river", VocabularyLookupDirection.ToNative, VocabularyLookupActivity.Writing));
        fixture.Preferences.ActiveId = ActivityPageFixture.OwnerA;

        var (id, page) = await fixture.Renderer.RenderAsync<RecentLookupsPage>();

        var shown = Members.Get<VocabularyLookupPage>(page, "lookups");
        shown.Items.Select(item => item.Lookup.Text).Should().Equal("산", "바다");
        var text = fixture.Renderer.RenderedText(id);
        text.Should().Contain("sea").And.Contain("mountain").And.Contain("Fixture resource");
        text.Should().NotContain("B's river");
    }

    [Fact]
    public async Task RecentLookups_AddToVocabulary_CreatesTheWordInTheChosenResource()
    {
        await using var fixture = await CreateAsync();
        var (resource, _) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("하늘", "sky"));
        await fixture.Services.GetRequiredService<VocabularyLookupRepository>().RecordAsync(
            ActivityPageFixture.OwnerA, new VocabularyLookupEntry(
                "바다", "sea", VocabularyLookupDirection.ToNative, VocabularyLookupActivity.Reading, resource.Id));
        var (id, page) = await fixture.Renderer.RenderAsync<RecentLookupsPage>();
        var lookupKey = Key(Members.Get<VocabularyLookupPage>(page, "lookups").Items.Single().Lookup.Id);

        await fixture.Renderer.ClickElementAsync(id, "id", $"lookup-{lookupKey}-add-{Key(resource.Id)}");

        fixture.Toasts.Should().Contain(t => t.Type == ToastType.Success && t.Text.Contains("Fixture resource"));
        var words = await fixture.RowsAsync<VocabularyWord>();
        var word = words.Should().ContainSingle(w => w.TargetLanguageTerm == "바다").Subject;
        word.NativeLanguageTerm.Should().Be("sea");
        word.UserProfileId.Should().Be(ActivityPageFixture.OwnerA);
        (await fixture.RowsAsync<ResourceVocabularyMapping>()).Should().Contain(
            mapping => mapping.VocabularyWordId == word.Id && mapping.ResourceId == resource.Id);

        await fixture.Renderer.ClickElementAsync(id, "id", $"lookup-{lookupKey}-add-{Key(resource.Id)}");
        fixture.Toasts.Should().Contain(t => t.Type == ToastType.Info && t.Text.Contains("Already in"));
        (await fixture.RowsAsync<VocabularyWord>()).Should().HaveCount(words.Count);
    }

    [Fact]
    public async Task RecentLookups_NoActiveProfile_ShowsNothing()
    {
        await using var fixture = await CreateAsync();
        fixture.Preferences.ActiveId = string.Empty;

        var (_, page) = await fixture.Renderer.RenderAsync<RecentLookupsPage>();

        Members.Get<string?>(page, "statusKey").Should().Be("Vocabulary_NoActiveProfile");
        Members.Get<VocabularyLookupPage>(page, "lookups").Items.Should().BeEmpty();
    }

    private static Task<ActivityPageFixture> CreateAsync() =>
        ActivityPageFixture.CreateAsync(services =>
        {
            ActivityPageFixture.WithLearnerScope(services);
            services.AddSingleton<IPlanDateContext>(new UtcDateContext());
        });

    private static async Task<WritingPage> OpenWritingAsync(ActivityPageFixture fixture)
    {
        var (_, page) = await fixture.Renderer.RenderAsync<WritingPage>();
        Members.Get<string>(page, "_owner").Should().Be(ActivityPageFixture.OwnerA);
        fixture.Toasts.Clear();
        return page;
    }

    private static async Task<ReadingPage> OpenReadingAsync(ActivityPageFixture fixture, LearningResource? resource)
    {
        var (_, page) = await fixture.Renderer.RenderAsync<ReadingPage>();
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            Members.Set(page, "_owner", ActivityPageFixture.OwnerA);
            Members.Set(page, "resource", resource);
        });
        return page;
    }

    private static Task SetInputAsync(ActivityPageFixture fixture, WritingPage page, string text) =>
        fixture.Renderer.Dispatcher.InvokeAsync(() => Members.Set(page, "userInput", text));

    /// <summary>Makes every lookup write fail (the database refuses it), as a broken store would.</summary>
    private static async Task BreakLookupTableAsync(ActivityPageFixture fixture)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database
            .ExecuteSqlRawAsync("DROP TABLE \"VocabularyLookup\"");
    }

    private static string Key(string id) =>
        new(id.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());

    private sealed class UtcDateContext : IPlanDateContext
    {
        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;
        public DateTime UtcNow => DateTime.UtcNow;
        public DateOnly UserLocalDate => DateOnly.FromDateTime(UtcNow);
        public DateOnly ToUserLocal(DateTime utc) => DateOnly.FromDateTime(utc);
        public DateTime ToUtcMidnight(DateOnly userLocal) =>
            userLocal.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    }
}
