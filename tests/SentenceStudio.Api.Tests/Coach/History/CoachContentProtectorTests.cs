using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The protector is the whole confidentiality story for durable history, so these tests treat
/// every element of the binding context as a security boundary and prove that crossing it fails.
/// </summary>
public sealed class CoachContentProtectorTests
{
    private static CoachProtectionContext Context(
        CoachOwner? owner = null,
        CoachProtectedContentKind kind = CoachProtectedContentKind.MessagePayload,
        string recordId = "record-1",
        int version = 1) =>
        new(owner ?? CoachHistorySamples.Owner, kind, recordId, version);

    [Fact]
    public void Protect_RoundTripsUnderTheSameContext()
    {
        using var harness = new CoachPersistenceHarness();
        var context = Context();

        var cipher = harness.ContentProtector.Protect(context, CoachPersistenceSamples.LearnerSentinel);

        Assert.DoesNotContain(CoachPersistenceSamples.LearnerSentinel, cipher, StringComparison.Ordinal);
        Assert.True(harness.ContentProtector.TryUnprotect(context, cipher, out var plaintext));
        Assert.Equal(CoachPersistenceSamples.LearnerSentinel, plaintext);
    }

    [Fact]
    public void TryUnprotect_FailsWhenOwnerDiffers()
    {
        using var harness = new CoachPersistenceHarness();
        var cipher = harness.ContentProtector.Protect(Context(), "secret");

        var swapped = Context(owner: CoachHistorySamples.Intruder);

        Assert.False(harness.ContentProtector.TryUnprotect(swapped, cipher, out var plaintext));
        Assert.Null(plaintext);
    }

    [Fact]
    public void TryUnprotect_FailsWhenContentKindDiffers()
    {
        using var harness = new CoachPersistenceHarness();
        var cipher = harness.ContentProtector.Protect(Context(), "secret");

        var swapped = Context(kind: CoachProtectedContentKind.ConversationTitle);

        Assert.False(harness.ContentProtector.TryUnprotect(swapped, cipher, out _));
    }

    [Fact]
    public void TryUnprotect_FailsWhenRecordIdDiffers()
    {
        using var harness = new CoachPersistenceHarness();
        var cipher = harness.ContentProtector.Protect(Context(), "secret");

        var swapped = Context(recordId: "record-2");

        Assert.False(harness.ContentProtector.TryUnprotect(swapped, cipher, out _));
    }

    [Fact]
    public void TryUnprotect_FailsWhenEnvelopeVersionDiffers()
    {
        using var harness = new CoachPersistenceHarness();
        var cipher = harness.ContentProtector.Protect(Context(), "secret");

        var swapped = Context(version: 2);

        Assert.False(harness.ContentProtector.TryUnprotect(swapped, cipher, out _));
    }

    [Fact]
    public void TryUnprotect_FailsWhenCiphertextIsTampered()
    {
        using var harness = new CoachPersistenceHarness();
        var context = Context();
        var cipher = harness.ContentProtector.Protect(context, "secret");

        // Flip one character in the middle of the payload.
        var middle = cipher.Length / 2;
        var tampered = cipher[..middle] + (cipher[middle] == 'A' ? 'B' : 'A') + cipher[(middle + 1)..];

        Assert.False(harness.ContentProtector.TryUnprotect(context, tampered, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-protected-payload")]
    public void TryUnprotect_FailsClosedOnMissingOrGarbagePayloads(string? payload)
    {
        using var harness = new CoachPersistenceHarness();

        Assert.False(harness.ContentProtector.TryUnprotect(Context(), payload, out var plaintext));
        Assert.Null(plaintext);
    }

    /// <summary>
    /// TenantId is nullable, is not an authority value in v1, and is expected to be backfilled.
    /// Binding ciphertext to it would turn that backfill into unrecoverable content loss, so a
    /// tenant change must leave existing content readable.
    /// </summary>
    [Fact]
    public void TryUnprotect_IgnoresTenantSoABackfillCannotDestroyContent()
    {
        using var harness = new CoachPersistenceHarness();
        var cipher = harness.ContentProtector.Protect(Context(), "secret");

        var laterTenant = Context(owner: CoachHistorySamples.OwnerOtherTenant);

        Assert.True(harness.ContentProtector.TryUnprotect(laterTenant, cipher, out var plaintext));
        Assert.Equal("secret", plaintext);
    }

    [Fact]
    public void Protect_RejectsAnEmptyOwner()
    {
        using var harness = new CoachPersistenceHarness();

        Assert.Throws<ArgumentException>(() =>
            harness.ContentProtector.Protect(Context(owner: CoachHistorySamples.Empty), "secret"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Protect_RejectsABlankRecordId(string recordId)
    {
        using var harness = new CoachPersistenceHarness();

        Assert.Throws<ArgumentException>(() =>
            harness.ContentProtector.Protect(Context(recordId: recordId), "secret"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Protect_RejectsANonPositiveVersion(int version)
    {
        using var harness = new CoachPersistenceHarness();

        Assert.Throws<ArgumentException>(() =>
            harness.ContentProtector.Protect(Context(version: version), "secret"));
    }

    [Fact]
    public void Protect_ProducesDistinctCiphertextForIdenticalPlaintext()
    {
        using var harness = new CoachPersistenceHarness();
        var context = Context();

        var first = harness.ContentProtector.Protect(context, "same");
        var second = harness.ContentProtector.Protect(context, "same");

        // Equal ciphertext would let an observer correlate repeated learner input by column value.
        Assert.NotEqual(first, second);
    }
}
