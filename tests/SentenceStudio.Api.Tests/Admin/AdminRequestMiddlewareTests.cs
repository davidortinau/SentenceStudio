using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Api.Tests.Coach;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// The middleware around every admin route, driven directly so the cases no phase 0 route reaches
/// yet (a target id, a throwing handler, a route mapped wrongly) are still pinned.
/// </summary>
public sealed class AdminRequestMiddlewareTests : IDisposable
{
    private static readonly AdminActor Captain =
        new(AdminActorKind.Admin, "admin-1", "captain@admin-tests.local", "Captain", "digest-1");

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sentencestudio_admin_mw_{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<AdminDbContext> _options;
    private readonly ServiceProvider _services;

    public AdminRequestMiddlewareTests()
    {
        _options = new DbContextOptionsBuilder<AdminDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
        using (var db = new AdminDbContext(_options))
        {
            db.Database.EnsureCreated();
        }

        _services = new ServiceCollection()
            .AddSingleton(_options)
            .AddSingleton<TimeProvider>(new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero)))
            .AddScoped<AdminAuditWriter>()
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private DefaultHttpContext NewContext(AdminActor? actor, params object[] metadata)
    {
        var http = new DefaultHttpContext { RequestServices = _services.CreateScope().ServiceProvider };
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"));
        if (actor is not null)
        {
            http.Features.Set<IAdminActorFeature>(new AdminActorFeature(actor));
        }

        return http;
    }

    private static AdminRequestMiddleware Middleware(RequestDelegate next) =>
        new(next, NullLogger<AdminRequestMiddleware>.Instance);

    private List<AdminAuditEntry> Rows()
    {
        using var db = new AdminDbContext(_options);
        return db.AuditEntries.AsNoTracking().OrderBy(e => e.Id).ToList();
    }

    [Fact]
    public async Task A_route_that_is_not_an_admin_route_passes_straight_through()
    {
        var ran = false;
        var http = NewContext(actor: null);

        await Middleware(_ => { ran = true; return Task.CompletedTask; }).InvokeAsync(http);

        ran.Should().BeTrue();
        http.Response.Headers.CacheControl.ToString().Should().BeEmpty();
        Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task An_admin_route_reached_without_an_admitted_caller_is_answered_404_and_never_runs()
    {
        // What a route mapped under the group without the policy would look like.
        var ran = false;
        var http = NewContext(actor: null, AdminRouteMetadata.Instance, new AdminActionMetadata(AdminActions.SessionRead));

        await Middleware(_ => { ran = true; return Task.CompletedTask; }).InvokeAsync(http);

        ran.Should().BeFalse();
        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task An_admin_route_that_declares_no_audit_decision_is_refused()
    {
        var ran = false;
        var http = NewContext(Captain, AdminRouteMetadata.Instance);

        await Middleware(_ => { ran = true; return Task.CompletedTask; }).InvokeAsync(http);

        ran.Should().BeFalse();
        http.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task The_row_exists_before_the_handler_runs_and_records_how_it_ended()
    {
        List<AdminAuditEntry>? seenByHandler = null;
        var http = NewContext(Captain, AdminRouteMetadata.Instance, new AdminActionMetadata(AdminActions.SessionRead));

        await Middleware(context =>
        {
            seenByHandler = Rows();
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        long? ownEntry = http.FindAdminAuditEntryId();
        seenByHandler.Should().ContainSingle().Which.Outcome.Should().Be(AdminAuditOutcome.Started);
        ownEntry.Should().Be(seenByHandler![0].Id, "a handler can tell its own open row from the rest");
        var row = Rows().Should().ContainSingle().Subject;
        row.Outcome.Should().Be(AdminAuditOutcome.Succeeded);
        row.StatusCode.Should().Be(200);
        http.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
    }

    [Fact]
    public async Task The_target_named_by_the_route_is_recorded()
    {
        var http = NewContext(Captain, AdminRouteMetadata.Instance,
            new AdminActionMetadata("learner.words.read", AdminTargetKinds.Learner, "learnerId"));
        http.Request.RouteValues["learnerId"] = "7384f806-0000-4000-8000-000000000001";

        await Middleware(_ => Task.CompletedTask).InvokeAsync(http);

        var row = Rows().Should().ContainSingle().Subject;
        row.TargetKind.Should().Be(AdminTargetKinds.Learner);
        row.TargetId.Should().Be("7384f806-0000-4000-8000-000000000001");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task A_target_id_that_can_name_nothing_is_answered_404_and_never_runs(string? targetId)
    {
        var ran = false;
        var http = NewContext(Captain, AdminRouteMetadata.Instance,
            new AdminActionMetadata("learner.words.read", AdminTargetKinds.Learner, "learnerId"));
        if (targetId is not null)
        {
            http.Request.RouteValues["learnerId"] = targetId;
        }

        await Middleware(_ => { ran = true; return Task.CompletedTask; }).InvokeAsync(http);

        ran.Should().BeFalse();
        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task A_refusal_records_its_code()
    {
        var http = NewContext(Captain, AdminRouteMetadata.Instance, new AdminActionMetadata(AdminActions.StepUpVerify));

        await Middleware(context =>
        {
            _ = AdminRefusal.Create(context, StatusCodes.Status403Forbidden, AdminFailureCodes.PasswordIncorrect, "no");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = Rows().Should().ContainSingle().Subject;
        row.Outcome.Should().Be(AdminAuditOutcome.Failed);
        row.StatusCode.Should().Be(403);
        row.FailureCode.Should().Be(AdminFailureCodes.PasswordIncorrect);
    }

    [Fact]
    public async Task A_handler_that_throws_is_recorded_as_failed_and_the_exception_still_propagates()
    {
        var http = NewContext(Captain, AdminRouteMetadata.Instance, new AdminActionMetadata(AdminActions.SessionRead));

        var act = () => Middleware(_ => throw new InvalidOperationException("boom")).InvokeAsync(http);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var row = Rows().Should().ContainSingle().Subject;
        row.Outcome.Should().Be(AdminAuditOutcome.Failed);
        row.StatusCode.Should().Be(500);
        row.FailureCode.Should().Be(AdminFailureCodes.Exception);
    }

    [Fact]
    public async Task A_caller_who_disconnects_is_recorded_as_cancelled()
    {
        using var aborted = new CancellationTokenSource();
        var http = NewContext(Captain, AdminRouteMetadata.Instance, new AdminActionMetadata(AdminActions.AuditRead));
        http.RequestAborted = aborted.Token;

        var act = () => Middleware(_ =>
        {
            aborted.Cancel();
            throw new OperationCanceledException(aborted.Token);
        }).InvokeAsync(http);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var row = Rows().Should().ContainSingle().Subject;
        row.Outcome.Should().Be(AdminAuditOutcome.Failed);
        row.FailureCode.Should().Be(AdminFailureCodes.Cancelled);
    }

    [Fact]
    public async Task When_the_row_cannot_be_written_the_handler_never_runs()
    {
        using (var db = new AdminDbContext(_options))
        {
            db.Database.ExecuteSqlRaw("DROP TABLE \"AdminAuditEntry\"");
        }

        var ran = false;
        var http = NewContext(Captain, AdminRouteMetadata.Instance, new AdminActionMetadata(AdminActions.SessionRead));

        var act = () => Middleware(_ => { ran = true; return Task.CompletedTask; }).InvokeAsync(http);

        await act.Should().ThrowAsync<Exception>();
        ran.Should().BeFalse("a read that cannot be recorded must not happen");
    }

    [Fact]
    public async Task When_only_the_ending_cannot_be_written_the_response_stands_and_the_row_stays_started()
    {
        var http = NewContext(Captain, AdminRouteMetadata.Instance, new AdminActionMetadata(AdminActions.SessionRead));

        await Middleware(_ =>
        {
            using var db = new AdminDbContext(_options);
            db.Database.ExecuteSqlRaw("ALTER TABLE \"AdminAuditEntry\" RENAME TO \"Moved\"");
            return Task.CompletedTask;
        }).InvokeAsync(http);

        using var check = new AdminDbContext(_options);
        check.Database.ExecuteSqlRaw("ALTER TABLE \"Moved\" RENAME TO \"AdminAuditEntry\"");
        Rows().Should().ContainSingle().Which.Outcome.Should().Be(AdminAuditOutcome.Started);
    }
}
