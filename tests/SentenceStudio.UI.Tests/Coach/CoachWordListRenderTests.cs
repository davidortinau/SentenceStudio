using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The words Sam lists under its reply (Captain, 2026-11-17): word, meaning and mastery on every
/// row, in English and Korean, and a row opens only when the server gave it the word's page.
/// </summary>
public class CoachWordListRenderTests
{
    private const string WordId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    [Fact]
    public async Task Each_row_shows_the_word_its_meaning_and_mastery()
    {
        var html = await RenderAsync(List(CoachWordListOrder.WeakestFirst,
            Row(WordId, "포도", "grape", 8, opens: true),
            Row("b1", "학교", "school", null, opens: true)));

        Assert.Contains("Words in Today's Plan, weakest first", html);
        Assert.Contains("포도", html);
        Assert.Contains("grape", html);
        Assert.Contains("8% mastery", html);
        Assert.Contains("Not practised yet", html);
    }

    [Fact]
    public async Task The_list_reads_in_korean()
    {
        var html = await RenderAsync(List(CoachWordListOrder.PlanOrder, Row(WordId, "포도", "grape", 40, opens: true)), "ko");

        Assert.Contains("오늘의 계획에 있는 단어", html);
        Assert.Contains("숙달도 40%", html);
    }

    [Fact]
    public async Task A_row_without_the_word_page_is_shown_but_does_not_open()
    {
        var html = await RenderAsync(List(CoachWordListOrder.PlanOrder, Row(WordId, "포도", "grape", 40, opens: false)));

        Assert.Contains("disabled", html);
        Assert.DoesNotContain("bi-chevron-right", html);
    }

    [Fact]
    public async Task An_empty_list_renders_nothing()
    {
        var html = await RenderAsync(List(CoachWordListOrder.PlanOrder));

        Assert.Equal(string.Empty, html.Trim());
    }

    private static CoachWordListDto List(CoachWordListOrder order, params CoachWordListRowDto[] rows) => new()
    {
        Kind = CoachWordListKind.PlanVocabulary,
        Order = order,
        Rows = rows
    };

    private static CoachWordListRowDto Row(string id, string target, string native, int? mastery, bool opens) =>
        new(id, target, native, mastery, opens
            ? CoachRouteCatalog.Build(CoachRouteName.Vocabulary,
                [new CoachRouteParameterDto(CoachRouteParameterKey.VocabularyWordId, id)])
            : null);

    private static async Task<string> RenderAsync(CoachWordListDto list, string culture = "en")
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<BlazorLocalizationService>();
            services.AddScoped<NavigationManager, TestNavigation>();

            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<CoachWordList>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(CoachWordList.List)] = list
                    }));
                return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
            });
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
