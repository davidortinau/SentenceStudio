using System;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
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
        [FromServices] ReleaseNotesService releaseNotesService)
    {
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
            ReleaseNotes = notes
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
