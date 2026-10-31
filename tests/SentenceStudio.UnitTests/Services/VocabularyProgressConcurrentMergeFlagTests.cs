using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

public sealed class VocabularyProgressConcurrentMergeFlagTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 23, 0, 0, DateTimeKind.Utc);

    private static VocabularyProgress Row(int total, int correct) => new()
    {
        Id = "p1",
        UserId = "owner-a",
        VocabularyWordId = "w1",
        TotalAttempts = total,
        CorrectAttempts = correct,
        FirstSeenAt = Now.AddDays(-3),
        CreatedAt = Now.AddDays(-3),
        UpdatedAt = Now.AddHours(-1),
    };

    [Fact]
    public void UploadThatAlreadyCountsTheStoredRowKeepsFlagsSetElsewhere()
    {
        // The web declared the word known and completed it; the device's upload carries
        // the same counts plus one attempt but none of those flags.
        var stored = Row(2, 2);
        stored.IsUserDeclared = true;
        stored.UserDeclaredAt = Now.AddMinutes(-30);
        stored.VerificationState = VerificationStatus.Pending;
        stored.IsCompleted = true;
        stored.MasteredAt = Now.AddMinutes(-30);
        var incoming = Row(3, 3);

        var changed = VocabularyProgressConcurrentMerge.TryMerge(
            stored, incoming, [], [], Now, out var merged);

        Assert.True(changed);
        Assert.Equal(3, merged.TotalAttempts);
        Assert.True(merged.IsUserDeclared);
        Assert.Equal(VerificationStatus.Pending, merged.VerificationState);
        Assert.Equal(stored.UserDeclaredAt, merged.UserDeclaredAt);
        Assert.True(merged.IsCompleted);
        Assert.Equal(stored.MasteredAt, merged.MasteredAt);
    }

    [Fact]
    public void UploadWithNoFlagDifferenceIsAppliedUnchanged()
    {
        var stored = Row(2, 2);
        var incoming = Row(3, 3);

        var changed = VocabularyProgressConcurrentMerge.TryMerge(
            stored, incoming, [], [], Now, out var merged);

        Assert.False(changed);
        Assert.Same(incoming, merged);
    }
}
