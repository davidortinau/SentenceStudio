using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Abstractions.Keychain;
using Xunit;

namespace SentenceStudio.UnitTests.Abstractions;

/// <summary>
/// Adoption of pre-namespacing credentials, and the ownership evidence it demands first.
/// </summary>
/// <remarks>
/// The account name proves nothing on this platform, so the payload has to. A triple is adopted
/// only when it is complete, self-consistent, and its SentenceStudio profile claim matches the
/// <c>active_profile_id</c> this install holds in its own preference store — which no other
/// application can write. Anything short of that and every bare item is left exactly as it was.
/// </remarks>
public class LegacyCredentialAdoptionTests
{
    private const string Scoped = KeychainSecureStorageService.AccountNamespace;
    private const string OurProfileId = "profile-ours-1234";
    private const string TheirProfileId = "profile-theirs-9999";

    private static string Jwt(string profileId, string? issuer = null, string? audience = null)
    {
        static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var claims = $"\"user_profile_id\":\"{profileId}\"";
        if (issuer is not null) claims += $",\"iss\":\"{issuer}\"";
        if (audience is not null) claims += $",\"aud\":\"{audience}\"";

        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{{claims}}}")}.sig";
    }

    private sealed class Harness
    {
        public OwnerAwareFakeKeychainGate Gate { get; } = new();
        public FakeAdoptionJournal Journal { get; } = new();
        public FakePreferences Preferences { get; } = new();
        public KeychainSecureStorageService Storage { get; }
        public LegacyCredentialAdoption Adoption { get; }

        public Harness(string? issuer = null, string? audience = null)
        {
            Storage = new KeychainSecureStorageService(
                Gate, NullLogger<KeychainSecureStorageService>.Instance);

            Adoption = new LegacyCredentialAdoption(
                Gate, Storage, Journal, Preferences,
                NullLogger<LegacyCredentialAdoption>.Instance)
            {
                ExpectedIssuer = issuer,
                ExpectedAudience = audience,
            };

            // Another product's item, always present, never ours.
            Gate.Seed("MAUI Sherpa Local Vault", "ANOTHER-PRODUCTS-SECRET", KeychainItemOwner.Foreign);
        }

        public void SeedBareTriple(string profileId, KeychainItemOwner owner, string? issuer = null)
        {
            Gate.Seed("auth_jwt", Jwt(profileId, issuer), owner);
            Gate.Seed("auth_refresh", $"REFRESH-FOR-{profileId}", owner);
            Gate.Seed("auth_expires", DateTimeOffset.UtcNow.AddHours(4).ToString("O"), owner);
        }

        public void AssertBareItemsUntouched()
        {
            Assert.True(Gate.Contains("MAUI Sherpa Local Vault"));
            Assert.Equal("ANOTHER-PRODUCTS-SECRET", Gate.ValueOf("MAUI Sherpa Local Vault"));
            Assert.Empty(Gate.DeleteAttempts);
        }

        public void AssertNothingCopied()
        {
            foreach (var key in new[] { "auth_jwt", "auth_refresh", "auth_expires" })
                Assert.False(Gate.Contains(Scoped + key), $"{key} must not have been copied");
        }
    }

    // ------------------------------------------------------- refusal to adopt

    /// <summary>
    /// THE credential-confusion case: a perfectly readable, perfectly coherent triple that belongs
    /// to somebody else. Adopting it would hand another learner's refresh token to our API.
    /// </summary>
    [Fact]
    public async Task A_readable_coherent_triple_for_a_different_profile_is_not_adopted()
    {
        var h = new Harness();
        h.Preferences.Set("active_profile_id", OurProfileId);
        h.SeedBareTriple(TheirProfileId, KeychainItemOwner.ThisApp); // readable!

        var verdict = await h.Adoption.TryAdoptAsync();

        Assert.Equal(LegacyOwnershipVerdict.ForeignIdentity, verdict);
        h.AssertNothingCopied();
        h.AssertBareItemsUntouched();
        Assert.True(h.Gate.Contains("auth_refresh"), "their token must be left where it is");
        Assert.Equal(LegacyAdoptionOutcome.Rejected, h.Journal.Read("auth_triple_v1"));
    }

    [Fact]
    public async Task An_unreadable_foreign_triple_is_not_adopted_and_not_deleted()
    {
        var h = new Harness();
        h.Preferences.Set("active_profile_id", OurProfileId);
        h.SeedBareTriple(TheirProfileId, KeychainItemOwner.Foreign);

        var verdict = await h.Adoption.TryAdoptAsync();

        Assert.NotEqual(LegacyOwnershipVerdict.Owned, verdict);
        h.AssertNothingCopied();
        h.AssertBareItemsUntouched();
        Assert.True(h.Gate.Contains("auth_refresh"));
    }

    /// <summary>A fresh install has no identity to compare against, so it must adopt nothing.</summary>
    [Fact]
    public async Task With_no_local_profile_nothing_is_adopted()
    {
        var h = new Harness();
        h.SeedBareTriple(TheirProfileId, KeychainItemOwner.ThisApp);

        var verdict = await h.Adoption.TryAdoptAsync();

        Assert.Equal(LegacyOwnershipVerdict.NoLocalIdentity, verdict);
        h.AssertNothingCopied();
        h.AssertBareItemsUntouched();
    }

    [Fact]
    public async Task An_incomplete_triple_is_not_adopted()
    {
        var h = new Harness();
        h.Preferences.Set("active_profile_id", OurProfileId);
        h.Gate.Seed("auth_jwt", Jwt(OurProfileId), KeychainItemOwner.ThisApp);
        // no refresh, no expiry

        var verdict = await h.Adoption.TryAdoptAsync();

        Assert.NotEqual(LegacyOwnershipVerdict.Owned, verdict);
        h.AssertNothingCopied();
        h.AssertBareItemsUntouched();
    }

    [Fact]
    public async Task A_triple_with_an_unparseable_expiry_is_not_adopted()
    {
        var h = new Harness();
        h.Preferences.Set("active_profile_id", OurProfileId);
        h.Gate.Seed("auth_jwt", Jwt(OurProfileId), KeychainItemOwner.ThisApp);
        h.Gate.Seed("auth_refresh", "R", KeychainItemOwner.ThisApp);
        h.Gate.Seed("auth_expires", "not-a-timestamp", KeychainItemOwner.ThisApp);

        Assert.Equal(LegacyOwnershipVerdict.Incoherent, await h.Adoption.TryAdoptAsync());
        h.AssertNothingCopied();
        h.AssertBareItemsUntouched();
    }

    [Fact]
    public async Task A_mismatched_issuer_is_not_adopted()
    {
        var h = new Harness(issuer: "https://api.sentencestudio.test");
        h.Preferences.Set("active_profile_id", OurProfileId);
        h.SeedBareTriple(OurProfileId, KeychainItemOwner.ThisApp, issuer: "https://someone-else.test");

        Assert.Equal(LegacyOwnershipVerdict.ForeignIdentity, await h.Adoption.TryAdoptAsync());
        h.AssertNothingCopied();
        h.AssertBareItemsUntouched();
    }

    [Fact]
    public async Task Nothing_stored_is_a_no_op()
    {
        var h = new Harness();
        h.Preferences.Set("active_profile_id", OurProfileId);

        Assert.Equal(LegacyOwnershipVerdict.Absent, await h.Adoption.TryAdoptAsync());
        h.AssertBareItemsUntouched();
    }

    // -------------------------------------------------------- adoption proper

    [Fact]
    public async Task Our_own_corroborated_triple_is_copied_and_the_originals_are_left_in_place()
    {
        var h = new Harness();
        h.Preferences.Set("active_profile_id", OurProfileId);
        h.SeedBareTriple(OurProfileId, KeychainItemOwner.ThisApp);
        var originalRefresh = h.Gate.ValueOf("auth_refresh");

        var verdict = await h.Adoption.TryAdoptAsync();

        Assert.Equal(LegacyOwnershipVerdict.Owned, verdict);
        Assert.Equal(originalRefresh, h.Gate.ValueOf(Scoped + "auth_refresh"));

        // Left in place on purpose: deleting a bare name this app cannot prove it owns is the
        // hazard. Suppression, not deletion, is what stops it being used again.
        Assert.True(h.Gate.Contains("auth_refresh"));
        Assert.Empty(h.Gate.DeleteAttempts);
        Assert.Equal(LegacyAdoptionOutcome.Adopted, h.Journal.Read("auth_triple_v1"));
    }

    // ------------------------------------------------------- durable decisions

    [Fact]
    public async Task A_recorded_decision_stops_the_probe_entirely_on_the_next_launch()
    {
        var h = new Harness();
        h.Preferences.Set("active_profile_id", OurProfileId);
        h.SeedBareTriple(TheirProfileId, KeychainItemOwner.ThisApp);

        await h.Adoption.TryAdoptAsync();
        h.Gate.ReadAttempts.Clear();

        // "Relaunch": a new instance over the same durable journal.
        var relaunched = new LegacyCredentialAdoption(
            h.Gate, h.Storage, h.Journal, h.Preferences,
            NullLogger<LegacyCredentialAdoption>.Instance);

        Assert.Equal(LegacyOwnershipVerdict.AlreadyDecided, await relaunched.TryAdoptAsync());
        Assert.Empty(h.Gate.ReadAttempts);
        h.AssertBareItemsUntouched();
    }

    /// <summary>
    /// Sign-out must close adoption permanently, or the next launch re-adopts the still-present
    /// bare triple and signs the learner straight back in.
    /// </summary>
    [Fact]
    public async Task Signing_out_prevents_re_adoption_after_a_relaunch()
    {
        var h = new Harness();
        h.Preferences.Set("active_profile_id", OurProfileId);
        h.SeedBareTriple(OurProfileId, KeychainItemOwner.ThisApp);

        Assert.Equal(LegacyOwnershipVerdict.Owned, await h.Adoption.TryAdoptAsync());

        h.Adoption.Retire();
        Assert.Equal(LegacyAdoptionOutcome.Retired, h.Journal.Read("auth_triple_v1"));

        h.Gate.ReadAttempts.Clear();
        var relaunched = new LegacyCredentialAdoption(
            h.Gate, h.Storage, h.Journal, h.Preferences,
            NullLogger<LegacyCredentialAdoption>.Instance);

        Assert.Equal(LegacyOwnershipVerdict.AlreadyDecided, await relaunched.TryAdoptAsync());
        Assert.Empty(h.Gate.ReadAttempts);
        Assert.True(h.Gate.Contains("auth_refresh"), "still not ours to delete");
    }

    [Fact]
    public async Task The_probe_runs_at_most_once_per_process_even_without_a_durable_journal()
    {
        var h = new Harness();
        h.Preferences.Set("active_profile_id", OurProfileId);
        h.SeedBareTriple(TheirProfileId, KeychainItemOwner.Foreign);

        await h.Adoption.TryAdoptAsync();
        var after = h.Gate.ReadAttempts.Count;
        await h.Adoption.TryAdoptAsync();
        await h.Adoption.TryAdoptAsync();

        Assert.Equal(after, h.Gate.ReadAttempts.Count);
    }

    // ---------------------------------------------------------------- hygiene

    [Fact]
    public async Task The_probe_restores_the_prompt_flag_it_found()
    {
        var h = new Harness();
        h.Preferences.Set("active_profile_id", OurProfileId);
        h.SeedBareTriple(OurProfileId, KeychainItemOwner.ThisApp);

        Assert.True(h.Gate.InteractionAllowed);
        await h.Adoption.TryAdoptAsync();
        Assert.True(h.Gate.InteractionAllowed, "the prompt flag must be put back");
    }

    [Fact]
    public async Task The_probe_never_prompts()
    {
        var h = new Harness { };
        h.Preferences.Set("active_profile_id", OurProfileId);
        h.SeedBareTriple(OurProfileId, KeychainItemOwner.ThisApp);
        h.Gate.CanSetInteraction = false; // cannot suppress => must not read at all

        var verdict = await h.Adoption.TryAdoptAsync();

        Assert.NotEqual(LegacyOwnershipVerdict.Owned, verdict);
        Assert.Empty(h.Gate.ReadAttempts);
        h.AssertNothingCopied();
        h.AssertBareItemsUntouched();
    }
}

/// <summary>Pure ownership corroboration, independent of any keychain.</summary>
public class LegacyCredentialOwnershipTests
{
    private static string Jwt(string profileId)
    {
        static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"user_profile_id\":\"{profileId}\"}}")}.sig";
    }

    private static LegacyCredentialTriple Triple(string profileId) =>
        new(Jwt(profileId), "refresh", DateTimeOffset.UtcNow.AddHours(1).ToString("O"));

    [Fact]
    public void Matching_profile_is_owned() =>
        Assert.Equal(
            LegacyOwnershipVerdict.Owned,
            LegacyCredentialOwnership.Corroborate(Triple("p1"), "p1"));

    [Fact]
    public void Different_profile_is_foreign() =>
        Assert.Equal(
            LegacyOwnershipVerdict.ForeignIdentity,
            LegacyCredentialOwnership.Corroborate(Triple("p2"), "p1"));

    [Fact]
    public void No_local_identity_is_never_owned() =>
        Assert.Equal(
            LegacyOwnershipVerdict.NoLocalIdentity,
            LegacyCredentialOwnership.Corroborate(Triple("p1"), null));

    [Fact]
    public void Absent_triple_is_absent() =>
        Assert.Equal(
            LegacyOwnershipVerdict.Absent,
            LegacyCredentialOwnership.Corroborate(null, "p1"));

    [Theory]
    [InlineData("", "refresh", "2026-01-01T00:00:00Z")]
    [InlineData("not-a-jwt", "refresh", "2026-01-01T00:00:00Z")]
    [InlineData("a.b.c", "", "2026-01-01T00:00:00Z")]
    public void Incoherent_triples_are_never_owned(string access, string refresh, string expires) =>
        Assert.NotEqual(
            LegacyOwnershipVerdict.Owned,
            LegacyCredentialOwnership.Corroborate(
                new LegacyCredentialTriple(access, refresh, expires), "p1"));

    [Fact]
    public void A_jwt_with_no_profile_claim_is_incoherent()
    {
        static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{B64("{\"alg\":\"none\"}")}.{B64("{\"sub\":\"someone\"}")}.sig";

        Assert.Equal(
            LegacyOwnershipVerdict.Incoherent,
            LegacyCredentialOwnership.Corroborate(
                new LegacyCredentialTriple(token, "r", DateTimeOffset.UtcNow.ToString("O")), "p1"));
    }
}
