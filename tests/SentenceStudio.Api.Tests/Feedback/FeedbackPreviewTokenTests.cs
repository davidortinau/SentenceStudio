using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// The signed preview token: its nonce, its lifetime, and what it refuses.
/// </summary>
public sealed class FeedbackPreviewTokenTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("feedback-test-hmac-key-at-least-32-chars!!");
    private static readonly byte[] OtherKey = Encoding.UTF8.GetBytes("a-completely-different-key-of-sufficient-length");

    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------------------- the nonce

    /// <summary>
    /// Every preview gets a distinct nonce, even for identical content from the same owner in the
    /// same second.
    /// </summary>
    /// <remarks>
    /// This is the property that makes the ledger possible. Without a nonce the token is a pure
    /// function of its content, so two identical previews are the same redeemable object — which
    /// silently refuses a learner re-reporting a recurring bug, and gives the ledger nothing to
    /// key a claim on.
    /// </remarks>
    [Fact]
    public void Every_preview_gets_a_distinct_nonce()
    {
        var nonces = Enumerable.Range(0, 512).Select(_ => FeedbackPreviewToken.NewJti()).ToArray();

        nonces.Distinct(StringComparer.Ordinal).Should().HaveCount(nonces.Length);
    }

    /// <summary>The nonce carries at least 128 bits of entropy.</summary>
    /// <remarks>
    /// It is the ledger's primary key, so a guessable value would let one account pre-empt
    /// another's submission by claiming its identifier first. A counter or a timestamp would pass
    /// the uniqueness test above and fail this requirement completely.
    /// </remarks>
    [Fact]
    public void The_nonce_is_long_enough_to_be_unguessable()
    {
        FeedbackPreviewToken.JtiByteLength.Should().BeGreaterThanOrEqualTo(16);

        var jti = FeedbackPreviewToken.NewJti();
        jti.Should().NotBeNullOrWhiteSpace();
        jti.Length.Should().BeLessThanOrEqualTo(FeedbackPreviewToken.MaxJtiLength);

        // Base64url of 16 bytes, unpadded.
        jti.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    /// <summary>The nonce is covered by the signature.</summary>
    /// <remarks>
    /// If it were outside the MAC, a caller could mint fresh nonces for one signed body and file
    /// the same report as many times as they liked — the exactly-once ledger would faithfully
    /// record each one as a distinct, legitimate submission.
    /// </remarks>
    [Fact]
    public void Editing_the_nonce_invalidates_the_token()
    {
        var token = FeedbackPreviewToken.Create(Payload(), Key);
        var tampered = RewritePayload(token, json => json.Replace(
            $"\"jti\":\"{Payload().Jti}\"", "\"jti\":\"AAAAAAAAAAAAAAAAAAAAAA\"", StringComparison.Ordinal));

        FeedbackPreviewToken.TryValidate(tampered, Key, Now, out var payload)
            .Should().Be(FeedbackTokenRejection.Invalid);
        payload.Should().BeNull();
    }

    // --------------------------------------------------------------------------- verification

    [Fact]
    public void A_well_formed_token_round_trips()
    {
        var original = Payload();
        var token = FeedbackPreviewToken.Create(original, Key);

        FeedbackPreviewToken.TryValidate(token, Key, Now, out var payload)
            .Should().Be(FeedbackTokenRejection.None);

        payload.Should().BeEquivalentTo(original);
    }

    /// <summary>A token signed with another key is refused.</summary>
    [Fact]
    public void A_token_signed_with_a_different_key_is_refused()
    {
        var token = FeedbackPreviewToken.Create(Payload(), OtherKey);

        FeedbackPreviewToken.TryValidate(token, Key, Now, out var payload)
            .Should().Be(FeedbackTokenRejection.Invalid);
        payload.Should().BeNull();
    }

    /// <summary>Editing any covered field invalidates the token.</summary>
    /// <remarks>
    /// The body case is the one that matters most: it is what would let a caller sign an innocuous
    /// preview, approve it, and then post something else into a public repository under our
    /// signature.
    /// </remarks>
    [Theory]
    [InlineData("\"title\":\"Reading freezes\"", "\"title\":\"Something else entirely\"")]
    [InlineData("\"body\":\"## Bug\\nIt freezes.\"", "\"body\":\"## Bug\\nSomething else.\"")]
    [InlineData("\"ownerProfileId\":\"owner-1\"", "\"ownerProfileId\":\"owner-2\"")]
    [InlineData("\"feedbackType\":\"bug\"", "\"feedbackType\":\"enhancement\"")]
    public void Editing_any_signed_field_invalidates_the_token(string from, string to)
    {
        var token = FeedbackPreviewToken.Create(Payload(), Key);
        var tampered = RewritePayload(token, json => json.Replace(from, to, StringComparison.Ordinal));

        tampered.Should().NotBe(token, "the test's own rewrite must actually have changed something");

        FeedbackPreviewToken.TryValidate(tampered, Key, Now, out _)
            .Should().Be(FeedbackTokenRejection.Invalid);
    }

    /// <summary>Flipping a single bit of the signature is refused.</summary>
    [Fact]
    public void Editing_the_signature_invalidates_the_token()
    {
        var token = FeedbackPreviewToken.Create(Payload(), Key);
        var separator = token.IndexOf('.');
        var signature = token[(separator + 1)..].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';

        var tampered = token[..(separator + 1)] + new string(signature);

        FeedbackPreviewToken.TryValidate(tampered, Key, Now, out _)
            .Should().Be(FeedbackTokenRejection.Invalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nodots")]
    [InlineData("three.parts.here")]
    [InlineData(".missingpayload")]
    [InlineData("missingsignature.")]
    [InlineData("!!!.!!!")]
    public void A_malformed_token_is_refused(string token)
    {
        FeedbackPreviewToken.TryValidate(token, Key, Now, out var payload)
            .Should().Be(FeedbackTokenRejection.Invalid);
        payload.Should().BeNull();
    }

    /// <summary>A truncated signature is refused rather than compared short.</summary>
    [Fact]
    public void A_short_signature_is_refused()
    {
        var token = FeedbackPreviewToken.Create(Payload(), Key);
        var separator = token.IndexOf('.');
        var tampered = token[..(separator + 1)] + token[(separator + 1)..(separator + 9)];

        FeedbackPreviewToken.TryValidate(tampered, Key, Now, out _)
            .Should().Be(FeedbackTokenRejection.Invalid);
    }

    // -------------------------------------------------------------------------------- expiry

    [Fact]
    public void An_expired_token_is_refused_as_expired()
    {
        var token = FeedbackPreviewToken.Create(Payload(), Key);

        FeedbackPreviewToken.TryValidate(token, Key, Now.AddMinutes(11), out var payload)
            .Should().Be(FeedbackTokenRejection.Expired);
        payload.Should().BeNull();
    }

    [Fact]
    public void A_token_is_still_valid_at_the_last_second_of_its_life()
    {
        var token = FeedbackPreviewToken.Create(Payload(), Key);

        FeedbackPreviewToken.TryValidate(token, Key, Now.AddMinutes(10), out _)
            .Should().Be(FeedbackTokenRejection.None);
    }

    /// <summary>Expiry is checked after the signature, never before.</summary>
    /// <remarks>
    /// If expiry were checked on unverified bytes, a caller could learn whether an arbitrary
    /// payload was "expired" or "invalid" without ever holding a valid signature — and the parse
    /// itself would be running on attacker-controlled input.
    /// </remarks>
    [Fact]
    public void An_expired_token_with_a_bad_signature_reports_invalid_not_expired()
    {
        var token = FeedbackPreviewToken.Create(Payload(), OtherKey);

        FeedbackPreviewToken.TryValidate(token, Key, Now.AddDays(1), out _)
            .Should().Be(FeedbackTokenRejection.Invalid);
    }

    // ------------------------------------------------------------------- payload rejection

    /// <summary>
    /// A correctly-signed payload carrying something unpostable is refused.
    /// </summary>
    /// <remarks>
    /// Not a tampering check — the signature already settled that. This is the second half of the
    /// closed-set rule: a value this server signed but should never have signed must not reach a
    /// public repository on the strength of our own signature.
    /// </remarks>
    [Theory]
    [InlineData("security")]
    [InlineData("good first issue")]
    [InlineData("@maintainer")]
    [InlineData("")]
    public void A_signed_token_carrying_a_label_outside_the_closed_set_is_refused(string label)
    {
        var token = FeedbackPreviewToken.Create(Payload() with { Labels = [label] }, Key);

        FeedbackPreviewToken.TryValidate(token, Key, Now, out _)
            .Should().Be(FeedbackTokenRejection.PayloadRejected);
    }

    [Fact]
    public void A_signed_token_with_no_labels_is_refused()
    {
        var token = FeedbackPreviewToken.Create(Payload() with { Labels = [] }, Key);

        FeedbackPreviewToken.TryValidate(token, Key, Now, out _)
            .Should().Be(FeedbackTokenRejection.PayloadRejected);
    }

    [Fact]
    public void A_signed_token_with_an_over_long_title_is_refused()
    {
        var token = FeedbackPreviewToken.Create(
            Payload() with { Title = new string('x', FeedbackPreviewToken.MaxTitleLength + 1) }, Key);

        FeedbackPreviewToken.TryValidate(token, Key, Now, out _)
            .Should().Be(FeedbackTokenRejection.PayloadRejected);
    }

    /// <summary>
    /// A signed token carrying an undeclared route ordinal is refused rather than published.
    /// </summary>
    /// <remarks>
    /// Belt and braces with the normaliser. The normaliser is what should stop this from ever
    /// being signed; this is what stops it being posted if the normaliser is ever bypassed.
    /// </remarks>
    [Fact]
    public void A_signed_token_with_an_undeclared_route_ordinal_is_refused()
    {
        var token = FeedbackPreviewToken.Create(
            Payload() with { RouteCategory = (FeedbackRouteCategory)4210 }, Key);

        FeedbackPreviewToken.TryValidate(token, Key, Now, out _)
            .Should().Be(FeedbackTokenRejection.PayloadRejected);
    }

    /// <summary>
    /// A signed token whose version field is absent is refused, not a 500.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AppVersion</c> is declared non-nullable on the payload record, which is a claim about C#
    /// and not about the bytes: a JSON body that simply omits <c>appVersion</c> deserialises with it
    /// null. Calling <c>.Length</c> on that throws a <see cref="NullReferenceException"/> out of
    /// <c>TryValidate</c>, which catches only <see cref="System.Text.Json.JsonException"/> — so the
    /// endpoint would answer 500 rather than refusing the token.
    /// </para>
    /// <para>
    /// Reaching this needs our signing key, so it is not an unauthenticated crash. It is still worth
    /// closing: the 500 would be indistinguishable from a real server fault, and a validator that
    /// throws on a value it was asked to validate is a validator with a hole in it.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_signed_token_with_a_null_version_is_refused_rather_than_throwing()
    {
        var token = FeedbackPreviewToken.Create(Payload() with { AppVersion = null! }, Key);

        var act = () => FeedbackPreviewToken.TryValidate(token, Key, Now, out _);

        act.Should().NotThrow();
        FeedbackPreviewToken.TryValidate(token, Key, Now, out var payload)
            .Should().Be(FeedbackTokenRejection.PayloadRejected);
        payload.Should().BeNull();
    }

    /// <summary>
    /// No signed payload with a missing string field throws out of the verifier.
    /// </summary>
    /// <remarks>
    /// The generalisation of the case above, over every string on the payload. Each one is either
    /// already guarded or must be — and a member added later that is not falls out here rather than
    /// as a 500 in production.
    /// </remarks>
    [Fact]
    public void No_signed_payload_with_a_missing_string_throws_out_of_the_verifier()
    {
        var payloads = new[]
        {
            Payload() with { Jti = null! },
            Payload() with { Title = null! },
            Payload() with { Body = null! },
            Payload() with { FeedbackType = null! },
            Payload() with { OwnerProfileId = null! },
            Payload() with { AppVersion = null! },
            Payload() with { Labels = null! },
            Payload() with { Labels = [null!] }
        };

        foreach (var payload in payloads)
        {
            var token = FeedbackPreviewToken.Create(payload, Key);

            var act = () => FeedbackPreviewToken.TryValidate(token, Key, Now, out _);
            act.Should().NotThrow();

            FeedbackPreviewToken.TryValidate(token, Key, Now, out var parsed)
                .Should().Be(FeedbackTokenRejection.PayloadRejected);
            parsed.Should().BeNull();
        }
    }

    // ------------------------------------------------------------------------ content digest

    /// <summary>
    /// The digest changes whenever any posted field changes.
    /// </summary>
    [Fact]
    public void The_content_digest_covers_every_posted_field()
    {
        var baseline = FeedbackPreviewToken.ContentDigest("T", "B", ["bug"], "bug");

        FeedbackPreviewToken.ContentDigest("T2", "B", ["bug"], "bug").Should().NotBe(baseline);
        FeedbackPreviewToken.ContentDigest("T", "B2", ["bug"], "bug").Should().NotBe(baseline);
        FeedbackPreviewToken.ContentDigest("T", "B", ["enhancement"], "bug").Should().NotBe(baseline);
        FeedbackPreviewToken.ContentDigest("T", "B", ["bug"], "enhancement").Should().NotBe(baseline);
        FeedbackPreviewToken.ContentDigest("T", "B", ["bug", "enhancement"], "bug").Should().NotBe(baseline);
    }

    /// <summary>
    /// No two different field splits can produce the same digest.
    /// </summary>
    /// <remarks>
    /// The reason the digest is length-prefixed rather than separator-joined. With a separator, a
    /// title containing it impersonates a title-and-body pair, and two genuinely different issues
    /// hash identically — which would make the binding this digest exists to prove unfalsifiable.
    /// </remarks>
    [Fact]
    public void Field_boundaries_cannot_be_confused_in_the_digest()
    {
        FeedbackPreviewToken.ContentDigest("a|b", "c", ["bug"], "bug")
            .Should().NotBe(FeedbackPreviewToken.ContentDigest("a", "b|c", ["bug"], "bug"));

        FeedbackPreviewToken.ContentDigest("ab", "", ["bug"], "bug")
            .Should().NotBe(FeedbackPreviewToken.ContentDigest("a", "b", ["bug"], "bug"));
    }

    [Fact]
    public void The_content_digest_is_stable_for_identical_input()
    {
        FeedbackPreviewToken.ContentDigest("T", "B", ["bug"], "bug")
            .Should().Be(FeedbackPreviewToken.ContentDigest("T", "B", ["bug"], "bug"));
    }

    // -------------------------------------------------------------------------- helpers

    private static FeedbackPreviewPayload Payload() => new(
        "AAAAAAAAAAAAAAAAAAAAAB",
        "Reading freezes",
        "## Bug\nIt freezes.",
        ["bug"],
        "bug",
        "owner-1",
        FeedbackRouteCategory.Activity,
        FeedbackPlatform.Web,
        "1.2.3",
        Now.ToUnixTimeSeconds(),
        Now.AddMinutes(10).ToUnixTimeSeconds());

    /// <summary>
    /// Rewrites the payload half of a token and re-signs nothing, so the signature no longer
    /// matches — which is exactly what an attacker editing a token in transit produces.
    /// </summary>
    private static string RewritePayload(string token, Func<string, string> edit)
    {
        var separator = token.IndexOf('.');
        var json = Encoding.UTF8.GetString(Base64UrlDecode(token[..separator]));
        var edited = edit(json);

        // Guard against a rewrite that silently matched nothing, which would make the test pass
        // for the wrong reason.
        if (string.Equals(json, edited, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The rewrite did not change the payload.");
        }

        return Base64UrlEncode(Encoding.UTF8.GetBytes(edited)) + token[separator..];
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return (s.Length % 4) switch
        {
            2 => Convert.FromBase64String(s + "=="),
            3 => Convert.FromBase64String(s + "="),
            _ => Convert.FromBase64String(s)
        };
    }
}

/// <summary>
/// The label allow-list.
/// </summary>
public sealed class FeedbackLabelTests
{
    [Fact]
    public void Only_bug_and_enhancement_are_allowed()
    {
        FeedbackLabels.Allowed.Should().BeEquivalentTo(["bug", "enhancement"]);
    }

    [Theory]
    [InlineData("security")]
    [InlineData("BUG")]
    [InlineData("bug ")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_outside_the_set_is_rejected(string? candidate)
    {
        FeedbackLabels.IsAllowed(candidate).Should().BeFalse();
    }

    /// <summary>
    /// An all-invalid label array becomes the feedback type, never an empty array.
    /// </summary>
    /// <remarks>
    /// An empty array posted to GitHub means "no labels", so a model whose entire output was
    /// rejected would produce an unlabelled issue that reads as a triage oversight rather than as
    /// a rejected model output.
    /// </remarks>
    [Fact]
    public void An_all_invalid_array_falls_back_to_the_feedback_type()
    {
        FeedbackLabels.Sanitize(["security", "P0", null], "bug").Should().BeEquivalentTo(["bug"]);
        FeedbackLabels.Sanitize([], "enhancement").Should().BeEquivalentTo(["enhancement"]);
        FeedbackLabels.Sanitize(null, "bug").Should().BeEquivalentTo(["bug"]);
    }

    [Fact]
    public void Valid_labels_survive_and_duplicates_collapse()
    {
        FeedbackLabels.Sanitize(["bug", "bug", "security"], "bug").Should().BeEquivalentTo(["bug"]);
        FeedbackLabels.Sanitize(["bug", "enhancement"], "bug")
            .Should().BeEquivalentTo(["bug", "enhancement"]);
    }

    [Theory]
    [InlineData("bug", "bug")]
    [InlineData("enhancement", "enhancement")]
    [InlineData("security", "enhancement")]
    [InlineData(null, "enhancement")]
    [InlineData("", "enhancement")]
    public void An_unrecognised_type_becomes_enhancement(string? input, string expected)
    {
        FeedbackLabels.NormalizeType(input).Should().Be(expected);
    }
}
