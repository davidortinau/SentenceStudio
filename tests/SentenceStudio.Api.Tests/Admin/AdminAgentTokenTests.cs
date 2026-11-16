using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// The agent token: a second way to sign in to the admin API, and the narrowest one.
/// </summary>
/// <remarks>
/// Created only by an admin who has just typed their password, shown once, stored as a digest,
/// good for 30 days, dead the moment it is revoked or its creator stops being an admin, and
/// refused on every route that does not name a permission it holds.
/// </remarks>
public sealed class AdminAgentTokenTests : IDisposable
{
    private const string AdminEmail = "captain@admin-tests.local";

    private readonly AdminApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    // ------------------------------------------------------------------ creating

    [Fact]
    public async Task Creating_a_token_asks_for_the_password_again()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        using var client = _factory.CreateClientFor(admin);

        var response = await client.PostAsJsonAsync(
            "/api/admin/tokens", new AdminTokenCreateRequest("weekday check", [AdminTokenPermissionWire.Reports]));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeOf(response)).Should().Be(AdminFailureCodes.StepUpRequired);
        await _factory.WithAdminDbAsync(async db => (await db.AgentTokens.CountAsync()).Should().Be(0));
    }

    [Fact]
    public async Task A_created_token_is_shown_once_lasts_thirty_days_and_is_stored_only_as_a_digest()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);

        var created = await _factory.IssueTokenAsync(
            admin, AdminTokenPermissionWire.Reports, AdminTokenPermissionWire.Triage);

        created.Token.Should().StartWith(AdminAgentTokenService.Prefix).And.HaveLength(82);
        created.Row.Name.Should().Be("test agent");
        created.Row.Permissions.Should().Equal(AdminTokenPermissionWire.Reports, AdminTokenPermissionWire.Triage);
        created.Row.State.Should().Be(AdminTokenStates.Live);
        created.Row.ExpiresAtUtc.Should().Be(_factory.Time.GetUtcNow().UtcDateTime.AddDays(30));
        created.Row.CreatedByUserId.Should().Be(admin.Id);

        var secret = created.Token[^43..];
        await _factory.WithAdminDbAsync(async db =>
        {
            var row = await db.AgentTokens.AsNoTracking().SingleAsync();
            row.Id.Should().Be(created.Row.Id);
            row.SecretHash.Should().HaveLength(64).And.NotContain(secret);
            row.Name.Should().NotContain(secret);
        });

        // The list never carries the token or its digest.
        using var client = _factory.CreateClientFor(admin);
        var listed = await client.GetStringAsync("/api/admin/tokens");
        listed.Should().Contain(created.Row.Id).And.NotContain(secret).And.NotContainEquivalentOf("hash");

        var audit = await _factory.ReadAuditAsync();
        audit.Single(r => r.Action == AdminActions.TokensCreate).Outcome.Should().Be(AdminAuditOutcome.Succeeded);
        audit.Should().NotContain(r => r.ActorId.Contains(secret) || (r.TargetId ?? "").Contains(secret));
    }

    [Theory]
    [InlineData("""{"name":"","permissions":["reports"]}""")]
    [InlineData("""{"name":"ok","permissions":[]}""")]
    [InlineData("""{"name":"ok"}""")]
    [InlineData("""{"name":"ok","permissions":["reports","reports"]}""")]
    [InlineData("""{"name":"ok","permissions":["accounts"]}""")]
    [InlineData("""{"name":"ok","permissions":["Reports"]}""")]
    [InlineData("""{"name":"ok","permissions":[null]}""")]
    [InlineData("""{"name":"line\nbreak","permissions":["reports"]}""")]
    [InlineData("""not json""")]
    [InlineData("""[]""")]
    public async Task A_token_request_that_is_not_exactly_right_creates_nothing(string body)
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        using var client = _factory.CreateClientFor(admin);
        await AdminApiFactory.ConfirmPasswordAsync(client);

        var response = await client.PostAsync(
            "/api/admin/tokens", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().Be(AdminFailureCodes.InvalidRequest);
        await _factory.WithAdminDbAsync(async db => (await db.AgentTokens.CountAsync()).Should().Be(0));
    }

    [Fact]
    public async Task A_name_longer_than_the_column_is_refused()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        using var client = _factory.CreateClientFor(admin);
        await AdminApiFactory.ConfirmPasswordAsync(client);

        var response = await client.PostAsJsonAsync("/api/admin/tokens",
            new AdminTokenCreateRequest(new string('n', AdminAgentTokenLimits.MaxNameLength + 1), [AdminTokenPermissionWire.Reports]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task No_more_than_ten_tokens_are_live_at_once_and_a_revoked_one_makes_room()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        using var client = _factory.CreateClientFor(admin);
        await AdminApiFactory.ConfirmPasswordAsync(client);

        var ids = new List<string>();
        for (var i = 0; i < AdminAgentTokenLimits.MaxLiveTokens; i++)
        {
            var created = await (await client.PostAsJsonAsync("/api/admin/tokens",
                    new AdminTokenCreateRequest($"agent {i}", [AdminTokenPermissionWire.Reports])))
                .Content.ReadFromJsonAsync<AdminTokenCreated>();
            ids.Add(created!.Row.Id);
        }

        var refused = await client.PostAsJsonAsync("/api/admin/tokens",
            new AdminTokenCreateRequest("one too many", [AdminTokenPermissionWire.Reports]));
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(refused)).Should().Be(AdminFailureCodes.TooManyTokens);

        (await client.PostAsync($"/api/admin/tokens/{ids[0]}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/admin/tokens",
            new AdminTokenCreateRequest("room now", [AdminTokenPermissionWire.Reports])))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ signing in

    [Fact]
    public async Task A_live_token_signs_in_with_no_account_and_is_recorded_as_a_token()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var created = await _factory.IssueTokenAsync(admin, AdminTokenPermissionWire.Reports);
        using var client = _factory.CreateClientForToken(created.Token);

        var self = await client.GetFromJsonAsync<AdminTokenSelf>("/api/admin/token");

        self!.Id.Should().Be(created.Row.Id);
        self.Name.Should().Be("test agent");
        self.Permissions.Should().Equal(AdminTokenPermissionWire.Reports);
        self.ExpiresAtUtc.Should().Be(created.Row.ExpiresAtUtc);
        self.NowUtc.Should().Be(_factory.Time.GetUtcNow().UtcDateTime);

        var row = (await _factory.ReadAuditAsync()).Single(r => r.Action == AdminActions.TokenSelfRead);
        row.ActorKind.Should().Be(AdminActorKind.AgentToken);
        row.ActorId.Should().Be(created.Row.Id);
        row.Outcome.Should().Be(AdminAuditOutcome.Succeeded);
    }

    [Fact]
    public async Task An_admin_account_is_not_a_token()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        using var client = _factory.CreateClientFor(admin);

        (await client.GetAsync("/api/admin/token")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anything_that_is_not_exactly_a_live_token_is_a_bare_404_and_leaves_no_audit_row()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var created = await _factory.IssueTokenAsync(admin, AdminTokenPermissionWire.Reports);
        var auditBefore = (await _factory.ReadAuditAsync()).Count;

        var token = created.Token;
        var wrongSecret = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');
        var wrongId = AdminAgentTokenService.Prefix + new string('0', 32) + token[38..];
        string[] presented =
        [
            wrongSecret,
            wrongId,
            token[..^1],
            token + "x",
            token.ToUpperInvariant(),
            "Bearer " + token,
            token.Replace(AdminAgentTokenService.Prefix, "ssxxx_"),
            "",
            " "
        ];

        foreach (var value in presented)
        {
            using var client = _factory.CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(AdminAgentTokenService.HeaderName, value);

            var response = await client.GetAsync("/api/admin/token");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"'{value}' is not the token");
            (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        }

        (await _factory.ReadAuditAsync()).Count.Should().Be(auditBefore);
    }

    [Fact]
    public async Task A_bad_token_is_refused_even_beside_a_valid_admin_sign_in()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        using var client = _factory.CreateClientFor(admin);
        (await client.GetAsync("/api/admin/session")).StatusCode.Should().Be(HttpStatusCode.OK);

        client.DefaultRequestHeaders.Add(AdminAgentTokenService.HeaderName, "ssadm_not-a-token");

        (await client.GetAsync("/api/admin/session")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_request_that_carries_the_token_twice_is_refused()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var created = await _factory.IssueTokenAsync(admin, AdminTokenPermissionWire.Reports);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminAgentTokenService.HeaderName, [created.Token, created.Token]);

        (await client.GetAsync("/api/admin/token")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ what a token may reach

    [Theory]
    [InlineData("GET", "/api/admin/session")]
    [InlineData("POST", "/api/admin/step-up")]
    [InlineData("GET", "/api/admin/audit")]
    [InlineData("GET", "/api/admin/tokens")]
    [InlineData("POST", "/api/admin/tokens")]
    [InlineData("POST", "/api/admin/tokens/0123456789abcdef0123456789abcdef/revoke")]
    [InlineData("GET", "/api/admin/sync-health/app")]
    [InlineData("GET", "/api/admin/sync-health/learners")]
    [InlineData("POST", "/api/admin/sync-health/learners/search")]
    [InlineData("GET", "/api/admin/usage/sam")]
    [InlineData("GET", "/api/admin/usage/video-imports")]
    [InlineData("GET", "/api/admin/usage/part-of-speech")]
    public async Task A_token_holding_every_permission_is_refused_on_every_route_that_names_none(
        string method, string route)
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var created = await _factory.IssueTokenAsync(
            admin, AdminTokenPermissionWire.Reports, AdminTokenPermissionWire.LearnerContent, AdminTokenPermissionWire.Triage);
        using var client = _factory.CreateClientForToken(created.Token);
        var tokensBefore = 1;

        using var request = new HttpRequestMessage(new HttpMethod(method), route)
        {
            Content = JsonContent.Create(new { password = AdminApiFactory.Password, name = "x", permissions = new[] { "reports" }, q = "a" })
        };
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeOf(response)).Should().Be(AdminFailureCodes.NotAllowedForToken);

        var row = (await _factory.ReadAuditAsync()).Last();
        row.ActorKind.Should().Be(AdminActorKind.AgentToken);
        row.ActorId.Should().Be(created.Row.Id);
        row.Outcome.Should().Be(AdminAuditOutcome.Failed);
        row.StatusCode.Should().Be(403);
        row.FailureCode.Should().Be(AdminFailureCodes.NotAllowedForToken);

        await _factory.WithAdminDbAsync(async db =>
        {
            (await db.AgentTokens.CountAsync()).Should().Be(tokensBefore, "a token can never create a token");
            (await db.AgentTokens.SingleAsync()).RevokedAtUtc.Should().BeNull("a token can never revoke a token");
        });
    }

    [Theory]
    [InlineData("/api/admin/access")]
    [InlineData("/api/admin/reports/new-count")]
    [InlineData("/api/admin/no-such-route")]
    public async Task A_token_gets_404_on_the_routes_that_leave_no_audit_row(string route)
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var created = await _factory.IssueTokenAsync(admin, AdminTokenPermissionWire.Reports);
        using var client = _factory.CreateClientForToken(created.Token);

        (await client.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(AdminTokenPermissionWire.Reports, true, false, false)]
    [InlineData(AdminTokenPermissionWire.LearnerContent, false, true, false)]
    [InlineData(AdminTokenPermissionWire.Triage, false, false, true)]
    public async Task A_token_does_only_what_its_permission_names(
        string permission, bool mayRead, bool mayOpen, bool mayTriage)
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var learner = await _factory.SeedAccountAsync("learner@admin-tests.local");
        var reportId = await _factory.SeedFlaggedAnswerAsync(learner, "QUESTION-TEXT", "ANSWER-TEXT");
        var created = await _factory.IssueTokenAsync(admin, permission);
        using var client = _factory.CreateClientForToken(created.Token);

        var expectations = new (Func<Task<HttpResponseMessage>> Send, bool Allowed)[]
        {
            (() => client.GetAsync("/api/admin/reports/sam"), mayRead),
            (() => client.GetAsync($"/api/admin/reports/sam/{reportId}"), mayRead),
            (() => client.GetAsync("/api/admin/reports/github"), mayRead),
            (() => client.PostAsync($"/api/admin/reports/sam/{reportId}/exchange", null), mayOpen),
            (() => client.PostAsync("/api/admin/reports/github/screen-states/fs-000000000000", null), mayOpen),
            (() => client.PostAsJsonAsync($"/api/admin/reports/sam/{reportId}/triage",
                new AdminSamTriageRequest(AdminReportCodes.StatusReviewed, null)), mayTriage)
        };

        foreach (var (send, allowed) in expectations)
        {
            var response = await send();
            if (allowed)
            {
                // 404 is the one screen state that does not exist: admitted, and nothing there.
                response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
            }
            else
            {
                response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await CodeOf(response)).Should().Be(AdminFailureCodes.NotAllowedForToken);
                (await response.Content.ReadAsStringAsync()).Should().NotContain("QUESTION-TEXT").And.NotContain("ANSWER-TEXT");
            }
        }
    }

    // ------------------------------------------------------------------ when a token stops

    [Fact]
    public async Task A_revoked_token_stops_at_once_and_revoking_twice_changes_nothing()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var created = await _factory.IssueTokenAsync(admin, AdminTokenPermissionWire.Reports);
        using var tokenClient = _factory.CreateClientForToken(created.Token);
        using var adminClient = _factory.CreateClientFor(admin);
        (await tokenClient.GetAsync("/api/admin/token")).StatusCode.Should().Be(HttpStatusCode.OK);

        var revoked = await (await adminClient.PostAsync($"/api/admin/tokens/{created.Row.Id}/revoke", null))
            .Content.ReadFromJsonAsync<AdminTokenRow>();
        revoked!.State.Should().Be(AdminTokenStates.Revoked);
        revoked.RevokedAtUtc.Should().Be(_factory.Time.GetUtcNow().UtcDateTime);

        (await tokenClient.GetAsync("/api/admin/token")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        _factory.Time.Advance(TimeSpan.FromHours(1));
        var again = await (await adminClient.PostAsync($"/api/admin/tokens/{created.Row.Id}/revoke", null))
            .Content.ReadFromJsonAsync<AdminTokenRow>();
        again!.RevokedAtUtc.Should().Be(revoked.RevokedAtUtc);

        var row = (await _factory.ReadAuditAsync()).First(r => r.Action == AdminActions.TokensRevoke);
        row.TargetKind.Should().Be(AdminTargetKinds.Token);
        row.TargetId.Should().Be(created.Row.Id);
    }

    [Fact]
    public async Task Revoking_a_token_that_does_not_exist_is_a_404()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        using var client = _factory.CreateClientFor(admin);

        (await client.PostAsync("/api/admin/tokens/0123456789abcdef0123456789abcdef/revoke", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_token_stops_after_thirty_days()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var created = await _factory.IssueTokenAsync(admin, AdminTokenPermissionWire.Reports);
        using var client = _factory.CreateClientForToken(created.Token);

        _factory.Time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1));
        (await client.GetAsync("/api/admin/token")).StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        (await client.GetAsync("/api/admin/token")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var adminClient = _factory.CreateClientFor(admin);
        var listed = await adminClient.GetFromJsonAsync<AdminTokenList>("/api/admin/tokens");
        listed!.Items.Single().State.Should().Be(AdminTokenStates.Expired);
        listed.LifetimeDays.Should().Be(30);
    }

    [Fact]
    public async Task A_token_stops_when_its_creator_stops_being_an_admin_and_works_again_when_they_are_one()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var created = await _factory.IssueTokenAsync(admin, AdminTokenPermissionWire.Reports);
        using var client = _factory.CreateClientForToken(created.Token);
        (await client.GetAsync("/api/admin/token")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Off the list: nobody is an admin, and nothing is admitted.
        _factory.SetAdminEmails();
        (await client.GetAsync("/api/admin/token")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        _factory.SetAdminEmails(AdminEmail);
        (await client.GetAsync("/api/admin/token")).StatusCode.Should().Be(HttpStatusCode.OK);

        await _factory.RevokeRoleAsync(admin);
        (await client.GetAsync("/api/admin/token")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_last_used_time_is_written_at_most_once_in_five_minutes()
    {
        var admin = await _factory.SeedAdminAsync(AdminEmail);
        var created = await _factory.IssueTokenAsync(admin, AdminTokenPermissionWire.Reports);
        using var client = _factory.CreateClientForToken(created.Token);
        var first = _factory.Time.GetUtcNow().UtcDateTime;

        async Task<DateTime?> LastUsedAsync()
        {
            DateTime? value = null;
            await _factory.WithAdminDbAsync(async db =>
                value = (await db.AgentTokens.AsNoTracking().SingleAsync()).LastUsedAtUtc);
            return value;
        }

        (await LastUsedAsync()).Should().BeNull();
        await client.GetAsync("/api/admin/token");
        (await LastUsedAsync()).Should().Be(first);

        _factory.Time.Advance(TimeSpan.FromMinutes(4));
        await client.GetAsync("/api/admin/token");
        (await LastUsedAsync()).Should().Be(first);

        _factory.Time.Advance(TimeSpan.FromMinutes(2));
        await client.GetAsync("/api/admin/token");
        (await LastUsedAsync()).Should().Be(first.AddMinutes(6));
    }
}

/// <summary>The permission codes: what is written on the wire and stored on the row.</summary>
public sealed class AdminTokenPermissionTests
{
    [Fact]
    public void The_stored_numbers_are_pinned()
    {
        ((int)AdminTokenPermissions.None).Should().Be(0);
        ((int)AdminTokenPermissions.Reports).Should().Be(1);
        ((int)AdminTokenPermissions.LearnerContent).Should().Be(2);
        ((int)AdminTokenPermissions.Triage).Should().Be(4);
        Enum.GetValues<AdminTokenPermissions>().Should().HaveCount(4, "a new permission is added here on purpose");
        AdminTokenPermissionWire.All.Should().Be((AdminTokenPermissions)7);
    }

    [Fact]
    public void Codes_round_trip_and_an_unknown_bit_names_nothing()
    {
        AdminTokenPermissionWire.TryParse(["triage", "reports"], out var parsed).Should().BeTrue();
        parsed.Should().Be(AdminTokenPermissions.Reports | AdminTokenPermissions.Triage);
        AdminTokenPermissionWire.Of(parsed).Should().Equal("reports", "triage");
        AdminTokenPermissionWire.Of((AdminTokenPermissions)8).Should().BeEmpty();
    }

    [Fact]
    public void An_admin_account_is_allowed_everywhere_and_an_unknown_kind_nowhere()
    {
        var admin = new AdminActor(AdminActorKind.Admin, "u", null, null, null);
        var unknown = new AdminActor((AdminActorKind)99, "u", null, null, null);

        AdminTokenAccess.IsAllowed(null, admin).Should().BeTrue();
        AdminTokenAccess.IsAllowed(null, unknown).Should().BeFalse();
    }
}
