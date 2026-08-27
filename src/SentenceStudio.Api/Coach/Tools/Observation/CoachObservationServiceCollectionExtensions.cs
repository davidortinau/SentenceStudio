using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// Registers the per-turn observation buffer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped, and one instance behind two interfaces.</b> The read half and the write half resolve
/// to the same object, so a consumer projecting evidence and the seam recording a call are looking
/// at one turn's list — while a holder of the read half still cannot append to it.
/// </para>
/// <para>
/// <b>Not called by the host yet, deliberately.</b> W4a delivers the seam; the first consumer that
/// needs a live buffer is W4b's trace subscriber, and W3b's evidence projection is the second.
/// Registering it now would add a scoped allocation to every coach request for a list nobody reads.
/// The seam resolves the buffer optionally, so wiring this later is a one-line change and turns the
/// collector on without touching the seam again.
/// </para>
/// <para>
/// The opportunity observer is <em>not</em> registered here. It is composed by
/// <c>CoachToolFactory</c> from the recorder that is already in the container, which keeps its
/// position as subscriber 1 a property of the seam rather than of registration order in a file
/// somebody may reorder.
/// </para>
/// </remarks>
public static class CoachObservationServiceCollectionExtensions
{
    /// <summary>Adds the request-scoped turn observation buffer and its collector.</summary>
    public static IServiceCollection AddCoachToolObservation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<CoachTurnObservationBuffer>();
        services.TryAddScoped<ICoachTurnObservationBuffer>(
            sp => sp.GetRequiredService<CoachTurnObservationBuffer>());
        services.TryAddScoped<ICoachTurnObservationSink>(
            sp => sp.GetRequiredService<CoachTurnObservationBuffer>());

        return services;
    }
}
