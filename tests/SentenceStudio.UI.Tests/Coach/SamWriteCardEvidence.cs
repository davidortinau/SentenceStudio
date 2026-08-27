using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Writes the rendered card at each stage to an evidence folder, for visual review.
/// </summary>
/// <remarks>
/// Not an assertion. It exists so a reviewer can look at the real markup, under the real
/// stylesheet, without standing up Aspire — and so a design change shows up as a diff in a
/// picture rather than only in a string comparison. Opt-in: it only writes when
/// <c>SAM_WRITE_EVIDENCE</c> names a directory, so an ordinary test run does nothing.
/// </remarks>
public class SamWriteCardEvidence
{
    private const string Conversation = "conv-1";

    [Fact]
    public async Task Write_the_card_at_each_stage()
    {
        var target = Environment.GetEnvironmentVariable("SAM_WRITE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        Directory.CreateDirectory(target);

        var sections = new List<(string Title, string Html)>
        {
            ("Proposed — reversible", await RenderAsync(soft: true)),
            ("Proposed — protected", await RenderAsync(soft: false)),
            ("Confirmation required", await RenderAsync(soft: false, act: s => s.BeginWriteConfirmationAsync("op-1"))),
            ("Applied — undo available", await RenderAsync(soft: true, act: s => s.AcceptWriteAsync("op-1"))),
            ("Applied — no undo", await RenderAsync(soft: false, act: async s =>
            {
                await s.BeginWriteConfirmationAsync("op-1");
                await s.ConfirmWriteAsync();
            })),
            ("Undone", await RenderAsync(soft: true, act: async s =>
            {
                await s.AcceptWriteAsync("op-1");
                await s.UndoWriteAsync("op-1");
            })),
            ("Declined", await RenderAsync(soft: true, act: s => s.RejectWriteAsync("op-1"))),
            ("Expired", await RenderAsync(soft: true, expiresAtUtc: DateTime.UtcNow.AddMinutes(-1)))
        };

        var css = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "SentenceStudio.UI", "wwwroot", "css", "app.css"));

        var body = string.Join("\n", sections.Select(s =>
            $"<section><h2 class=\"stage\">{s.Title}</h2>{s.Html}</section>"));

        var page = $$"""
            <!doctype html>
            <html lang="en" data-bs-theme="light">
            <head>
            <meta charset="utf-8">
            <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1/font/bootstrap-icons.css">
            <style>
            :root { --bs-border-color:#d8d8dc; --bs-body-bg:#fff; --bs-body-color:#16161a;
                    --bs-border-radius:.5rem; --bs-primary:#3f6ae0; --bs-danger:#c2352e;
                    --bs-success:#1f7a44; --bs-warning:#a86800; --bs-secondary-color:#5c5c66; }
            body { font-family: -apple-system, system-ui, sans-serif; margin:0; padding:24px;
                   background:#f4f4f7; color:var(--bs-body-color); }
            section { max-width: 420px; margin: 0 0 24px; }
            .stage { font-size:12px; letter-spacing:.08em; text-transform:uppercase;
                     color:#6b6b76; margin:0 0 6px; }
            .ss-body1-strong { font-weight:600; font-size:15px; }
            .ss-body2 { font-size:14px; }
            .ss-body2-strong { font-weight:600; font-size:14px; }
            .ss-caption1 { font-size:12px; }
            .btn { border-radius:.5rem; border:1px solid var(--bs-border-color); padding:.45rem .9rem;
                   font-size:14px; background:#fff; cursor:pointer; }
            .btn-ss-primary { background:var(--bs-primary); border-color:var(--bs-primary); color:#fff; }
            .btn-ss-danger { background:var(--bs-danger); border-color:var(--bs-danger); color:#fff; }
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

        File.WriteAllText(Path.Combine(target, "sam-write-card.html"), page);
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

    private static async Task<string> RenderAsync(
        bool soft,
        Func<CoachWorkspaceState, Task>? act = null,
        DateTime? expiresAtUtc = null)
    {
        var client = new FakeCoachApiClient
        {
            DurableHistoryAvailable = true,
            Availability = new CoachAvailabilityResponse
            {
                IsAvailable = true,
                State = CoachAvailabilityState.Available,
                IsDurableHistoryAvailable = true,
                IsSamOverlayAvailable = true,
                IsSamWriteAvailable = true
            }
        };

        client.AddConversation(Conversation);

        var write = client.AddWrite(
            Conversation,
            "op-1",
            requiresConfirmation: !soft,
            isReversible: soft,
            kind: soft ? CoachWriteChangeKind.VocabularyAdd : CoachWriteChangeKind.VocabularyRemove,
            expiresAtUtc: expiresAtUtc);

        client.Seed(Conversation, CoachMessageRole.Coach, "I can do that.", writeOperation: write);

        var flags = new CoachFeatureFlags(client);
        await flags.EnsureLoadedAsync();

        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client, flags), flags);
        await state.RefreshAvailabilityAsync();
        await state.OpenConversationAsync(CoachPresentation.Overlay, Conversation);

        if (act is not null)
        {
            await act(state);
        }

        var current = state.Timeline
            .Select(entry => entry.WriteOperation)
            .First(candidate => candidate?.OperationId == "op-1")!;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped(_ => state);

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SamWriteCard>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(SamWriteCard.Operation)] = current
                }));

            return output.ToHtmlString();
        });
    }
}
