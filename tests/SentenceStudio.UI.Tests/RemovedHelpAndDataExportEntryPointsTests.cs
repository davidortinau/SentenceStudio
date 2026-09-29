using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests;

public sealed class RemovedHelpAndDataExportEntryPointsTests
{
    [Fact]
    public void Navigation_keeps_app_sections_without_HelpKit()
    {
        var source = ReadSource("Layout/NavMenu.razor");

        source.Should().NotContain("Plugin.Maui.HelpKit");
        source.Should().NotContain("ShowHelpAsync");
        source.Should().Contain("NavMemory.GetSectionRoute(sectionKey)");
        source.Should().Contain("new NavItem(\"profile\"");
        source.Should().Contain("new NavItem(\"settings\"");
        source.Should().Contain("new NavItem(\"feedback\"");
    }

    [Theory]
    [InlineData("Profile.razor", "Profile_SaveProfile")]
    [InlineData("Settings.razor", "Settings_HarvestSentences")]
    public void Account_pages_keep_other_actions_without_bulk_export(string page, string remainingAction)
    {
        var source = ReadSource($"Pages/{page}");

        source.Should().Contain(remainingAction);
        source.Should().NotContain("ExportData");
        source.Should().NotContain("DataExportService");
        source.Should().NotContain("ExportAllDataAsZipAsync");
        source.Should().NotContain("ExportSuccess");
    }

    [Theory]
    [InlineData("https://sentencestudio.example/")]
    [InlineData("app://0.0.0.0/")]
    public async Task Stale_export_only_offer_is_not_presented_as_available(string baseUri)
    {
        var html = await RenderLimitationAsync(new CoachLimitationDto
        {
            Code = CoachLimitationCode.ExceedsSafeChangeScope,
            ExportSurface = CoachRouteCatalog.Build(CoachRouteName.Settings),
            Alternatives = [CoachAlternativeCode.ExportBeforeRemoving]
        }, baseUri);

        html.Should().Contain("data-coach-limitation=\"ExceedsSafeChangeScope\"");
        html.Should().NotContain("data-coach-limitation-export");
        html.Should().NotContain("coach-limitation-export");
        html.Should().NotContain("data-coach-limitation-destination=\"Settings\"");
        html.Should().NotContain("data-coach-limitation-alternative");
        html.Should().NotContain("coach-limitation-alternatives");

        var withoutExport = await RenderLimitationAsync(new CoachLimitationDto
        {
            Code = CoachLimitationCode.ExceedsSafeChangeScope
        }, baseUri);
        html.Should().Be(withoutExport,
            "stale export metadata must not change any learner-visible markup");
    }

    [Theory]
    [InlineData("https://sentencestudio.example/")]
    [InlineData("app://0.0.0.0/")]
    public async Task Removing_export_offer_preserves_valid_destinations_and_alternatives(string baseUri)
    {
        var html = await RenderLimitationAsync(new CoachLimitationDto
        {
            Code = CoachLimitationCode.ExceedsSafeChangeScope,
            Destination = CoachRouteCatalog.Build(CoachRouteName.Vocabulary),
            ExportSurface = CoachRouteCatalog.Build(CoachRouteName.Settings),
            Alternatives =
            [
                CoachAlternativeCode.ExportBeforeRemoving,
                CoachAlternativeCode.RemoveOneListAtATime,
                CoachAlternativeCode.StartAFreshList
            ]
        }, baseUri);

        html.Should().Contain("data-coach-limitation-destination=\"Vocabulary\"");
        html.Should().Contain("data-coach-limitation-alternative=\"RemoveOneListAtATime\"");
        html.Should().Contain("data-coach-limitation-alternative=\"StartAFreshList\"");
        html.Should().NotContain("data-coach-limitation-export");
        html.Should().NotContain("coach-limitation-export");
        html.Should().NotContain("data-coach-limitation-alternative=\"ExportBeforeRemoving\"");
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the repository root should be locatable from the test output directory");
        return File.ReadAllText(Path.Combine(
            directory!.FullName, "src", "SentenceStudio.UI",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static async Task<string> RenderLimitationAsync(CoachLimitationDto limitation, string baseUri)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        services.AddSingleton<NavigationManager>(new StubNavigationManager(baseUri));

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachLimitationCard>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CoachLimitationCard.Limitation)] = limitation
                }));
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private sealed class StubNavigationManager : NavigationManager
    {
        public StubNavigationManager(string baseUri) => Initialize(baseUri, baseUri);

        protected override void NavigateToCore(string uri, bool forceLoad) =>
            throw new InvalidOperationException("A limitation card cannot navigate on its own.");
    }
}
