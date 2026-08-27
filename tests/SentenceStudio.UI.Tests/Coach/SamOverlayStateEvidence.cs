using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Writes the overlay panel at each size, under the real stylesheet, for visual review.
/// </summary>
/// <remarks>
/// <para>
/// Not an assertion — the sibling of <see cref="SamWriteCardEvidence"/> and opt-in the same way, so
/// an ordinary test run writes nothing. It exists so a reviewer can see the compact, expanded and
/// full-screen panel, in both persona languages, without standing up Aspire, and so a design change
/// to any of them shows up as a diff in a picture.
/// </para>
/// <para>
/// Set <c>SAM_WRITE_EVIDENCE</c> to a directory to produce <c>sam-overlay-states.html</c>.
/// </para>
/// </remarks>
public class SamOverlayStateEvidence
{
    private const string Conversation = "conv-1";

    [Fact]
    public async Task Write_the_panel_at_each_size()
    {
        var target = Environment.GetEnvironmentVariable("SAM_WRITE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        Directory.CreateDirectory(target);

        var sections = new List<(string Title, string Html)>
        {
            ("Compact — English target (Sam)",
                await RenderAsync(SamOverlayVisualState.Compact, "English")),
            ("Expanded — English target (Sam)",
                await RenderAsync(SamOverlayVisualState.Expanded, "English")),
            ("Expanded — Korean target (쌤)",
                await RenderAsync(SamOverlayVisualState.Expanded, "Korean")),
            ("Full screen — Korean target (쌤)",
                await RenderAsync(SamOverlayVisualState.FullScreen, "Korean"))
        };

        var css = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "SentenceStudio.UI", "wwwroot", "css", "app.css"));

        var body = string.Join("\n", sections.Select(s =>
            $"<section><h2 class=\"stage\">{s.Title}</h2><div class=\"stage-frame\">{s.Html}</div></section>"));

        var page = $$"""
            <!doctype html>
            <html lang="en" data-bs-theme="light">
            <head>
            <meta charset="utf-8">
            <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1/font/bootstrap-icons.css">
            <style>
            :root { --bs-border-color:#d8d8dc; --bs-body-bg:#fff; --bs-body-color:#16161a;
                    --bs-border-radius:.5rem; --bs-primary:#3f6ae0; --bs-danger:#c2352e;
                    --bs-success:#1f7a44; --bs-warning:#a86800; --bs-secondary-color:#5c5c66;
                    --bs-secondary-bg:#eceff5; }
            body { font-family: -apple-system, system-ui, sans-serif; margin:0; padding:24px;
                   background:#f4f4f7; color:var(--bs-body-color); }
            section { margin: 0 0 32px; }
            .stage { font-size:12px; letter-spacing:.08em; text-transform:uppercase;
                     color:#6b6b76; margin:0 0 6px; }
            /* The panel is position:fixed in the app. Each sample is given its own containing
               block so the sizes can be compared side by side on one page. */
            .stage-frame { position:relative; width:1024px; height:640px; overflow:hidden;
                           background:#fff; border:1px solid var(--bs-border-color);
                           border-radius:.5rem; }
            .stage-frame .sam-panel { position:absolute; }
            .ss-body1 { font-size:15px; }
            .ss-body2 { font-size:14px; }
            .ss-caption1 { font-size:12px; }
            .form-control { width:100%; border:1px solid var(--bs-border-color);
                            border-radius:.5rem; padding:.45rem .6rem; font-size:14px; }
            .btn { border-radius:.5rem; border:1px solid var(--bs-border-color); padding:.45rem .9rem;
                   font-size:14px; background:#fff; cursor:pointer; }
            .btn-ss-primary { background:var(--bs-primary); border-color:var(--bs-primary); color:#fff; }
            .btn-ss-secondary { background:#fff; }
            .text-secondary-ss { color:var(--bs-secondary-color); }
            {{css}}
            </style>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;

        File.WriteAllText(Path.Combine(target, "sam-overlay-states.html"), page);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    private sealed class FixedLanguage(string language) : ICoachPersonaLanguageSource
    {
        public Task<string?> GetStudyLanguageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(language);
    }

    private static async Task<string> RenderAsync(
        SamOverlayVisualState state, string studyLanguage)
    {
        var client = new FakeCoachApiClient
        {
            DurableHistoryAvailable = true,
            Availability = new CoachAvailabilityResponse
            {
                IsAvailable = true,
                State = CoachAvailabilityState.Available,
                CanEditPlan = true,
                IsDurableHistoryAvailable = true,
                IsSamOverlayAvailable = true
            }
        };

        client.AddConversation(Conversation);
        client.Seed(Conversation, CoachMessageRole.Learner, "What is the difference between 은/는 and 이/가?");
        client.Seed(Conversation, CoachMessageRole.Coach,
            "은/는 marks the topic — what the sentence is about. 이/가 marks the subject — who or what is doing something.");

        var directory = new CoachConversationDirectory(client);
        var workspace = new CoachWorkspaceState(client, directory);
        await workspace.OpenConversationAsync(CoachPresentation.Overlay, Conversation);

        var persona = new CoachPersona(new FixedLanguage(studyLanguage));
        await persona.RefreshAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped(_ => persona);
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped(_ => workspace);
        services.AddScoped(_ => directory);

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(SamPanel.VisualState)] = state,
                [nameof(SamPanel.ViewportWidth)] = 1024
            });

            var output = await renderer.RenderComponentAsync<SamPanel>(parameters);
            return output.ToHtmlString();
        });
    }
}
