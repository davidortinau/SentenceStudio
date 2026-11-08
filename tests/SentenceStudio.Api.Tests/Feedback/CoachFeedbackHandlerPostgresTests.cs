using System.Text.Json;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// Sam's feedback write: the screen comes from the turn, the issue carries only public-safe
/// identifiers, and the private state is stored under the reference the issue names.
/// </summary>
public sealed class CoachFeedbackHandlerPostgresTests : IAsyncLifetime
{
    private const string Owner = "user-sam-feedback";

    private FeedbackPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await FeedbackPostgresHarness.CreateAsync("samfeedback");
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private static CoachClientScreenContext Screen() => new()
    {
        ScreenId = "native.vocabulary.edit",
        Platform = "ios",
        AppVersion = "1.4.0 (Debug)",
        OsVersion = "iOS 26.1",
        Device = "iPhone18,1",
        Locale = "ko-KR",
        Theme = "dark",
        VisibleControlIds = ["native.vocab.save", "native.vocab.term", "native.vocab.save", "has spaces & <b>"],
        Route = "vocabulary/word/9f3a",
        State =
        [
            new CoachClientScreenStateEntry { Key = "term", Value = "사과" },
            new CoachClientScreenStateEntry { Key = "toolbarItems", Value = "save,cancel" }
        ]
    };

    private static string ModelArgs(string kind = "bug") => JsonSerializer.Serialize(
        new CoachFeedbackArgs(kind, "Save button should not be in the toolbar",
            "The Save button should not appear in the toolbar. Remove it."));

    private (CoachFeedbackHandler Handler, RecordingFiler Filer, CoachWriteTurnScope Turn) NewHandler(
        CoachClientScreenContext? screen)
    {
        var turn = new CoachWriteTurnScope();
        turn.Enter("conversation-1", "turn-1");
        turn.ClientScreen = screen;
        var filer = new RecordingFiler();
        return (new CoachFeedbackHandler(turn, filer, _harness.NewContext(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CoachFeedbackHandler>.Instance), filer, turn);
    }

    [PostgresFact]
    public async Task The_preview_names_the_screen_and_says_the_issue_is_public()
    {
        var (handler, _, _) = NewHandler(Screen());

        var preview = await handler.PrepareAsync(Owner, ModelArgs(), CancellationToken.None);

        preview.Summary.Should().Be("File a bug report on GitHub");
        preview.Lines.Should().Contain("Screen: native.vocabulary.edit");
        preview.Lines.Should().Contain(l => l.StartsWith("Build: ios"));
        preview.Lines.Should().NotContain(l => l.Contains('`'), "the card shows plain values");
        preview.Lines.Should().Contain(l => l.Contains("public GitHub issue") && l.Contains("cannot be undone"));
        preview.Lines.Should().Contain(l => l.StartsWith("Private screen state will be saved under reference fs-"));
        preview.Lines.Count.Should().BeLessThanOrEqualTo(CoachWriteLimits.LineMax);
        preview.Lines.Should().OnlyContain(l => l.Length <= CoachWriteLimits.LineMaxLength);
        string.Join("\n", preview.Lines).Should().NotContain("사과", "private state is never shown as public");
    }

    [PostgresFact]
    public async Task The_model_cannot_supply_the_screen_or_the_ledger_key()
    {
        var (handler, _, _) = NewHandler(screen: null);
        // A screen object of the wrong shape is refused by the binder outright; these two
        // scalar fields bind, and must still be replaced by server values.
        var forged = """{"Kind":"bug","Title":"t","Description":"d","Jti":"forged","Reference":"fs-forged"}""";

        var preview = await handler.PrepareAsync(Owner, forged, CancellationToken.None);
        var stored = JsonSerializer.Deserialize<CoachFeedbackStoredArgs>(
            preview.CanonicalArgumentsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        stored.Jti.Should().NotBe("forged");
        stored.Reference.Should().BeNull("this turn sent no screen, so there is nothing private to store");
        stored.Screen.Should().BeNull();
    }

    [PostgresFact]
    public async Task Filing_publishes_identifiers_only_and_stores_the_private_state()
    {
        var (handler, filer, _) = NewHandler(Screen());
        var preview = await handler.PrepareAsync(Owner, ModelArgs(), CancellationToken.None);

        var result = await handler.ExecuteAsync(Owner, preview.CanonicalArgumentsJson, CancellationToken.None);

        result.Summary.Should().Be("Filed issue #42");
        result.EntityId.Should().Be("42");

        var payload = filer.Payloads.Should().ContainSingle().Subject;
        payload.Labels.Should().Equal("bug");
        payload.RouteCategory.Should().Be(FeedbackRouteCategory.Coach);
        payload.Platform.Should().Be(FeedbackPlatform.Native);
        payload.Body.Should().Contain("The Save button should not appear in the toolbar. Remove it.");
        payload.Body.Should().Contain("`native.vocabulary.edit`").And.Contain("`native.vocab.save`");
        payload.Body.Should().NotContain("has spaces", "a control id that is not identifier-shaped is dropped");
        payload.Body.Should().NotContain("사과").And.NotContain("vocabulary/word/9f3a")
            .And.NotContain("toolbarItems", "route and state are private");

        var reference = result.Lines.Single(l => l.StartsWith("Private screen state:"))["Private screen state: ".Length..];
        payload.Body.Should().Contain(reference);

        await using var db = _harness.NewContext();
        var row = db.FeedbackScreenSnapshots.Single();
        row.Reference.Should().Be(reference);
        row.UserProfileId.Should().Be(Owner);
        row.Jti.Should().Be(payload.Jti);
        row.ContextJson.Should().Contain("사과").And.Contain("vocabulary/word/9f3a");
    }

    [PostgresFact]
    public async Task Running_the_same_approval_twice_files_with_one_key_and_one_snapshot()
    {
        var (handler, filer, _) = NewHandler(Screen());
        var preview = await handler.PrepareAsync(Owner, ModelArgs(), CancellationToken.None);

        await handler.ExecuteAsync(Owner, preview.CanonicalArgumentsJson, CancellationToken.None);
        await handler.ExecuteAsync(Owner, preview.CanonicalArgumentsJson, CancellationToken.None);

        filer.Payloads.Select(p => p.Jti).Distinct().Should().ContainSingle(
            "the ledger key is fixed at preview, so the filer replays instead of filing twice");
        await using var db = _harness.NewContext();
        db.FeedbackScreenSnapshots.Count().Should().Be(1);
    }

    [PostgresFact]
    public async Task A_refused_filing_is_reported_as_not_filed()
    {
        var (handler, filer, _) = NewHandler(Screen());
        filer.Next = new FeedbackFileOutcome(FeedbackFileOutcomeKind.GitHubRefused);
        var preview = await handler.PrepareAsync(Owner, ModelArgs(), CancellationToken.None);

        var act = () => handler.ExecuteAsync(Owner, preview.CanonicalArgumentsJson, CancellationToken.None);

        (await act.Should().ThrowAsync<CoachToolException>()).Which.Message.Should().Contain("Nothing was filed");
    }

    [PostgresFact]
    public async Task The_card_shows_everything_the_issue_publishes()
    {
        var (handler, filer, _) = NewHandler(Screen());
        var longDescription = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"Step {i}: tap the thing and watch it."));
        var args = JsonSerializer.Serialize(new CoachFeedbackArgs("bug", "Long report", longDescription));

        var preview = await handler.PrepareAsync(Owner, args, CancellationToken.None);
        await handler.ExecuteAsync(Owner, preview.CanonicalArgumentsJson, CancellationToken.None);
        var body = filer.Payloads.Single().Body;

        var shownDetails = string.Concat(preview.Lines.Where(l => !l.StartsWith("Type:") && !l.StartsWith("Title:")
            && !l.StartsWith("Screen:") && !l.StartsWith("Build:") && !l.StartsWith("Display:")
            && !l.StartsWith("Controls") && !l.StartsWith("Private") && !l.StartsWith("This creates")));
        var published = body.Split("### Where")[0].Replace("## Bug report", string.Empty).Trim();
        shownDetails.Should().Be("Details: " + published.Replace("\n", " / "),
            "the card shows the whole published description, not a prefix");
        body.Should().Contain("Step 1: tap the thing and watch it.\nStep 2:", "line breaks are kept");
        preview.Lines.Count.Should().BeLessThanOrEqualTo(CoachWriteLimits.LineMax,
            "a long description is trimmed, never the warning or the screen lines");
        preview.Lines.Should().Contain(l => l.Contains("public GitHub issue") && l.Contains("cannot be undone"));

        foreach (var line in preview.Lines.Where(l => l.StartsWith("Screen:") || l.StartsWith("Build:")
                     || l.StartsWith("Display:") || l.StartsWith("Controls")))
        {
            // Same facts on the card and in the issue; the issue only adds code spans.
            var colon = line.IndexOf(':');
            var label = line[..colon];
            var publishedLine = body.Split('\n').Single(l => l.StartsWith($"- **{label}:**"));
            publishedLine.Replace("`", string.Empty).Should().Be($"- **{label}:**{line[(colon + 1)..]}",
                "each shown screen line is published with the same values");
        }
    }

    [PostgresFact]
    public async Task Public_values_are_code_spans_so_a_url_cannot_become_a_link()
    {
        var screen = Screen();
        var (handler, filer, _) = NewHandler(new CoachClientScreenContext
        {
            ScreenId = screen.ScreenId, Platform = "ios", Locale = "https://evil.example/x", Theme = "dark"
        });

        var preview = await handler.PrepareAsync(Owner, ModelArgs(), CancellationToken.None);
        await handler.ExecuteAsync(Owner, preview.CanonicalArgumentsJson, CancellationToken.None);

        filer.Payloads.Single().Body.Should().Contain("locale `https://evil.example/x`");
    }

    [PostgresFact]
    public async Task A_report_that_was_not_filed_leaves_no_private_snapshot()
    {
        var (handler, filer, _) = NewHandler(Screen());
        filer.Next = new FeedbackFileOutcome(FeedbackFileOutcomeKind.Unconfigured);
        var preview = await handler.PrepareAsync(Owner, ModelArgs(), CancellationToken.None);

        var act = () => handler.ExecuteAsync(Owner, preview.CanonicalArgumentsJson, CancellationToken.None);
        await act.Should().ThrowAsync<CoachToolException>();

        await using var db = _harness.NewContext();
        db.FeedbackScreenSnapshots.Count().Should().Be(0);
    }

    [PostgresFact]
    public async Task An_in_doubt_report_keeps_its_snapshot_because_an_issue_may_name_it()
    {
        var (handler, filer, _) = NewHandler(Screen());
        filer.Next = new FeedbackFileOutcome(FeedbackFileOutcomeKind.GitHubUnreachable);
        var preview = await handler.PrepareAsync(Owner, ModelArgs(), CancellationToken.None);

        var act = () => handler.ExecuteAsync(Owner, preview.CanonicalArgumentsJson, CancellationToken.None);
        (await act.Should().ThrowAsync<CoachToolException>()).Which.Message.Should().Contain("may have reached GitHub");

        await using var db = _harness.NewContext();
        db.FeedbackScreenSnapshots.Count().Should().Be(1);
    }

    [PostgresFact]
    public async Task An_oversized_screen_is_trimmed_whole_and_still_parses()
    {
        var huge = new CoachClientScreenContext
        {
            ScreenId = "native.study",
            Route = new string('r', 1000),
            State = Enumerable.Range(0, 200)
                .Select(i => new CoachClientScreenStateEntry { Key = $"key{i:D3}", Value = new string('"', 500) })
                .ToArray()
        };
        var (handler, _, _) = NewHandler(huge);

        var preview = await handler.PrepareAsync(Owner, ModelArgs(), CancellationToken.None);
        var stored = JsonSerializer.Deserialize<CoachFeedbackStoredArgs>(
            preview.CanonicalArgumentsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        stored.Screen!.ToPrivateJson().Length.Should().BeLessThanOrEqualTo(FeedbackScreenContextNormalizer.MaxPrivateJsonLength);
        var parse = () => JsonDocument.Parse(stored.Screen.ToPrivateJson());
        parse.Should().NotThrow();
    }

    [PostgresFact]
    public async Task An_issue_created_without_a_recorded_link_is_reported_as_filed()
    {
        var (handler, filer, _) = NewHandler(Screen());
        filer.Next = new FeedbackFileOutcome(FeedbackFileOutcomeKind.SettlementFailed);
        var preview = await handler.PrepareAsync(Owner, ModelArgs(), CancellationToken.None);

        var result = await handler.ExecuteAsync(Owner, preview.CanonicalArgumentsJson, CancellationToken.None);

        result.Summary.Should().Be("Filed the issue on GitHub");
        result.EntityId.Should().BeNull("there is no issue number to point at");
        result.Lines.Should().Contain(l => l.Contains("could not be recorded"));
        await using var db = _harness.NewContext();
        db.FeedbackScreenSnapshots.Count().Should().Be(1, "the issue exists and names the reference");
    }

    [PostgresFact]
    public async Task An_unknown_kind_is_refused_before_anything_is_stored()
    {
        var (handler, _, _) = NewHandler(Screen());

        var act = () => handler.PrepareAsync(Owner, ModelArgs(kind: "complaint"), CancellationToken.None);

        await act.Should().ThrowAsync<CoachToolException>();
    }

    private sealed class RecordingFiler : IFeedbackIssueFiler
    {
        public List<FeedbackPreviewPayload> Payloads { get; } = [];

        public FeedbackFileOutcome? Next { get; set; }

        public Task<FeedbackFileOutcome> FileAsync(
            FeedbackPreviewPayload payload, string userProfileId, CancellationToken cancellationToken)
        {
            Payloads.Add(payload);
            var outcome = Next ?? new FeedbackFileOutcome(
                Payloads.Count == 1 ? FeedbackFileOutcomeKind.Created : FeedbackFileOutcomeKind.Replayed,
                42, "https://github.com/davidortinau/SentenceStudio/issues/42", payload.Title);
            return Task.FromResult(outcome);
        }
    }
}
