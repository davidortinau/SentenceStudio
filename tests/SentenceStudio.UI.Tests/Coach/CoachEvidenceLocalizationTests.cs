using System.Text.RegularExpressions;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
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
/// The evidence panel reads in the learner's own language.
/// </summary>
/// <remarks>
/// <para>
/// The defect: <c>CoachEvidenceDto.Label</c>, <c>.Summary</c> and <c>value.Label</c> were
/// documented as localized and were not — the server writes them in English from a fixed switch.
/// A Korean learner opened one card with an English heading and English prose sitting on top of
/// correctly-Korean coverage, order and withheld lines. The panel is the surface a learner opens
/// to check whether a claim is real, and they cannot check a claim they cannot read.
/// </para>
/// <para>
/// The fix localizes from the closed codes already on the wire. The server keeps sending the prose
/// so an older client is untouched; a current client never renders it when a known code is present.
/// </para>
/// </remarks>
public class CoachEvidenceLocalizationTests
{
    /// <summary>
    /// Every English string the server can put on an evidence card. None may reach a Korean
    /// reader when the codes are present.
    /// </summary>
    private static readonly string[] ServerEnglish =
    [
        // Headings, from CoachTurnEvidenceProjection.LabelFor.
        "Practice balance", "Vocabulary", "Resources", "Settings", "Plan",
        // Summaries, from CoachTurnEvidenceProjection.SummaryFor.
        "Your resources, ranked by how recently you used them.",
        "Your resources, ranked by when you last changed them.",
        "One of your resources.",
        "Every word you are tracking, counted by review schedule.",
        "Your words that are not currently due for review.",
        "One word you are tracking.",
        "Your study settings.",
        "Your study settings, and how much you have saved.",
        "Today's plan and what you have logged against it.",
        "A plan worked out from your data. Nothing was saved.",
        "Practice you logged over the window.",
        // Value labels, from CoachTurnEvidenceProjection.Describe.
        "Rows read", "Rows matched", "Rows withheld"
    ];

    // ---------------------------------------------------------------- resource census and parity

    [Fact]
    public void Every_known_kind_definition_and_value_code_has_a_key_in_both_locales()
    {
        var keys = ExpectedKeys().ToList();

        // Census, so a member added later without a string fails here rather than on screen.
        keys.Should().HaveCount(22, "5 kinds + 14 definitions + 3 value codes");

        foreach (var key in keys)
        {
            foreach (var culture in new[] { "en", "ko" })
            {
                var value = Localized(key, culture);
                value.Should().NotBe(key, $"'{key}' must resolve in {culture}");
                value.Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    [Fact]
    public void No_korean_string_leaks_its_english_source()
    {
        foreach (var key in ExpectedKeys())
        {
            var en = Localized(key, "en");
            var ko = Localized(key, "ko");

            ko.Should().NotBe(en, $"'{key}' must be translated, not copied");
        }
    }

    [Fact]
    public void The_sentinel_members_get_no_key_because_they_have_no_words()
    {
        // Unknown/Unrecognized deliberately have no resource: the ladder falls back or omits.
        Localized("Coach_EvidenceKind_Unrecognized", "en").Should().Be("Coach_EvidenceKind_Unrecognized");
        Localized("Coach_EvidenceDefinition_Unknown", "en").Should().Be("Coach_EvidenceDefinition_Unknown");
        Localized("Coach_EvidenceValue_Unknown", "en").Should().Be("Coach_EvidenceValue_Unknown");
    }

    // ---------------------------------------------------------------- Korean render, both components

    [Fact]
    public async Task A_korean_learner_sees_no_server_english_when_the_codes_are_present()
    {
        var html = await RenderAsync(FullyCodedItem(), "ko");

        foreach (var english in ServerEnglish)
        {
            html.Should().NotContain(english, $"'{english}' is server prose and must not reach a Korean reader");
        }

        // And the Korean words are actually there — otherwise "no English" passes on an empty panel.
        html.Should().Contain(Localized("Coach_EvidenceKind_VocabularyDue", "ko"));
        html.Should().Contain(Localized("Coach_EvidenceDefinition_UndueVocabularySearch", "ko"));
        html.Should().Contain(Localized("Coach_EvidenceValue_RowsRead", "ko"));
    }

    [Fact]
    public async Task An_english_learner_sees_the_english_resource_not_the_server_prose()
    {
        // Same ladder, other culture. The resource happens to match the server's wording for the
        // heading, so the value label is what proves the resource is the source.
        var html = await RenderAsync(FullyCodedItem(), "en");

        html.Should().Contain(Localized("Coach_EvidenceValue_RowsRead", "en"));
        html.Should().Contain(Localized("Coach_EvidenceKind_VocabularyDue", "en"));
    }

    [Fact]
    public async Task Switching_culture_re_renders_from_the_same_payload()
    {
        // No server call: the identical DTO renders in both languages.
        var item = FullyCodedItem();

        var korean = await RenderAsync(item, "ko");
        var english = await RenderAsync(item, "en");

        korean.Should().Contain(Localized("Coach_EvidenceValue_RowsRead", "ko"));
        english.Should().Contain(Localized("Coach_EvidenceValue_RowsRead", "en"));
        korean.Should().NotBe(english);
    }

    [Fact]
    public void Both_components_subscribe_to_culture_changes()
    {
        foreach (var name in new[] { "CoachEvidenceList.razor", "CoachMessageEvidence.razor" })
        {
            var source = ReadUiSource(Path.Combine("Shared", "Coach", name));

            source.Should().Contain("Localize.CultureChanged +=", $"{name} must re-render on a culture change");
            source.Should().Contain("Localize.CultureChanged -=", $"{name} must unsubscribe");
            source.Should().Contain("EvidenceText.Heading(item)", $"{name} must localize the heading");
            source.Should().Contain("EvidenceText.Summary(item)", $"{name} must localize the summary");
            source.Should().Contain("EvidenceText.ValueLabel(value)", $"{name} must localize value labels");
            source.Should().NotContain("@item.Label", $"{name} must not render server prose directly");
            source.Should().NotContain("@item.Summary", $"{name} must not render server prose directly");
            source.Should().NotContain("@value.Label", $"{name} must not render server prose directly");
        }
    }

    // ---------------------------------------------------------------- the fallback ladder

    [Fact]
    public void An_unrecognized_kind_falls_back_to_server_prose_never_to_another_heading()
    {
        var localizer = Localizer("ko");
        var unrecognized = Clone(FullyCodedItem(), kind: CoachEvidenceKind.Unrecognized, label: "Something new");

        localizer.Heading(unrecognized).Should().Be("Something new");
        localizer.Heading(unrecognized).Should().NotBe(Localized("Coach_EvidenceKind_PracticeBalance", "ko"));
    }

    [Fact]
    public void An_unrecognized_kind_with_no_prose_renders_no_heading_at_all()
    {
        Localizer("ko").Heading(Clone(FullyCodedItem(), kind: CoachEvidenceKind.Unrecognized, label: ""))
            .Should().BeEmpty("a borrowed heading over real numbers is worse than none");
    }

    [Fact]
    public void An_unknown_definition_code_omits_the_summary_rather_than_showing_english()
    {
        var localizer = Localizer("ko");
        var item = Clone(FullyCodedItem(), definitionCode: CoachDefinitionCode.Unknown);

        localizer.HasSummary(item).Should().BeFalse();
        localizer.Summary(item).Should().BeEmpty();
    }

    [Fact]
    public void A_null_definition_code_keeps_the_server_prose_because_it_is_the_only_description()
    {
        // null and Unknown are different. null is the old-payload case: the server named nothing,
        // so its prose is all there is and it is true. Unknown is the leak case, covered above.
        var localizer = Localizer("ko");
        var item = Clone(FullyCodedItem(), clearDefinition: true);

        localizer.HasSummary(item).Should().BeTrue();
        localizer.Summary(item).Should().Be(item.Summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CoachEvidenceValueCode.Unknown)]
    public void A_missing_or_unknown_value_code_falls_back_to_the_server_label(CoachEvidenceValueCode? code)
    {
        var value = new CoachEvidenceValueDto
        {
            Code = code,
            Label = "Rows read",
            Value = 3,
            Unit = CoachEvidenceUnit.Items
        };

        Localizer("ko").ValueLabel(value).Should().Be("Rows read", "the old behaviour is the floor");
    }

    // ---------------------------------------------------------------- wire safety

    [Fact]
    public void An_old_payload_with_no_codes_still_renders_the_server_prose()
    {
        var localizer = Localizer("ko");
        var legacy = new CoachEvidenceDto
        {
            Kind = CoachEvidenceKind.PracticeBalance,
            Label = "Practice balance",
            Summary = "Practice you logged over the window.",
            WindowStartDate = new DateOnly(2026, 1, 1),
            WindowEndDate = new DateOnly(2026, 1, 7),
            Values = [new CoachEvidenceValueDto { Label = "Rows read", Value = 1, Unit = CoachEvidenceUnit.Items }]
        };

        // Kind is a known member, so the heading still localizes. The summary has no code at all,
        // so the server's prose is kept — losing it would leave an old payload with less than it
        // renders today.
        localizer.Heading(legacy).Should().Be(Localized("Coach_EvidenceKind_PracticeBalance", "ko"));
        localizer.Summary(legacy).Should().Be("Practice you logged over the window.");
        localizer.ValueLabel(legacy.Values[0]).Should().Be("Rows read");
    }

    [Fact]
    public void A_value_code_the_client_cannot_read_degrades_to_the_prose_label()
    {
        var parsed = JsonSerializer.Deserialize<CoachEvidenceValueDto>(
            """{"label":"Rows read","value":3,"unit":"Items","code":"SomethingFromTheFuture"}""",
            SentenceStudio.Contracts.Wire.WireJson.Client);

        parsed.Should().NotBeNull();
        parsed!.Code.Should().Be(CoachEvidenceValueCode.Unknown);
        Localizer("ko").ValueLabel(parsed).Should().Be("Rows read");
    }

    [Fact]
    public void An_evidence_kind_the_client_cannot_read_lands_on_the_appended_sentinel()
    {
        var parsed = JsonSerializer.Deserialize<CoachEvidenceDto>(
            """
            {"kind":"SomethingFromTheFuture","label":"New card","summary":"x",
             "windowStartDate":"2026-01-01","windowEndDate":"2026-01-07"}
            """,
            SentenceStudio.Contracts.Wire.WireJson.Client);

        parsed.Should().NotBeNull();
        parsed!.Kind.Should().Be(CoachEvidenceKind.Unrecognized,
            "collapsing onto PracticeBalance would print the wrong heading over real numbers");
        Localizer("ko").Heading(parsed).Should().Be("New card");
    }

    [Fact]
    public void The_sentinel_is_appended_so_stored_ordinals_keep_their_meaning()
    {
        ((int)CoachEvidenceKind.PracticeBalance).Should().Be(0);
        ((int)CoachEvidenceKind.Unrecognized).Should()
            .Be(Enum.GetValues<CoachEvidenceKind>().Length - 1, "the sentinel is last, never inserted");

        ((int)CoachEvidenceValueCode.Unknown).Should().Be(0, "the value code's sentinel is its zero");
    }

    [Fact]
    public void No_evidence_string_carries_a_term_a_gloss_or_an_example()
    {
        // The embargo, restated over the new strings: every one names a population or a count.
        foreach (var key in ExpectedKeys())
        {
            foreach (var culture in new[] { "en", "ko" })
            {
                var value = Localized(key, culture);
                value.Should().NotContain("\"", $"'{key}' must not quote learner content");
                value.Should().NotMatchRegex(@"\d+\s*(개|words?|terms?)\b",
                    $"'{key}' is a template, not a rendered count");
            }
        }
    }

    // ---------------------------------------------------------------- the other renderer

    /// <summary>
    /// The plan canvas list, driven through the real state path rather than a parameter.
    /// </summary>
    /// <remarks>
    /// <c>CoachMessageEvidence</c> takes its items as a parameter, so it was straightforward to
    /// cover first. <c>CoachEvidenceList</c> reads <c>CoachWorkspaceState.Evidence</c>, and the
    /// review was right that asserting the shared localizer for it was weaker than rendering it:
    /// a component can inject the right helper and still fail to call it on one of the three
    /// fields. This drives the real path — a session response carrying evidence — so the
    /// guarantee is the same one the message panel already has.
    /// </remarks>
    [Fact]
    public async Task A_korean_learner_sees_no_server_english_in_the_plan_canvas_list()
    {
        var item = FullyCodedItem();
        var html = await RenderListAsync(item, "ko");

        // Non-vacuity 1: the payload really is carrying server English, so "it is suppressed"
        // below is a claim about something that exists rather than about an empty fixture.
        ServerProseCarriedBy(item).Should().BeEquivalentTo(
            new[]
            {
                "Vocabulary",
                "Your words that are not currently due for review.",
                "Rows read", "Rows matched", "Rows withheld"
            },
            "the fixture must carry exactly the server prose this test claims never reaches the reader");

        // Non-vacuity 2: the list rendered a card, not its empty state.
        html.Should().NotContain(
            SentenceStudio.LocalizationManager.Instance.GetString("Coach_NoEvidence", new CultureInfo("ko")),
            "an empty list would pass every assertion below without rendering a card");

        // The guarantee, asserted per slot by equality rather than by scanning the whole
        // document for bare words. Equality is the stronger claim: if server prose leaked,
        // the slot would read "Vocabulary" / "Rows read" and this fails. It is also immune
        // to unrelated churn in the shared resx, which other workstreams edit heavily and
        // which supplies five further keys to this same component.
        Slot(html, HeadingRx, "card heading")
            .Should().Be(Localized("Coach_EvidenceKind_VocabularyDue", "ko"));
        Slot(html, SummaryRx, "card summary")
            .Should().Be(Localized("Coach_EvidenceDefinition_UndueVocabularySearch", "ko"));
        ValueLabels(html).Should().Equal(
            Localized("Coach_EvidenceValue_RowsRead", "ko"),
            Localized("Coach_EvidenceValue_RowsMatched", "ko"),
            Localized("Coach_EvidenceValue_RowsWithheld", "ko"));

        // Breadth retained for the multi-word projection prose, which cannot collide with
        // Korean copy the way a bare "Plan" or "Settings" can.
        var visible = VisibleText(html);
        foreach (var english in UnambiguousServerProse)
        {
            visible.Should().NotContain(english, $"'{english}' is server prose and must not reach a Korean reader");
        }
    }

    /// <summary>The exact server-authored strings this payload carries — the bytes that must not surface.</summary>
    private static IEnumerable<string> ServerProseCarriedBy(CoachEvidenceDto item)
    {
        if (!string.IsNullOrWhiteSpace(item.Label)) yield return item.Label!;
        if (!string.IsNullOrWhiteSpace(item.Summary)) yield return item.Summary!;

        foreach (var value in item.Values ?? [])
        {
            if (!string.IsNullOrWhiteSpace(value.Label)) yield return value.Label!;
        }
    }

    // "Vocabulary", "Resources", "Settings" and "Plan" are ordinary English nouns. Scanning a
    // whole rendered component for them produces false reds whenever neighbouring localized
    // copy happens to contain one, so they are asserted as slot equality above instead.
    private static readonly string[] UnambiguousServerProse =
        ServerEnglish.Where(prose => prose.Contains(' ')).ToArray();

    private static readonly Regex HeadingRx =
        new("<div class=\"ss-body2 fw-semibold\">(.*?)</div>", RegexOptions.Singleline);

    private static readonly Regex SummaryRx =
        new("<div class=\"ss-body2\">(.*?)</div>", RegexOptions.Singleline);

    private static readonly Regex ValueLabelRx =
        new("<li class=\"ss-caption1 d-flex justify-content-between\">\\s*<span>(.*?)</span>", RegexOptions.Singleline);

    private static string Slot(string html, Regex pattern, string what)
    {
        var match = pattern.Match(html);
        match.Success.Should().BeTrue($"the {what} slot must be present, or this test asserts nothing about it");
        return VisibleText(match.Groups[1].Value).Trim();
    }

    private static string[] ValueLabels(string html)
    {
        var labels = ValueLabelRx.Matches(html).Select(m => VisibleText(m.Groups[1].Value).Trim()).ToArray();
        labels.Should().HaveCount(3, "the fixture carries three values, so three labels must have been extracted");
        return labels;
    }

    /// <summary>
    /// The text a learner actually reads, with markup and attributes removed.
    /// </summary>
    /// <remarks>
    /// Attributes are excluded deliberately. <c>data-coach-definition</c> carries the stable,
    /// unlocalized definition name on purpose — it is there to be diagnosable and testable without
    /// spending screen on it — so a raw-HTML scan for English matches
    /// <c>UndueVocabularySearch</c> and calls a fully-Korean card a leak. The message-panel test
    /// avoids this by construction because the harness returns rendered text; this one renders to
    /// HTML, so it has to say what it means.
    /// </remarks>
    private static string VisibleText(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ");

    [Fact]
    public async Task The_plan_canvas_list_switches_language_from_the_same_payload()
    {
        var item = FullyCodedItem();

        var korean = await RenderListAsync(item, "ko");
        var english = await RenderListAsync(item, "en");

        korean.Should().Contain(Localized("Coach_EvidenceValue_RowsRead", "ko"));
        english.Should().Contain(Localized("Coach_EvidenceValue_RowsRead", "en"));
        korean.Should().NotBe(english);
    }

    /// <summary>Renders the plan-canvas list with one evidence item in workspace state.</summary>
    private static async Task<string> RenderListAsync(CoachEvidenceDto item, string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);

        try
        {
            var client = new FakeCoachApiClient
            {
                OnStartSession = () =>
                {
                    var session = FakeCoachApiClient.Session();
                    return new CoachSessionResponse
                    {
                        SessionId = session.SessionId,
                        Status = session.Status,
                        Messages = session.Messages,
                        Evidence = [item],
                        Revisions = session.Revisions,
                        ActiveConstraints = session.ActiveConstraints,
                        PlanState = session.PlanState,
                        PendingSuggestion = session.PendingSuggestion,
                        ClarificationsRemaining = session.ClarificationsRemaining,
                        CreatedAtUtc = session.CreatedAtUtc,
                        ExpiresAtUtc = session.ExpiresAtUtc
                    };
                }
            };

            var directory = new CoachConversationDirectory(client);
            var state = new CoachWorkspaceState(client, directory);
            await state.OpenAsync(CoachPresentation.Overlay);

            state.Evidence.Should().ContainSingle("the real state path must have absorbed the item");

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<BlazorLocalizationService>();
            services.AddScoped<CoachPersona>();
            services.AddScoped<IJSRuntime>(_ => new StubJSRuntime());
            services.AddScoped(_ => state);
            services.AddScoped(_ => directory);

            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                // The list takes its rows explicitly now, so the caller says which ones it means.
                // They still come from the real state path above, not from a hand-built list.
                var output = await renderer.RenderComponentAsync<CoachEvidenceList>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(CoachEvidenceList.Items)] = state.Evidence
                    }));
                return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
            });
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<string> ExpectedKeys()
    {
        foreach (var kind in Enum.GetValues<CoachEvidenceKind>())
        {
            if (kind != CoachEvidenceKind.Unrecognized)
            {
                yield return $"Coach_EvidenceKind_{kind}";
            }
        }

        foreach (var definition in Enum.GetValues<CoachDefinitionCode>())
        {
            if (definition != CoachDefinitionCode.Unknown)
            {
                yield return $"Coach_EvidenceDefinition_{definition}";
            }
        }

        foreach (var code in Enum.GetValues<CoachEvidenceValueCode>())
        {
            if (code != CoachEvidenceValueCode.Unknown)
            {
                yield return $"Coach_EvidenceValue_{code}";
            }
        }
    }

    private static string Localized(string key, string culture) =>
        SentenceStudio.LocalizationManager.Instance.GetString(key, new CultureInfo(culture));

    private static CoachEvidenceLocalizer Localizer(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            return new CoachEvidenceLocalizer(new BlazorLocalizationService());
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static CoachEvidenceDto FullyCodedItem() => new()
    {
        Kind = CoachEvidenceKind.VocabularyDue,
        Label = "Vocabulary",
        Summary = "Your words that are not currently due for review.",
        WindowStartDate = new DateOnly(2026, 1, 1),
        WindowEndDate = new DateOnly(2026, 1, 7),
        DefinitionCode = CoachDefinitionCode.UndueVocabularySearch,
        Coverage = CoachEvidenceCoverage.PageOfOwnedSet,
        Order = CoachEvidenceOrder.MasteryDescending,
        WithheldReason = CoachWithheldReason.DueReviewEmbargo,
        WithheldCount = 4,
        MatchedCount = 14,
        ReturnedCount = 10,
        Values =
        [
            new CoachEvidenceValueDto
            {
                Code = CoachEvidenceValueCode.RowsRead, Label = "Rows read",
                Value = 10, Unit = CoachEvidenceUnit.Items
            },
            new CoachEvidenceValueDto
            {
                Code = CoachEvidenceValueCode.RowsMatched, Label = "Rows matched",
                Value = 14, Unit = CoachEvidenceUnit.Items
            },
            new CoachEvidenceValueDto
            {
                Code = CoachEvidenceValueCode.RowsWithheld, Label = "Rows withheld",
                Value = 4, Unit = CoachEvidenceUnit.Items
            }
        ]
    };

    private static CoachEvidenceDto Clone(
        CoachEvidenceDto source,
        CoachEvidenceKind? kind = null,
        string? label = null,
        CoachDefinitionCode? definitionCode = null,
        bool clearDefinition = false) => new()
    {
        Kind = kind ?? source.Kind,
        Label = label ?? source.Label,
        Summary = source.Summary,
        WindowStartDate = source.WindowStartDate,
        WindowEndDate = source.WindowEndDate,
        DefinitionCode = clearDefinition ? null : definitionCode ?? source.DefinitionCode,
        Values = source.Values
    };

    private static string ReadUiSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.UI")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "SentenceStudio.UI", relativePath));
    }

    /// <summary>
    /// Renders the panel <b>expanded</b>. It ships collapsed, so a static render returns only the
    /// toggle and every "does not contain English" assertion would pass over an empty panel.
    /// </summary>
    private static async Task<string> RenderAsync(CoachEvidenceDto item, string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<BlazorLocalizationService>();
            services.AddScoped<CoachPersona>();
            services.AddScoped<IJSRuntime>(_ => new StubJSRuntime());

            var provider = services.BuildServiceProvider();
            var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

            var id = await renderer.RenderAsync<CoachMessageEvidence>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Items"] = (IReadOnlyList<CoachEvidenceDto>)[item],
                    ["MessageId"] = "m-1"
                }));

            await renderer.ClickButtonByIdAsync(id, "coach-evidence-toggle-m-1");

            return renderer.RenderedText(id);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
