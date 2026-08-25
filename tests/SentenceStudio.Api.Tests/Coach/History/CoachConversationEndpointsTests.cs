using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The HTTP surface of durable conversations: routes, headers, ownership, and the shape of
/// what comes back over the wire.
/// </summary>
/// <remarks>
/// The service tests prove the behaviour. These prove a client can actually reach it — that the
/// routes are mapped where the contract says, that the required headers are read, and that one
/// learner's conversations are unreachable to another even with a valid token.
/// </remarks>
public class CoachConversationEndpointsTests
{
    private const string CohortUser = "coach-cohort-user";
    private const string OtherUser = "coach-other-user";
    private const string Root = "/api/v1/coach/conversations";

    private static CoachApiFactory NewFactory() =>
        new() { CoachEnabled = true, CohortUserProfileId = CohortUser, DurableHistory = true };

    [Fact]
    public async Task Creating_a_conversation_requires_an_idempotency_key()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory, CohortUser);

        using var response = await client.PostAsJsonAsync(Root, new StartCoachConversationRequest());

        response.StatusCode.Should().Be(
            HttpStatusCode.UnprocessableEntity,
            "a create without a key cannot be retried safely, so it is refused rather than duplicated");
    }

    [Fact]
    public async Task Creating_a_conversation_returns_it_and_a_repeat_returns_the_same_one()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory, CohortUser);

        var key = Guid.NewGuid().ToString("N");
        var first = await CreateAsync(client, key);
        var second = await CreateAsync(client, key);

        first.ConversationId.Should().NotBeNullOrWhiteSpace();
        second.ConversationId.Should().Be(first.ConversationId, "a retried create is the same conversation, not a second one");
        first.Title.Should().NotBeNullOrWhiteSpace("a conversation the learner cannot recognise is not listable");
    }

    [Fact]
    public async Task An_unauthenticated_request_is_refused_on_every_route()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        foreach (var request in new[]
                 {
                     new HttpRequestMessage(HttpMethod.Get, Root),
                     new HttpRequestMessage(HttpMethod.Post, Root),
                     new HttpRequestMessage(HttpMethod.Get, $"{Root}/anything"),
                     new HttpRequestMessage(HttpMethod.Get, $"{Root}/anything/messages"),
                     new HttpRequestMessage(HttpMethod.Delete, $"{Root}/anything"),
                     new HttpRequestMessage(HttpMethod.Get, $"{Root}/anything/export")
                 })
        {
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, request.RequestUri!.ToString());
        }
    }

    [Fact]
    public async Task Listing_returns_only_the_callers_own_conversations()
    {
        await using var factory = NewFactory();

        using var owner = Authenticated(factory, CohortUser);
        var mine = await CreateAsync(owner, Guid.NewGuid().ToString("N"));

        using var stranger = Authenticated(factory, OtherUser);
        using var response = await stranger.GetAsync(Root);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var page = await response.Content.ReadFromJsonAsync<CoachConversationPageDto>();
            page!.Items.Should().NotContain(c => c.ConversationId == mine.ConversationId);
        }
        else
        {
            // Outside the cohort the coach is simply absent, which is also a correct answer.
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Another_learner_cannot_read_a_conversation_they_do_not_own()
    {
        await using var factory = NewFactory();

        using var owner = Authenticated(factory, CohortUser);
        var mine = await CreateAsync(owner, Guid.NewGuid().ToString("N"));

        using var stranger = Authenticated(factory, OtherUser);
        using var response = await stranger.GetAsync($"{Root}/{mine.ConversationId}");

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "a conversation someone else owns is indistinguishable from one that never existed");
    }

    [Fact]
    public async Task Reading_messages_returns_an_empty_page_for_a_new_conversation()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory, CohortUser);

        var created = await CreateAsync(client, Guid.NewGuid().ToString("N"));
        using var response = await client.GetAsync($"{Root}/{created.ConversationId}/messages");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<CoachMessagePageDto>();
        page!.ConversationId.Should().Be(created.ConversationId);
        page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tampered_cursor_is_refused_rather_than_guessed_at()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory, CohortUser);

        var created = await CreateAsync(client, Guid.NewGuid().ToString("N"));
        using var response = await client.GetAsync($"{Root}/{created.ConversationId}/messages?before=not-a-real-cursor");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Renaming_a_conversation_changes_the_title_the_learner_sees()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory, CohortUser);

        var created = await CreateAsync(client, Guid.NewGuid().ToString("N"));

        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"{Root}/{created.ConversationId}")
        {
            Content = JsonContent.Create(new UpdateCoachConversationRequest { Title = "Politeness levels" })
        };

        using var response = await client.SendAsync(patch);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<CoachConversationDto>();
        updated!.Title.Should().Be("Politeness levels");
    }

    [Fact]
    public async Task Renaming_with_a_stale_version_is_a_conflict_not_a_silent_overwrite()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory, CohortUser);

        var created = await CreateAsync(client, Guid.NewGuid().ToString("N"));

        using var first = new HttpRequestMessage(HttpMethod.Patch, $"{Root}/{created.ConversationId}")
        {
            Content = JsonContent.Create(new UpdateCoachConversationRequest { Title = "First" })
        };
        (await client.SendAsync(first)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var stale = new HttpRequestMessage(HttpMethod.Patch, $"{Root}/{created.ConversationId}")
        {
            Content = JsonContent.Create(new UpdateCoachConversationRequest
            {
                Title = "Second",
                ExpectedStateVersion = created.StateVersion
            })
        };

        using var response = await client.SendAsync(stale);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Deleting_a_conversation_hides_it_and_repeats_are_harmless()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory, CohortUser);

        var created = await CreateAsync(client, Guid.NewGuid().ToString("N"));

        using var first = await client.DeleteAsync($"{Root}/{created.ConversationId}");
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var second = await client.DeleteAsync($"{Root}/{created.ConversationId}");
        second.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "deleting twice is the same outcome as deleting once");

        using var read = await client.GetAsync($"{Root}/{created.ConversationId}");
        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Exporting_streams_the_conversation_to_its_owner()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory, CohortUser);

        var created = await CreateAsync(client, Guid.NewGuid().ToString("N"));

        using var response = await client.GetAsync($"{Root}/{created.ConversationId}/export");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(created.ConversationId);
    }

    [Fact]
    public async Task Exporting_as_markdown_returns_markdown()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory, CohortUser);

        var created = await CreateAsync(client, Guid.NewGuid().ToString("N"));

        using var response = await client.GetAsync($"{Root}/{created.ConversationId}/export?format=Markdown");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task Another_learner_cannot_export_a_conversation_they_do_not_own()
    {
        await using var factory = NewFactory();

        using var owner = Authenticated(factory, CohortUser);
        var mine = await CreateAsync(owner, Guid.NewGuid().ToString("N"));

        using var stranger = Authenticated(factory, OtherUser);
        using var response = await stranger.GetAsync($"{Root}/{mine.ConversationId}/export");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_operation_is_not_found_rather_than_invented()
    {
        await using var factory = NewFactory();
        using var client = Authenticated(factory, CohortUser);

        var created = await CreateAsync(client, Guid.NewGuid().ToString("N"));

        using var response = await client.GetAsync($"{Root}/{created.ConversationId}/operations/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task With_history_off_the_conversation_routes_are_simply_absent()
    {
        await using var factory = new CoachApiFactory
        {
            CoachEnabled = true,
            CohortUserProfileId = CohortUser,
            DurableHistory = false
        };

        using var client = Authenticated(factory, CohortUser);

        using var post = new HttpRequestMessage(HttpMethod.Post, Root)
        {
            Content = JsonContent.Create(new StartCoachConversationRequest())
        };
        post.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        using var response = await client.SendAsync(post);

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "a feature that is off looks like a feature that does not exist");
        factory.ChatClient.CallCount.Should().Be(0);
    }

    private static async Task<CoachConversationDto> CreateAsync(HttpClient client, string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Root)
        {
            Content = JsonContent.Create(new StartCoachConversationRequest())
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<CoachConversationDto>())!;
    }

    private static HttpClient Authenticated(CoachApiFactory factory, string userProfileId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                TestJwtGenerator.GenerateToken(userProfileId: userProfileId));
        return client;
    }
}
