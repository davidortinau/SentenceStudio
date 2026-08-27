using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Proves the coach registration adds every tool as a scoped service, so each
/// request resolves its own user scope.
/// </summary>
public class CoachToolRegistrationTests
{
    private static readonly Type[] ScopedTools =
    [
        typeof(LearnerProfileSummaryTool),
        typeof(PracticeBalanceTool),
        typeof(VocabularyDueSummaryTool),
        typeof(ResourceCatalogTool),
        typeof(PreviewPracticePlanTool),
        typeof(ICoachToolFactory),
        typeof(ICoachPlanPreviewFailureAdapter),
        typeof(CoachDueItemLeakValidator)
    ];

    [Fact]
    public void Every_tool_is_registered_as_a_scoped_service()
    {
        var services = new ServiceCollection().AddCoachReadOnlyTools();

        foreach (var toolType in ScopedTools)
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == toolType);
            descriptor.Should().NotBeNull($"{toolType.Name} must be registered");
            descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped, $"{toolType.Name} must be per request");
        }
    }

    [Fact]
    public void The_validators_are_registered()
    {
        var services = new ServiceCollection().AddCoachReadOnlyTools();

        services.Should().Contain(d => d.ServiceType == typeof(CoachEmbargoScanner));
        services.Should().Contain(d => d.ServiceType == typeof(CoachIntentValidator));
        services.Should().Contain(d => d.ServiceType == typeof(CoachToolAllowList));
    }

    [Fact]
    public void The_tool_factory_resolves_and_produces_the_allowed_set()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddDbContext<ApplicationDbContext>(o => o
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        // The tools read through the application query contracts now, so the container has to
        // carry the repositories those contracts alias onto. A host that registered the tools and
        // forgot these would resolve nothing and fail on the first turn; this is where that shows
        // up instead.
        services.AddCoachToolDataServices();

        services.AddScoped<IUserScopeProvider>(_ => new FakeUserScopeProvider(CoachToolTestFixture.UserA));
        services.AddScoped<IPlanDateContext>(_ => new PlanDateContext(TimeZoneInfo.Utc));
        services.AddScoped<IDeterministicPlanGenerator>(_ => new RecordingPlanGenerator(_ => null));
        services.AddSingleton<ILanguageSegmenter, KoreanLanguageSegmenter>();
        services.Configure<SentenceStudio.Api.Coach.Runtime.CoachOptions>(_ => { });
        services.AddCoachReadOnlyTools();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var tools = scope.ServiceProvider.GetRequiredService<ICoachToolFactory>().CreateTools();

        tools.Select(t => t.Name).Should().Equal(CoachToolNames.All);
        new CoachToolAllowList().Validate(tools).IsValid.Should().BeTrue();
        scope.ServiceProvider.GetRequiredService<CoachDueItemLeakValidator>().Should().NotBeNull();

        connection.Dispose();
    }
}
