using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Write handlers stay request-scoped and cover every write tool. Kept from the removed growth
/// freeze: adding a handler is fine, registering one as a singleton is not, because each
/// depends on a request-scoped database context and the current learner.
/// </summary>
public class CoachWriteHandlerRegistrationTests
{
    private static IReadOnlyList<ServiceDescriptor> HandlerDescriptors()
    {
        var services = new ServiceCollection();
        services.AddCoachReadOnlyTools();
        return services.Where(d => d.ServiceType == typeof(ICoachWriteHandler)).ToList();
    }

    [Fact]
    public void Every_write_handler_is_scoped()
    {
        HandlerDescriptors().Should().NotBeEmpty()
            .And.OnlyContain(d => d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void Every_write_tool_has_exactly_one_handler_type()
    {
        HandlerDescriptors().Should().HaveCount(CoachToolNames.AllWrite.Count,
            "each propose_ tool needs one handler and no handler may be registered twice");
    }
}
