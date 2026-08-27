using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Api.Auth;
using SentenceStudio.Api.Coach.Persistence.Deletion;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// What the learner is told when an account deletion fails part-way.
/// </summary>
/// <remarks>
/// "Nothing was removed" is a factual claim about their data, and it was previously made
/// unconditionally — including in the case where the legacy conversation delete had already
/// committed on its own connection and only the coach half rolled back. A learner who reads that
/// message and moves on has been told their conversation history is intact when it is gone. These
/// tests pin the message to the report, so the sentence can only appear when it is true.
/// </remarks>
public sealed class AccountDeletionFailureMessageTests
{
    private const string Password = "Test1234!";

    [Fact]
    public async Task A_clean_rollback_tells_the_learner_nothing_was_removed()
    {
        var detail = await DeleteAccountAndReadDetailAsync(
            new StubCoachDataDeletionService(dataWasRemoved: false));

        detail.Should().Contain(
            "Nothing was removed",
            "the rollback restored every row, so the reassurance is accurate");
    }

    [Fact]
    public async Task A_partial_erasure_never_claims_nothing_was_removed()
    {
        var detail = await DeleteAccountAndReadDetailAsync(
            new StubCoachDataDeletionService(dataWasRemoved: true));

        detail.Should().NotContain(
            "Nothing was removed",
            "some of the learner's data is already gone, and telling them otherwise is the failure "
            + "this message exists to avoid");

        detail.Should().Contain(
            "already been removed",
            "a partial erasure has to be stated plainly so the learner knows what happened");
        detail.Should().Contain(
            "try again",
            "the deletion is idempotent, so the retry is the action that finishes it");
    }

    /// <summary>
    /// The message for the failure this hardening was about: a deferred external delete that
    /// committed and then threw before it could report a count, with no coach rows to make the
    /// total non-zero.
    /// </summary>
    /// <remarks>
    /// The report here is produced by the real coordinator rather than by a stub value, so what is
    /// pinned is the whole path — coordinator to report to sentence. A stub would only prove that
    /// the endpoint branches on a boolean, which was never the part in doubt.
    /// </remarks>
    [Fact]
    public async Task A_deferred_delete_that_committed_before_failing_never_claims_nothing_was_removed()
    {
        var (report, external) = await RealReportForACommittedDeferredDeleteAsync();

        external.RowsCommitted.Should().BeGreaterThan(
            0, "the learner's rows are genuinely gone in this scenario");
        report.RowsDeleted.Should().Be(
            0, "no contributor ever reported a count, which is what made the old message wrong");

        var detail = await DeleteAccountAndReadDetailAsync(new StubCoachDataDeletionService(report));

        detail.Should().NotContain(
            "Nothing was removed",
            "an external delete had already committed, so this sentence would be false");
        detail.Should().Contain(
            "already been removed",
            "the learner has to be told plainly that some of their data is gone");
        detail.Should().Contain(
            "try again",
            "the retry is idempotent and is what finishes the erasure");
    }

    /// <summary>
    /// Runs the real coordinator over a real relational context in the shape under test.
    /// </summary>
    private static async Task<(CoachDeletionReport Report, CommittedThenFailingExternalContributor External)>
        RealReportForACommittedDeferredDeleteAsync()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var external = new CommittedThenFailingExternalContributor();

        var service = new CoachDataDeletionService(
            db,
            [
                new CoachCheckpointDeletionContributor(
                    db, Microsoft.Extensions.Logging.Abstractions.NullLogger<CoachCheckpointDeletionContributor>.Instance),
                external
            ],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(
            CoachOwner.ForUser(CoachPersistenceSamples.OwnerUserId));

        report.Succeeded.Should().BeFalse();

        return (report, external);
    }

    private static async Task<string> DeleteAccountAndReadDetailAsync(ICoachDataDeletionService deletion)
    {
        using var factory = new JwtBearerApiFactory();
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICoachDataDeletionService>();
                services.AddSingleton(deletion);
            }));

        using var client = host.CreateClient();

        var email = $"delete-message-{Guid.NewGuid():N}@test.local";
        var token = await RegisterConfirmAndLoginAsync(host, client, email);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.DeleteAsync("/api/auth/account");

        response.StatusCode.Should().Be(
            HttpStatusCode.InternalServerError,
            "a deletion that could not erase everything must fail closed rather than report success");

        var problem = await response.Content.ReadFromJsonAsync<ProblemShape>();
        problem.Should().NotBeNull();
        problem!.Detail.Should().NotBeNullOrWhiteSpace();

        return problem.Detail!;
    }

    private static async Task<string> RegisterConfirmAndLoginAsync(
        WebApplicationFactory<Program> host,
        HttpClient client,
        string email)
    {
        var registration = await client.PostAsJsonAsync("/api/auth/register", new { Email = email, Password });
        registration.EnsureSuccessStatusCode();

        using (var scope = host.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            user.Should().NotBeNull();

            var confirmation = await userManager.GenerateEmailConfirmationTokenAsync(user!);
            (await userManager.ConfirmEmailAsync(user!, confirmation)).Succeeded.Should().BeTrue();

            // The coach deletion step only runs for a learner who has a profile, which is exactly
            // the account shape this message is about.
            if (string.IsNullOrEmpty(user!.UserProfileId))
            {
                user.UserProfileId = Guid.NewGuid().ToString();
                (await userManager.UpdateAsync(user)).Succeeded.Should().BeTrue();
            }
        }

        var login = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password });
        login.EnsureSuccessStatusCode();

        var auth = await login.Content.ReadFromJsonAsync<AuthResponse>();
        auth.Should().NotBeNull();
        auth!.Token.Should().NotBeNullOrWhiteSpace();

        return auth.Token!;
    }

    private sealed record ProblemShape(string? Detail);

    /// <summary>Reports the failure shape under test without touching a database.</summary>
    private sealed class StubCoachDataDeletionService : ICoachDataDeletionService
    {
        private readonly CoachDeletionReport _report;

        public StubCoachDataDeletionService(bool dataWasRemoved)
            : this(new CoachDeletionReport(
                Succeeded: false,
                RowsDeleted: dataWasRemoved ? 4 : 0,
                DeletesByContributor: new Dictionary<string, int>(),
                FailureCode: "deletion_failed",
                DataWasRemoved: dataWasRemoved))
        {
        }

        public StubCoachDataDeletionService(CoachDeletionReport report) => _report = report;

        public Task<CoachDeletionReport> DeleteAllForOwnerAsync(
            CoachOwner owner,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_report);
    }
}
