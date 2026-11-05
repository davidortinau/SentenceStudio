using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Proves <see cref="DeviceInterpreterQueryGuard"/> fires on the two shapes that crashed DX24 on
/// 2026-11-05 and on related client-evaluated shapes, stays quiet on shapes EF translates or that
/// were read into locals, and fails the test even when app code catches its exception.
/// </summary>
public sealed class DeviceInterpreterQueryGuardTests
{
    [Fact]
    public void Flags_TheContextsDatabaseInsideAProjection()
    {
        using var db = Context();

        var query = db.LearningResources.Select(resource =>
            db.Database.IsSqlite() ? resource.Title : null);

        Assert.Contains("Database", DeviceInterpreterQueryGuard.FindClientDispatch(query.Expression));
    }

    [Fact]
    public void Flags_AnInterfaceCountInsideAClientEvaluatedCondition()
    {
        using var db = Context();
        IReadOnlyList<string> ids = ["resource"];

        var query = db.LearningResources.Where(resource =>
            ids.Count == 0 || resource.Id == "resource");

        Assert.Contains("Count", DeviceInterpreterQueryGuard.FindClientDispatch(query.Expression));
    }

    [Fact]
    public void Flags_ACountOverACapturedSubqueryWithItsOwnLambda()
    {
        using var db = Context();
        List<string> ids = ["resource"];

        // The inner lambda's parameter does not make the subtree depend on the row.
        var query = db.LearningResources.Where(resource =>
            ids.Where(id => id != "").ToList().Count == 0 || resource.Id == "resource");

        Assert.Contains("Count", DeviceInterpreterQueryGuard.FindClientDispatch(query.Expression));
    }

    [Fact]
    public void Flags_AVirtualCallInsideALambdaThatEfEvaluatesOnTheClient()
    {
        using var db = Context();
        List<List<string>> lists = [["resource"]];

        // lists.Any(...) uses no row, so EF interprets it, calling List<T>.Count for each l.
        var query = db.LearningResources.Where(resource =>
            lists.Any(list => list.Count > 0) || resource.Id == "resource");

        Assert.Contains("Count", DeviceInterpreterQueryGuard.FindClientDispatch(query.Expression));
    }

    [Fact]
    public void Flags_AComparerCallEvenWhenItsArgumentsUseTheRow()
    {
        using var db = Context();
        IEqualityComparer<string> comparer = StringComparer.Ordinal;

        // EF cannot translate a comparer, so the projection runs it on the client.
        var query = db.LearningResources.Select(resource =>
            comparer.Equals(resource.Title, resource.Description));

        Assert.Contains("Equals", DeviceInterpreterQueryGuard.FindClientDispatch(query.Expression));
    }

    [Fact]
    public void Ignores_AnInlineLambdaInvokedWithTheRow()
    {
        using var db = Context();
        System.Linq.Expressions.Expression<Func<LearningResource, bool>> owned =
            resource => resource.UserProfileId == "owner";
        var row = System.Linq.Expressions.Expression.Parameter(typeof(LearningResource), "row");
        var predicate = System.Linq.Expressions.Expression.Lambda<Func<LearningResource, bool>>(
            System.Linq.Expressions.Expression.Invoke(owned, row), row);

        var query = db.LearningResources.Where(predicate);

        Assert.Null(DeviceInterpreterQueryGuard.FindClientDispatch(query.Expression));
    }

    [Fact]
    public void Flags_ACapturedDelegateInvokedInsideTheQuery()
    {
        using var db = Context();
        Func<string> owner = () => "owner";

        var query = db.LearningResources.Where(resource => resource.UserProfileId == owner());

        Assert.Contains("delegate", DeviceInterpreterQueryGuard.FindClientDispatch(query.Expression));
    }

    [Fact]
    public void Ignores_ValuesReadIntoLocalsAndCallsEfTranslatesWithTheRow()
    {
        using var db = Context();
        IReadOnlyList<string> ids = ["resource"];
        ICollection<string> selected = ["resource"];
        var owner = "owner";
        var anyResource = ids.Count == 0;

        var query = db.LearningResources.Where(resource =>
            (anyResource || selected.Contains(resource.Id))
            && owner.Equals(resource.UserProfileId)
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

    // Builds queries for inspection only; nothing is executed, so no database is opened.
    private static ApplicationDbContext Context() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
}
