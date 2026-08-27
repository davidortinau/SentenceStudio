using System.Security.Claims;
using SentenceStudio.Contracts;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Pins the rule that decides whether the coach surfaces have changed hands.
/// </summary>
/// <remarks>
/// <para>
/// Two failure modes, in opposite directions, and both matter. Reading two principals for the same
/// learner as two learners throws away a conversation on every token refresh — the MAUI
/// optimistic-principal handoff would do it on every cold start. Reading two learners as one is the
/// leak this revision exists to close. The tests below are written in pairs for that reason.
/// </para>
/// <para>
/// The third group is the one review added: matching must be <em>typed</em>. An earlier revision
/// pooled every readable claim value into one bucket, so a learner whose display name was another
/// learner's email address matched that learner. Those cases are pinned here because nothing else
/// in the suite would notice the bucket coming back.
/// </para>
/// </remarks>
public class CoachAccountIdentityTests
{
    private static ClaimsPrincipal Jwt(string profileId, string email, string? subject = null) =>
        new(new ClaimsIdentity(
            [
                new Claim(AuthClaimTypes.UserProfileId, profileId),
                new Claim("sub", subject ?? profileId),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, email)
            ],
            authenticationType: "jwt"));

    private static ClaimsPrincipal Optimistic(string email) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.AuthenticationMethod, "refresh_token_pending"),
                new Claim(ClaimTypes.Name, email),
                new Claim(ClaimTypes.Email, email)
            ],
            authenticationType: "optimistic"));

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)),
            authenticationType: "jwt"));

    // ================================================================ anonymous

    [Fact]
    public void A_null_principal_is_anonymous()
    {
        CoachAccountIdentity.From(null).IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public void An_unauthenticated_identity_is_anonymous()
    {
        CoachAccountIdentity.From(new ClaimsPrincipal(new ClaimsIdentity()))
            .IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public void Signed_out_twice_is_not_a_boundary()
    {
        CoachAccountIdentity.Anonymous
            .IsSameAccountAs(CoachAccountIdentity.From(null))
            .Should().BeTrue("a repeated signed-out notification must not keep clearing");
    }

    [Fact]
    public void Signing_out_crosses_the_boundary()
    {
        var signedIn = CoachAccountIdentity.From(Jwt("profile-a", "a@example.test"));

        signedIn.IsSameAccountAs(CoachAccountIdentity.Anonymous).Should().BeFalse();
        CoachAccountIdentity.Anonymous.IsSameAccountAs(signedIn).Should().BeFalse();
    }

    // ================================================================ same learner

    [Fact]
    public void The_same_token_read_twice_is_the_same_learner()
    {
        var first = CoachAccountIdentity.From(Jwt("profile-a", "a@example.test"));
        var second = CoachAccountIdentity.From(Jwt("profile-a", "a@example.test"));

        first.IsSameAccountAs(second).Should().BeTrue();
    }

    /// <summary>
    /// The MAUI cold-start handoff: an optimistic principal built from a remembered email, then the
    /// real JWT once the refresh completes. The email is the only field they share, which is
    /// exactly why email is a matched field and display name is not.
    /// </summary>
    [Fact]
    public void The_optimistic_principal_and_the_token_that_replaces_it_are_the_same_learner()
    {
        var optimistic = CoachAccountIdentity.From(Optimistic("a@example.test"));
        var real = CoachAccountIdentity.From(Jwt("profile-a", "a@example.test"));

        optimistic.IsSameAccountAs(real).Should().BeTrue(
            "a token refresh for the learner already on screen must not cost their conversation");
        real.IsSameAccountAs(optimistic).Should().BeTrue("and the comparison is symmetric");
    }

    /// <summary>A refreshed token can carry claims the first one did not. Profile id is enough.</summary>
    [Fact]
    public void A_topped_up_principal_is_still_the_same_learner()
    {
        var thin = CoachAccountIdentity.From(
            Principal((AuthClaimTypes.UserProfileId, "profile-a")));

        var full = CoachAccountIdentity.From(Jwt("profile-a", "a@example.test"));

        thin.IsSameAccountAs(full).Should().BeTrue();
    }

    /// <summary>Profile id is matched against profile id, on its own.</summary>
    [Fact]
    public void A_shared_profile_id_alone_is_the_same_learner()
    {
        var left = CoachAccountIdentity.From(
            Principal((AuthClaimTypes.UserProfileId, "profile-a"), ("sub", "device-1")));
        var right = CoachAccountIdentity.From(
            Principal((AuthClaimTypes.UserProfileId, "profile-a"), ("sub", "device-2")));

        left.IsSameAccountAs(right).Should().BeTrue("the profile id is the account");
    }

    /// <summary>Subject is matched against subject, on its own.</summary>
    [Fact]
    public void A_shared_subject_alone_is_the_same_learner()
    {
        var left = CoachAccountIdentity.From(Principal(("sub", "subject-a")));
        var right = CoachAccountIdentity.From(Principal(("sub", "subject-a")));

        left.IsSameAccountAs(right).Should().BeTrue();
    }

    /// <summary>
    /// A raw <c>sub</c> and a mapped <see cref="ClaimTypes.NameIdentifier"/> are one token read two
    /// ways, not two learners. MAUI reads the JWT unmapped; ASP.NET Core maps by default.
    /// </summary>
    [Fact]
    public void A_mapped_and_an_unmapped_subject_are_the_same_learner()
    {
        var raw = CoachAccountIdentity.From(Principal(("sub", "subject-a")));
        var mapped = CoachAccountIdentity.From(Principal((ClaimTypes.NameIdentifier, "subject-a")));

        raw.IsSameAccountAs(mapped).Should().BeTrue();
    }

    /// <summary>The same for the two spellings of email.</summary>
    [Fact]
    public void A_mapped_and_an_unmapped_email_are_the_same_learner()
    {
        var raw = CoachAccountIdentity.From(Principal(("email", "a@example.test")));
        var mapped = CoachAccountIdentity.From(Principal((ClaimTypes.Email, "a@example.test")));

        raw.IsSameAccountAs(mapped).Should().BeTrue();
    }

    /// <summary>Case and stray whitespace in an email are not two learners.</summary>
    [Fact]
    public void The_same_email_in_different_letter_case_is_the_same_learner()
    {
        var lower = CoachAccountIdentity.From(Optimistic("a@example.test"));
        var upper = CoachAccountIdentity.From(Optimistic("  A@Example.TEST "));

        lower.IsSameAccountAs(upper).Should().BeTrue();
        upper.IsSameAccountAs(lower).Should().BeTrue();
    }

    // ================================================================ different learner

    [Fact]
    public void Two_accounts_are_never_the_same_learner()
    {
        var a = CoachAccountIdentity.From(Jwt("profile-a", "a@example.test"));
        var b = CoachAccountIdentity.From(Jwt("profile-b", "b@example.test"));

        a.IsSameAccountAs(b).Should().BeFalse();
    }

    /// <summary>
    /// Signing in as somebody else without an observable signed-out step in between — the case a
    /// logout-button-only defence misses entirely.
    /// </summary>
    [Fact]
    public void Switching_accounts_directly_crosses_the_boundary()
    {
        var a = CoachAccountIdentity.From(Optimistic("a@example.test"));
        var b = CoachAccountIdentity.From(Optimistic("b@example.test"));

        a.IsSameAccountAs(b).Should().BeFalse();
        b.IsSameAccountAs(a).Should().BeFalse();
    }

    /// <summary>Different profile ids are different learners even when nothing else is readable.</summary>
    [Fact]
    public void Different_profile_ids_are_different_learners()
    {
        var a = CoachAccountIdentity.From(Principal((AuthClaimTypes.UserProfileId, "profile-a")));
        var b = CoachAccountIdentity.From(Principal((AuthClaimTypes.UserProfileId, "profile-b")));

        a.IsSameAccountAs(b).Should().BeFalse();
    }

    // ================================================================ typed matching

    /// <summary>
    /// A display name is learner-chosen text, and a learner may set it to anything — including
    /// another learner's email address. Matching it would hand that learner's conversation over.
    /// </summary>
    [Fact]
    public void A_display_name_equal_to_another_learners_email_is_not_that_learner()
    {
        var impostor = CoachAccountIdentity.From(Principal(
            (AuthClaimTypes.UserProfileId, "profile-a"),
            ("sub", "subject-a"),
            (ClaimTypes.Email, "a@example.test"),
            (ClaimTypes.Name, "b@example.test")));

        var victim = CoachAccountIdentity.From(Principal(
            (AuthClaimTypes.UserProfileId, "profile-b"),
            ("sub", "subject-b"),
            (ClaimTypes.Email, "b@example.test"),
            (ClaimTypes.Name, "Learner B")));

        impostor.IsSameAccountAs(victim).Should().BeFalse(
            "a display name is not an identifier and must never be an overlap key");
        victim.IsSameAccountAs(impostor).Should().BeFalse();
    }

    /// <summary>
    /// The same, with the display name as the <em>only</em> thing either principal carries beyond
    /// its own identifiers — so nothing else can accidentally rescue the assertion.
    /// </summary>
    [Fact]
    public void A_display_name_is_not_read_at_all()
    {
        var namedOnly = CoachAccountIdentity.From(Principal((ClaimTypes.Name, "a@example.test")));
        var emailOnly = CoachAccountIdentity.From(Principal((ClaimTypes.Email, "a@example.test")));

        namedOnly.HasStableIdentifier.Should().BeFalse("a display name is not a stable identifier");
        namedOnly.IsSameAccountAs(emailOnly).Should().BeFalse();
        emailOnly.IsSameAccountAs(namedOnly).Should().BeFalse();
    }

    /// <summary><c>preferred_username</c> is display text too, and is read no differently.</summary>
    [Fact]
    public void A_preferred_username_is_not_read_either()
    {
        var left = CoachAccountIdentity.From(Principal(("preferred_username", "shared-handle")));
        var right = CoachAccountIdentity.From(Principal(("preferred_username", "shared-handle")));
        var email = CoachAccountIdentity.From(Principal((ClaimTypes.Email, "shared-handle")));

        left.HasStableIdentifier.Should().BeFalse();
        left.IsSameAccountAs(email).Should().BeFalse();

        // Two principals with identical content are still a re-read of the same principal.
        left.IsSameAccountAs(right).Should().BeTrue(
            "content identity is the fallback when nothing typed is asserted");
    }

    /// <summary>
    /// The same raw string under two different claim types is a coincidence, not an account.
    /// </summary>
    [Theory]
    [InlineData(AuthClaimTypes.UserProfileId, "sub")]
    [InlineData(AuthClaimTypes.UserProfileId, "email")]
    [InlineData("sub", "email")]
    public void The_same_string_under_different_claim_types_is_not_the_same_learner(
        string leftType, string rightType)
    {
        const string Shared = "collision-value";

        var left = CoachAccountIdentity.From(Principal((leftType, Shared)));
        var right = CoachAccountIdentity.From(Principal((rightType, Shared)));

        left.HasStableIdentifier.Should().BeTrue();
        right.HasStableIdentifier.Should().BeTrue();
        left.IsSameAccountAs(right).Should().BeFalse(
            "fields are compared like against like, never pooled");
    }

    /// <summary>
    /// Two principals that assert different typed fields prove nothing about each other. The
    /// conservative answer is "different", because a wrong "same" hands over a conversation and a
    /// wrong "different" costs a reload.
    /// </summary>
    [Fact]
    public void Principals_with_no_field_in_common_are_treated_as_different_learners()
    {
        var byProfile = CoachAccountIdentity.From(
            Principal((AuthClaimTypes.UserProfileId, "profile-a")));
        var byEmail = CoachAccountIdentity.From(
            Principal((ClaimTypes.Email, "a@example.test")));

        byProfile.IsSameAccountAs(byEmail).Should().BeFalse();
    }

    // ================================================================ synthetic fallback

    /// <summary>
    /// An authenticated principal with no typed identifier is not everybody and not nobody. It
    /// matches a re-read of itself and nothing else.
    /// </summary>
    [Fact]
    public void An_unreadable_principal_matches_only_an_identical_principal()
    {
        var blank = CoachAccountIdentity.From(
            new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "jwt")));
        var blankAgain = CoachAccountIdentity.From(
            new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "jwt")));
        var typed = CoachAccountIdentity.From(Jwt("profile-a", "a@example.test"));

        blank.IsAuthenticated.Should().BeTrue();
        blank.HasStableIdentifier.Should().BeFalse();
        blank.IsSameAccountAs(blankAgain).Should().BeTrue("a re-read of the same principal");
        blank.IsSameAccountAs(typed).Should().BeFalse();
        typed.IsSameAccountAs(blank).Should().BeFalse();
        blank.IsSameAccountAs(CoachAccountIdentity.Anonymous).Should().BeFalse();
    }

    /// <summary>
    /// Two <em>different</em> unreadable principals are not each other. A shared constant here
    /// would have made every such principal the same learner.
    /// </summary>
    [Fact]
    public void Two_different_unreadable_principals_do_not_match()
    {
        var left = CoachAccountIdentity.From(Principal((ClaimTypes.Name, "Learner A")));
        var right = CoachAccountIdentity.From(Principal((ClaimTypes.Name, "Learner B")));

        left.HasStableIdentifier.Should().BeFalse();
        right.HasStableIdentifier.Should().BeFalse();
        left.IsSameAccountAs(right).Should().BeFalse();
    }

    /// <summary>The synthetic key never widens a match that a typed field already refused.</summary>
    [Fact]
    public void The_synthetic_key_is_absent_whenever_anything_typed_is_asserted()
    {
        CoachAccountIdentity.From(Jwt("profile-a", "a@example.test"))
            .SyntheticKey.Should().BeNull();

        CoachAccountIdentity.From(Principal((ClaimTypes.Name, "Learner A")))
            .SyntheticKey.Should().NotBeNull();
    }

    /// <summary>Nothing readable is kept verbatim in the fallback key.</summary>
    [Fact]
    public void The_synthetic_key_does_not_carry_the_values_it_was_derived_from()
    {
        var identity = CoachAccountIdentity.From(Principal((ClaimTypes.Name, "a@example.test")));

        identity.SyntheticKey.Should().NotBeNull();
        identity.SyntheticKey!.Should().NotContain("a@example.test");
        identity.ToString().Should().NotContain("a@example.test");
    }
}
