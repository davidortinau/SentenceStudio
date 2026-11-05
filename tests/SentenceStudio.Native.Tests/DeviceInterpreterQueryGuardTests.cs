using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Proves <see cref="DeviceInterpreterQueryGuard"/> fires on the two shapes that crashed DX24 on
/// 2026-11-05, stays quiet on shapes EF translates or that were read into locals, and fails the
/// test even when app code catches its exception.
/// </summary>
public sealed class DeviceInterpreterQueryGuardTests
{
    [Fact]
    public void Flags_TheContextsDatabaseInsideAProjection()
    {
        using var fixture = new NativeSliceFixture();
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var query = db.LearningResources.Select(resource =>
            db.Database.IsSqlite() ? resource.Title : null);

        Assert.Contains("Database", DeviceInterpreterQueryGuard.FindClientDispatch(query.Expression));
    }

    [Fact]
    public void Flags_AnInterfaceCountInsideAClientEvaluatedCondition()
    {
        using var fixture = new NativeSliceFixture();
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IReadOnlyList<string> ids = ["resource"];

        var query = db.LearningResources.Where(resource =>
            ids.Count == 0 || resource.Id == "resource");

        Assert.Contains("Count", DeviceInterpreterQueryGuard.FindClientDispatch(query.Expression));
    }

    [Fact]
    public void Ignores_ValuesReadIntoLocalsAndCallsThatUseTheRow()
    {
        using var fixture = new NativeSliceFixture();
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IReadOnlyList<string> ids = ["resource"];
        ICollection<string> selected = ["resource"];
        var anyResource = ids.Count == 0;

        var query = db.LearningResources.Where(resource =>
            (anyResource || selected.Contains(resource.Id))
            && db.Set<LearningResource>().Any(other => other.Id == resource.Id));

        Assert.Null(DeviceInterpreterQueryGuard.FindClientDispatch(query.Expression));
    }

    [Fact]
    public async Task RecordsAFinding_EvenWhenTheCallerCatchesTheException()
    {
        using var fixture = new NativeSliceFixture();
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            IReadOnlyList<string> ids = [];

            try
            {
                await db.LearningResources
                    .Where(resource => ids.Count == 0 || resource.Id == "resource")
                    .ToListAsync();
                Assert.Fail("The guard did not throw.");
            }
            catch (InvalidOperationException)
            {
                // App code that degrades here would hide the finding without the recorded list.
            }
        }

        var findings = fixture.TakeQueryGuardFindings();

        Assert.Single(findings);
        Assert.Contains("Count", findings[0]);
    }
}
