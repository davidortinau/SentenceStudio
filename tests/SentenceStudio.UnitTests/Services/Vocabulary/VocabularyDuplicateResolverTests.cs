using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Services;
using SentenceStudio.Services.DTOs;
using Xunit;

namespace SentenceStudio.UnitTests.Services.Vocabulary;

/// <summary>
/// The AI duplicate resolver's prompt and answer checks (docs/duplicate-merge-ai-plan.md): no
/// network, canned answers only. A call that fails a check goes to the learner, never applies.
/// </summary>
public class VocabularyDuplicateResolverTests
{
    private static readonly VocabularyDuplicateAiGroup Dal = Group("달", ("w-month", "month"), ("w-moon", "moon"));
    private static readonly VocabularyDuplicateAiGroup Sori = Group("소리", ("w-sound", "sound"), ("w-noise", "sound / noise"));
    private static readonly VocabularyDuplicateAiGroup Yak = Group("약",
        ("w-med1", "medicine"), ("w-med2", "drug; medicine"), ("w-about", "approximately"));

    [Fact]
    public void Prompt_names_groups_and_copies_by_alias_and_never_by_word_id()
    {
        var prompt = VocabularyDuplicateResolver.BuildPrompt([Dal, Yak], "English", "Korean");

        prompt.Should().Contain("English-speaking learner of Korean");
        prompt.Should().Contain("\"id\":\"g1\"").And.Contain("\"id\":\"g2c3\"");
        prompt.Should().Contain("\"term\":\"달\"").And.Contain("approximately");
        prompt.Should().NotContain("w-month").And.NotContain("w-about");
    }

    [Fact]
    public void Prompt_leaves_out_empty_clues_and_keeps_resources()
    {
        var group = new VocabularyDuplicateAiGroup("안", "안", [
            Copy("w-in", "inside", resources: ["Lesson 3: My room"]),
            Copy("w-not", "not", example: "안 가요 (I am not going)")]);

        var json = Json(VocabularyDuplicateResolver.BuildPrompt([group], "English", "Korean"));

        var copies = json[0].GetProperty("copies");
        copies[0].GetProperty("resources")[0].GetString().Should().Be("Lesson 3: My room");
        copies[0].TryGetProperty("example", out _).Should().BeFalse();
        copies[1].GetProperty("example").GetString().Should().Be("안 가요 (I am not going)");
        copies[1].TryGetProperty("resources", out _).Should().BeFalse();
    }

    [Fact]
    public void Batches_hold_at_most_the_batch_size()
    {
        var groups = Enumerable.Range(0, 51).Select(index => Group($"t{index}", ($"a{index}", "a"), ($"b{index}", "b")));

        VocabularyDuplicateResolver.Batches(groups).Select(batch => batch.Count).Should().Equal(25, 25, 1);
    }

    [Fact]
    public void Same_word_maps_aliases_back_to_word_ids_and_is_sure_at_the_threshold()
    {
        var call = Single([Sori], Decision("g1", "SameWord", 0.85, Set("sound; noise", "g1c1", "g1c2")));

        call.Usable.Should().BeTrue();
        call.IsSure.Should().BeTrue();
        call.Verdict.Should().Be(VocabularyDuplicateAiVerdict.SameWord);
        call.GroupId.Should().Be("소리");
        call.Sets.Single().CopyIds.Should().Equal("w-sound", "w-noise");
        call.Sets.Single().Meaning.Should().Be("sound; noise");
    }

    [Fact]
    public void Below_the_threshold_a_usable_call_is_not_sure()
    {
        var call = Single([Sori], Decision("g1", "SameWord", 0.84, Set("sound; noise", "g1c1", "g1c2")));

        call.Usable.Should().BeTrue();
        call.IsSure.Should().BeFalse();
    }

    [Fact]
    public void Different_words_keep_one_set_per_copy_with_clarified_meanings()
    {
        var call = Single([Dal], Decision("g1", "DifferentWords", 0.95,
            Set("month", "g1c1"), Set("moon (in the sky)", "g1c2")));

        call.IsSure.Should().BeTrue();
        call.Sets.Select(set => set.Meaning).Should().Equal("month", "moon (in the sky)");
    }

    [Fact]
    public void Mixed_needs_the_higher_threshold()
    {
        var mixed = Decision("g1", "Mixed", 0.88,
            Set("medicine", "g1c1", "g1c2"), Set("approximately", "g1c3"));

        var call = Single([Yak], mixed);
        mixed.Confidence = 0.9;

        call.Usable.Should().BeTrue();
        call.IsSure.Should().BeFalse("Mixed applies only at 0.9");
        Single([Yak], mixed).IsSure.Should().BeTrue();
    }

    [Fact]
    public void A_verdict_must_match_its_sets()
    {
        var singles = new[] { Set("medicine", "g1c1"), Set("drug", "g1c2"), Set("approximately", "g1c3") };
        var paired = new[] { Set("medicine", "g1c1", "g1c2"), Set("approximately", "g1c3") };

        Single([Yak], Decision("g1", "SameWord", 0.99, paired)).Problem.Should().Be("SameWord needs one set");
        Single([Yak], Decision("g1", "DifferentWords", 0.99, paired)).Problem
            .Should().Be("DifferentWords needs one set per copy");
        Single([Yak], Decision("g1", "Mixed", 0.99, singles)).Problem
            .Should().Be("Mixed needs two sets and one with two copies");
        Single([Yak], Decision("g1", "Mixed", 0.99, Set("medicine", "g1c1", "g1c2", "g1c3"))).Problem
            .Should().Be("Mixed needs two sets and one with two copies");
    }

    [Fact]
    public void A_copy_left_out_unknown_or_listed_twice_is_refused()
    {
        Single([Yak], Decision("g1", "Mixed", 0.99, Set("medicine", "g1c1", "g1c2")))
            .Problem.Should().Be("copies left out of every set");
        Single([Dal], Decision("g1", "DifferentWords", 0.99, Set("month", "g1c1"), Set("moon", "g1c9")))
            .Problem.Should().Be("unknown copy 'g1c9'");
        Single([Dal], Decision("g1", "SameWord", 0.99, Set("month", "g1c1", "g1c1", "g1c2")))
            .Problem.Should().Be("copy 'g1c1' listed twice");
        Single([Dal], Decision("g1", "SameWord", 0.99, Set("month", "w-month", "w-moon")))
            .Problem.Should().Be("unknown copy 'w-month'", "the AI answers in aliases, never word IDs");
    }

    [Theory]
    [InlineData("", "a set without a meaning")]
    [InlineData("달", "a meaning that holds the term")]
    [InlineData("moon (달)", "a meaning that holds the term")]
    public void A_meaning_must_be_a_gloss_not_the_term(string meaning, string problem)
    {
        var call = Single([Dal], Decision("g1", "SameWord", 0.99, Set(meaning, "g1c1", "g1c2")));

        call.Problem.Should().Be(problem);
    }

    [Fact]
    public void A_meaning_that_is_too_long_is_refused()
    {
        var call = Single([Dal], Decision("g1", "SameWord", 0.99, Set(new string('a', 121), "g1c1", "g1c2")));

        call.Problem.Should().Be("a meaning that is too long");
    }

    [Theory]
    [InlineData("Maybe")]
    [InlineData("1")]
    [InlineData("")]
    public void An_unknown_verdict_is_refused(string verdict)
    {
        var call = Single([Dal], Decision("g1", verdict, 0.99, Set("month", "g1c1"), Set("moon", "g1c2")));

        call.Usable.Should().BeFalse();
        call.Problem.Should().StartWith("unknown verdict");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void A_confidence_outside_zero_to_one_is_refused(double confidence)
    {
        var call = Single([Dal], Decision("g1", "DifferentWords", confidence, Set("month", "g1c1"), Set("moon", "g1c2")));

        call.Problem.Should().Be("a confidence outside 0 to 1");
    }

    [Fact]
    public void A_group_with_no_answer_or_two_answers_goes_to_the_learner()
    {
        var answer = Decision("g2", "SameWord", 0.99, Set("sound; noise", "g2c1", "g2c2"));
        var calls = VocabularyDuplicateResolver.Interpret([Dal, Sori],
            new DuplicateResolutionResponse { Groups = [answer, answer] }, "Korean");

        calls.Select(call => call.Problem).Should().Equal("no answer", "answered more than once");
        VocabularyDuplicateResolver.Interpret([Dal], null, "Korean").Single().Problem.Should().Be("no answer");
    }

    [Fact]
    public void Only_the_target_language_fills_an_unknown_language()
    {
        var call = Single([Dal], Decision("g1", "DifferentWords", 0.95,
            SetIn("korean", "month", "g1c1"), SetIn("Japanese", "moon", "g1c2")));

        call.Sets.Select(set => set.Language).Should().Equal("Korean", null);
    }

    [Fact]
    public void The_answer_type_round_trips_through_the_gateway_serializer()
    {
        // The native heads receive the typed answer through /api/v1/ai/chat as JSON.
        var answer = new DuplicateResolutionResponse
        {
            Groups = [Decision("g1", "Mixed", 0.92, Set("medicine", "g1c1", "g1c2"), Set("approximately", "g1c3"))]
        };
        var json = JsonSerializer.Serialize(answer, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var back = JsonSerializer.Deserialize<DuplicateResolutionResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Single([Yak], back!.Groups.Single()).IsSure.Should().BeTrue();
    }

    [Fact]
    public async Task Resolve_asks_again_on_the_reasoning_tier_only_for_unsure_groups()
    {
        var asked = new List<(AiTier Tier, string Prompt)>();
        Task<DuplicateResolutionResponse?> Ask(string prompt, AiTier tier, CancellationToken ct)
        {
            asked.Add((tier, prompt));
            // Fast: sure of 달, unsure of 소리. Reasoning (asked only 소리, now g1): sure.
            DuplicateResolutionResponse response = tier == AiTier.Fast
                ? new() { Groups = [
                    Decision("g1", "DifferentWords", 0.95, Set("month", "g1c1"), Set("moon", "g1c2")),
                    Decision("g2", "SameWord", 0.6, Set("sound", "g2c1", "g2c2"))] }
                : new() { Groups = [Decision("g1", "SameWord", 0.93, Set("sound; noise", "g1c1", "g1c2"))] };
            return Task.FromResult<DuplicateResolutionResponse?>(response);
        }

        var results = await VocabularyDuplicateResolver.ResolveAsync([Dal, Sori], "English", "Korean", Ask, null,
            CancellationToken.None);

        asked.Select(call => call.Tier).Should().Equal(AiTier.Fast, AiTier.Reasoning);
        asked[1].Prompt.Should().Contain("소리").And.NotContain("\"달\"");
        results[0].DecidedBy.Should().Be(AiTier.Fast);
        results[0].Call.IsSure.Should().BeTrue();
        results[1].DecidedBy.Should().Be(AiTier.Reasoning);
        results[1].Call.Sets.Single().Meaning.Should().Be("sound; noise");
        results[1].FastCall!.Confidence.Should().Be(0.6);
    }

    [Fact]
    public async Task Resolve_keeps_the_fast_call_when_the_second_answer_is_unusable()
    {
        Task<DuplicateResolutionResponse?> Ask(string prompt, AiTier tier, CancellationToken ct) =>
            Task.FromResult<DuplicateResolutionResponse?>(tier == AiTier.Fast
                ? new() { Groups = [Decision("g1", "SameWord", 0.7, Set("sound; noise", "g1c1", "g1c2"))] }
                : new() { Groups = [Decision("g1", "SameWord", 0.99, Set("소리", "g1c1", "g1c2"))] });

        var result = (await VocabularyDuplicateResolver.ResolveAsync([Sori], "English", "Korean", Ask, null,
            CancellationToken.None)).Single();

        result.DecidedBy.Should().Be(AiTier.Fast);
        result.Call.Confidence.Should().Be(0.7);
        result.Call.IsSure.Should().BeFalse("an unsure call is the learner's to accept");
    }

    [Fact]
    public async Task Resolve_leaves_a_batch_the_AI_did_not_answer_to_the_learner()
    {
        var groups = Enumerable.Range(0, 26).Select(index => Group($"t{index}", ($"a{index}", "a"), ($"b{index}", "b"))).ToList();
        Task<DuplicateResolutionResponse?> Ask(string prompt, AiTier tier, CancellationToken ct) =>
            prompt.Contains("\"term\":\"t25\"")
                ? throw new HttpRequestException("offline")
                : Task.FromResult<DuplicateResolutionResponse?>(new()
                {
                    Groups = Enumerable.Range(1, 25).Select(index =>
                        Decision($"g{index}", "DifferentWords", 0.9, Set("a", $"g{index}c1"), Set("b", $"g{index}c2"))).ToList()
                });

        var results = await VocabularyDuplicateResolver.ResolveAsync(groups, "English", "Korean", Ask, null,
            CancellationToken.None);

        results.Take(25).Should().OnlyContain(result => result.Call.IsSure);
        results[25].Call.Problem.Should().Be("the AI did not answer");
    }

    [Fact]
    public async Task Resolve_stops_when_cancelled()
    {
        using var cancel = new CancellationTokenSource();
        Task<DuplicateResolutionResponse?> Ask(string prompt, AiTier tier, CancellationToken ct)
        {
            cancel.Cancel();
            throw new OperationCanceledException(ct);
        }

        var act = () => VocabularyDuplicateResolver.ResolveAsync([Dal], "English", "Korean", Ask, null, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // The contract every client builds the same way (docs/sync-all-tables-plan.md, "Duplicate
    // decisions"); the vectors were computed independently with Python's hashlib.
    [Fact]
    public void Decision_ids_follow_the_written_contract()
    {
        SyncIds.VocabularyDuplicateDecision("owner-1", SentenceStudio.Shared.Models.VocabularyDuplicateDecisionKind.KeptApart,
                "달", ["word-b", " word-a", "word-b"])
            .Should().Be("dd:3ed9d4f0bba69544346fa2c557bdab74");
        SyncIds.VocabularyDuplicateDecision("owner-1", SentenceStudio.Shared.Models.VocabularyDuplicateDecisionKind.Merged,
                "소리", ["word-a", "word-b"])
            .Should().Be("dd:9fdcc6f4069260e2ed9bbdf609451f24");
        SyncIds.DuplicateDecisionCopies(["b", "a", " a ", ""]).Should().Be("a,b");
    }

    private static VocabularyDuplicateAiCall Single(IReadOnlyList<VocabularyDuplicateAiGroup> groups,
        DuplicateGroupDecision decision) =>
        VocabularyDuplicateResolver.Interpret(groups,
            new DuplicateResolutionResponse { Groups = [decision] }, "Korean").Single();

    private static DuplicateGroupDecision Decision(string groupId, string verdict, double confidence,
        params DuplicateWordSet[] sets) =>
        new() { GroupId = groupId, Verdict = verdict, Confidence = confidence, Sets = sets.ToList(), Reason = "test" };

    private static DuplicateWordSet Set(string meaning, params string[] copyIds) =>
        new() { Meaning = meaning, CopyIds = copyIds.ToList() };

    private static DuplicateWordSet SetIn(string language, string meaning, string copyId) =>
        new() { Meaning = meaning, CopyIds = [copyId], Language = language };

    private static VocabularyDuplicateAiGroup Group(string term, params (string Id, string Meaning)[] copies) =>
        new(term, term, copies.Select(copy => Copy(copy.Id, copy.Meaning)).ToArray());

    private static VocabularyDuplicateAiCopy Copy(string id, string meaning, string? example = null,
        IReadOnlyList<string>? resources = null) =>
        new(id, meaning, null, null, "Word", "Korean", null, example, resources ?? []);

    private static JsonElement Json(string prompt) =>
        JsonDocument.Parse(prompt[(prompt.IndexOf("GROUPS:", StringComparison.Ordinal) + "GROUPS:".Length)..]).RootElement;
}
