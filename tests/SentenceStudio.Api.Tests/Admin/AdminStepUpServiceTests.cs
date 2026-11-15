using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Tests.Coach;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>The password-again proof: who it names, how long it lasts, and what ends it early.</summary>
public sealed class AdminStepUpServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly TestTimeProvider _time = new(Now);
    private readonly AdminStepUpService _service;

    public AdminStepUpServiceTests()
    {
        _service = new AdminStepUpService(new EphemeralDataProtectionProvider(), _time);
    }

    private static AdminActor Admin(string id = "admin-1", string? stamp = "stamp-1") =>
        new(AdminActorKind.Admin, id, "captain@admin-tests.local", "Captain", AdminStepUpService.DigestOf(stamp));

    [Fact]
    public void A_proof_is_valid_for_the_account_it_was_issued_to_until_it_expires()
    {
        var proof = _service.Issue(Admin())!;

        proof.ExpiresAtUtc.Should().Be(Now.AddMinutes(10));
        _service.Validate(Admin(), proof.Token).Should().Be(proof.ExpiresAtUtc);

        _time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        _service.Validate(Admin(), proof.Token).Should().NotBeNull();

        _time.Advance(TimeSpan.FromSeconds(1));
        _service.Validate(Admin(), proof.Token).Should().BeNull("a proof is dead at its expiry, not after it");
    }

    [Fact]
    public void A_proof_names_one_account()
    {
        var proof = _service.Issue(Admin("admin-1"))!;

        _service.Validate(Admin("admin-2"), proof.Token).Should().BeNull();
    }

    [Fact]
    public void A_changed_security_stamp_ends_the_proof()
    {
        var proof = _service.Issue(Admin(stamp: "before"))!;

        _service.Validate(Admin(stamp: "after"), proof.Token).Should().BeNull();
    }

    [Fact]
    public void An_agent_token_can_neither_earn_nor_use_a_proof()
    {
        var token = new AdminActor(
            AdminActorKind.AgentToken, "admin-1", null, null, AdminStepUpService.DigestOf("stamp-1"));
        var adminProof = _service.Issue(Admin())!;

        _service.Issue(token).Should().BeNull();
        _service.Validate(token, adminProof.Token).Should().BeNull(
            "a token that shares an id with an account must not inherit the account's proof");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_account_with_no_security_stamp_cannot_hold_a_proof(string? stamp)
    {
        _service.Issue(Admin(stamp: stamp)).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-proof")]
    [InlineData("CfDJ8AAAAAAAAAAAAAAAAAAAAAA")]
    public void Anything_that_is_not_a_proof_is_refused_without_throwing(string? value)
    {
        _service.Validate(Admin(), value).Should().BeNull();
    }

    [Fact]
    public void The_actor_carries_a_digest_of_the_security_stamp_never_the_stamp()
    {
        // A record prints its members, so whatever the actor holds can end up in a log line.
        var actor = Admin(stamp: "the-real-security-stamp");

        actor.ToString().Should().NotContain("the-real-security-stamp");
        AdminStepUpService.DigestOf("the-real-security-stamp").Should().NotContain("the-real-security-stamp");
        AdminStepUpService.DigestOf(null).Should().BeNull();
        AdminStepUpService.DigestOf("").Should().BeNull();
    }

    [Fact]
    public void An_oversized_header_is_refused_before_it_is_unsealed()
    {
        _service.Validate(Admin(), new string('A', 5000)).Should().BeNull();
    }

    [Fact]
    public void A_tampered_proof_is_refused()
    {
        var proof = _service.Issue(Admin())!.Token;
        var flipped = proof[..^2] + (proof[^2] == 'A' ? 'B' : 'A') + proof[^1];

        _service.Validate(Admin(), flipped).Should().BeNull();
    }

    [Fact]
    public void A_proof_sealed_by_another_key_ring_is_refused()
    {
        var other = new AdminStepUpService(new EphemeralDataProtectionProvider(), _time);
        var foreign = other.Issue(Admin())!;

        _service.Validate(Admin(), foreign.Token).Should().BeNull();
    }

    [Fact]
    public void Something_sealed_for_another_purpose_on_the_same_key_ring_is_not_a_proof()
    {
        // The purpose string is what keeps a value the API protected for some other feature from
        // being replayed here as a proof.
        var provider = new EphemeralDataProtectionProvider();
        var service = new AdminStepUpService(provider, _time);
        var payload = string.Join('\n', "admin-1",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("stamp-1"u8)),
            Now.AddHours(1).ToUnixTimeSeconds());
        var elsewhere = provider.CreateProtector("SomethingElse").Protect(payload);

        service.Validate(Admin(), elsewhere).Should().BeNull();
    }

    [Fact]
    public async Task The_filter_refuses_a_request_without_a_live_proof_and_passes_one_with_it()
    {
        var filter = new AdminStepUpFilter(_service);
        var proof = _service.Issue(Admin())!;

        var without = NewContext(proof: null);
        var refused = await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(without), _ => ValueTask.FromResult<object?>("ran"));
        refused.Should().NotBe("ran");
        AdminRefusal.FindCode(without).Should().Be(AdminFailureCodes.StepUpRequired);

        var with = NewContext(proof.Token);
        var passed = await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(with), _ => ValueTask.FromResult<object?>("ran"));
        passed.Should().Be("ran");

        static DefaultHttpContext NewContext(string? proof)
        {
            var http = new DefaultHttpContext();
            http.Features.Set<IAdminActorFeature>(new AdminActorFeature(Admin()));
            if (proof is not null)
            {
                http.Request.Headers[AdminStepUpService.HeaderName] = proof;
            }

            return http;
        }
    }
}
