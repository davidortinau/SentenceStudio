using FluentAssertions;
using SentenceStudio.WebApp.Auth;

namespace SentenceStudio.UI.Tests.Auth;

/// <summary>
/// The webapp's auth log lines must identify an account without recording its address.
/// </summary>
/// <remarks>
/// <para>
/// <c>ServerAuthService</c> writes a line on every registration, every successful sign-in and every
/// failed sign-in. Those three together are enough to reconstruct who uses the product and when, so
/// an unmasked address there turns the ordinary application log into personal data at rest —
/// retained wherever logs are retained, and readable by anyone who can read a log.
/// </para>
/// <para>
/// Found by the final verification gate on 2026-08-19: searching Aspire's structured logs for a
/// test account's address returned two entries carrying it in full, in both the rendered message
/// and an <c>Email</c> attribute. The Coach subsystem was already clean; this was the auth path.
/// </para>
/// </remarks>
public class ServerAuthEmailMaskingTests
{
    [Theory]
    [InlineData("squad-jayne@sentencestudio.test", "squ***@sentencestudio.test")]
    [InlineData("squad-kaylee@sentencestudio.test", "squ***@sentencestudio.test")]
    // A local part short enough to fit inside the prefix must be dropped whole. Keeping "ab" out of
    // "ab@example.com" is not a rounding error: the addresses most likely to be two or three
    // characters are a person's initials, and a three-letter prefix of a three-letter local part is
    // the entire address.
    [InlineData("ab@example.com", "***@example.com")]
    [InlineData("a@example.com", "***@example.com")]
    [InlineData("abc@example.com", "***@example.com")]
    [InlineData("abcd@example.com", "abc***@example.com")]
    public void An_address_is_reduced_to_a_short_prefix_and_its_domain(string email, string expected)
    {
        ServerAuthLogRedaction.MaskEmail(email).Should().Be(expected);
    }

    [Theory]
    [InlineData("squad-jayne@sentencestudio.test")]
    [InlineData("someone.with.a.long.local.part@example.org")]
    [InlineData("ab@example.com")]
    [InlineData("abcd@example.com")]
    [InlineData("a@example.com")]
    public void The_local_part_never_survives_masking(string email)
    {
        var local = email[..email.IndexOf('@')];

        var masked = ServerAuthLogRedaction.MaskEmail(email);

        // Compare against the masked value's own local segment rather than the whole string. The
        // domain is kept deliberately, and a one-character local part like "a" occurs inside
        // "example.com" by coincidence — asserting on the whole string would fail on that
        // coincidence while saying nothing about whether the person was identified.
        var maskedLocal = masked[..masked.IndexOf('@')];

        maskedLocal.Should().NotContain(local,
            "the part that identifies the person is what must go");
        masked.Should().NotBe(email);
    }

    [Theory]
    [InlineData(null, "(empty)")]
    [InlineData("", "(empty)")]
    [InlineData("   ", "(empty)")]
    [InlineData("not-an-email", "***")]
    [InlineData("@nolocalpart.test", "***")]
    [InlineData("trailing@", "***")]
    [InlineData("@", "***")]
    public void Anything_that_is_not_an_address_is_refused_rather_than_echoed(string? value, string expected)
    {
        // A malformed value is still learner-supplied input. Echoing it because it failed a parse
        // is how the unmasked string gets into the log by the back door.
        ServerAuthLogRedaction.MaskEmail(value).Should().Be(expected);
    }

    [Fact]
    public void The_domain_survives_so_a_wrong_environment_is_still_diagnosable()
    {
        ServerAuthLogRedaction.MaskEmail("someone@sentencestudio.test")
            .Should().EndWith("@sentencestudio.test");
    }

    [Fact]
    public void It_matches_the_convention_the_data_recovery_path_already_uses()
    {
        // Same shape as DataRecoveryService.MaskEmail — three characters, then the domain — so an
        // operator reading either log does not have to learn two conventions.
        ServerAuthLogRedaction.MaskEmail("davidortinau@ortinau.com").Should().Be("dav***@ortinau.com");
    }
}
