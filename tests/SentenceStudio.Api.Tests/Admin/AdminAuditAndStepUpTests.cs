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

    // ------------------------------------------- a check that is interrupted

    /// <summary>An account database context whose commands pass through <paramref name="interceptor"/>.</summary>
    private static ApplicationDbContext WatchedDatabase(IServiceScope scope, DbCommandInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<ApplicationDbContext>>())
            .AddInterceptors(interceptor)
            .Options);

    private AdminPasswordCheck CheckOn(IServiceScope scope, ApplicationDbContext db) =>
        new(
            db,
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>(),
            _factory.Time,
            NullLogger<AdminPasswordCheck>.Instance);

    [Fact]
    public async Task A_caller_that_stops_waiting_once_the_attempt_is_counted_does_not_lose_a_right_password()
    {
        // The web app stops waiting after 15 seconds. The attempt is counted before the password
        // is looked at, so a check abandoned half way would leave a wrong guess on the account
        // for a right password, and five of those would lock an admin out with no wrong guess.
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var gaveUp = new CancellationTokenSource();
        using var scope = _factory.Services.CreateScope();
        await using var db = WatchedDatabase(scope, new AfterTheCount(() => { gaveUp.Cancel(); return Task.CompletedTask; }));
        var check = CheckOn(scope, db);

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
        await using var db = WatchedDatabase(scope, new AfterTheCount(() => { gaveUp.Cancel(); return Task.CompletedTask; }));
        var check = CheckOn(scope, db);

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

    [Fact]
    public async Task An_attempt_counted_before_a_lock_and_refused_after_it_does_not_take_the_count_below_zero()
    {
        // A burst: this attempt is counted, another attempt of the burst then earns the lock and
        // resets the count, and this one, finding the account locked, takes itself back off.
        // There is nothing left to take. A count of -1 would be a sixth guess after the lock;
        // a burst of hundreds, hundreds of them.
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var scope = _factory.Services.CreateScope();
        await using var db = WatchedDatabase(scope, new AfterTheCount(async () =>
        {
            using var other = _factory.Services.CreateScope();
            await other.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users
                .Where(u => u.Id == admin.Id)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(u => u.LockoutEnd, _factory.Time.GetUtcNow() + TimeSpan.FromMinutes(5))
                    .SetProperty(u => u.AccessFailedCount, 0));
        }));
        var check = CheckOn(scope, db);

        var result = await check.CheckAsync(admin.Id, "NotThePassword1!", CancellationToken.None);

        result.Verdict.Should().Be(AdminPasswordVerdict.LockedOut);
        (await FailedCountInDatabaseAsync(admin)).Should().Be(0);
    }

    [Fact]
    public async Task After_a_burst_and_its_lock_no_more_wrong_guesses_are_looked_at_than_the_limit()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
            client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest($"WrongGuess{i}!"))));

        (await FailedCountInDatabaseAsync(admin)).Should().Be(0, "the lock starts the count again, at zero and not below");

        _factory.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var afterTheLock = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++)
        {
            afterTheLock.Add((await client.PostAsJsonAsync(
                "/api/admin/step-up", new AdminStepUpRequest($"LaterGuess{i}!"))).StatusCode);
        }

        afterTheLock.Count(status => status == HttpStatusCode.Forbidden).Should().Be(5);
        afterTheLock.Skip(5).Should().OnlyContain(status => status == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_check_that_reaches_no_verdict_takes_its_attempt_back_off_the_count()
    {
        // A database fault after the count. Nobody looked at the password, so nothing was
        // guessed; left counted, five such faults would lock an admin out.
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var scope = _factory.Services.CreateScope();
        await using var db = WatchedDatabase(scope, new FailsTheReadAfterTheCount());
        var check = CheckOn(scope, db);

        var act = () => check.CheckAsync(admin.Id, AdminApiFactory.Password, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*simulated*");
        (await FailedCountInDatabaseAsync(admin)).Should().Be(0);
    }

    [Fact]
    public async Task The_clock_running_out_before_a_verdict_takes_the_attempt_back_off_the_count()
    {
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var scope = _factory.Services.CreateScope();
        await using var db = WatchedDatabase(scope, new AfterTheCount(() =>
        {
            _factory.Time.Advance(AdminPasswordCheck.SettleTimeout + TimeSpan.FromSeconds(1));
            return Task.CompletedTask;
        }));
        var check = CheckOn(scope, db);

        var act = () => check.CheckAsync(admin.Id, AdminApiFactory.Password, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await FailedCountInDatabaseAsync(admin)).Should().Be(0);
    }

    [Fact]
    public async Task A_wrong_guess_whose_lock_cannot_be_written_is_still_answered_as_wrong_and_stays_counted()
    {
        // The fifth wrong guess was looked at; the lock it earns cannot be written. It is still a
        // wrong guess, for the caller and for the log, and taking it back off the count would be
        // a free guess for anyone who could make that write fail.
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);
        for (var i = 0; i < 4; i++)
        {
            await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest("NotThePassword1!"));
        }

        using var scope = _factory.Services.CreateScope();
        await using var db = WatchedDatabase(scope, new FailsTheLockWrite());
        var check = CheckOn(scope, db);

        var result = await check.CheckAsync(admin.Id, "NotThePassword1!", CancellationToken.None);

        result.Verdict.Should().Be(AdminPasswordVerdict.Incorrect);
        (await FailedCountInDatabaseAsync(admin)).Should().Be(5);

        // The next attempt finds the count over the limit, writes the lock, and is not looked at.
        (await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password)))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task While_locked_out_a_refused_attempt_writes_nothing_so_the_admins_password_change_still_saves()
    {
        // Somebody holding the session keeps asking while the account is locked. If each refused
        // attempt wrote to the account, the admin's own password change, which Identity saves
        // under the concurrency stamp it read, would fail every time.
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest("NotThePassword1!"));
        }

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var asReadForTheChange = await users.FindByIdAsync(admin.Id);
        var resetToken = await users.GeneratePasswordResetTokenAsync(asReadForTheChange!);

        for (var i = 0; i < 3; i++)
        {
            (await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest("NotThePassword1!")))
                .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        (await users.ResetPasswordAsync(asReadForTheChange!, resetToken, "ANewPassword1234!"))
            .Succeeded.Should().BeTrue("the refused attempts did not touch the account");
        (await FailedCountInDatabaseAsync(admin)).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_slow_look_at_the_password_does_not_stop_the_verdict_being_written(bool rightPassword)
    {
        // Reading the account and hashing the password do not watch the check's clock. If that
        // took the whole of it, the lock for a fifth wrong guess, or the reset for a right
        // password, must still be written.
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = _factory.CreateClientFor(admin);
        for (var i = 0; i < 4; i++)
        {
            await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest("NotThePassword1!"));
        }

        using var scope = _factory.Services.CreateScope();
        await using var db = WatchedDatabase(scope, new AfterTheReadThatFollowsTheCount(() =>
            _factory.Time.Advance(AdminPasswordCheck.SettleTimeout + TimeSpan.FromSeconds(1))));
        var check = CheckOn(scope, db);

        var result = await check.CheckAsync(
            admin.Id, rightPassword ? AdminApiFactory.Password : "NotThePassword1!", CancellationToken.None);

        if (rightPassword)
        {
            result.Verdict.Should().Be(AdminPasswordVerdict.Correct);
            (await FailedCountInDatabaseAsync(admin)).Should().Be(0);
        }
        else
        {
            result.Verdict.Should().Be(AdminPasswordVerdict.Incorrect);
            (await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password)))
                .StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the fifth wrong guess wrote its lock");
        }
    }

    [Fact]
    public async Task A_sign_in_that_read_the_account_earlier_cannot_put_back_the_old_count_or_erase_a_lock()
    {
        // Identity saves the whole account row as it read it. A failed sign-in that read the
        // account before these guesses and saved after them would forget them, and the lock too.
        var admin = await _factory.SeedAdminAsync("captain@admin-tests.local");
        using var signInScope = _factory.Services.CreateScope();
        var signInUsers = signInScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var asReadBySignIn = await signInUsers.FindByIdAsync(admin.Id);

        using var client = _factory.CreateClientFor(admin);
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest("NotThePassword1!"));
        }

        var staleSave = await signInUsers.AccessFailedAsync(asReadBySignIn!);

        staleSave.Succeeded.Should().BeFalse("the row changed under the sign-in since it read it");
        staleSave.Errors.Should().Contain(e => e.Code == "ConcurrencyFailure");
        (await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password)))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the lock earned by five wrong guesses is still there");
    }

    [Fact]
    public async Task A_caller_that_leaves_after_a_wrong_guess_is_still_recorded_as_a_wrong_guess()
    {
        // Dropping the connection must not hide a guess from the log.
        using var gaveUp = new CancellationTokenSource();
        var watcher = new AfterTheCount(() => { gaveUp.Cancel(); return Task.CompletedTask; }) { Armed = false };
        using var factory = new AdminApiFactory { AccountDatabaseInterceptor = watcher };
        var admin = await factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = factory.CreateClientFor(admin);
        watcher.Armed = true;

        var act = () => client.PostAsJsonAsync(
            "/api/admin/step-up", new AdminStepUpRequest("NotThePassword1!"), gaveUp.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        watcher.Fired.Should().BeTrue();

        AdminAuditEntry? row = null;
        for (var i = 0; i < 100 && row?.Outcome is null or AdminAuditOutcome.Started; i++)
        {
            await Task.Delay(50);
            row = (await factory.ReadAuditAsync()).SingleOrDefault(e => e.Action == AdminActions.StepUpVerify);
        }

        row.Should().NotBeNull();
        row!.Outcome.Should().Be(AdminAuditOutcome.Failed);
        row.StatusCode.Should().Be(403);
        row.FailureCode.Should().Be(AdminFailureCodes.PasswordIncorrect);

        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users
            .AsNoTracking().Where(u => u.Id == admin.Id).Select(u => u.AccessFailedCount).SingleAsync())
            .Should().Be(1);
    }

    [Fact]
    public async Task A_caller_that_leaves_mid_check_gets_no_proof_and_the_log_says_the_attempt_was_abandoned()
    {
        using var gaveUp = new CancellationTokenSource();
        var watcher = new AfterTheCount(() => { gaveUp.Cancel(); return Task.CompletedTask; }) { Armed = false };
        using var factory = new AdminApiFactory { AccountDatabaseInterceptor = watcher };
        var admin = await factory.SeedAdminAsync("captain@admin-tests.local");
        using var client = factory.CreateClientFor(admin);
        watcher.Armed = true;

        var act = () => client.PostAsJsonAsync(
            "/api/admin/step-up", new AdminStepUpRequest(AdminApiFactory.Password), gaveUp.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        watcher.Fired.Should().BeTrue("the caller left after the attempt was counted, not before");

        // The host finishes the request on its own after the caller has gone.
        AdminAuditEntry? row = null;
        for (var i = 0; i < 100 && row?.Outcome is null or AdminAuditOutcome.Started; i++)
        {
            await Task.Delay(50);
            row = (await factory.ReadAuditAsync()).SingleOrDefault(e => e.Action == AdminActions.StepUpVerify);
        }

        row.Should().NotBeNull();
        row!.Outcome.Should().Be(AdminAuditOutcome.Failed);
        row.StatusCode.Should().Be(499);
        row.FailureCode.Should().Be(AdminFailureCodes.Cancelled);

        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users
            .AsNoTracking().Where(u => u.Id == admin.Id).Select(u => u.AccessFailedCount).SingleAsync())
            .Should().Be(0, "the password was right, and the check settled its count after the caller left");
    }

    /// <summary>The count as stored, read in a scope of its own: the check's scope holds the account as it first read it.</summary>
    private async Task<int> FailedCountInDatabaseAsync(ApplicationUser account)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users
            .AsNoTracking().Where(u => u.Id == account.Id).Select(u => u.AccessFailedCount).SingleAsync();
    }

    /// <summary>Runs once, right after the first write (the count) has been executed.</summary>
    private sealed class AfterTheCount(Func<Task> then) : DbCommandInterceptor
    {
        private int _done;

        /// <summary>Off while a test is still seeding through the same host.</summary>
        public bool Armed { get; set; } = true;

        /// <summary>Whether the count was seen, so a test knows its interruption landed where it meant.</summary>
        public bool Fired { get; private set; }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            // Only the count itself: a host makes other writes that are none of this test's business.
            if (Armed
                && command.CommandText.Contains("\"AccessFailedCount\" + 1", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _done, 1) == 0)
            {
                Fired = true;
                await then();
            }

            return result;
        }
    }

    /// <summary>Runs once, right after the read that follows the count: the last step before the password is looked at.</summary>
    private sealed class AfterTheReadThatFollowsTheCount(Action then) : DbCommandInterceptor
    {
        private int _state;   // 0 waiting for the count, 1 counted, 2 done

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"AccessFailedCount\" + 1", StringComparison.Ordinal))
            {
                Interlocked.CompareExchange(ref _state, 1, 0);
            }

            return ValueTask.FromResult(result);
        }

        // When the reader is put away, not when the command has run: the rows are read in between.
        public override InterceptionResult DataReaderDisposing(
            DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 1) == 1)
            {
                then();
            }

            return result;
        }
    }

    /// <summary>Fails the statement that writes a lock.</summary>
    private sealed class FailsTheLockWrite : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("\"LockoutEnd\" =", StringComparison.Ordinal)
                ? throw new InvalidOperationException("simulated database fault")
                : ValueTask.FromResult(result);
    }

    /// <summary>Fails the first read that follows the count, once.</summary>
    private sealed class FailsTheReadAfterTheCount : DbCommandInterceptor
    {
        private int _state;   // 0 waiting for the count, 1 counted, 2 failed once

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.CompareExchange(ref _state, 1, 0);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 1) == 1)
            {
                throw new InvalidOperationException("simulated database fault");
            }

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
