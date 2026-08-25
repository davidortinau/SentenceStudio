using FluentAssertions;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Where W7's limitation surface is allowed to appear, and who owns the sentences on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a source test rather than a render test.</b> Both facts pinned here are facts about the
/// repository, not about a rendered card. "No production screen shows this yet" cannot be asserted
/// by rendering the card — rendering it is exactly what a test does. It can only be asserted by
/// looking at what the application source references.
/// </para>
/// <para>
/// <b>The hint ladder is the reason.</b> The card can render "ask me for a category clue" as a
/// standing offer. Nothing in the app delivers a category clue yet: the rung carries a
/// <c>CoachHintKind</c> and no content, and no service turns that kind into a hint. A menu that
/// offers an action the app cannot perform is worse than no menu, and worst of all on this surface,
/// whose entire purpose is to stop the coach claiming capabilities it does not have. So W7 stays
/// contract-and-renderer until the delivery stage ships, and this test is what makes that a
/// decision rather than an oversight.
/// </para>
/// <para>
/// <b>The third fact is the rung order.</b> The existing quotation guard only asks whether each
/// documented string exists in the resx, which a transposed ladder passes unchanged — every rung is
/// still a real string, just in the wrong sequence. So the order is bound separately, against
/// <c>CoachLimitations.HintLadder</c>, in both English and Korean. It matters because the ladder
/// ascends in how much of the written <em>form</em> it discloses, and a row that puts the form cue
/// before the cloze documents a ladder that hands part of the answer over one rung early.
/// </para>
/// </remarks>
public class CoachLimitationWiringContractTests
{
    // AC-S16b: the ladder must not be on a learner's screen before anything can execute a rung.
    [Fact]
    public void No_production_caller_offers_a_hint_ladder_or_alternatives()
    {
        // W9 changed the shape of this rule. The card now has one production caller: CoachChatPane
        // renders it for a grounding refusal, which is a reason and at most a destination. What is
        // still forbidden is the part nothing can execute — a hint rung, or an alternative the app
        // cannot carry out. So the check moved from "nobody renders this card" to "nobody feeds it
        // a ladder or alternatives".
        // Scoped to what a learner can actually see. The server may construct W7 boundary
        // limitations that carry ladders — CoachLimitations.cs is exactly that catalogue — and
        // that is fine precisely because no rendering surface consumes them. The rule is about
        // reaching a screen, not about a DTO existing.
        var renderSurfaces = SourceFilesUnder("src")
            .Where(file => file.EndsWith(".razor", StringComparison.Ordinal))
            .Where(file => !file.EndsWith("CoachLimitationCard.razor", StringComparison.Ordinal))
            .ToList();

        renderSurfaces.Should().NotBeEmpty("the scan must have surfaces to look at");

        var offenders = renderSurfaces
            .Select(file => (Path: file, Text: File.ReadAllText(file)))
            .Where(f => f.Text.Contains("HintLadder", StringComparison.Ordinal)
                     || f.Text.Contains("ShorterSession", StringComparison.Ordinal)
                     || f.Text.Contains("CoachAlternativeCode", StringComparison.Ordinal))
            .Select(f => Relative(f.Path))
            .ToList();

        offenders.Should().BeEmpty(
            "no rendering surface may put a hint ladder, a shorter-session offer or an alternative "
            + "in front of a learner while no service can deliver one. The grounding refusal sends "
            + "all three empty, which is why it is allowed to render");
    }

    // The one production caller there is, pinned to the refusal shape.
    [Fact]
    public void The_only_production_caller_is_the_grounding_refusal_region()
    {
        var callers = SourceFilesUnder("src")
            .Where(file => !file.EndsWith("CoachLimitationCard.razor", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("<CoachLimitationCard", StringComparison.Ordinal))
            .Select(Relative)
            .ToList();

        callers.Should().ContainSingle("W9 adds exactly one mount, and a second would need review")
            .Which.Should().EndWith("CoachChatPane.razor");

        var pane = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "SentenceStudio.UI", "Shared", "Coach", "CoachChatPane.razor"));

        pane.Should().Contain("Coach.Limitation is { } withheld",
            "the mount is gated on a limitation being present, not rendered unconditionally");
        // Bounded to the refusal region: the pane legitimately uses role="alert" elsewhere for
        // real failures, so a pane-wide exclusion would be asserting the wrong thing.
        var start = pane.IndexOf("<section class=\"coach-refusal", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the refusal region must exist to be checked");
        var region = pane[start..pane.IndexOf(">", pane.IndexOf("<CoachLimitationCard", start), StringComparison.Ordinal)];

        region.Should().Contain("role=\"status\"",
            "a withheld answer is announced politely; it is not an error");
        region.Should().Contain("aria-live=\"polite\"");
        region.Should().NotContain("role=\"alert\"", "nothing failed, so nothing shouts");
    }

    // The server side of the same claim: a grounding refusal carries no ladder.
    [Fact]
    public void The_grounding_refusal_projection_sends_no_ladder_or_alternatives()
    {
        var projection = File.ReadAllText(ApiSourcePath(
            "Coach", "Validation", "Claims", "CoachRefusalLimitationProjection.cs"));

        foreach (var empty in new[] { "Alternatives = []", "HintLadder = []", "ShorterSession = null" })
        {
            projection.Should().Contain(empty,
                $"the refusal must send {empty}; the card renders whatever it is given");
        }
    }

    // Non-vacuity for the test above: the scan must be capable of finding a reference at all.
    [Fact]
    public void The_scan_that_finds_callers_can_actually_find_one()
    {
        var componentExists = SourceFilesUnder("src")
            .Any(file => file.EndsWith("CoachLimitationCard.razor", StringComparison.Ordinal));

        componentExists.Should().BeTrue("the component must exist, or the caller scan proves nothing");

        var testCallers = SourceFilesUnder("tests")
            .Count(file => File.ReadAllText(file).Contains("CoachLimitationCard", StringComparison.Ordinal));

        testCallers.Should().BeGreaterThan(0,
            "the standalone component tests reference it, which is how we know a reference is "
            + "detectable and the scans above measure real presence and absence");
    }

    // Sentence ownership: every learner-visible word on the card is a client resource lookup.
    [Fact]
    public void Every_learner_visible_string_on_the_card_comes_from_the_client_resx()
    {
        var card = File.ReadAllText(CardPath());

        var markup = card[card.IndexOf("@if (Limitation is not null)", StringComparison.Ordinal)..];
        markup = markup[..markup.IndexOf("@code {", StringComparison.Ordinal)];

        // Strip the tags. What is left is what a learner reads, plus Razor control flow.
        var textNodes = System.Text.RegularExpressions.Regex
            .Replace(markup, "<[^>]*>", " ", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line.Any(char.IsLetter))
            .ToList();

        textNodes.Should().HaveCountGreaterThan(20, "the card does render text, so this scan has something to check");

        textNodes.Where(node => !node.StartsWith('@')).Should().BeEmpty(
            "every learner-visible word must arrive through a Razor expression. A bare text node is "
            + "a sentence the client resx does not own and no translator will ever see");

        // And every literal inside those expressions is a resource key, not prose.
        var literals = textNodes
            .SelectMany(node => System.Text.RegularExpressions.Regex.Matches(node, "\"([^\"]*)\"")
                .Select(match => match.Groups[1].Value))
            .ToList();

        literals.Should().NotBeEmpty("the card does look keys up by name");
        literals.Should().OnlyContain(
            literal => literal.StartsWith("Coach_", StringComparison.Ordinal),
            "a quoted string in the markup is either a resource key or a hardcoded sentence");
    }

    // Sentence ownership, the other half: the server's hardcoded English is not what a learner reads.
    [Fact]
    public void The_servers_hardcoded_limitation_copy_is_not_shipped_to_the_client()
    {
        var serverCopy = File.ReadAllText(ApiSourcePath("Coach", "Application", "CoachDeterministicCopy.cs"));
        var card = File.ReadAllText(CardPath());

        // The server holds a shorter-session sentence of its own. The card must not carry a copy of
        // it: two spellings of one sentence is two places for it to go stale, and only one of them
        // is translated.
        serverCopy.Should().Contain("shorter set today",
            "this test is about a sentence that exists on the server; if it moved, re-point the test");

        card.Should().NotContain("shorter set today",
            "the card renders Coach_Limitation_ShorterSessionRetrieval from the client resx. A "
            + "duplicated server sentence here would be untranslated and would drift");
    }

    // The stage that would make that server copy learner-visible, pinned so promotion is deliberate.
    [Fact]
    public void Server_authored_copy_only_reaches_a_learner_at_Repair_or_above()
    {
        var ladder = File.ReadAllText(ApiSourcePath("Coach", "Validation", "Claims", "CoachClaimVocabulary.cs"));

        ladder.Should().Contain("Off = 0");
        ladder.Should().Contain("Observe = 1");
        ladder.Should().Contain("Repair = 2");

        ladder.Should().Contain("The answer is never altered",
            "Observe is what production ships. While it holds, no CoachDeterministicCopy string is "
            + "substituted into an answer, so none of that hardcoded English is learner-visible");

        ladder.Should().Contain("substitute",
            "Repair is the promotion that makes those strings learner-visible. It must not happen "
            + "until they have a localization path, and this assertion is where that gets noticed");

        // And the stage the app actually ships with is below the one that substitutes.
        var options = File.ReadAllText(ApiSourcePath("Coach", "Runtime", "CoachOptions.cs"));
        var shipped = System.Text.RegularExpressions.Regex.Match(
            options, @"CoachGroundingStage Stage \{ get; set; \} = CoachGroundingStage\.(\w+)");

        shipped.Success.Should().BeTrue("the shipped default must be readable, or this pins nothing");
        shipped.Groups[1].Value.Should().BeOneOf(["Off", "Observe"],
            "at Off the answer is not scanned and at Observe it is not altered. Either leaves the "
            + "server's hardcoded English unseen. A default of Repair or Enforce would put "
            + "untranslated const strings in front of a Korean learner");
    }

    // The acceptance cases and the gate note quote resource values verbatim. Quoting drifts.
    [Fact]
    public void The_strings_quoted_in_the_acceptance_cases_are_the_strings_the_app_ships()
    {
        var english = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "SentenceStudio.Shared", "Resources", "Strings", "AppResources.resx"));

        var cases = File.ReadAllText(AcceptanceCasesPath());

        var gateNote = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "w7-learning-value-gate.md"));

        // Every string the §35 cases and the gate note put in a learner's mouth must be a real
        // resource value. Three of these were paraphrased on the first pass and had to be corrected
        // by hand; this is what makes the next paraphrase a build failure instead.
        string[] quoted =
        [
            "This is more than Sam will change in one step.",
            "Words this would affect",
            "What can change there",
            "Instead, you could",
            "Nudges you can ask for",
            "Shorter set today",
            "Still full practice, just fewer words.",
            "Something Sam can\u2019t do here",
            "Counted at",
            "What kind of word it is",
            "How it starts and how long it is",
            "The sentence with it missing",
            "Not available",
            "Consequences not stated.",
            "Nothing changes \u2014 this screen only shows information.",
            "Clear one list at a time",
            "Export them first, so you can get them back",
            "Take a copy first"
        ];

        foreach (var value in quoted)
        {
            english.Should().Contain(value, $"'{value}' is quoted as shipped copy and must exist in the resx");
        }

        var documented = quoted.Where(value => cases.Contains(value) || gateNote.Contains(value)).ToList();
        documented.Should().HaveCount(quoted.Length,
            "every string checked here must actually appear in the acceptance cases or the gate "
            + "note, or this test is guarding quotations nobody made");
    }

    // AC-S16b / SAM-LIM-17: the documented rung order is the shipped rung order, in both languages.
    [Fact]
    public void The_rung_order_in_the_acceptance_cases_matches_the_shipped_hint_ladder()
    {
        // The shipped ladder, read from the one place that owns it. Parsed from source rather than
        // referenced as a type because this project does not reference the API assembly — the same
        // reason CoachOptions' default is regex-read above. The parse is guarded below, so a
        // rename that stops it matching fails loudly instead of vacuously passing.
        var ladder = ShippedHintLadder();

        ladder.Should().HaveCount(3,
            "CoachLimitations.HintLadder ships three rungs; if the parse found a different number "
            + "the regex has drifted from the source and this test is checking nothing");
        ladder.Select(rung => rung.Rung).Should().Equal([1, 2, 3],
            "the rungs must be 1-based and ascending, or 'the same order' is not well defined");
        ladder.Select(rung => rung.Kind).Should().OnlyHaveUniqueItems(
            "a repeated kind would make the order assertions below unfalsifiable");

        // What a learner actually reads for each rung, in each language. The card renders
        // Coach_Hint_{Kind}, so these are the exact strings the acceptance rows must be quoting.
        var englishRungs = ladder.Select(rung => ResourceValue("AppResources.resx", rung.Kind)).ToList();
        var koreanRungs = ladder.Select(rung => ResourceValue("AppResources.ko.resx", rung.Kind)).ToList();

        englishRungs.Should().OnlyHaveUniqueItems("indistinguishable rungs cannot be ordered");
        koreanRungs.Should().OnlyHaveUniqueItems("indistinguishable rungs cannot be ordered");
        koreanRungs.Should().NotIntersectWith(englishRungs,
            "a Korean rung equal to its English value would mean the ko resx fell back, and the "
            + "Korean half of this check would silently be an English check");

        var cases = File.ReadAllLines(AcceptanceCasesPath());

        // §35.3, the English ladder row. §35.4, the Korean one. Scoped to a single line each so a
        // rung name appearing elsewhere in a 3,800-line file cannot satisfy the order check.
        AssertRowListsRungsInLadderOrder(
            SingleLineContaining(cases, "Count the rungs"), englishRungs, "§35.3 (SAM-LIM-16b), English");

        AssertRowListsRungsInLadderOrder(
            SingleLineContaining(cases, "| Rungs |"), koreanRungs, "§35.4 (SAM-LIM-17), Korean");
    }

    /// <summary>
    /// Asserts every rung appears in <paramref name="row"/>, and that they appear in ladder order.
    /// </summary>
    /// <remarks>
    /// Presence first, then order. A missing rung and a transposed one are different defects and a
    /// combined assertion would report the wrong one — which is how the original transposition
    /// survived a review that did check the rungs were all there.
    /// </remarks>
    private static void AssertRowListsRungsInLadderOrder(
        string row,
        IReadOnlyList<string> rungsInLadderOrder,
        string label)
    {
        foreach (var rung in rungsInLadderOrder)
        {
            row.Should().Contain(rung,
                $"{label} must quote every shipped rung; '{rung}' is missing from that row");
        }

        var positions = rungsInLadderOrder.Select(rung => row.IndexOf(rung, StringComparison.Ordinal)).ToList();

        positions.Should().BeInAscendingOrder(
            $"{label} must list the rungs in the shipped order — "
            + string.Join(" then ", rungsInLadderOrder)
            + ". The ladder ascends in how much of the written form it discloses, so a transposed "
            + "row documents a ladder that hands a learner part of the answer one rung early");
    }

    /// <summary>The rungs declared in <c>CoachLimitations.HintLadder</c>, in declaration order.</summary>
    private static IReadOnlyList<(int Rung, string Kind)> ShippedHintLadder()
    {
        var source = File.ReadAllText(ApiSourcePath(
            "Coach", "Application", "Limitations", "CoachLimitations.cs"));

        var start = source.IndexOf("HintLadder =", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the ladder must be locatable, or this test pins nothing");

        var end = source.IndexOf("];", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "the ladder's collection expression must terminate");

        return System.Text.RegularExpressions.Regex
            .Matches(source[start..end], @"new CoachHintRungDto\((\d+),\s*CoachHintKind\.(\w+)\)")
            .Select(match => (int.Parse(match.Groups[1].Value), match.Groups[2].Value))
            .ToList();
    }

    /// <summary>The learner-visible string the card renders for a rung kind, from a client resx.</summary>
    private static string ResourceValue(string resxFileName, string hintKind)
    {
        var key = $"Coach_Hint_{hintKind}";
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(
            RepoRoot(), "src", "SentenceStudio.Shared", "Resources", "Strings", resxFileName));

        var value = document.Root?
            .Elements("data")
            .FirstOrDefault(data => (string?)data.Attribute("name") == key)?
            .Element("value")?.Value;

        value.Should().NotBeNullOrWhiteSpace(
            $"{resxFileName} must carry '{key}'; the card renders that key for every rung");

        return value!;
    }

    private static string SingleLineContaining(IEnumerable<string> lines, string marker)
    {
        var matches = lines.Where(line => line.Contains(marker, StringComparison.Ordinal)).ToList();

        matches.Should().ContainSingle(
            $"exactly one acceptance row must contain '{marker}'; zero means the row was renamed "
            + "and this check went vacuous, more than one means it is ambiguous which row is bound");

        return matches[0];
    }

    private static string AcceptanceCasesPath() => Path.Combine(
        RepoRoot(), ".claude", "skills", "e2e-testing", "references", "learning-coach.md");


    // LVG-W9-8: the evidence list must not decide for itself which rows it is showing.
    [Fact]
    public void The_evidence_list_takes_its_rows_from_the_caller_and_never_from_the_workspace()
    {
        var list = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "SentenceStudio.UI", "Shared", "Coach", "CoachEvidenceList.razor"));

        list.Should().NotContain("@inject CoachWorkspaceState",
            "reading workspace evidence implicitly made the list a window onto whatever was last "
            + "held, so a refusal could render above rows from an earlier question that the caller "
            + "had no way to exclude");
        list.Should().NotContain("Coach.Evidence",
            "the rows arrive as a parameter; there is no second source");
        list.Should().Contain("[Parameter, EditorRequired] public IReadOnlyList<CoachEvidenceDto> Items",
            "and the parameter is required, so a caller cannot forget to say which rows it means");

        // Every caller says so explicitly.
        var callers = SourceFilesUnder("src")
            .Select(file => (Path: file, Text: File.ReadAllText(file)))
            .Where(f => f.Text.Contains("<CoachEvidenceList", StringComparison.Ordinal))
            .ToList();

        callers.Should().NotBeEmpty("the list is mounted somewhere, or this guard proves nothing");

        foreach (var caller in callers)
        {
            caller.Text.Should().Contain("<CoachEvidenceList",
                "sanity: the match that selected this file");
            System.Text.RegularExpressions.Regex
                .Matches(caller.Text, "<CoachEvidenceList[^>]*>")
                .Should().OnlyContain(m => m.Value.Contains("Items=", StringComparison.Ordinal),
                    $"{Relative(caller.Path)} must pass the rows it means");
        }
    }

    private static string CardPath() =>
        Path.Combine(RepoRoot(), "src", "SentenceStudio.UI", "Shared", "Coach", "CoachLimitationCard.razor");

    private static string ApiSourcePath(params string[] segments) =>
        Path.Combine(new[] { RepoRoot(), "src", "SentenceStudio.Api" }.Concat(segments).ToArray());

    private static IEnumerable<string> SourceFilesUnder(string top)
    {
        var root = Path.Combine(RepoRoot(), top);
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".razor", StringComparison.Ordinal)
                        || file.EndsWith(".cs", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    private static string Relative(string path) => path[(RepoRoot().Length + 1)..];

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.UI")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root must be locatable from the test output directory");
        return directory!.FullName;
    }
}
