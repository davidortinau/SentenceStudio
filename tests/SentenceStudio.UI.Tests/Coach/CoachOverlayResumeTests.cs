using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Regression cover for the Sam overlay's resume path.
/// </summary>
/// <remarks>
/// <para>
/// The overlay keeps no id in the URL, so the only thing it could resume from inside one circuit
/// is <see cref="CoachWorkspaceState.ConversationId"/>. A page reload throws that circuit away, and
/// the overlay used to read the resulting null as "start a new conversation" — every reload
/// stranded the previous thread, together with any write proposal still pending inside it, and
/// added another empty row to the ledger. Found in browser E2E (`SAM-CONT-01`) on 2026-08-19:
/// four messages were durably stored, the panel came back empty, and Postgres showed a second
/// conversation created seconds later with no messages.
/// </para>
/// <para>
/// These tests are written against <see cref="CoachWorkspaceState.ResumeMostRecentAsync"/> rather
/// than the component, because the component only forwards to it; the resolution order is the part
/// that has to stay true.
/// </para>
/// </remarks>
public class CoachOverlayResumeTests
{
    private static (CoachWorkspaceState State, CoachConversationDirectory Directory, FakeCoachApiClient Client)
        Create(bool durable = true)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = durable };
        var directory = new CoachConversationDirectory(client);
        return (new CoachWorkspaceState(client, directory), directory, client);
    }

    /// <summary>A second workspace over the same server, standing in for a page reload.</summary>
    private static CoachWorkspaceState Reload(FakeCoachApiClient client)
        => new(client, new CoachConversationDirectory(client));

    private static readonly DateTime Older = new(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 8, 19, 17, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ResumeMostRecentAsync_ReturnsToTheStoredConversationAfterAReload()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1", updatedAtUtc: Newer);
        client.Seed("c-1", CoachMessageRole.Learner, "How do I say thank you politely in Korean?");
        client.Seed("c-1", CoachMessageRole.Coach, "The neutral-polite way is 감사합니다.");

        await state.ResumeMostRecentAsync(CoachPresentation.Overlay);
        var createdBefore = client.CreateConversationCalls;

        // Nothing of the first circuit survives a reload; only the server does.
        var reloaded = Reload(client);
        await reloaded.ResumeMostRecentAsync(CoachPresentation.Overlay);

        reloaded.ConversationId.Should().Be("c-1");
        reloaded.Timeline.Select(m => m.ReadableText()).Should().ContainInOrder(
            "How do I say thank you politely in Korean?",
            "The neutral-polite way is 감사합니다.");
        client.CreateConversationCalls.Should().Be(
            createdBefore,
            "a reload resumes the stored thread and must not open another one");
    }

    [Fact]
    public async Task ResumeMostRecentAsync_PicksTheMostRecentlyUpdatedThread()
    {
        var (state, _, client) = Create();
        client.AddConversation("stale", updatedAtUtc: Older);
        client.AddConversation("current", updatedAtUtc: Newer);

        await state.ResumeMostRecentAsync(CoachPresentation.Overlay);

        state.ConversationId.Should().Be("current");
    }

    [Fact]
    public async Task ResumeMostRecentAsync_SkipsClosedThreadsBecauseTheyRefuseNewTurns()
    {
        var (state, _, client) = Create();
        client.AddConversation("open-but-older", updatedAtUtc: Older);
        client.AddConversation("closed-and-newer", updatedAtUtc: Newer, isClosed: true);

        await state.ResumeMostRecentAsync(CoachPresentation.Overlay);

        state.ConversationId.Should().Be(
            "open-but-older",
            "resuming into a closed thread would hand the learner a composer that cannot send");
    }

    [Fact]
    public async Task ResumeMostRecentAsync_StartsOneWhenEveryThreadIsClosed()
    {
        var (state, _, client) = Create();
        client.AddConversation("closed", updatedAtUtc: Newer, isClosed: true);

        await state.ResumeMostRecentAsync(CoachPresentation.Overlay);

        state.ConversationId.Should().NotBe("closed");
        client.CreateConversationCalls.Should().Be(1);
    }

    [Fact]
    public async Task ResumeMostRecentAsync_StartsOneWhenTheLearnerHasNoHistory()
    {
        var (state, _, client) = Create();

        await state.ResumeMostRecentAsync(CoachPresentation.Overlay);

        state.ConversationId.Should().NotBeNull();
        client.CreateConversationCalls.Should().Be(1);
    }

    [Fact]
    public async Task ResumeMostRecentAsync_KeepsTheConversationAlreadyInHand()
    {
        var (state, _, client) = Create();
        client.AddConversation("in-hand", updatedAtUtc: Older);
        client.AddConversation("newer-elsewhere", updatedAtUtc: Newer);

        await state.OpenConversationAsync(CoachPresentation.Overlay, "in-hand");
        state.Close();

        await state.ResumeMostRecentAsync(CoachPresentation.Overlay);

        state.ConversationId.Should().Be(
            "in-hand",
            "a re-open inside a live circuit lands where the learner left off, not on a newer thread");
    }

    [Fact]
    public async Task ResumeMostRecentAsync_FallsBackToTheSessionFlowWhenDurableHistoryIsOff()
    {
        var (state, _, client) = Create(durable: false);

        await state.ResumeMostRecentAsync(CoachPresentation.Overlay);

        state.IsDurableHistoryEnabled.Should().BeFalse();
        state.IsOpen.Should().BeTrue("the learner still gets a working session-only panel");
        client.CreateConversationCalls.Should().Be(0);
    }

    [Fact]
    public async Task ResumeMostRecentAsync_RebuildsAPendingProposalCardAfterAReload()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1", updatedAtUtc: Newer);
        client.Seed("c-1", CoachMessageRole.Learner, "Add 감사합니다 to my vocabulary.");
        client.Seed(
            "c-1",
            CoachMessageRole.Coach,
            "I can add that word.",
            writeOperation: new CoachWriteOperationDto
            {
                OperationId = "op-1",
                ConversationId = "c-1",
                ChangeKind = CoachWriteChangeKind.VocabularyAdd,
                RiskClass = CoachWriteRiskClass.WriteSoft,
                Status = CoachWriteStatus.Proposed,
                ApprovalMode = "accept",
                Summary = "Add 감사합니다",
                Lines = new[] { "감사합니다 — thank you (polite)" },
                ExpiresAtUtc = Newer.AddMinutes(10),
                IsReversible = true
            });

        var reloaded = Reload(client);
        await reloaded.ResumeMostRecentAsync(CoachPresentation.Overlay);

        reloaded.ConversationId.Should().Be("c-1");
        reloaded.Timeline.Any(m => m.WriteOperation?.OperationId == "op-1").Should().BeTrue(
            "a proposal the learner has not answered yet must survive the reload that stranded it");
    }

    // ---------------------------------------------------------------- directory selection

    [Fact]
    public void MostRecentResumableId_IsNullWhenNothingHasLoaded()
    {
        var client = new FakeCoachApiClient();
        var directory = new CoachConversationDirectory(client);

        directory.MostRecentResumableId.Should().BeNull();
    }

    [Fact]
    public async Task MostRecentResumableId_TracksTheNewestOpenThread()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("older", updatedAtUtc: Older);
        client.AddConversation("newer", updatedAtUtc: Newer);
        var directory = new CoachConversationDirectory(client);

        await directory.EnsureLoadedAsync();

        directory.MostRecentResumableId.Should().Be("newer");
    }
}
