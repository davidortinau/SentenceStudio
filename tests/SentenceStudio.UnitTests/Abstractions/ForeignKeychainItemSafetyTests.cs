using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Abstractions.Keychain;
using Xunit;

namespace SentenceStudio.UnitTests.Abstractions;

/// <summary>
/// The shared-namespace hazard: MAUI stores every macOS app's secrets in the machine-global
/// keychain service <c>maui_secure_storage</c>, keyed by the bare key name. A real login keychain on
/// this machine contains <c>auth_jwt</c>/<c>auth_refresh</c>/<c>auth_expires</c> alongside another
/// product's <c>MAUI Sherpa Local Vault</c> — same service, different owner.
/// </summary>
/// <remarks>
/// <para>
/// Two things follow, and both were defects in the previous revision:
/// </para>
/// <list type="number">
/// <item>A bare account name is <b>not evidence of ownership</b>. Copying what is there hands
/// another product's refresh token to the SentenceStudio API.</item>
/// <item>Deleting a bare account <b>succeeds against foreign items</b> — legacy generic-password
/// items carry no <c>ACLAuthorizationDelete</c> entry — so an unconditional delete destroys another
/// application's credential.</item>
/// </list>
/// <para>
/// <see cref="OwnerAwareFakeKeychainGate"/> reproduces exactly that asymmetry, so these tests fail
/// if the app ever regains the ability to touch a bare account.
/// </para>
/// </remarks>
public class ForeignKeychainItemSafetyTests
{
    private const string Scoped = KeychainSecureStorageService.AccountNamespace;

    private static KeychainSecureStorageService CreateStorage(OwnerAwareFakeKeychainGate gate) =>
        new(gate, NullLogger<KeychainSecureStorageService>.Instance);

    /// <summary>A gate seeded the way a real machine looks: foreign bare items, plus another app.</summary>
    private static OwnerAwareFakeKeychainGate GateWithForeignBareItems()
    {
        var gate = new OwnerAwareFakeKeychainGate();
        gate.Seed("auth_jwt", "FOREIGN-ACCESS-TOKEN", KeychainItemOwner.Foreign);
        gate.Seed("auth_refresh", "FOREIGN-REFRESH-TOKEN", KeychainItemOwner.Foreign);
        gate.Seed("auth_expires", DateTimeOffset.UtcNow.AddDays(1).ToString("O"), KeychainItemOwner.Foreign);
        gate.Seed("MAUI Sherpa Local Vault", "ANOTHER-PRODUCTS-SECRET", KeychainItemOwner.Foreign);
        return gate;
    }

    private static void AssertForeignItemsIntact(OwnerAwareFakeKeychainGate gate)
    {
        Assert.True(gate.Contains("auth_jwt"), "the foreign bare access token must survive");
        Assert.True(gate.Contains("auth_refresh"), "the foreign bare refresh token must survive");
        Assert.True(gate.Contains("auth_expires"), "the foreign bare expiry must survive");
        Assert.True(gate.Contains("MAUI Sherpa Local Vault"), "another product's item must survive");

        Assert.Equal("FOREIGN-REFRESH-TOKEN", gate.ValueOf("auth_refresh"));
        Assert.Equal("ANOTHER-PRODUCTS-SECRET", gate.ValueOf("MAUI Sherpa Local Vault"));
        Assert.Equal(KeychainItemOwner.Foreign, gate.OwnerOf("auth_refresh"));

        Assert.DoesNotContain("auth_jwt", gate.DeleteAttempts);
        Assert.DoesNotContain("auth_refresh", gate.DeleteAttempts);
        Assert.DoesNotContain("auth_expires", gate.DeleteAttempts);
        Assert.DoesNotContain("MAUI Sherpa Local Vault", gate.DeleteAttempts);
    }

    // ------------------------------------------------------------------ SetAsync

    /// <summary>
    /// The first Mal blocker. <c>SetAsync</c> used to call <c>RetireLegacyAccount</c>, deleting the
    /// bare name after every successful scoped write.
    /// </summary>
    [Fact]
    public async Task SetAsync_never_deletes_the_bare_account()
    {
        var gate = GateWithForeignBareItems();
        var sut = CreateStorage(gate);

        await sut.SetAsync("auth_refresh", "OUR-NEW-REFRESH-TOKEN");

        AssertForeignItemsIntact(gate);
        Assert.Equal("OUR-NEW-REFRESH-TOKEN", gate.ValueOf(Scoped + "auth_refresh"));
        Assert.All(gate.WriteAttempts, a => Assert.StartsWith(Scoped, a, StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------- Remove

    /// <summary>The second Mal blocker: <c>Remove</c> used to delete the bare twin as well.</summary>
    [Fact]
    public void Remove_never_deletes_the_bare_account()
    {
        var gate = GateWithForeignBareItems();
        gate.Seed(Scoped + "auth_refresh", "OURS", KeychainItemOwner.ThisApp);
        var sut = CreateStorage(gate);

        sut.Remove("auth_refresh");

        AssertForeignItemsIntact(gate);
        Assert.False(gate.Contains(Scoped + "auth_refresh"), "our own item should be gone");
    }

    [Fact]
    public void Remove_of_a_key_we_never_stored_touches_nothing()
    {
        var gate = GateWithForeignBareItems();
        var sut = CreateStorage(gate);

        sut.Remove("auth_jwt");

        AssertForeignItemsIntact(gate);
    }

    // ------------------------------------------------------------- read paths

    [Fact]
    public async Task TryGetAsync_never_reads_or_deletes_a_bare_account()
    {
        var gate = GateWithForeignBareItems();
        var sut = CreateStorage(gate);

        var result = await sut.TryGetAsync("auth_refresh", SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.NotFound, result.Status);
        Assert.All(gate.ReadAttempts, a => Assert.StartsWith(Scoped, a, StringComparison.Ordinal));
        AssertForeignItemsIntact(gate);
    }

    /// <summary>
    /// A refused read of a <b>legacy</b> account must never mark the app's own scoped account as
    /// needing interaction — that would suppress reads of a key this app can perfectly well read.
    /// Structurally guaranteed now, because the storage service never reads legacy accounts at all.
    /// </summary>
    [Fact]
    public async Task A_foreign_bare_item_does_not_poison_the_scoped_needs_interaction_cache()
    {
        var gate = GateWithForeignBareItems();
        gate.Seed(Scoped + "auth_refresh", "OURS", KeychainItemOwner.ThisApp);
        var sut = CreateStorage(gate);

        var first = await sut.TryGetAsync("auth_refresh", SecureStorageAccess.NoInteraction);
        var second = await sut.TryGetAsync("auth_refresh", SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.Found, first.Status);
        Assert.Equal("OURS", first.Value);
        Assert.Equal(SecureStorageReadStatus.Found, second.Status);
    }

    // ------------------------------------------------- AuthTokenStore lifecycle

    /// <summary>
    /// Sign-out through the real <see cref="AuthTokenStore"/> — the path a learner actually takes —
    /// must not remove a single foreign item.
    /// </summary>
    [Fact]
    public async Task AuthTokenStore_ClearAsync_leaves_every_foreign_item_intact()
    {
        var gate = GateWithForeignBareItems();
        var storage = CreateStorage(gate);
        var prefs = new FakePreferences();
        var store = new AuthTokenStore(storage, prefs, NullLogger.Instance);

        await store.PersistAsync("OUR-JWT", "OUR-REFRESH", DateTimeOffset.UtcNow.AddHours(1));
        await store.ClearAsync();

        AssertForeignItemsIntact(gate);
    }

    /// <summary>
    /// A failed persist rolls back by removing all three keys. That rollback must also stay inside
    /// this app's namespace.
    /// </summary>
    [Fact]
    public async Task AuthTokenStore_persist_rollback_leaves_every_foreign_item_intact()
    {
        var gate = new FailingWriteGate();
        foreach (var (account, value) in new[]
                 {
                     ("auth_jwt", "FOREIGN-ACCESS-TOKEN"),
                     ("auth_refresh", "FOREIGN-REFRESH-TOKEN"),
                     ("MAUI Sherpa Local Vault", "ANOTHER-PRODUCTS-SECRET"),
                 })
        {
            gate.Seed(account, value, KeychainItemOwner.Foreign);
        }

        var storage = CreateStorage(gate);
        var prefs = new FakePreferences();
        var store = new AuthTokenStore(storage, prefs, NullLogger.Instance);

        gate.FailWritesForAccountsContaining = "auth_expires";

        await Assert.ThrowsAnyAsync<Exception>(
            () => store.PersistAsync("OUR-JWT", "OUR-REFRESH", DateTimeOffset.UtcNow.AddHours(1)));

        Assert.True(gate.Contains("auth_jwt"));
        Assert.True(gate.Contains("auth_refresh"));
        Assert.True(gate.Contains("MAUI Sherpa Local Vault"));
        Assert.DoesNotContain("auth_jwt", gate.DeleteAttempts);
        Assert.DoesNotContain("auth_refresh", gate.DeleteAttempts);
        Assert.DoesNotContain("MAUI Sherpa Local Vault", gate.DeleteAttempts);
    }

    private sealed class FailingWriteGate : OwnerAwareFakeKeychainGate
    {
        public string? FailWritesForAccountsContaining { get; set; }

        public override int Write(string account, byte[] data)
        {
            if (FailWritesForAccountsContaining is not null
                && account.Contains(FailWritesForAccountsContaining, StringComparison.Ordinal))
            {
                WriteAttempts.Add(account);
                return KeychainStatus.AuthFailed;
            }

            return base.Write(account, data);
        }
    }
}
