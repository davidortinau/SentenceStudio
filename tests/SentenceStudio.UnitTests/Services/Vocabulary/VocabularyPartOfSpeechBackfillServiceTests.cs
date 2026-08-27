using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Services.Vocabulary;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services.Vocabulary;

/// <summary>
/// The part-of-speech backfill reads real learner vocabulary and sends part of it to a model, so
/// these tests are about refusal as much as function: it must not run un-scoped, must not classify
/// a word the learner does not own, must not trust a malformed response, must not overwrite an
/// existing classification, and must not log content.
/// </summary>
public class VocabularyPartOfSpeechBackfillServiceTests
{
    // ---------------------------------------------------------------- refusal

    [Fact]
    public async Task Disabled_DoesNothingAndIssuesNoQuery()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-1", PartOfSpeechBackfillHarness.OwnerId);

        var chat = new FakePartOfSpeechChatClient();
        var options = PartOfSpeechBackfillHarness.EnabledFor();
        options.Enabled = false;

        var db = harness.NewContext();
        var service = harness.CreateService(chat, options, db);

        // Any database access at all now throws, so "no query" is proven rather than asserted.
        harness.BreakDatabase();

        var report = await service.RunAsync();

        report.Outcome.Should().Be(VocabularyPartOfSpeechBackfillOutcome.Disabled);
        report.WordsAttempted.Should().Be(0);
        chat.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnabledWithNoAllowlist_RefusesAndIssuesNoQuery()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-1", PartOfSpeechBackfillHarness.OwnerId);

        var chat = new FakePartOfSpeechChatClient();
        var options = PartOfSpeechBackfillHarness.EnabledFor(userProfileId: null);

        var db = harness.NewContext();
        var service = harness.CreateService(chat, options, db);
        harness.BreakDatabase();

        var report = await service.RunAsync();

        report.Outcome.Should().Be(VocabularyPartOfSpeechBackfillOutcome.NoScope);
        chat.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AllowlistOfBlanks_IsTreatedAsNoScope(string blank)
    {
        using var harness = new PartOfSpeechBackfillHarness();
        var chat = new FakePartOfSpeechChatClient();
        var options = PartOfSpeechBackfillHarness.EnabledFor();
        options.UserProfileIds = new List<string> { blank };

        var db = harness.NewContext();
        var service = harness.CreateService(chat, options, db);
        harness.BreakDatabase();

        var report = await service.RunAsync();

        report.Outcome.Should().Be(VocabularyPartOfSpeechBackfillOutcome.NoScope,
            "a blank entry must never become an empty user id that matches unowned rows");
        chat.CallCount.Should().Be(0);
    }

    // ---------------------------------------------------------------- scope

    [Fact]
    public async Task ClassifiesWordsOwnedThroughEitherProgressOrResource()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-progress", PartOfSpeechBackfillHarness.OwnerId);
        harness.AddWordOwnedByResource("w-resource", PartOfSpeechBackfillHarness.OwnerId);

        var chat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("verb");
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        var report = await service.RunAsync();

        report.WordsUpdated.Should().Be(2);
        harness.PartOfSpeechOf("w-progress").Should().Be(VocabularyPartOfSpeech.Verb);
        harness.PartOfSpeechOf("w-resource").Should().Be(VocabularyPartOfSpeech.Verb);
    }

    [Fact]
    public async Task NeverTouchesAnotherTenantsWordOrAnUnownedWord()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-mine", PartOfSpeechBackfillHarness.OwnerId);
        harness.AddWordOwnedByProgress("w-theirs", PartOfSpeechBackfillHarness.OtherTenantId);
        harness.AddWordOwnedByResource("w-their-resource", PartOfSpeechBackfillHarness.OtherTenantId);
        harness.AddUnownedWord("w-orphan");

        var chat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("noun");
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        var report = await service.RunAsync();

        report.WordsUpdated.Should().Be(1);
        harness.PartOfSpeechOf("w-mine").Should().Be(VocabularyPartOfSpeech.Noun);
        harness.PartOfSpeechOf("w-theirs").Should().BeNull();
        harness.PartOfSpeechOf("w-their-resource").Should().BeNull();
        harness.PartOfSpeechOf("w-orphan").Should().BeNull();

        chat.SentPayloads.Should().NotContain(p => p.Contains("w-theirs"));
        chat.SentPayloads.Should().NotContain(p => p.Contains("w-orphan"));
    }

    // ---------------------------------------------------------------- response validation

    [Fact]
    public async Task RejectsABatchThatReturnsAnUnrequestedId()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-1", PartOfSpeechBackfillHarness.OwnerId);

        var chat = new FakePartOfSpeechChatClient()
            .Respond(ids => ids.Select(id => (id, "noun")).Append(("w-hallucinated", "verb")));
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        var report = await service.RunAsync();

        report.BatchesRejected.Should().Be(1);
        report.WordsUpdated.Should().Be(0);
        harness.PartOfSpeechOf("w-1").Should().BeNull("a batch with any invalid id is rejected whole");
    }

    [Fact]
    public async Task RejectsABatchWithADuplicateId()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-1", PartOfSpeechBackfillHarness.OwnerId);

        var chat = new FakePartOfSpeechChatClient()
            .Respond(ids => ids.Select(id => (id, "noun")).Concat(ids.Select(id => (id, "verb"))));
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        var report = await service.RunAsync();

        report.BatchesRejected.Should().Be(1);
        harness.PartOfSpeechOf("w-1").Should().BeNull();
    }

    [Fact]
    public async Task RejectsABatchThatOmitsARequestedId()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-1", PartOfSpeechBackfillHarness.OwnerId);
        harness.AddWordOwnedByProgress("w-2", PartOfSpeechBackfillHarness.OwnerId);

        var chat = new FakePartOfSpeechChatClient()
            .Respond(ids => ids.Take(1).Select(id => (id, "noun")));
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        var report = await service.RunAsync();

        report.BatchesRejected.Should().Be(1);
        report.WordsUpdated.Should().Be(0);
        harness.CountClassified().Should().Be(0, "a partial answer is not partially applied");
    }

    [Theory]
    [InlineData("""{"classifications":[{"id":"","partOfSpeech":"noun"}]}""")]
    [InlineData("""{"classifications":[]}""")]
    [InlineData("""{"classifications":null}""")]
    [InlineData("not json at all")]
    public async Task RejectsAMalformedOrEmptyResponse(string raw)
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-1", PartOfSpeechBackfillHarness.OwnerId);

        var chat = new FakePartOfSpeechChatClient().RespondWithRaw(raw);
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        var report = await service.RunAsync();

        report.WordsUpdated.Should().Be(0);
        harness.PartOfSpeechOf("w-1").Should().BeNull();
    }

    [Fact]
    public async Task AnUnmodelledTokenBecomesOtherAndABlankTokenBecomesUnknown()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-1", PartOfSpeechBackfillHarness.OwnerId);
        harness.AddWordOwnedByProgress("w-2", PartOfSpeechBackfillHarness.OwnerId);

        var chat = new FakePartOfSpeechChatClient().Respond(ids => ids.Select(id =>
            id == "w-1" ? (id, "gerundive-participle") : (id, "")));
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        await service.RunAsync();

        harness.PartOfSpeechOf("w-1").Should().Be(VocabularyPartOfSpeech.Other,
            "a real token outside the taxonomy is preserved as Other, never an undefined enum value");
        harness.PartOfSpeechOf("w-2").Should().Be(VocabularyPartOfSpeech.Unknown,
            "a blank token means the classifier could not decide");
    }

    [Fact]
    public async Task AFailedModelCallSkipsTheBatchWithoutWriting()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-1", PartOfSpeechBackfillHarness.OwnerId);

        var chat = new FakePartOfSpeechChatClient().Throws();
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        var report = await service.RunAsync();

        report.BatchesFailed.Should().Be(1);
        report.WordsUpdated.Should().Be(0);
        harness.PartOfSpeechOf("w-1").Should().BeNull();
    }

    // ---------------------------------------------------------------- write discipline

    [Fact]
    public async Task NeverOverwritesAnExistingClassification()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-classified", PartOfSpeechBackfillHarness.OwnerId,
            partOfSpeech: VocabularyPartOfSpeech.Particle);
        harness.AddWordOwnedByProgress("w-null", PartOfSpeechBackfillHarness.OwnerId);

        var chat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("adverb");
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        var report = await service.RunAsync();

        harness.PartOfSpeechOf("w-classified").Should().Be(VocabularyPartOfSpeech.Particle);
        harness.PartOfSpeechOf("w-null").Should().Be(VocabularyPartOfSpeech.Adverb);
        report.WordsAttempted.Should().Be(1, "an already-classified row is never even sent to the model");
        chat.SentPayloads.Should().NotContain(p => p.Contains("w-classified"));
    }

    [Fact]
    public async Task LeavesEveryOtherColumnUntouched()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-1", PartOfSpeechBackfillHarness.OwnerId, term: "책");

        var chat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("noun");
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        await service.RunAsync();

        using var db = harness.NewContext();
        var word = db.VocabularyWords.AsNoTracking().Single(w => w.Id == "w-1");
        word.TargetLanguageTerm.Should().Be("책");
        word.NativeLanguageTerm.Should().Be("SECRET-GLOSS");
        word.MnemonicText.Should().Be("SECRET-MNEMONIC");
        word.Tags.Should().Be("SECRET-TAG");
        word.LexicalUnitType.Should().Be(LexicalUnitType.Word);
        word.PartOfSpeech.Should().Be(VocabularyPartOfSpeech.Noun);
    }

    [Fact]
    public async Task IsIdempotentAndResumable()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-1", PartOfSpeechBackfillHarness.OwnerId);
        harness.AddWordOwnedByProgress("w-2", PartOfSpeechBackfillHarness.OwnerId);

        var firstChat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("noun");
        var first = await harness.CreateService(firstChat, PartOfSpeechBackfillHarness.EnabledFor()).RunAsync();
        first.WordsUpdated.Should().Be(2);

        var secondChat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("verb");
        var second = await harness.CreateService(secondChat, PartOfSpeechBackfillHarness.EnabledFor()).RunAsync();

        second.Outcome.Should().Be(VocabularyPartOfSpeechBackfillOutcome.NothingToDo);
        second.WordsAttempted.Should().Be(0);
        secondChat.CallCount.Should().Be(0, "a converged backfill costs nothing to re-run");
        harness.PartOfSpeechOf("w-1").Should().Be(VocabularyPartOfSpeech.Noun);
        harness.PartOfSpeechOf("w-2").Should().Be(VocabularyPartOfSpeech.Noun);
    }

    [Fact]
    public async Task ResumesTheRemainderAfterABudgetedRun()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        for (var i = 1; i <= 5; i++)
        {
            harness.AddWordOwnedByProgress($"w-{i}", PartOfSpeechBackfillHarness.OwnerId);
        }

        var firstChat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("noun");
        var first = await harness
            .CreateService(firstChat, PartOfSpeechBackfillHarness.EnabledFor(batchSize: 2, maxWords: 2))
            .RunAsync();

        first.Outcome.Should().Be(VocabularyPartOfSpeechBackfillOutcome.BudgetReached);
        first.WordsUpdated.Should().Be(2);
        harness.CountClassified().Should().Be(2);

        var secondChat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("noun");
        var second = await harness
            .CreateService(secondChat, PartOfSpeechBackfillHarness.EnabledFor(batchSize: 10, maxWords: 100))
            .RunAsync();

        second.WordsUpdated.Should().Be(3, "the second run picks up exactly the rows still null");
        harness.CountClassified().Should().Be(5);
    }

    // ---------------------------------------------------------------- bounds

    [Theory]
    [InlineData(0, VocabularyPartOfSpeechBackfillOptions.MinBatchSize)]
    [InlineData(-5, VocabularyPartOfSpeechBackfillOptions.MinBatchSize)]
    [InlineData(40, 40)]
    [InlineData(500, VocabularyPartOfSpeechBackfillOptions.MaxBatchSize)]
    public void BatchSizeIsClampedIntoTheAcceptedRange(int configured, int expected) =>
        new VocabularyPartOfSpeechBackfillOptions { BatchSize = configured }
            .EffectiveBatchSize.Should().Be(expected);

    [Fact]
    public void DefaultsAreOffAndEmpty()
    {
        var options = new VocabularyPartOfSpeechBackfillOptions();

        options.Enabled.Should().BeFalse();
        options.UserProfileIds.Should().BeEmpty();
        options.BatchSize.Should().Be(40);
        options.MaxWords.Should().Be(500);
        options.CanRun().Should().BeFalse();
    }

    [Fact]
    public async Task SendsNoMoreThanTheBatchSizePerCall()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        for (var i = 1; i <= 5; i++)
        {
            harness.AddWordOwnedByProgress($"w-{i}", PartOfSpeechBackfillHarness.OwnerId);
        }

        var chat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("noun");
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor(batchSize: 2));

        var report = await service.RunAsync();

        report.WordsUpdated.Should().Be(5);
        chat.CallCount.Should().Be(3, "5 words at 2 per batch is 2 + 2 + 1");
        chat.SentPayloads.Should().OnlyContain(p => CountIds(p) <= 2);
    }

    [Fact]
    public async Task MaxWordsCapsTheRun()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        for (var i = 1; i <= 6; i++)
        {
            harness.AddWordOwnedByProgress($"w-{i}", PartOfSpeechBackfillHarness.OwnerId);
        }

        var chat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("noun");
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor(batchSize: 4, maxWords: 3));

        var report = await service.RunAsync();

        report.WordsAttempted.Should().Be(3);
        harness.CountClassified().Should().Be(3);
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task CancellationRollsBackTheInFlightBatchAndKeepsCommittedOnes()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        for (var i = 1; i <= 4; i++)
        {
            harness.AddWordOwnedByProgress($"w-{i}", PartOfSpeechBackfillHarness.OwnerId);
        }

        using var cts = new CancellationTokenSource();
        var chat = new FakePartOfSpeechChatClient()
            .RespondClassifyingAll("noun")   // first batch commits
            .OnCall(() => cts.Cancel());      // second batch cancels mid-call

        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor(batchSize: 2));

        var report = await service.RunAsync(cts.Token);

        report.Outcome.Should().Be(VocabularyPartOfSpeechBackfillOutcome.Cancelled);
        harness.CountClassified().Should().Be(2, "the committed batch stands and the cancelled one wrote nothing");
        harness.PartOfSpeechOf("w-1").Should().Be(VocabularyPartOfSpeech.Noun);
        harness.PartOfSpeechOf("w-2").Should().Be(VocabularyPartOfSpeech.Noun);
        harness.PartOfSpeechOf("w-3").Should().BeNull();
        harness.PartOfSpeechOf("w-4").Should().BeNull();
    }

    [Fact]
    public async Task CancellationBeforeAnyWorkWritesNothing()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-1", PartOfSpeechBackfillHarness.OwnerId);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var chat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("noun");
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        var report = await service.RunAsync(cts.Token);

        report.Outcome.Should().Be(VocabularyPartOfSpeechBackfillOutcome.Cancelled);
        chat.CallCount.Should().Be(0);
        harness.CountClassified().Should().Be(0);
    }

    // ---------------------------------------------------------------- privacy

    [Fact]
    public async Task SendsOnlyTheClassificationFieldsToTheModel()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-1", PartOfSpeechBackfillHarness.OwnerId, term: "책");

        var chat = new FakePartOfSpeechChatClient().AlwaysClassifyAll("noun");
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        await service.RunAsync();

        var payload = chat.SentPayloads.Should().ContainSingle().Subject;

        // Parse rather than substring-match: the serializer escapes non-ASCII, so the raw text
        // would not contain the literal term even though the model receives it.
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        var item = document.RootElement.EnumerateArray().Single();

        item.GetProperty("Term").GetString().Should().Be("책", "the target term is what gets classified");
        item.GetProperty("Id").GetString().Should().Be("w-1", "the opaque id maps the answer back");
        item.GetProperty("Lemma").GetString().Should().Be("lemma-form");
        item.GetProperty("Language").GetString().Should().Be("Korean");
        item.GetProperty("LexicalUnitType").GetString().Should().Be("Word");

        item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            new[] { "Id", "Term", "Lemma", "Language", "LexicalUnitType" },
            "the payload shape is the whole privacy surface of this feature");

        payload.Should().NotContain("SECRET-GLOSS", "the native-language gloss is private");
        payload.Should().NotContain("SECRET-MNEMONIC");
        payload.Should().NotContain("SECRET-TAG");

        // No identifier for the learner ever leaves with the batch.
        chat.SentText.Should().OnlyContain(t => !t.Contains(PartOfSpeechBackfillHarness.OwnerId));
    }

    [Fact]
    public async Task LogsCountsOnlyAndNeverContentOrIdentifiers()
    {
        using var harness = new PartOfSpeechBackfillHarness();
        harness.AddWordOwnedByProgress("w-secret-id", PartOfSpeechBackfillHarness.OwnerId, term: "책");

        var chat = new FakePartOfSpeechChatClient()
            .Respond(ids => ids.Select(id => (id, "noun")).Append(("w-hallucinated", "verb")));
        var service = harness.CreateService(chat, PartOfSpeechBackfillHarness.EnabledFor());

        await service.RunAsync();

        var text = harness.Logs.Entries.SelectMany(e => e.AllText()).ToList();

        text.Should().NotBeEmpty();
        text.Should().OnlyContain(t => !t.Contains("w-secret-id"), "a word id is an identifier, not a count");
        text.Should().OnlyContain(t => !t.Contains(PartOfSpeechBackfillHarness.OwnerId));
        text.Should().OnlyContain(t => !t.Contains("책"));
        text.Should().OnlyContain(t => !t.Contains("SECRET-GLOSS"));
        text.Should().OnlyContain(t => !t.Contains("w-hallucinated"), "the raw response is never echoed");
    }

    private static int CountIds(string payload)
    {
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        return document.RootElement.GetArrayLength();
    }
}
