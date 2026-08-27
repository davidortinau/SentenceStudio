using System.Reflection;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Reports.Endpoints;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Reports;

/// <summary>
/// A report is a person pressing a button, and nothing else may produce one.
/// </summary>
/// <remarks>
/// <para>
/// This is the property that makes a <c>UserReportedResponse</c> ledger row worth reading. Every
/// other row on that table is the server observing itself; this one is a claim about what a human
/// thought, and the moment a model can produce one the claim is worthless — an attacker who can
/// place text in a corpus Sam reads would have a channel into a reviewer's screen, and the coach
/// would have a channel for filing complaints about itself.
/// </para>
/// <para>
/// Proven by a type-graph walk and a registry check rather than by asserting that no current call
/// site does it, for the same reason <c>CoachOpportunityNoFeedbackLoopTests</c> uses that
/// technique: a call site can be added, and a reachability proof cannot be quietly undone.
/// </para>
/// </remarks>
public class CoachResponseReportNoModelPathTests
{
    private static readonly Type[] ModelVisibleRoots =
    [
        typeof(CoachTurnIntent),
        typeof(CoachTurnResponse),
        typeof(CoachWritePreview),
        typeof(CoachWriteOperationDto)
    ];

    [Fact]
    public void NoModelVisibleShapeCanReachTheReportSurface()
    {
        foreach (var root in ModelVisibleRoots)
        {
            var offenders = Reachable(root)
                .Where(type => type.Namespace?.StartsWith(
                    "SentenceStudio.Api.Coach.Reports", StringComparison.Ordinal) == true)
                .Select(type => type.FullName)
                .ToList();

            offenders.Should().BeEmpty(
                $"nothing the model can see or produce may reach the report surface, but " +
                $"{root.Name} does");
        }
    }

    /// <summary>
    /// No registered tool names a report route, and no tool name appears in one.
    /// </summary>
    /// <remarks>
    /// The registry is the complete set of things the model may invoke. If none of them names the
    /// route prefix, and the route prefix names none of them, there is no path from a model
    /// completion to a report — not by calling one, and not by talking a handler into one.
    /// </remarks>
    [Fact]
    public void NoRegisteredToolNamesTheReportRoutes()
    {
        var registry = new CoachToolRegistry(new Api.Coach.Runtime.CoachOptions
        {
            Enabled = true,
            DurableHistory = new Api.Coach.Runtime.CoachFeatureSwitch { Enabled = true },
            SamOverlay = new Api.Coach.Runtime.CoachFeatureSwitch { Enabled = true },
            SamReadTools = new Api.Coach.Runtime.CoachFeatureSwitch { Enabled = true },
            SamWriteTools = new Api.Coach.Runtime.CoachFeatureSwitch { Enabled = true }
        });

        var toolNames = registry.All.Select(registration => registration.Name).ToList();

        toolNames.Should().NotBeEmpty("the check is only meaningful against a populated registry");

        toolNames.Should().NotContain(name =>
            name.Contains("report", StringComparison.OrdinalIgnoreCase));

        toolNames.Should().NotContain(name =>
            CoachResponseReportEndpoints.RoutePrefix.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The service takes nothing a model could have produced.
    /// </summary>
    /// <remarks>
    /// The owner comes from the request scope, the response is named in the route, and the only
    /// body member is a closed enum. There is no parameter a model completion could flow into, so
    /// "a model cannot file a report" is a property of the signature rather than of the callers.
    /// </remarks>
    [Fact]
    public void TheServiceTakesNoStringAModelCouldHaveProduced()
    {
        var report = typeof(CoachResponseReportService)
            .GetMethod(nameof(CoachResponseReportService.ReportAsync))!;

        report.GetParameters().Select(p => p.ParameterType)
            .Should().BeEquivalentTo(new[]
            {
                typeof(string),                      // conversationId, from the route
                typeof(string),                      // messageId, from the route
                typeof(CoachResponseReportRequest),  // the reason, from the learner's body
                typeof(CancellationToken)
            });

        typeof(CoachResponseReportRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Should().ContainSingle()
            .Which.PropertyType.Should().Be(typeof(CoachResponseReportReason),
                "a closed enum is the only thing a report body may carry");
    }

    private static HashSet<Type> Reachable(Type root)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (type is null || !seen.Add(type))
            {
                continue;
            }

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    queue.Enqueue(argument);
                }
            }

            if (type.IsArray && type.GetElementType() is { } element)
            {
                queue.Enqueue(element);
            }

            if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                queue.Enqueue(property.PropertyType);
            }
        }

        return seen;
    }
}
