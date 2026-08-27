using Microsoft.Extensions.Hosting;
using SentenceStudio.Api.Coach.Telemetry;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Pins the coach OpenTelemetry names to the literals registered in
/// <c>SentenceStudio.ServiceDefaults</c>.
/// </summary>
/// <remarks>
/// ServiceDefaults is MAUI-safe and cannot reference the API, so the names exist in two places.
/// If they drift, coach spans and metrics are still created but never exported, and nothing else
/// fails — exactly the kind of silent hole a test has to cover.
/// </remarks>
public class CoachTelemetryNameContractTests
{
    [Fact]
    public void ActivitySourceName_MatchesTheNameRegisteredWithOpenTelemetry()
        => CoachTelemetry.ActivitySourceName.Should().Be(CoachTelemetryNames.ActivitySourceName);

    [Fact]
    public void MeterName_MatchesTheNameRegisteredWithOpenTelemetry()
        => CoachTelemetry.MeterName.Should().Be(CoachTelemetryNames.MeterName);
}
