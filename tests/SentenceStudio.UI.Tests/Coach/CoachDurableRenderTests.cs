using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Renders the durable history surfaces to real HTML: the conversation shelf, the history
/// controls in the transcript, and the things that must never reach the page — internal
/// identifiers, and a title that tries to be markup.
/// </summary>
public class CoachDurableRenderTests
{
    private static async Task<string> RenderAsync<TComponent>(
        CoachWorkspaceState state,
        CoachConversationDirectory directory,
        string culture = "en")
        where TComponent : IComponent
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<BlazorLocalizationService>();
            // The coach's name comes from the learner's study language, so every component that
            // names it needs the resolver. The all-optional constructor makes this a one-liner:
            // with no language source it answers with the default persona.
            services.AddScoped<CoachPersona>();
            services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
            services.AddScoped(_ => state);
            services.AddScoped(_ => directory);

            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.Empty);
                return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
            });
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    private static (CoachWorkspaceState State, CoachConversationDirectory Directory, FakeCoachApiClient Client)
        Create()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        var directory = new CoachConversationDirectory(client);
        return (new CoachWorkspaceState(client, directory), directory, client);
    }

    // ---------------------------------------------------------------- the shelf

    [Fact]
    public async Task ConversationList_OffersANewConversationAndNamesEveryRow()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1", updatedAtUtc: new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc));
        client.AddConversation("c-2", title: "Ordering coffee",
            titleOrigin: CoachConversationTitleOrigin.Learner,
            updatedAtUtc: new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        await directory.RefreshAsync();
        directory.Select("c-1");

        var html = await RenderAsync<CoachConversationList>(state, directory);

        html.Should().Contain("coach-new-conversation");
        html.Should().Contain("Ordering coffee");
        html.Should().Contain("aria-current", "the open conversation has to be identifiable without colour");
        html.Should().Contain("role=\"list\"");
    }

    [Fact]
    public async Task ConversationList_GivesAnUntitledConversationAPlainDateRatherThanItsContents()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1", updatedAtUtc: new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc));
        await directory.RefreshAsync();

        var html = await RenderAsync<CoachConversationList>(state, directory);

        // A generated title must not quote the learner. What they practiced is their business,
        // and a shelf is read over a shoulder more often than a transcript is.
        html.Should().Contain("2026");
        html.Should().NotContain("c-1", "an internal id is not a name");
    }

    [Fact]
    public async Task ConversationList_MarksAClosedConversationInText()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1", isClosed: true);
        await directory.RefreshAsync();

        var html = await RenderAsync<CoachConversationList>(state, directory);

        html.Should().Contain("Closed");
    }

    [Fact]
    public async Task ConversationList_SaysWhatDeletingDoesNotTakeAway()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1");
        await directory.RefreshAsync();

        var html = await RenderAsync<CoachConversationList>(state, directory);

        // The impact sentence lives in the resources whether or not the dialog is open; this
        // pins the copy itself, which is the part that must stay true.
        var impact = new BlazorLocalizationService()["Coach_ConversationDeleteImpact"];
        impact.Should().NotBeNullOrWhiteSpace();

        // The promise this copy makes: a transcript is not the learner's progress, and it is not
        // their account. Deleting one must not read like deleting the others.
        impact.Should().Contain("Today's Plan");
        impact.Should().Contain("progress");
        impact.Should().Contain("account");
        html.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ConversationList_ShowsAnEmptyStateRatherThanAnEmptyBox()
    {
        var (state, directory, _) = Create();
        await directory.RefreshAsync();

        var html = await RenderAsync<CoachConversationList>(state, directory);

        html.Should().Contain("coach-conversations-empty");
    }

    [Fact]
    public async Task ConversationList_ShowsAnOfflineStateSeparatelyFromAFailure()
    {
        var (state, directory, client) = Create();
        client.OnListConversations = (_, _) => throw new HttpRequestException("no network");
        await directory.RefreshAsync();

        var html = await RenderAsync<CoachConversationList>(state, directory);

        html.Should().Contain(new BlazorLocalizationService()["Coach_ConversationsOffline"]);
    }

    // ---------------------------------------------------------------- escaping and privacy

    [Fact]
    public async Task ConversationList_RendersATitleThatLooksLikeMarkupAsText()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1",
            title: "<script>alert('x')</script>",
            titleOrigin: CoachConversationTitleOrigin.Learner);
        await directory.RefreshAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped(_ => state);
        services.AddScoped(_ => directory);

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        // Read the raw HTML, undecoded: the escaping is exactly what is under test here.
        var raw = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachConversationList>(ParameterView.Empty);
            return output.ToHtmlString();
        });

        raw.Should().NotContain("<script>");
        raw.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public async Task ConversationList_KeepsStateVersionsAndOtherBookkeepingOutOfThePage()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1", title: "Ordering coffee",
            titleOrigin: CoachConversationTitleOrigin.Learner);
        await directory.RefreshAsync();

        var html = await RenderAsync<CoachConversationList>(state, directory);

        html.Should().NotContain("StateVersion");
        html.Should().NotContain("stateVersion");
        html.Should().NotContain("HasActiveCheckpoint");
        html.Should().NotContain("digest");
        html.Should().NotContain("lease");
    }

    // ---------------------------------------------------------------- transcript

    [Fact]
    public async Task ChatPane_OffersToLoadEarlierMessagesWhenThereAreMore()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1");

        for (var i = 1; i <= 60; i++)
        {
            client.Seed("c-1", CoachMessageRole.Learner, $"message {i}");
        }

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        var html = await RenderAsync<CoachChatPane>(state, directory);

        html.Should().Contain(new BlazorLocalizationService()["Coach_LoadEarlier"]);
        html.Should().Contain("message 60");
    }

    [Fact]
    public async Task ChatPane_SaysWhereTheHistoryStartsRatherThanImplyingThereIsNoMore()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1", historyStartsAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        client.Seed("c-1", CoachMessageRole.Coach, "The oldest thing kept.");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        var html = await RenderAsync<CoachChatPane>(state, directory);

        state.IsAtHistoryBoundary.Should().BeTrue();
        html.Should().Contain(new BlazorLocalizationService()["Coach_HistoryBoundary"]);
    }

    [Fact]
    public async Task ChatPane_AccountsForAMessageItCannotRenderInsteadOfDroppingIt()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Coach, "readable");
        client.Seed("c-1", CoachMessageRole.Coach, string.Empty, isReadable: false);
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        var html = await RenderAsync<CoachChatPane>(state, directory);

        // Silently dropping a message would make the transcript a lie about what was said.
        html.Should().Contain(new BlazorLocalizationService()["Coach_UnreadableMessage"]);
        html.Should().Contain(new BlazorLocalizationService()["Coach_UnreadableCountOne"]);
        html.Should().NotContain("1 messages", "the count line has to read as English at every count");
    }

    [Fact]
    public async Task ChatPane_RendersAStoredStructuredAnswerTheSameWayALiveOneRenders()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1");
        var answer = CoachAnswerStateTests.KoreanContrastAnswer();
        client.Seed("c-1", CoachMessageRole.Coach, string.Empty, answer: answer);

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        var html = await RenderAsync<CoachChatPane>(state, directory);

        var firstSpan = answer.Blocks[0].Spans[0].Text;
        html.Should().Contain(firstSpan);
    }

    [Fact]
    public async Task ChatPane_ShowsThatAConversationIsClosedInsteadOfLettingTheLearnerType()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1", isClosed: true);
        client.Seed("c-1", CoachMessageRole.Coach, "Archived.");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        var html = await RenderAsync<CoachChatPane>(state, directory);

        html.Should().Contain(new BlazorLocalizationService()["Coach_ConversationClosedNotice"]);
    }

    [Fact]
    public async Task ChatPane_KeepsMessageIdentifiersOutOfWhatTheLearnerCanCopy()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1");
        var seeded = client.Seed("c-1", CoachMessageRole.Coach, "Just the words.");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        var copied = string.Join("\n", state.Timeline.Select(e => e.ReadableText()));

        copied.Should().Be("Just the words.");
        copied.Should().NotContain(seeded.Message.MessageId);
        copied.Should().NotContain(seeded.Sequence.ToString());
    }

    // ---------------------------------------------------------------- localization

    [Fact]
    public async Task ConversationList_UsesTheKoreanNameForSamWithoutLeavingEnglishBehind()
    {
        var (state, directory, client) = Create();
        client.AddConversation("c-1");
        await directory.RefreshAsync();

        var korean = await RenderAsync<CoachConversationList>(state, directory, culture: "ko");

        korean.Should().NotContain("Sam", "the Korean UI calls the coach 쌤");
        korean.Should().NotContain("New conversation");
    }

    [Fact]
    public async Task DurableStrings_ExistInBothCulturesAndCarryNoEmoji()
    {
        var keys = new[]
        {
            "Coach_Conversations",
            "Coach_NewConversation",
            "Coach_LoadEarlier",
            "Coach_HistoryBoundary",
            "Coach_UnreadableMessage",
            "Coach_UnreadableCountOne",
            "Coach_ConversationDeleteImpact",
            "Coach_ConversationClosedNotice",
            "Coach_ConversationGone",
            "Coach_ConversationsOffline"
        };

        foreach (var culture in new[] { "en", "ko" })
        {
            var previous = System.Globalization.CultureInfo.CurrentUICulture;
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);

            try
            {
                var localize = new BlazorLocalizationService();

                foreach (var key in keys)
                {
                    var value = localize[key];
                    value.Should().NotBeNullOrWhiteSpace($"{key} must be translated for {culture}");
                    value.Should().NotBe(key, $"{key} must resolve to real copy for {culture}");
                    value.Should().NotContainAny("✅", "⚠️", "🎉", "❌", "🔥");
                }
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = previous;
            }
        }
    }
}
