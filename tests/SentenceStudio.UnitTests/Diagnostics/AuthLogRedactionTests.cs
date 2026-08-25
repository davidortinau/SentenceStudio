using System.Globalization;
using FluentAssertions;
using SentenceStudio.Shared.Diagnostics;

namespace SentenceStudio.UnitTests.Diagnostics;

/// <summary>
/// The masking rule every auth and account log line depends on.
/// </summary>
/// <remarks>
/// <para>
/// These lines are written on every registration, sign-in, failed sign-in, password change and
/// deletion, so an address that survives masking is not one leaked record — it is a roster of who
/// uses the product and when, retained for as long as logs are retained.
/// </para>
/// <para>
/// The first attempt at this fix masked four call sites and shipped a helper that returned
/// <c>ab***@example.com</c> for <c>ab@example.com</c>. Both halves of that failure are pinned here:
/// the short-local-part case below, and the source guard in <c>AuthLogPiiGuardTests</c> for the
/// call sites.
/// </para>
/// </remarks>
public class AuthLogRedactionTests
{
    private const string Sentinel = "squad-jayne@sentencestudio.test";

    [Theory]
    [InlineData("squad-jayne@sentencestudio.test", "squ***@sentencestudio.test")]
    [InlineData("davidortinau@ortinau.com", "dav***@ortinau.com")]
    [InlineData("abcd@example.com", "abc***@example.com")]
    public void A_long_enough_local_part_is_reduced_to_three_characters(string email, string expected)
    {
        AuthLogRedaction.MaskEmail(email).Should().Be(expected);
    }

    [Theory]
    [InlineData("a@example.com")]
    [InlineData("ab@example.com")]
    [InlineData("abc@example.com")]
    public void A_local_part_no_longer_than_the_prefix_is_dropped_whole(string email)
    {
        // Showing three characters of a three-character local part is the whole address with
        // decoration. Short addresses are disproportionately people's initials, which is exactly
        // the case where "it's only a prefix" stops being true.
        var local = email[..email.IndexOf('@', StringComparison.Ordinal)];

        var masked = AuthLogRedaction.MaskEmail(email);

        masked.Should().Be("***@example.com");
        masked.Should().StartWith(AuthLogRedaction.RedactedMarker);
        masked.Should().NotStartWith(local);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_value_is_reported_as_absent(string? value)
    {
        AuthLogRedaction.MaskEmail(value).Should().Be(AuthLogRedaction.EmptyMarker);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@nolocalpart.test")]
    [InlineData("@")]
    [InlineData("trailing@")]
    [InlineData("  spaced-out  ")]
    public void A_value_that_is_not_an_address_is_withheld_rather_than_echoed(string value)
    {
        // Echoing a value because it failed a parse is the back door: a learner who mistypes their
        // address still typed their address, and a "malformed" branch that prints the input
        // reintroduces exactly what the masked branch removed.
        AuthLogRedaction.MaskEmail(value).Should().Be(AuthLogRedaction.RedactedMarker);
    }

    [Fact]
    public void A_trailing_at_sign_does_not_leak_the_local_part()
    {
        // The naive implementation split on '@' and printed everything before it, so "foo@" came
        // back as "foo***@" — an unmasked local part produced by the masking function.
        AuthLogRedaction.MaskEmail("someone@").Should().NotContain("someone");
        AuthLogRedaction.MaskEmail("someone@").Should().Be(AuthLogRedaction.RedactedMarker);
    }

    [Fact]
    public void The_last_at_sign_separates_local_part_from_domain()
    {
        // Splitting on the FIRST '@' would treat "a" as a prefix of a local part that is really
        // "a@b", printing a character the caller believed was hidden.
        var masked = AuthLogRedaction.MaskEmail("a@b@example.com");

        masked.Should().Be("***@example.com");
        masked.Should().NotContain("a@b");
    }

    [Theory]
    [InlineData("사용자이름@example.com")]
    [InlineData("👨‍👩‍👧‍👦family@example.com")]
    [InlineData("e\u0301clair-lover@example.com")]
    public void A_unicode_local_part_is_never_split_mid_character(string email)
    {
        var masked = AuthLogRedaction.MaskEmail(email);

        // Slicing by char would cut a surrogate pair in half or strip a combining mark from its
        // base letter, producing a replacement glyph in the log — and, worse, a prefix that is not
        // the prefix a human would read.
        masked.Should().NotContain("\uFFFD");
        masked.Should().EndWith("@example.com");

        var prefix = masked[..masked.IndexOf(AuthLogRedaction.RedactedMarker, StringComparison.Ordinal)];
        CountTextElements(prefix).Should().BeLessThanOrEqualTo(3);
        email.Should().StartWith(prefix);
    }

    [Theory]
    [InlineData("someone@exa\nmple.com")]
    [InlineData("someone@exa\u001b[31mmple.com")]
    [InlineData("someone@\"example\".com")]
    [InlineData("someone@example com")]
    public void A_domain_that_could_forge_a_log_line_is_withheld(string email)
    {
        // A domain is echoed verbatim, so it is the one attacker-influenced substring in the
        // output. A newline there lets a registered value fabricate an entire second log entry.
        AuthLogRedaction.MaskEmail(email).Should().Be(AuthLogRedaction.RedactedMarker);
    }

    [Fact]
    public void An_overlong_domain_cannot_pad_the_log()
    {
        var email = "someone@" + new string('d', 500) + ".com";

        var masked = AuthLogRedaction.MaskEmail(email);

        masked.Length.Should().BeLessThan(100);
        masked.Should().EndWith("...");
    }

    [Fact]
    public void The_domain_survives_so_a_wrong_environment_is_still_diagnosable()
    {
        // Dropping the domain too would make the line useless: "which tenant / which environment"
        // is the question these lines exist to answer.
        AuthLogRedaction.MaskEmail(Sentinel).Should().EndWith("@sentencestudio.test");
    }

    [Theory]
    [InlineData(null, "(empty)")]
    [InlineData("", "(empty)")]
    [InlineData("squad-jayne@sentencestudio.test", "squ***@sentencestudio.test")]
    public void A_user_name_that_is_an_address_is_masked_as_one(string? userName, string expected)
    {
        AuthLogRedaction.MaskUserName(userName).Should().Be(expected);
    }

    [Theory]
    [InlineData("Jayne Cobb")]
    [InlineData("captain")]
    public void A_user_name_that_is_not_an_address_is_still_withheld(string userName)
    {
        // ApplicationUser.UserName is the address for every account created through registration,
        // but a display-name-shaped value is not therefore safe: it is usually a real name.
        AuthLogRedaction.MaskUserName(userName).Should().Be(AuthLogRedaction.RedactedMarker);
        AuthLogRedaction.MaskUserName(userName).Should().NotContain(userName);
    }

    [Theory]
    [InlineData(null, "(unset)")]
    [InlineData("", "(unset)")]
    [InlineData("   ", "(unset)")]
    [InlineData("Jayne Cobb", "(set)")]
    public void A_display_name_is_reported_as_present_or_absent_only(string? displayName, string expected)
    {
        AuthLogRedaction.DescribeDisplayName(displayName).Should().Be(expected);
    }

    [Fact]
    public void Error_codes_are_deduplicated_and_kept()
    {
        AuthLogRedaction
            .DescribeErrorCodes(["DuplicateUserName", "DuplicateEmail", "DuplicateUserName"])
            .Should().Be("DuplicateUserName, DuplicateEmail");
    }

    [Fact]
    public void An_empty_or_null_error_list_says_so()
    {
        AuthLogRedaction.DescribeErrorCodes(null).Should().Be(AuthLogRedaction.NoErrorsMarker);
        AuthLogRedaction.DescribeErrorCodes([]).Should().Be(AuthLogRedaction.NoErrorsMarker);
    }

    [Fact]
    public void A_long_error_list_is_bounded()
    {
        var codes = Enumerable.Range(0, 50).Select(i => $"Code{i}");

        var rendered = AuthLogRedaction.DescribeErrorCodes(codes);

        rendered.Should().EndWith(", ...");
        rendered.Split(',').Length.Should().BeLessThanOrEqualTo(11);
    }

    [Theory]
    [InlineData("User name 'squad-jayne@sentencestudio.test' is already taken.")]
    [InlineData("code with spaces")]
    [InlineData("code\nwith\nnewlines")]
    [InlineData("")]
    [InlineData(null)]
    public void A_code_that_is_not_an_identifier_is_replaced(string? code)
    {
        // A custom IdentityErrorDescriber can mint codes, so "Code is a closed framework set" is a
        // convention rather than a guarantee. If a description ever reaches this parameter, the
        // address inside it must not reach the log.
        var rendered = AuthLogRedaction.DescribeErrorCodes([code]);

        rendered.Should().Be("UnknownCode");
        rendered.Should().NotContain(Sentinel);
    }

    private static int CountTextElements(string value)
    {
        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            count++;
        }

        return count;
    }
}
