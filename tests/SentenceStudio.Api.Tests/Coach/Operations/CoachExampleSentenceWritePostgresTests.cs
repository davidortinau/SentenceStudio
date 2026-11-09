using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// Sam adds, edits and deletes example sentences through the approval ledger, against real
/// PostgreSQL (docs/coach/sam-capability-gap.md, gap 2).
/// </summary>
public sealed class CoachExampleSentenceWritePostgresTests : IAsyncLifetime
{
    private const string Owner = "user-owner";
    private const string Stranger = "user-stranger";
    private const string Conversation = "conv-1";

    private CoachPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("example-sentences", withApplicationSchema: true);

        await using var db = _harness.NewContext();
        var now = _harness.Time.GetUtcNow().UtcDateTime;
        db.CoachConversations.Add(new SentenceStudio.Api.Coach.Persistence.History.CoachConversation
        {
            Id = Conversation,
            UserProfileId = Owner,
            ProtectedTitle = "seeded",
            HistoryStartsAt = now,
            ContentProtectionVersion = _harness.ContentProtector.CurrentVersion,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private CoachWriteOperationService NewLedger(ApplicationDbContext appDb, string? owner)
    {
        var store = new CoachExampleSentenceStore(
            appDb, new PlanDateContext(TimeZoneInfo.Utc, () => _harness.Time.GetUtcNow().UtcDateTime));
        var handlers = new ICoachWriteHandler[]
        {
            new CoachExampleSentenceEntryHandler(store),
            new CoachExampleSentenceEditHandler(store),
            new CoachExampleSentenceRemovalHandler(store)
        };

        return CoachWriteTestScope.NewLedger(
            _harness.NewContext(), _harness.ContentProtector, handlers, new FakeUserScope(owner), _harness.Time);
    }

    private async Task<string> SeedWordAsync(string owner, string term, bool due = false)
    {
        await using var db = _harness.NewApplicationContext();
        var word = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = owner,
            TargetLanguageTerm = term,
            NativeLanguageTerm = term + " meaning",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.VocabularyWords.Add(word);
        if (due)
        {
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = Guid.NewGuid().ToString(),
                UserId = owner,
                VocabularyWordId = word.Id,
                TotalAttempts = 3,
                NextReviewDate = _harness.Time.GetUtcNow().UtcDateTime.AddDays(-1)
            });
        }

        await db.SaveChangesAsync();
        return word.Id;
    }

    private async Task<string> SeedSentenceAsync(string owner, string wordId, string target)
    {
        await using var db = _harness.NewApplicationContext();
        var sentence = new ExampleSentence
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = owner,
            VocabularyWordId = wordId,
            TargetSentence = target,
            Status = ExampleSentenceStatus.Curated,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.ExampleSentences.Add(sentence);
        await db.SaveChangesAsync();
        return sentence.Id;
    }

    private static string Json<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, CoachNormalizedJson.Options);

    // ------------------------------------------------------------------ add

    [PostgresFact]
    public async Task Adding_a_sentence_writes_nothing_until_accepted_then_one_owned_curated_row()
    {
        var wordId = await SeedWordAsync(Owner, "사과");
        await using var appDb = _harness.NewApplicationContext();
        var ledger = NewLedger(appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeExampleSentenceEntry,
            Json(new CoachExampleSentenceEntryArgs(wordId, "사과가 맛있어요.", "The apple is tasty.", WrittenByLearner: true)));

        proposal.Summary.Should().Contain("사과");
        proposal.Lines.Should().Contain("Sentence: 사과가 맛있어요.");
        await using (var before = _harness.NewApplicationContext())
        {
            (await before.ExampleSentences.CountAsync()).Should().Be(0, "a proposal writes nothing");
        }

        var receipt = await ledger.AcceptAsync(Conversation, proposal.OperationId);
        receipt.Status.Should().Be(CoachWriteOperationStatus.Executed);
        receipt.EntityKind.Should().Be(CoachWriteEntityKind.ExampleSentence);

        await using var after = _harness.NewApplicationContext();
        var row = await after.ExampleSentences.SingleAsync();
        row.Id.Should().Be(receipt.EntityId);
        row.UserProfileId.Should().Be(Owner);
        row.VocabularyWordId.Should().Be(wordId);
        row.TargetSentence.Should().Be("사과가 맛있어요.");
        row.NativeSentence.Should().Be("The apple is tasty.");
        row.Status.Should().Be(ExampleSentenceStatus.Curated);
        row.Source.Should().Be(ExampleSentenceSource.UserAuthored);
        row.IsCore.Should().BeTrue("the first sentence on a word is its main one");
    }

    [PostgresFact]
    public async Task Undoing_an_addition_removes_exactly_that_sentence()
    {
        var wordId = await SeedWordAsync(Owner, "사과");
        var keep = await SeedSentenceAsync(Owner, wordId, "사과를 샀어요.");
        await using var appDb = _harness.NewApplicationContext();
        var ledger = NewLedger(appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeExampleSentenceEntry,
            Json(new CoachExampleSentenceEntryArgs(wordId, "사과가 빨개요.")));
        var receipt = await ledger.AcceptAsync(Conversation, proposal.OperationId);

        await using (var mid = _harness.NewApplicationContext())
        {
            var added = await mid.ExampleSentences.SingleAsync(e => e.Id == receipt.EntityId);
            added.IsCore.Should().BeFalse("the word already had a main sentence");
            added.Source.Should().Be(ExampleSentenceSource.AiGenerated);
        }

        (await ledger.UndoAsync(Conversation, proposal.OperationId)).Status.Should().Be(CoachWriteOperationStatus.Undone);

        await using var after = _harness.NewApplicationContext();
        (await after.ExampleSentences.Select(e => e.Id).ToListAsync()).Should().Equal(keep);
    }

    [PostgresFact]
    public async Task A_duplicate_sentence_or_another_learners_word_is_refused()
    {
        var mine = await SeedWordAsync(Owner, "사과");
        await SeedSentenceAsync(Owner, mine, "사과가 맛있어요.");
        var theirs = await SeedWordAsync(Stranger, "바나나");
        await using var appDb = _harness.NewApplicationContext();
        var ledger = NewLedger(appDb, Owner);

        var duplicate = () => ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeExampleSentenceEntry,
            Json(new CoachExampleSentenceEntryArgs(mine, "사과가 맛있어요.")));
        await duplicate.Should().ThrowAsync<CoachToolException>();

        var foreign = () => ledger.ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeExampleSentenceEntry,
            Json(new CoachExampleSentenceEntryArgs(theirs, "바나나가 좋아요.")));
        await foreign.Should().ThrowAsync<CoachToolException>();

        await using var after = _harness.NewApplicationContext();
        (await after.ExampleSentences.CountAsync()).Should().Be(1);
    }

    [PostgresFact]
    public async Task A_sentence_longer_than_a_card_line_is_refused_not_cut()
    {
        var wordId = await SeedWordAsync(Owner, "사과");
        await using var appDb = _harness.NewApplicationContext();
        var ledger = NewLedger(appDb, Owner);

        var tooLong = () => ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeExampleSentenceEntry,
            Json(new CoachExampleSentenceEntryArgs(wordId, new string('가', CoachExampleSentenceStore.SentenceMaxLength + 1))));

        await tooLong.Should().ThrowAsync<CoachToolException>();
    }

    // ------------------------------------------------------------------ edit

    [PostgresFact]
    public async Task Editing_a_sentence_changes_it_and_undo_restores_it()
    {
        var wordId = await SeedWordAsync(Owner, "사과");
        var sentenceId = await SeedSentenceAsync(Owner, wordId, "사과 맛있어요.");
        await using var appDb = _harness.NewApplicationContext();
        var ledger = NewLedger(appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeExampleSentenceEdit,
            Json(new CoachExampleSentenceEditArgs(sentenceId, TargetSentence: "사과가 맛있어요.", NativeSentence: "The apple is tasty.")));
        proposal.Lines.Should().Contain("Sentence now: 사과가 맛있어요.");

        await ledger.AcceptAsync(Conversation, proposal.OperationId);
        await using (var mid = _harness.NewApplicationContext())
        {
            var row = await mid.ExampleSentences.SingleAsync(e => e.Id == sentenceId);
            row.TargetSentence.Should().Be("사과가 맛있어요.");
            row.NativeSentence.Should().Be("The apple is tasty.");
        }

        await ledger.UndoAsync(Conversation, proposal.OperationId);
        await using var after = _harness.NewApplicationContext();
        var restored = await after.ExampleSentences.SingleAsync(e => e.Id == sentenceId);
        restored.TargetSentence.Should().Be("사과 맛있어요.");
        restored.NativeSentence.Should().BeNull();
    }

    [PostgresFact]
    public async Task A_due_words_sentence_cannot_be_edited_or_deleted()
    {
        // Its preview would show a sentence the read tool hides until the review is done.
        var wordId = await SeedWordAsync(Owner, "만기", due: true);
        var sentenceId = await SeedSentenceAsync(Owner, wordId, "만기 문장이에요.");
        await using var appDb = _harness.NewApplicationContext();
        var ledger = NewLedger(appDb, Owner);

        var edit = () => ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeExampleSentenceEdit,
            Json(new CoachExampleSentenceEditArgs(sentenceId, TargetSentence: "바뀐 문장이에요.")));
        await edit.Should().ThrowAsync<CoachToolException>();

        var remove = () => ledger.ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeExampleSentenceRemoval,
            Json(new CoachExampleSentenceRemovalArgs(sentenceId)));
        await remove.Should().ThrowAsync<CoachToolException>();

        await using var after = _harness.NewApplicationContext();
        (await after.ExampleSentences.SingleAsync()).TargetSentence.Should().Be("만기 문장이에요.");
    }

    [PostgresFact]
    public async Task Another_learners_sentence_cannot_be_edited_or_deleted()
    {
        var theirWord = await SeedWordAsync(Stranger, "바나나");
        var theirSentence = await SeedSentenceAsync(Stranger, theirWord, "바나나가 좋아요.");
        await using var appDb = _harness.NewApplicationContext();
        var ledger = NewLedger(appDb, Owner);

        var edit = () => ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeExampleSentenceEdit,
            Json(new CoachExampleSentenceEditArgs(theirSentence, TargetSentence: "바뀌었어요.")));
        await edit.Should().ThrowAsync<CoachToolException>();

        var remove = () => ledger.ProposeAsync(
            Conversation, "turn-2", CoachToolNames.ProposeExampleSentenceRemoval,
            Json(new CoachExampleSentenceRemovalArgs(theirSentence)));
        await remove.Should().ThrowAsync<CoachToolException>();

        await using var after = _harness.NewApplicationContext();
        (await after.ExampleSentences.SingleAsync()).TargetSentence.Should().Be("바나나가 좋아요.");
    }

    // ------------------------------------------------------------------ remove

    [PostgresFact]
    public async Task Deleting_a_sentence_needs_confirmation_and_removes_only_it()
    {
        var wordId = await SeedWordAsync(Owner, "사과");
        var drop = await SeedSentenceAsync(Owner, wordId, "사과 하나 주세요.");
        var keep = await SeedSentenceAsync(Owner, wordId, "사과가 맛있어요.");
        await using var appDb = _harness.NewApplicationContext();
        var ledger = NewLedger(appDb, Owner);

        var proposal = await ledger.ProposeAsync(
            Conversation, "turn-1", CoachToolNames.ProposeExampleSentenceRemoval,
            Json(new CoachExampleSentenceRemovalArgs(drop)));
        proposal.ApprovalMode.Should().Be(CoachWriteApprovalModes.Confirm);
        proposal.Lines.Should().Contain("This cannot be undone.");

        var acceptOnly = () => ledger.AcceptAsync(Conversation, proposal.OperationId);
        await acceptOnly.Should().ThrowAsync<Exception>("a protected write needs its confirmation secret");

        var challenge = await ledger.IssueConfirmationAsync(Conversation, proposal.OperationId);
        var receipt = await ledger.ConfirmAsync(Conversation, proposal.OperationId, challenge!.ConfirmationSecret);
        receipt.Status.Should().Be(CoachWriteOperationStatus.Executed);
        receipt.CanUndo.Should().BeFalse();

        await using var after = _harness.NewApplicationContext();
        (await after.ExampleSentences.Select(e => e.Id).ToListAsync()).Should().Equal(keep);
    }
}
