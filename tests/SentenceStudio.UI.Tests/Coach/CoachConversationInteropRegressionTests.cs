using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Regression tests for the Coach conversation-shelf circuit crash.
/// </summary>
/// <remarks>
/// <para>
/// The real defect: <see cref="CoachConversationList"/> invoked the app.js exports
/// <c>focusElement</c> and <c>downloadFileFromStream</c> through the default
/// <see cref="IJSRuntime"/> instead of through the imported module. Because those functions are
/// ES-module exports and not globals, the browser threw
/// <c>JSException: The value '&lt;name&gt;' is not a function</c>. That exception is neither a
/// <see cref="JSDisconnectedException"/> nor an <see cref="ObjectDisposedException"/>, so the
/// component's catch blocks let it escape and the whole Blazor circuit terminated — after the
/// server-side write (rename) or export stream had already succeeded, exactly matching the
/// reported reproduction.
/// </para>
/// <para>
/// Each test renders the real component with <see cref="InteractiveTestRenderer"/> and dispatches
/// the real button click, so the changed handler executes end to end. With the fix reverted (the
/// component calling the export on the default runtime) the same click throws the JSException into
/// the circuit and these tests go red; with the module routing in place they are green.
/// </para>
/// </remarks>
public class CoachConversationInteropRegressionTests
{
    private static (InteractiveTestRenderer Renderer, ModuleAwareJSRuntime Js, IServiceProvider Provider) Build(
        out FakeCoachApiClient client)
    {
        client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1", title: "Test conversation",
            titleOrigin: CoachConversationTitleOrigin.Learner);

        var js = new ModuleAwareJSRuntime();
        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<IJSRuntime>(_ => js);
        services.AddScoped(_ => directory);
        services.AddScoped(_ => state);

        IServiceProvider provider = services.BuildServiceProvider();
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return (renderer, js, provider);
    }

    private static async Task<CoachConversationDirectory> PreloadedDirectoryAsync(IServiceProvider provider)
    {
        var directory = provider.GetRequiredService<CoachConversationDirectory>();
        await directory.EnsureLoadedAsync();
        directory.Conversations.Should().ContainSingle("the shelf renders exactly one seeded conversation");
        return directory;
    }

    // ---------------------------------------------------------------- harness fidelity

    [Fact]
    public async Task TheJsDoubleThrowsForAGlobalModuleExportAndSucceedsThroughTheModule()
    {
        // Proves the double can tell the bug from the fix: a bare global call to an app.js export
        // throws exactly the production JSException, while the imported-module call returns.
        var js = new ModuleAwareJSRuntime();

        var act = async () => await js.InvokeVoidAsync("focusElement", "some-id");
        (await act.Should().ThrowAsync<JSException>())
            .WithMessage("The value 'focusElement' is not a function.");

        var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/SentenceStudio.UI/js/app.js");
        await module.InvokeVoidAsync("focusElement", "some-id");

        js.ModuleInvocations.Should().Contain("focusElement");
    }

    // ---------------------------------------------------------------- export (downloadFileFromStream)

    [Theory]
    [InlineData("Download as JSON")]
    [InlineData("Download as Markdown")]
    public async Task ExportingAConversationStreamsThroughTheModuleWithoutKillingTheCircuit(
        string buttonText)
    {
        var (renderer, js, provider) = Build(out _);
        await using var _provider = (IAsyncDisposable)provider;
        await PreloadedDirectoryAsync(provider);

        var id = await renderer.RenderAsync<CoachConversationList>();
        await renderer.ClickButtonAsync(id, buttonText);

        renderer.Unhandled.Should().NotContain(e => e is JSException,
            "the export must reach the browser download path without throwing into the circuit");
        js.ModuleInvocations.Should().Contain("downloadFileFromStream",
            "the download is invoked on the imported app.js module");
        js.GlobalInvocations.Should().NotContain("downloadFileFromStream",
            "invoking the export on the default runtime is the bug that crashed the circuit");
    }

    // ---------------------------------------------------------------- rename focus (focusElement)

    [Fact]
    public async Task RenamingAConversationRestoresFocusThroughTheModuleWithoutKillingTheCircuit()
    {
        var (renderer, js, provider) = Build(out _);
        await using var _provider = (IAsyncDisposable)provider;
        await PreloadedDirectoryAsync(provider);

        var id = await renderer.RenderAsync<CoachConversationList>();

        // Open the rename dialog, then confirm — ConfirmRename restores focus via focusElement.
        await renderer.ClickButtonAsync(id, "Rename");
        await renderer.ClickButtonAsync(id, "Save name");

        renderer.Unhandled.Should().NotContain(e => e is JSException,
            "restoring focus after a rename must not throw into the circuit");
        js.ModuleInvocations.Should().Contain("focusElement",
            "focus is restored through the imported app.js module");
        js.GlobalInvocations.Should().NotContain("focusElement",
            "invoking focusElement on the default runtime is the bug that crashed the circuit");
    }

    // ---------------------------------------------------------------- load earlier (restoreScrollAnchor)

    [Fact]
    public async Task LoadingEarlierMessagesRestoresScrollThroughTheModuleWithoutKillingTheCircuit()
    {
        // CoachChatPane is the component the original two-file patch missed entirely: its
        // "Load earlier messages" handler restores scroll position through restoreScrollAnchor,
        // another app.js export that was being invoked on the default runtime.
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        for (var i = 1; i <= 60; i++)
        {
            client.Seed("c-1", CoachMessageRole.Learner, $"message {i}");
        }

        var js = new ModuleAwareJSRuntime();
        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");
        state.HasEarlierMessages.Should().BeTrue("60 messages exceed one page, so there is earlier history");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<IJSRuntime>(_ => js);
        services.AddScoped(_ => directory);
        services.AddScoped(_ => state);

        IServiceProvider provider = services.BuildServiceProvider();
        await using var _provider = (IAsyncDisposable)provider;
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var id = await renderer.RenderAsync<CoachChatPane>();
        await renderer.ClickButtonAsync(id, "Load earlier messages");

        renderer.Unhandled.Should().NotContain(e => e is JSException,
            "restoring scroll position after a prepend must not throw into the circuit");
        js.ModuleInvocations.Should().Contain("restoreScrollAnchor",
            "the scroll position is restored through the imported app.js module");
        js.GlobalInvocations.Should().NotContain("restoreScrollAnchor",
            "invoking restoreScrollAnchor on the default runtime is the bug that crashed the circuit");
    }
}
