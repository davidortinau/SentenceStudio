using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// What an admitted admin request leaves behind, and how the password-again proof is earned.
/// </summary>
public sealed class AdminAuditAndStepUpTests : IDisposable
{
    private readonly AdminApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    // ------------------------------------------------------------------ audit

    [Fact]
    public async Task Each_admin_request_leaves_one_audit_row_naming_who_what_and_how_it_ended()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        (await client.GetAsync("/api/admin/session")).StatusCode.Should().Be(HttpStatusCode.OK);

        var row = (await _factory.ReadAuditAsync()).Should().ContainSingle().Subject;
        row.ActorKind.Should().Be(AdminActorKind.Admin);
        row.ActorId.Should().Be(admin.Id);
        row.Action.Should().Be(AdminActions.SessionRead);
        row.Outcome.Should().Be(AdminAuditOutcome.Succeeded);
        row.StatusCode.Should().Be(200);
        row.FailureCode.Should().BeNull();
        row.TargetKind.Should().BeNull();
        row.TargetId.Should().BeNull();
        row.OccurredAtUtc.Should().Be(_factory.Time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Refused_callers_and_the_link_probe_leave_no_audit_row()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        var learner = await _factory.SeedAccountAsync("learner@admin-tests.local");
        using var adminClient = _factory.CreateClientFor(admin);
        using var learnerClient = _factory.CreateClientFor(learner);
        using var anonymous = _factory.CreateClient();

        await learnerClient.GetAsync("/api/admin/session");
        await learnerClient.GetAsync("/api/admin/audit");
        await anonymous.GetAsync("/api/admin/session");
        (await adminClient.GetAsync("/api/admin/access")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await adminClient.GetAsync("/api/admin/no-such-thing")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await _factory.ReadAuditAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_audit_row_carries_no_email_and_no_name()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);
        await client.GetAsync("/api/admin/session");
        await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest("wrong-password"));

        foreach (var row in await _factory.ReadAuditAsync())
        {
            var text = JsonSerializer.Serialize(row);
            text.Should().NotContain("captain@admin-tests.local");
            text.Should().NotContain("wrong-password");
        }
    }

    [Fact]
    public async Task The_audit_page_lists_rows_newest_first_and_pages_backwards()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);
        for (var i = 0; i < 3; i++)
        {
            await client.GetAsync("/api/admin/session");
        }

        var first = await client.GetFromJsonAsync<AdminAuditPage>("/api/admin/audit?take=2");
        first!.Items.Should().HaveCount(2);
        first.Items.Select(r => r.Id).Should().BeInDescendingOrder();
        first.Items.Should().OnlyContain(r =>
            r.ActorEmail == admin.Email
            && r.ActorKind == AdminAuditWire.ActorAdmin
            && r.Outcome == AdminAuditWire.OutcomeSucceeded
            && r.Action == AdminActions.SessionRead);
        first.NextBefore.Should().Be(first.Items[^1].Id);

        var second = await client.GetFromJsonAsync<AdminAuditPage>(
            $"/api/admin/audit?take=2&before={first.NextBefore}");
        second!.Items.Should().OnlyContain(r => r.Id < first.NextBefore);
        second.Items.Select(r => r.Id).Should().NotIntersectWith(first.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task Reading_the_log_is_on_the_log_but_a_read_never_lists_its_own_open_row()
    {
        // The row for the request in flight is still "started" while its handler runs. Listing it
        // would make every visit to the log look like a request whose ending was lost.
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        var first = await client.GetFromJsonAsync<AdminAuditPage>("/api/admin/audit");
        first!.Items.Should().BeEmpty("the only row so far is this read's own, and it is still open");

        var second = await client.GetFromJsonAsync<AdminAuditPage>("/api/admin/audit");
        var row = second!.Items.Should().ContainSingle("the first read is complete by now").Subject;
        row.Action.Should().Be(AdminActions.AuditRead);
        row.Outcome.Should().Be(AdminAuditWire.OutcomeSucceeded);

        (await _factory.ReadAuditAsync()).Should().HaveCount(2)
            .And.OnlyContain(r => r.Action == AdminActions.AuditRead && r.Outcome == AdminAuditOutcome.Succeeded);
    }

    [Fact]
    public void The_wire_codes_do_not_follow_the_enum_member_names()
    {
        // The Audit page and the agent match on these strings. Renaming an enum member must not
        // change them, and a value this build does not know is "unknown", not a guess.
        AdminAuditWire.Of(AdminActorKind.Admin).Should().Be("admin");
        AdminAuditWire.Of(AdminActorKind.AgentToken).Should().Be("agent_token");
        AdminAuditWire.Of((AdminActorKind)99).Should().Be("unknown");
        AdminAuditWire.Of(AdminAuditOutcome.Started).Should().Be("started");
        AdminAuditWire.Of(AdminAuditOutcome.Succeeded).Should().Be("succeeded");
        AdminAuditWire.Of(AdminAuditOutcome.Failed).Should().Be("failed");
        AdminAuditWire.Of((AdminAuditOutcome)99).Should().Be("unknown");
    }

    [Theory]
    [InlineData("take=0")]
    [InlineData("take=201")]
    [InlineData("before=0")]
    [InlineData("before=-5")]
    [InlineData("before=abc")]
    [InlineData("take=abc")]
    [InlineData("take=%2B5")]
    public async Task A_malformed_audit_query_is_refused_and_recorded_as_failed(string query)
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        var response = await client.GetAsync($"/api/admin/audit?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var row = (await _factory.ReadAuditAsync()).Should().ContainSingle().Subject;
        row.Action.Should().Be(AdminActions.AuditRead);
        row.Outcome.Should().Be(AdminAuditOutcome.Failed);
        row.StatusCode.Should().Be(400);
    }

    // ---------------------------------------------------------- password again

    [Fact]
    public async Task The_right_password_earns_a_proof_that_the_session_then_reports()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        var response = await client.PostAsJsonAsync(
            "/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var proof = await response.Content.ReadFromJsonAsync<AdminStepUpResponse>();
        proof!.ExpiresAtUtc.Should().Be(_factory.Time.GetUtcNow().Add(AdminStepUpService.Lifetime));

        client.DefaultRequestHeaders.Add(AdminStepUpService.HeaderName, proof.Proof);
        var session = await client.GetFromJsonAsync<AdminSessionResponse>("/api/admin/session");
        session!.StepUpExpiresAtUtc.Should().Be(proof.ExpiresAtUtc);

        var stepUpRow = (await _factory.ReadAuditAsync()).Single(r => r.Action == AdminActions.StepUpVerify);
        stepUpRow.Outcome.Should().Be(AdminAuditOutcome.Succeeded);
    }

    [Fact]
    public async Task The_proof_stops_working_after_ten_minutes()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);
        var proof = await (await client.PostAsJsonAsync(
                "/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password)))
            .Content.ReadFromJsonAsync<AdminStepUpResponse>();
        client.DefaultRequestHeaders.Add(AdminStepUpService.HeaderName, proof!.Proof);

        _factory.Time.Advance(TimeSpan.FromMinutes(9));
        (await client.GetFromJsonAsync<AdminSessionResponse>("/api/admin/session"))!
            .StepUpExpiresAtUtc.Should().NotBeNull();

        _factory.Time.Advance(TimeSpan.FromMinutes(1));
        (await client.GetFromJsonAsync<AdminSessionResponse>("/api/admin/session"))!
            .StepUpExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task A_wrong_password_earns_nothing_is_recorded_and_counts_toward_lockout()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        var response = await client.PostAsJsonAsync(
            "/api/admin/step-up", new AdminStepUpRequest("NotThePassword1!"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response)).Should().Be(AdminFailureCodes.PasswordIncorrect);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("proof");

        var row = (await _factory.ReadAuditAsync()).Should().ContainSingle().Subject;
        row.Action.Should().Be(AdminActions.StepUpVerify);
        row.Outcome.Should().Be(AdminAuditOutcome.Failed);
        row.StatusCode.Should().Be(403);
        row.FailureCode.Should().Be(AdminFailureCodes.PasswordIncorrect);

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await users.FindByIdAsync(admin.Id))!.AccessFailedCount.Should().Be(1);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_check_even_for_the_right_password()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        for (var i = 0; i < 5; i++)
        {
            (await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest("NotThePassword1!")))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        var locked = await client.PostAsJsonAsync(
            "/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password));

        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ProblemCodeAsync(locked)).Should().Be(AdminFailureCodes.LockedOut);
        (await _factory.ReadAuditAsync()).Last().FailureCode.Should().Be(AdminFailureCodes.LockedOut);
    }

    [Fact]
    public async Task Parallel_wrong_guesses_cannot_outrun_the_lockout()
    {
        // Identity's own check-then-count sequence loses all but one of a burst of simultaneous
        // failures to its concurrency stamp, so a burst of any size would cost one attempt. Here
        // each attempt is counted before it is looked at.
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        var burst = await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
            client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest($"WrongGuess{i}!"))));

        var verified = burst.Count(r => r.StatusCode == HttpStatusCode.Forbidden);
        var refused = burst.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        verified.Should().BeInRange(1, 5, "no more guesses are looked at than the lockout allows");
        (verified + refused).Should().Be(24, "every other guess is refused unverified");

        var after = await client.PostAsJsonAsync(
            "/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password));
        after.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the burst locked the account, so even the right password waits");
    }

    [Fact]
    public async Task The_lock_ends_on_time_and_a_right_password_clears_the_count()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest("NotThePassword1!"));
        }

        // Attempts made while locked are not looked at and do not count toward the next lock.
        for (var i = 0; i < 8; i++)
        {
            (await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password)))
                .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        _factory.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        (await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest("NotThePassword1!")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "after the lock, a wrong guess is a wrong guess again");
        (await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await users.FindByIdAsync(admin.Id))!.AccessFailedCount.Should().Be(0);
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_once_the_attempt_is_counted_does_not_lose_a_right_password()
    {
        // The web app stops waiting after 15 seconds. The attempt is counted before the password
        // is looked at, so a check abandoned half way would leave a wrong guess on the account
        // for a right password, and five of those would lock an admin out with no wrong guess.
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var gaveUp = new CancellationTokenSource();
        using var scope = _factory.Services.CreateScope();
        await using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>(
                    scope.ServiceProvider.GetRequiredService<DbContextOptions<ApplicationDbContext>>())
                .AddInterceptors(new CancelAfterFirstWrite(gaveUp))
                .Options);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var check = new AdminPasswordCheck(
            db,
            users,
            scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>(),
            _factory.Time,
            NullLogger<AdminPasswordCheck>.Instance);

        var result = await check.CheckAsync(admin.Id, AdminApiFactory.Password, gaveUp.Token);

        gaveUp.IsCancellationRequested.Should().BeTrue("the caller left as soon as the attempt was counted");
        result.Verdict.Should().Be(AdminPasswordVerdict.Correct);
        (await FailedCountInDatabaseAsync(admin)).Should().Be(0);
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_once_the_attempt_is_counted_still_has_a_wrong_password_counted()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var gaveUp = new CancellationTokenSource();
        using var scope = _factory.Services.CreateScope();
        await using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>(
                    scope.ServiceProvider.GetRequiredService<DbContextOptions<ApplicationDbContext>>())
                .AddInterceptors(new CancelAfterFirstWrite(gaveUp))
                .Options);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var check = new AdminPasswordCheck(
            db,
            users,
            scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>(),
            _factory.Time,
            NullLogger<AdminPasswordCheck>.Instance);

        var result = await check.CheckAsync(admin.Id, "NotThePassword1!", gaveUp.Token);

        result.Verdict.Should().Be(AdminPasswordVerdict.Incorrect);
        (await FailedCountInDatabaseAsync(admin)).Should().Be(1);
    }

    [Fact]
    public async Task A_caller_already_gone_before_the_check_starts_is_not_counted()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var scope = _factory.Services.CreateScope();
        var check = scope.ServiceProvider.GetRequiredService<AdminPasswordCheck>();

        var act = () => check.CheckAsync(admin.Id, "NotThePassword1!", new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await FailedCountInDatabaseAsync(admin)).Should().Be(0);
    }

    /// <summary>The count as stored, read in a scope of its own: the check's scope holds the account as it first read it.</summary>
    private async Task<int> FailedCountInDatabaseAsync(ApplicationUser account)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users
            .AsNoTracking().Where(u => u.Id == account.Id).Select(u => u.AccessFailedCount).SingleAsync();
    }

    /// <summary>Cancels as soon as one write has been executed: the caller leaving mid-check.</summary>
    private sealed class CancelAfterFirstWrite(CancellationTokenSource source) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_missing_password_is_refused_without_touching_the_lockout_count(string? password)
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        var response = await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest(password));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await users.FindByIdAsync(admin.Id))!.AccessFailedCount.Should().Be(0);
    }

    [Fact]
    public async Task A_body_that_cannot_be_read_is_still_recorded_as_a_failed_attempt()
    {
        // The row is written before the framework binds the body, so a request the handler never
        // sees is on the log all the same.
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        var response = await client.PostAsync("/api/admin/step-up",
            new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        response.IsSuccessStatusCode.Should().BeFalse();
        var row = (await _factory.ReadAuditAsync()).Should().ContainSingle().Subject;
        row.Action.Should().Be(AdminActions.StepUpVerify);
        row.Outcome.Should().Be(AdminAuditOutcome.Failed);
    }

    [Fact]
    public async Task A_step_up_request_that_is_not_JSON_matches_no_admin_route()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);

        var response = await client.PostAsync("/api/admin/step-up", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _factory.ReadAuditAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task One_admins_proof_does_not_work_for_another_admin()
    {
        var first = await _factory.SeedAdminAsync("captain@admin-tests.local");
        var second = await _factory.SeedAdminAsync("mate@admin-tests.local");
        using var firstClient = _factory.CreateClientFor(first);
        using var secondClient = _factory.CreateClientFor(second);

        var proof = await (await firstClient.PostAsJsonAsync(
                "/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password)))
            .Content.ReadFromJsonAsync<AdminStepUpResponse>();

        secondClient.DefaultRequestHeaders.Add(AdminStepUpService.HeaderName, proof!.Proof);
        (await secondClient.GetFromJsonAsync<AdminSessionResponse>("/api/admin/session"))!
            .StepUpExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Changing_the_password_ends_a_proof_issued_before_it()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);
        var proof = await (await client.PostAsJsonAsync(
                "/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password)))
            .Content.ReadFromJsonAsync<AdminStepUpResponse>();
        client.DefaultRequestHeaders.Add(AdminStepUpService.HeaderName, proof!.Proof);

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(admin.Id);
            (await users.ChangePasswordAsync(user!, AdminApiFactory.Password, "ANewPassword1234!"))
                .Succeeded.Should().BeTrue();
        }

        (await client.GetFromJsonAsync<AdminSessionResponse>("/api/admin/session"))!
            .StepUpExpiresAtUtc.Should().BeNull();
    }

    // ------------------------------------------------------------- fail closed

    [Fact]
    public async Task When_the_audit_log_cannot_be_written_the_request_fails_and_returns_nothing()
    {
        // A PostgreSQL address nothing listens on: the audit insert fails before the handler runs.
        using var factory = new AdminApiFactory(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1;Command Timeout=1");
        var admin = await factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = factory.CreateClientFor(admin);

        var session = await client.GetAsync("/api/admin/session");
        var stepUp = await client.PostAsJsonAsync(
            "/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password));

        session.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await session.Content.ReadAsStringAsync()).Should().NotContain(admin.Id).And.NotContain(admin.Email);
        stepUp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await stepUp.Content.ReadAsStringAsync()).Should().NotContain("proof");

        // The link probe writes nothing, so it does not depend on the log.
        (await client.GetAsync("/api/admin/access")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
