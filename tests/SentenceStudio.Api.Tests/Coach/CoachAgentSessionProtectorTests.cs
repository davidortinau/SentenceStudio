using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Persistence;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The binding rules for the protected agent-session column.
/// </summary>
/// <remarks>
/// The agent session carries the running conversation, so ciphertext must be bound to the row it
/// belongs to. v1 bound nothing: every learner's session shared one static purpose, so a payload
/// moved between <c>CoachSessions</c> rows decrypted cleanly. These tests fix the v2 chain and
/// the single bounded fallback that keeps pre-upgrade sessions readable.
/// </remarks>
public class CoachAgentSessionProtectorTests
{
    private const string Owner = "profile-owner";
    private const string Stranger = "profile-stranger";
    private const string SessionId = "session-1";
    private const string Payload = """{"messages":["learner asked about 은/는"]}""";

    private static DataProtectionCoachAgentSessionProtector Create(IDataProtectionProvider provider) =>
        new(provider, NullLogger<DataProtectionCoachAgentSessionProtector>.Instance);

    private static DataProtectionCoachAgentSessionProtector Create() =>
        Create(new EphemeralDataProtectionProvider());

    [Fact]
    public void RoundTrips_UnderTheSameOwnerAndSession()
    {
        var protector = Create();
        var context = new CoachAgentSessionContext(Owner, SessionId);

        var cipher = protector.Protect(context, Payload);

        cipher.Should().NotBeNullOrEmpty().And.NotContain("은/는", "the column must be ciphertext");
        protector.TryUnprotect(context, cipher, out var plaintext).Should().BeTrue();
        plaintext.Should().Be(Payload);
    }

    [Fact]
    public void Protect_ReturnsNull_ForNothingToStore()
    {
        var protector = Create();
        var context = new CoachAgentSessionContext(Owner, SessionId);

        protector.Protect(context, null).Should().BeNull();
        protector.Protect(context, string.Empty).Should().BeNull();
    }

    [Fact]
    public void TryUnprotect_RejectsCiphertextMovedToAnotherLearnersRow()
    {
        // The v1 failure this change closes: anyone able to write the table could copy one
        // learner's ProtectedAgentSession into another learner's row and have it decrypt.
        var protector = Create();

        var cipher = protector.Protect(new CoachAgentSessionContext(Owner, SessionId), Payload);

        protector.TryUnprotect(new CoachAgentSessionContext(Stranger, SessionId), cipher, out var plaintext)
            .Should().BeFalse();
        plaintext.Should().BeNull();
    }

    [Fact]
    public void TryUnprotect_RejectsCiphertextMovedToAnotherSessionOfTheSameLearner()
    {
        var protector = Create();

        var cipher = protector.Protect(new CoachAgentSessionContext(Owner, SessionId), Payload);

        protector.TryUnprotect(new CoachAgentSessionContext(Owner, "session-2"), cipher, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryUnprotect_ReadsALegacyV1PayloadExactlyOnce()
    {
        // Bounded migration: a session written before the owner/record chain existed must stay
        // readable, or an in-progress conversation is lost on deploy.
        var provider = new EphemeralDataProtectionProvider();
        var legacyCipher = provider
            .CreateProtector(DataProtectionCoachAgentSessionProtector.LegacyPurpose)
            .Protect(Payload);

        var protector = Create(provider);

        protector.TryUnprotect(new CoachAgentSessionContext(Owner, SessionId), legacyCipher, out var plaintext)
            .Should().BeTrue();
        plaintext.Should().Be(Payload);
    }

    [Fact]
    public void LegacyFallback_IsNotAnOwnerBypassForV2Ciphertext()
    {
        // The fallback must widen exactly one thing — the legacy purpose — and nothing else.
        // A v2 payload belonging to someone else stays unreadable even though a fallback exists.
        var protector = Create();

        var cipher = protector.Protect(new CoachAgentSessionContext(Owner, SessionId), Payload);

        protector.TryUnprotect(new CoachAgentSessionContext(Stranger, SessionId), cipher, out _)
            .Should().BeFalse();
        protector.TryUnprotect(new CoachAgentSessionContext(Stranger, "session-2"), cipher, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void Protect_AlwaysWritesTheCurrentPurpose_SoALegacyPayloadRetiresOnTheNextSave()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = Create(provider);
        var context = new CoachAgentSessionContext(Owner, SessionId);

        var rewritten = protector.Protect(context, Payload);

        // The rewritten payload no longer reads under the legacy purpose, which is what makes the
        // fallback age out rather than becoming permanent.
        var legacyProtector = provider.CreateProtector(DataProtectionCoachAgentSessionProtector.LegacyPurpose);
        legacyProtector.Invoking(p => p.Unprotect(rewritten!))
            .Should().Throw<Exception>();

        protector.TryUnprotect(context, rewritten, out var plaintext).Should().BeTrue();
        plaintext.Should().Be(Payload);
    }

    [Fact]
    public void TryUnprotect_FailsClosedOnGarbage()
    {
        var protector = Create();
        var context = new CoachAgentSessionContext(Owner, SessionId);

        protector.TryUnprotect(context, "not-a-data-protection-payload", out var plaintext).Should().BeFalse();
        plaintext.Should().BeNull();

        protector.TryUnprotect(context, null, out _).Should().BeFalse();
        protector.TryUnprotect(context, string.Empty, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("", SessionId)]
    [InlineData("   ", SessionId)]
    [InlineData(Owner, "")]
    [InlineData(Owner, "   ")]
    public void Protect_RefusesAnUnboundContext(string userProfileId, string sessionId)
    {
        var protector = Create();

        protector.Invoking(p => p.Protect(new CoachAgentSessionContext(userProfileId, sessionId), Payload))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ContextIsWhitespaceInsensitive_SoAPaddedIdStillReadsItsOwnRow()
    {
        var protector = Create();

        var cipher = protector.Protect(new CoachAgentSessionContext(Owner, SessionId), Payload);

        protector.TryUnprotect(new CoachAgentSessionContext($"  {Owner} ", $" {SessionId}  "), cipher, out var plaintext)
            .Should().BeTrue();
        plaintext.Should().Be(Payload);
    }
}
