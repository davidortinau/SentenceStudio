using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// What a message this build cannot classify actually renders as, in HTML.
/// </summary>
/// <remarks>
/// <para>
/// The wire-tolerance tests next door prove a degraded <see cref="CoachMessageKind"/> lands in the
/// <see cref="CoachTimelineKind.UnsupportedMessage"/> slot. They stop at the model. That leaves the
/// part the learner sees untested, and the part the learner sees is where the risk is: the reason
/// the slot exists is that a newer server's message is most likely missing <i>its controls</i> in
/// this build, so prose stripped of its Accept and Reject reads like a statement rather than a
/// question. Rendering the text with no way to answer it is worse than rendering nothing.
/// </para>
/// <para>
/// So this file asserts four withholdings and one obligation. Withheld: the original text, Copy,
/// the report flag, and any action affordance. Owed: a truthful role label — the placeholder
/// declines to show the content, which is not a licence to misattribute it. The learner's own
/// unsupported message used to be captioned with Sam's name.
/// </para>
/// <para>
/// Every negative assertion is paired with a positive control in the same thread. "No Copy button
/// on screen" passes trivially if the pane rendered nothing at all, so each test also proves a
/// supported message beside it kept everything the unsupported one lost.
/// </para>
/// </remarks>
public class CoachUnsupportedMessageRenderTests
{
    /// <summary>The text the server sent, which must never reach the page.</summary>
    private const string UnsupportedText =
        "I'll delete the five words you missed most often. Accept or reject?";

    /// <summary>A supported message in the same thread, as the positive control.</summary>
    private const string SupportedText = "Twenty minutes today.";

    private const string LearnerText = "How long should I practice?";

    // ---------------------------------------------------------------- fixtures

    // Fully qualified: the app has its own static SentenceStudio.ServiceProvider helper in scope.
    private static Microsoft.Extensions.DependencyInjection.ServiceProvider Services(
        CoachWorkspaceState state)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<CoachPersona>();
        services.AddScoped<IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped(_ => state);
        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderAsync(CoachWorkspaceState state)
    {
        await using var provider = Services(state);
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachChatPane>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    /// <summary>
    /// A thread containing one unsupported message from <paramref name="unsupportedFrom"/>, a
    /// learner question, and one ordinary answer from Sam to serve as the positive control.
    /// </summary>
    private static async Task<CoachWorkspaceState> ThreadAsync(CoachMessageRole unsupportedFrom)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, LearnerText);
        client.Seed("c-1", unsupportedFrom, UnsupportedText, kind: CoachMessageKind.Unrecognized);
        client.Seed("c-1", CoachMessageRole.Coach, SupportedText);

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");
        return state;
    }

    // ---------------------------------------------------------------- the slot renders at all

    [Theory]
    [InlineData(CoachMessageRole.Coach)]
    [InlineData(CoachMessageRole.Learner)]
    public async Task The_placeholder_takes_the_messages_place_in_the_thread(CoachMessageRole role)
    {
        var html = await RenderAsync(await ThreadAsync(role));

        html.Should().Contain("coach-message-unsupported",
            "the slot stays so the thread keeps its shape rather than silently losing a turn");
        html.Should().Contain("This message needs a newer version of the app.");
    }

    // ---------------------------------------------------------------- what is withheld

    [Theory]
    [InlineData(CoachMessageRole.Coach)]
    [InlineData(CoachMessageRole.Learner)]
    public async Task The_original_text_never_reaches_the_page(CoachMessageRole role)
    {
        var html = await RenderAsync(await ThreadAsync(role));

        html.Should().NotContain(UnsupportedText,
            "the content arrived intact, and printing it is exactly the failure the slot prevents: "
            + "a proposal shown without its Accept and Reject reads as a statement");

        // The positive control: the pane did render, and a supported message kept its text.
        html.Should().Contain(SupportedText);
        html.Should().Contain(LearnerText);
    }

    [Theory]
    [InlineData(CoachMessageRole.Coach)]
    [InlineData(CoachMessageRole.Learner)]
    public async Task No_copy_control_is_offered_for_it(CoachMessageRole role)
    {
        var html = await RenderAsync(await ThreadAsync(role));

        // Copy would put the withheld text back on the learner's clipboard, which defeats the
        // withholding entirely. Counted by the button's accessible name rather than its class,
        // because "coach-copy" is also a prefix of the label span inside the same button.
        CountOf(html, @"aria-label=""Copy message""").Should().Be(
            1,
            "only the one supported response Sam gave should carry Copy");

        SliceOfPlaceholder(html).Should().NotContain("Copy message");
    }

    [Theory]
    [InlineData(CoachMessageRole.Coach)]
    [InlineData(CoachMessageRole.Learner)]
    public async Task No_report_flag_is_offered_for_it(CoachMessageRole role)
    {
        var html = await RenderAsync(await ThreadAsync(role));

        // Reporting names a message the server can pair to a learner turn. There is nothing
        // coherent to say about a message this build could not present.
        CountOf(html, "coach-report-flag").Should().Be(
            1,
            "only the one supported response Sam gave should carry the flag");
        // Matched on the class attribute specifically: the footer also carries an id built from
        // the same token, so a bare substring counts one element twice.
        CountOf(html, @"class=""coach-message-footer""").Should().Be(
            1,
            "the footer row is the container both controls live in");

        SliceOfPlaceholder(html).Should().NotContain("coach-report-flag");
    }

    [Theory]
    [InlineData(CoachMessageRole.Coach)]
    [InlineData(CoachMessageRole.Learner)]
    public async Task No_actions_are_offered_for_it(CoachMessageRole role)
    {
        var html = await RenderAsync(await ThreadAsync(role));

        // The whole reason the kind was unrecognised is most likely that its controls are the part
        // this build has no case for. Rendering any of them would be guessing at which.
        html.Should().NotContain("coach-suggestion-accept");
        html.Should().NotContain("coach-suggestion-reject");
        html.Should().NotContain("coach-write-card");
        html.Should().NotContain("coach-evidence-toggle");
    }

    [Fact]
    public async Task The_placeholder_carries_no_interactive_control_of_its_own()
    {
        // Stated structurally rather than by naming each class: nothing inside the placeholder is
        // a button, a link or an input, so a new control cannot be added to it by accident.
        var html = await RenderAsync(await ThreadAsync(CoachMessageRole.Coach));

        var placeholder = SliceOfPlaceholder(html);

        placeholder.Should().NotContain("<button");
        placeholder.Should().NotContain("<a ");
        placeholder.Should().NotContain("<input");
        placeholder.Should().NotContain("role=\"button\"");
    }

    // ---------------------------------------------------------------- what is owed

    [Fact]
    public async Task A_message_from_sam_is_captioned_with_sams_name()
    {
        var html = await RenderAsync(await ThreadAsync(CoachMessageRole.Coach));

        SliceOfPlaceholder(html).Should().Contain("Sam");
    }

    [Fact]
    public async Task A_message_from_the_learner_is_captioned_as_theirs_not_as_sams()
    {
        // The defect this test was written for. The placeholder hard-coded the persona name, so a
        // learner's own unsupported message appeared over Sam's name — declining to show the
        // content is not a licence to misattribute it.
        var html = await RenderAsync(await ThreadAsync(CoachMessageRole.Learner));

        var placeholder = SliceOfPlaceholder(html);

        placeholder.Should().Contain("You");
        placeholder.Should().NotContain("Sam", "the learner wrote this one");
    }

    [Theory]
    [InlineData(CoachMessageRole.Coach)]
    [InlineData(CoachMessageRole.Learner)]
    public async Task The_placeholder_is_still_timestamped(CoachMessageRole role)
    {
        // A row in a transcript with no time is harder to place than one with no content.
        SliceOfPlaceholder(await RenderAsync(await ThreadAsync(role)))
            .Should().Contain("<time");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The markup of the unsupported placeholder alone, so an assertion about it cannot be
    /// satisfied — or defeated — by the supported messages around it.
    /// </summary>
    private static string SliceOfPlaceholder(string html)
    {
        // Split on the opening tag of a top-level message block. The trailing space is what makes
        // this precise: every message block opens `class="coach-message ...`, while the inner role
        // header opens `class="coach-message-role"` and does not match.
        const string BlockStart = "<div class=\"coach-message ";

        var block = html
            .Split(BlockStart, StringSplitOptions.None)
            .FirstOrDefault(part => part.StartsWith("coach-message-unsupported", StringComparison.Ordinal));

        block.Should().NotBeNull("the placeholder must be on the page as its own message block");
        return block!;
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
