using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Regression tests for <see cref="CoachCopyButton"/>: verifies the JS module import path
/// resolves to the RCL content path (not a relative path that 404s in the host) and that the
/// clipboard interop receives the correct text argument.
/// </summary>
public class CoachCopyButtonInteropTests
{
    private const string ExpectedModulePath = "./_content/SentenceStudio.UI/js/app.js";

    private static (InteractiveTestRenderer Renderer, ModuleAwareJSRuntime Js) Build()
    {
        var js = new ModuleAwareJSRuntime();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<IJSRuntime>(_ => js);

        var provider = services.BuildServiceProvider();
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return (renderer, js);
    }

    [Fact]
    public async Task CopyButton_ImportsModuleFromRclContentPath()
    {
        var (renderer, js) = Build();

        var parameters = ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Text"] = "hello world" });

        var id = await renderer.RenderAsync<CoachCopyButton>(parameters);
        await renderer.ClickButtonAsync(id, "");

        // The import must use the RCL _content path — "./js/app.js" would 404 in the WebApp host.
        js.ImportedPaths.Should().ContainSingle()
            .Which.Should().Be(ExpectedModulePath);
    }

    [Fact]
    public async Task CopyButton_PassesTextParameterToClipboardInterop()
    {
        var (renderer, js) = Build();
        const string expected = "Korean sentence to copy";

        var parameters = ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Text"] = expected });

        var id = await renderer.RenderAsync<CoachCopyButton>(parameters);
        await renderer.ClickButtonAsync(id, "");

        js.ModuleInvocations.Should().Contain("copyTextToClipboard");
        js.LastModuleArgs.Should().NotBeNull();
        js.LastModuleArgs![0].Should().Be(expected);
    }

    [Fact]
    public async Task CopyButton_DoesNotThrowUnhandledOnClipboardFailure()
    {
        var (renderer, js) = Build();

        // Module returns false (clipboard refused) — component should show Failed state, not crash.
        var parameters = ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Text"] = "test" });

        var id = await renderer.RenderAsync<CoachCopyButton>(parameters);
        await renderer.ClickButtonAsync(id, "");

        // copyTextToClipboard returns default(bool) = false from the double, modeling a refused clipboard.
        renderer.Unhandled.Should().BeEmpty("clipboard failure is handled gracefully, not thrown");
    }
}
