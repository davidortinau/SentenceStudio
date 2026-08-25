using System.Net;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Behavioural tests for the conversation shelf: how it decides durable history exists, how it
/// orders and pages the list, and what a learner is told when a lifecycle action does not land.
/// </summary>
public class CoachConversationDirectoryTests
{
    private static (CoachConversationDirectory Directory, FakeCoachApiClient Client) Create(
        bool durable = true)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = durable };
        return (new CoachConversationDirectory(client), client);
    }

    // ---------------------------------------------------------------- feature detection

    [Fact]
    public async Task EnsureLoadedAsync_ReadsA404AsFeatureOffRatherThanAsAnError()
    {
        var (directory, _) = Create(durable: false);

        var availability = await directory.EnsureLoadedAsync();

        availability.Should().Be(CoachDurableHistoryAvailability.Unavailable);
        directory.IsDurableHistoryAvailable.Should().BeFalse();
        directory.ErrorKey.Should().BeNull("a feature that is off is not a failure the learner can act on");
    }

    [Fact]
    public async Task EnsureLoadedAsync_ReportsAvailableWhenTheListRouteAnswers()
    {
        var (directory, client) = Create();
        client.AddConversation("c-1");

        var availability = await directory.EnsureLoadedAsync();

        availability.Should().Be(CoachDurableHistoryAvailability.Available);
        directory.IsDurableHistoryAvailable.Should().BeTrue();
        directory.Conversations.Should().HaveCount(1);
    }

    [Fact]
    public async Task EnsureLoadedAsync_ProbesOnceAndThenTrustsTheAnswer()
    {
        var (directory, client) = Create();

        await directory.EnsureLoadedAsync();
        await directory.EnsureLoadedAsync();

        client.ListConversationCalls.Should().Be(1);
    }

    // ---------------------------------------------------------------- ordering

    [Fact]
    public async Task RefreshAsync_OrdersByMostRecentlyUpdatedFirst()
    {
        var (directory, client) = Create();
        client.AddConversation("older", updatedAtUtc: new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc));
        client.AddConversation("newest", updatedAtUtc: new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc));
        client.AddConversation("middle", updatedAtUtc: new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc));

        await directory.RefreshAsync();

        directory.Conversations.Select(c => c.ConversationId)
            .Should().ContainInOrder("newest", "middle", "older");
    }

    // ---------------------------------------------------------------- paging

    [Fact]
    public async Task LoadMoreAsync_AppendsTheNextPageAndKeepsTheOrder()
    {
        var (directory, client) = Create();

        client.OnListConversations = (_, cursor) => cursor is null
            ? Page(NewestFirst(("a", 3), ("b", 2)), nextCursor: "cursor-2")
            : Page(NewestFirst(("c", 1)), nextCursor: null);

        await directory.RefreshAsync();
        directory.HasMore.Should().BeTrue();

        await directory.LoadMoreAsync();

        directory.Conversations.Select(c => c.ConversationId).Should().ContainInOrder("a", "b", "c");
        directory.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task LoadMoreAsync_RecoversFromARejectedCursorByReloadingCleanly()
    {
        var (directory, client) = Create();
        var calls = 0;

        client.OnListConversations = (_, cursor) =>
        {
            calls++;

            if (cursor is null)
            {
                // The second pass sees the shorter list that made the cursor stale in the
                // first place, which is the ordinary reason a cursor stops resolving.
                return calls == 1
                    ? Page(NewestFirst(("a", 2)), nextCursor: "stale")
                    : Page(NewestFirst(("a", 2)), nextCursor: null);
            }

            throw new CoachApiException(
                HttpStatusCode.BadRequest, CoachProblemTypes.InvalidCursor, null, null);
        };

        await directory.RefreshAsync();
        await directory.LoadMoreAsync();

        // The stale cursor is dropped and the newest page is fetched again, so the learner is
        // looking at a list that is actually valid rather than an error they cannot act on.
        calls.Should().Be(3);
        directory.ErrorKey.Should().BeNull();
        directory.HasMore.Should().BeFalse();
        directory.Conversations.Should().HaveCount(1);
    }

    [Fact]
    public async Task LoadMoreAsync_DoesNothingWithoutACursor()
    {
        var (directory, client) = Create();
        client.AddConversation("only");

        await directory.RefreshAsync();
        await directory.LoadMoreAsync();

        client.ListConversationCalls.Should().Be(1);
    }

    // ---------------------------------------------------------------- create

    [Fact]
    public async Task CreateAsync_AlwaysMakesANewThreadAndSelectsIt()
    {
        var (directory, client) = Create();
        await directory.RefreshAsync();

        var first = await directory.CreateAsync();
        var second = await directory.CreateAsync();

        first!.ConversationId.Should().NotBe(second!.ConversationId);
        directory.SelectedConversationId.Should().Be(second.ConversationId);
        directory.Conversations.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateAsync_MintsAFreshIdempotencyKeyForEachDeliberateAsk()
    {
        var (directory, client) = Create();

        await directory.CreateAsync();
        await directory.CreateAsync();

        // The key exists to make a retry of one request safe. It must not collapse two separate
        // decisions by the learner into a single thread.
        client.ConversationCreateRequests.Should().HaveCount(2);
        client.ConversationCreateRequests[0].IdempotencyKey
            .Should().NotBe(client.ConversationCreateRequests[1].IdempotencyKey);
        client.ConversationCreateRequests.Should().AllSatisfy(
            r => r.IdempotencyKey.Should().NotBeNullOrWhiteSpace());
    }

    // ---------------------------------------------------------------- rename / close / reopen

    [Fact]
    public async Task RenameAsync_StoresTheLearnerTitleAndMarksItsOrigin()
    {
        var (directory, client) = Create();
        client.AddConversation("c-1");
        await directory.RefreshAsync();

        var ok = await directory.RenameAsync("c-1", "Ordering coffee");

        ok.Should().BeTrue();
        directory.Find("c-1")!.Title.Should().Be("Ordering coffee");
        directory.Find("c-1")!.TitleOrigin.Should().Be(CoachConversationTitleOrigin.Learner);
    }

    [Fact]
    public async Task RenameAsync_SendsTheVersionItLastSawSoAStaleWriteIsRefused()
    {
        var (directory, client) = Create();
        client.AddConversation("c-1");
        await directory.RefreshAsync();

        await directory.RenameAsync("c-1", "First");

        client.ConversationUpdates[0].ExpectedStateVersion.Should().Be(1);
    }

    [Fact]
    public async Task RenameAsync_OnAVersionConflictExplainsItAndRefetchesWhatIsActuallyStored()
    {
        var (directory, client) = Create();
        client.AddConversation("c-1");
        await directory.RefreshAsync();

        client.OnUpdateConversation = (_, _) => throw new CoachApiException(
            HttpStatusCode.Conflict, CoachProblemTypes.ConversationStateConflict, null, null);

        // Somebody else renamed it on another device in the meantime.
        client.Conversations[0] = client.AddConversation("c-1", title: "Renamed elsewhere");
        client.Conversations.RemoveAt(1);

        var ok = await directory.RenameAsync("c-1", "Mine");

        ok.Should().BeFalse();
        directory.ErrorKey.Should().Be("Coach_ConversationConflict");
        directory.Find("c-1")!.Title.Should().Be("Renamed elsewhere",
            "the learner should decide again against the title that is really stored");
    }

    [Fact]
    public async Task RenameAsync_OnAnOwnerMismatchSaysTheConversationIsGoneAndDropsIt()
    {
        var (directory, client) = Create();
        client.AddConversation("c-1");
        await directory.RefreshAsync();

        client.OnUpdateConversation = (_, _) => throw new CoachApiException(
            HttpStatusCode.NotFound, CoachProblemTypes.ConversationNotFound, null, null);

        var ok = await directory.RenameAsync("c-1", "Mine");

        ok.Should().BeFalse();
        directory.ErrorKey.Should().Be("Coach_ConversationGone");
        directory.Find("c-1").Should().BeNull();
    }

    [Fact]
    public async Task CloseAsync_ThenReopenAsync_RoundTripsWithoutLosingTheConversation()
    {
        var (directory, client) = Create();
        client.AddConversation("c-1");
        await directory.RefreshAsync();

        await directory.CloseAsync("c-1");
        directory.Find("c-1")!.IsClosed.Should().BeTrue();

        await directory.ReopenAsync("c-1");
        directory.Find("c-1")!.IsClosed.Should().BeFalse();
        directory.Conversations.Should().HaveCount(1);
    }

    // ---------------------------------------------------------------- delete

    [Fact]
    public async Task DeleteAsync_RemovesTheRowFromTheShelf()
    {
        var (directory, client) = Create();
        client.AddConversation("c-1");
        client.AddConversation("c-2");
        await directory.RefreshAsync();

        var ok = await directory.DeleteAsync("c-1");

        ok.Should().BeTrue();
        client.DeleteConversationCalls.Should().Be(1);
        directory.Conversations.Select(c => c.ConversationId).Should().ContainSingle().Which.Should().Be("c-2");
    }

    // ---------------------------------------------------------------- export

    [Fact]
    public async Task ExportAsync_HandsBackAStreamAndNamesTheFileByFormat()
    {
        var (directory, client) = Create();
        client.AddConversation("c-1");
        await directory.RefreshAsync();

        await using var stream = await directory.ExportAsync("c-1", CoachExportFormat.Markdown);

        stream.Should().NotBeNull();
        client.Exports.Should().ContainSingle().Which.Format.Should().Be(CoachExportFormat.Markdown);

        CoachConversationDirectory.ExportFileName("c-1", CoachExportFormat.Markdown)
            .Should().EndWith(".md");
        CoachConversationDirectory.ExportFileName("c-1", CoachExportFormat.Json)
            .Should().EndWith(".json");
    }

    // ---------------------------------------------------------------- offline

    [Fact]
    public async Task RefreshAsync_ReportsOfflineSeparatelyFromAServerRefusal()
    {
        var (directory, client) = Create();
        client.OnListConversations = (_, _) => throw new HttpRequestException("no network");

        await directory.RefreshAsync();

        directory.IsOffline.Should().BeTrue();
        directory.ErrorKey.Should().Be("Coach_ConversationsOffline");
        directory.Availability.Should().NotBe(CoachDurableHistoryAvailability.Unavailable,
            "a dropped connection is not evidence the feature is switched off");
    }

    [Fact]
    public async Task RefreshAsync_SurfacesAServerFailureAsALoadError()
    {
        var (directory, client) = Create();
        client.OnListConversations = (_, _) => throw new CoachApiException(
            HttpStatusCode.InternalServerError, CoachProblemTypes.ToolFailure, null, null);

        await directory.RefreshAsync();

        directory.ErrorKey.Should().Be("Coach_ConversationsLoadFailed");
        directory.IsOffline.Should().BeFalse();
    }

    // ---------------------------------------------------------------- selection

    [Fact]
    public async Task Select_NotifiesOnceAndOnlyWhenTheSelectionActuallyChanges()
    {
        var (directory, client) = Create();
        client.AddConversation("c-1");
        await directory.RefreshAsync();

        var notifications = 0;
        directory.Changed += () => notifications++;

        directory.Select("c-1");
        directory.Select("c-1");

        notifications.Should().Be(1);
        directory.Selected!.ConversationId.Should().Be("c-1");
    }

    // ---------------------------------------------------------------- helpers

    private static CoachConversationPageDto Page(
        IEnumerable<CoachConversationDto> items,
        string? nextCursor) => new()
        {
            Items = items.ToList(),
            NextCursor = nextCursor
        };

    private static List<CoachConversationDto> NewestFirst(params (string Id, int Day)[] rows)
        => rows.Select(row => new CoachConversationDto
        {
            ConversationId = row.Id,
            Title = string.Empty,
            TitleOrigin = CoachConversationTitleOrigin.Generated,
            TargetLanguageCode = "ko",
            CreatedAtUtc = new DateTime(2026, 1, row.Day, 8, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 1, row.Day, 8, 0, 0, DateTimeKind.Utc),
            HistoryStartsAtUtc = new DateTime(2026, 1, row.Day, 8, 0, 0, DateTimeKind.Utc),
            MessageCount = 0,
            StateVersion = 1,
            HasActiveCheckpoint = false,
            IsClosed = false
        }).ToList();
}
