using System;
using System.Data.Common;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SentenceStudio.Contracts.Startup;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.Api;

public static class VersionEndpoints
{
    public static WebApplication MapVersionEndpoints(this WebApplication app)
    {
        // Version endpoints are public — no authorization required
        var group = app.MapGroup("/api/version");

        group.MapGet("/info", GetVersionInfo);
        group.MapGet("/latest", GetLatestVersion);
        group.MapGet("/notes/{version}", GetReleaseNotes);
        group.MapGet("/notes", GetAllReleaseNotes);

        return app;
    }

    private static async Task<IResult> GetVersionInfo(
        [FromServices] ReleaseNotesService releaseNotesService,
        [FromServices] ApplicationDbContext db,
        [FromServices] IConfiguration configuration,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("SentenceStudio.Api.Version.Readiness");
        if (configuration.GetValue("Database:SkipMigrateOnStartup", false))
        {
            return Results.Problem(
                title: "API application startup initialization was skipped",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!db.Database.IsNpgsql())
        {
            logger.LogWarning("API readiness withheld: PostgreSQL database identity has the wrong provider.");
            return Results.Problem(
                title: "PostgreSQL database identity is unavailable",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var applicationMigrationId = db.Database.GetMigrations().LastOrDefault();
        if (applicationMigrationId is null)
        {
            return Results.Problem(
                title: "Application migrations are unavailable",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        string databaseIdentity;
        try
        {
            // A pooled reconnect can preserve the fingerprint while invalidating history
            // attestation. Latch any backend transition before returning HTTP 200.
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                var connection = db.Database.GetDbConnection();
                var readiness = await PostgresDatabaseIdentity.ReadReadinessAsync(
                    connection,
                    () => db.Database.GetDbConnection(),
                    () => connection is NpgsqlConnection npgsql ? npgsql.ProcessID : 0,
                    async ct => (await db.Database.GetPendingMigrationsAsync(ct)).Count(),
                    applicationMigrationId, cancellationToken);
                databaseIdentity = readiness.DatabaseIdentity;
                if (readiness.PendingMigrationCount != 0)
                {
                    throw new PostgresDatabaseIdentityException(
                        PostgresDatabaseIdentityFailure.UnexpectedSchemaMarker);
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (PostgresDatabaseIdentityException ex)
        {
            logger.LogWarning("API readiness withheld: PostgreSQL database identity {Reason}.", ex.Reason);
            return Results.Problem(
                title: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (DbException)
        {
            logger.LogWarning("API readiness withheld: PostgreSQL database identity query unavailable.");
            return Results.Problem(
                title: "PostgreSQL database identity is unavailable",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("API readiness withheld: PostgreSQL database or migration history unavailable.");
            return Results.Problem(
                title: "PostgreSQL database identity is unavailable",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("API readiness withheld: PostgreSQL database or migration history timed out.");
            return Results.Problem(
                title: "PostgreSQL database identity is unavailable",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Get current version from assembly
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionString = version != null 
            ? $"{version.Major}.{version.Minor}" 
            : "1.0";

        // Get release notes for current version
        var notes = await releaseNotesService.GetNotesForVersionAsync(versionString);

        return Results.Ok(new
        {
            Version = versionString,
            ReleaseNotes = notes,
            ApplicationStartupContract = ApiApplicationStartupContract.Current,
            ApplicationMigrationId = applicationMigrationId,
            DatabaseIdentity = databaseIdentity
        });
    }

    private static async Task<IResult> GetLatestVersion(
        [FromServices] ReleaseNotesService releaseNotesService)
    {
        var latestNotes = await releaseNotesService.GetLatestNotesAsync();
        if (latestNotes == null)
            return Results.NotFound();

        return Results.Ok(new
        {
            Version = latestNotes.Version,
            Date = latestNotes.Date,
            Title = latestNotes.Title
        });
    }

    private static async Task<IResult> GetReleaseNotes(
        string version,
        [FromServices] ReleaseNotesService releaseNotesService)
    {
        var notes = await releaseNotesService.GetNotesForVersionAsync(version);
        if (notes == null)
            return Results.NotFound();

        return Results.Ok(notes);
    }

    private static async Task<IResult> GetAllReleaseNotes(
        [FromServices] ReleaseNotesService releaseNotesService)
    {
        var notes = await releaseNotesService.GetAllNotesAsync();
        return Results.Ok(notes);
    }
}
