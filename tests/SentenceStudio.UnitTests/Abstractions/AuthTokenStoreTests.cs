using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using Xunit;

namespace SentenceStudio.UnitTests.Abstractions;

/// <summary>
/// Pins the two properties credential storage has to have and previously did not.
///
/// <para><b>Persisting is all-or-nothing.</b> The access token, refresh token and expiry used to be
/// three independent <c>SetAsync</c> calls with the in-memory cache populated before any of them.
/// A failure on the second call left account B's access token beside account A's refresh token, and
/// the next silent refresh presented A's token and signed the learner in as A.</para>
///
/// <para><b>Sign-out is honest.</b> Removal used to discard all three <c>Remove</c> return values
/// and log "tokens and profile cleared" unconditionally. <c>Remove</c> returns <c>false</c> both for
/// "wasn't there" and for "couldn't remove it", so that line was not evidence of anything — a
/// learner could be told they were signed out while a working refresh token stayed on the machine.
/// </para>
/// </summary>
public class AuthTokenStoreTests
{
    private const string Jwt = AuthTokenStore.JwtKey;
    private const string Refresh = AuthTokenStore.RefreshKey;
    private const string Expires = AuthTokenStore.ExpiresKey;
    private const string Latch = AuthTokenStore.CleanupPendingPreferenceKey;

    private static readonly DateTimeOffset Expiry =
        new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static AuthTokenStore CreateSut(
        FakeSecureStorage storage,
        FakePreferences preferences,
        out CapturingLogger log)
    {
        log = new CapturingLogger();
        return new AuthTokenStore(storage, preferences, log);
    }

    // ------------------------------------------------------------- happy path

    [Fact]
    public async Task PersistAsync_AllWritesSucceed_StoresTripleAndClearsTheLatch()
    {
        var storage = new FakeSecureStorage();
        var prefs = new FakePreferences();
        var sut = CreateSut(storage, prefs, out _);

        await sut.PersistAsync("access-1", "refresh-1", Expiry);

        Assert.Equal("access-1", storage.Items[Jwt]);
        Assert.Equal("refresh-1", storage.Items[Refresh]);
        Assert.Equal(Expiry.ToString("O"), storage.Items[Expires]);
        Assert.False(sut.IsCleanupPending);
        Assert.False(prefs.Values.ContainsKey(Latch));
    }

    /// <summary>
    /// A successful write is how a device stuck behind a failed cleanup gets unstuck: storage is
    /// back in a known state for a known account, so the latch has no more work to do.
    /// </summary>
    [Fact]
    public async Task PersistAsync_ClearsALatchLeftBehindByAnEarlierFailedCleanup()
    {
        var storage = new FakeSecureStorage();
        var prefs = new FakePreferences();
        prefs.Set(Latch, true);
        var sut = CreateSut(storage, prefs, out _);

        await sut.PersistAsync("access-1", "refresh-1", Expiry);

        Assert.False(sut.IsCleanupPending);
    }

    // ------------------------------------------------- atomicity: each position

    [Theory]
    [InlineData(Jwt)]
    [InlineData(Refresh)]
    [InlineData(Expires)]
    public async Task PersistAsync_WriteFailsAtAnyPosition_RemovesAllThreeAndThrows(string failingKey)
    {
        var storage = new FakeSecureStorage();
        storage.FailWrites.Add(failingKey);
        var prefs = new FakePreferences();
        var sut = CreateSut(storage, prefs, out _);

        var ex = await Assert.ThrowsAsync<AuthTokenPersistenceException>(
            () => sut.PersistAsync("access-1", "refresh-1", Expiry));

        Assert.Equal(failingKey, ex.FailedKey);
        Assert.True(ex.RollbackVerified);

        // Not one of the three survives — including the ones that were written successfully
        // before the failure.
        Assert.False(storage.Items.ContainsKey(Jwt));
        Assert.False(storage.Items.ContainsKey(Refresh));
        Assert.False(storage.Items.ContainsKey(Expires));

        // Rollback attempts every owned key, not merely the ones it believes it wrote: a failed
        // write can still have destroyed the item it was replacing.
        Assert.Contains(Jwt, storage.RemoveCalls);
        Assert.Contains(Refresh, storage.RemoveCalls);
        Assert.Contains(Expires, storage.RemoveCalls);
    }

    /// <summary>
    /// THE regression test. Account A is signed in; account B signs in and the refresh-token write
    /// fails. Nothing belonging to either account may survive — a surviving A refresh token beside
    /// a B access token is a silent cross-account sign-in on the next launch.
    /// </summary>
    [Fact]
    public async Task PersistAsync_FailsOverAnExistingSession_LeavesNoMixedAccountTriple()
    {
        var storage = new FakeSecureStorage();
        storage.Items[Jwt] = "ACCOUNT-A-ACCESS";
        storage.Items[Refresh] = "ACCOUNT-A-REFRESH";
        storage.Items[Expires] = Expiry.AddDays(-1).ToString("O");

        storage.FailWrites.Add(Refresh);
        var prefs = new FakePreferences();
        var sut = CreateSut(storage, prefs, out _);

        await Assert.ThrowsAsync<AuthTokenPersistenceException>(
            () => sut.PersistAsync("ACCOUNT-B-ACCESS", "ACCOUNT-B-REFRESH", Expiry));

        Assert.Empty(storage.Items);
    }

    /// <summary>
    /// The macOS gate implements a write as delete-then-add, so a write that fails can leave the
    /// slot empty rather than untouched. Rollback must still sweep every key.
    /// </summary>
    [Fact]
    public async Task PersistAsync_WriteDestroysThePreviousItemBeforeFailing_StillLeavesNothingBehind()
    {
        var storage = new FakeSecureStorage { DestroyOnFailedWrite = true };
        storage.Items[Jwt] = "ACCOUNT-A-ACCESS";
        storage.Items[Refresh] = "ACCOUNT-A-REFRESH";
        storage.Items[Expires] = Expiry.AddDays(-1).ToString("O");
        storage.FailWrites.Add(Expires);

        var sut = CreateSut(storage, new FakePreferences(), out _);

        await Assert.ThrowsAsync<AuthTokenPersistenceException>(
            () => sut.PersistAsync("ACCOUNT-B-ACCESS", "ACCOUNT-B-REFRESH", Expiry));

        Assert.Empty(storage.Items);
    }

    [Fact]
    public async Task PersistAsync_RollbackCannotClearAKey_ReportsItAndLeavesTheLatchSet()
    {
        var storage = new FakeSecureStorage();
        storage.FailWrites.Add(Expires);
        storage.Unremovable.Add(Refresh);      // Remove() is a no-op for this key
        var prefs = new FakePreferences();
        var sut = CreateSut(storage, prefs, out _);

        var ex = await Assert.ThrowsAsync<AuthTokenPersistenceException>(
            () => sut.PersistAsync("access-1", "refresh-1", Expiry));

        Assert.False(ex.RollbackVerified);
        Assert.Equal(new[] { Refresh }, ex.AffectedKeys);
        Assert.True(sut.IsCleanupPending);
    }

    [Fact]
    public async Task PersistAsync_LatchesBeforeTheFirstWrite_SoACrashMidSequenceCannotRestore()
    {
        var journal = new List<string>();
        var storage = new FakeSecureStorage(journal);
        var prefs = new FakePreferences(journal);
        var sut = CreateSut(storage, prefs, out _);

        await sut.PersistAsync("access-1", "refresh-1", Expiry);

        var latchSet = journal.IndexOf($"pref-set:{Latch}");
        var firstWrite = journal.IndexOf($"write:{Jwt}");

        Assert.True(latchSet >= 0, "the latch must be recorded before writing starts");
        Assert.True(
            latchSet < firstWrite,
            "the latch has to precede the first write, or a crash between writes leaves a trusted half-triple");
    }

    // ---------------------------------------------------------------- clearing

    [Fact]
    public async Task ClearAsync_EverythingRemoved_ReportsCleanAndClearsTheLatch()
    {
        var storage = new FakeSecureStorage();
        storage.Items[Jwt] = "a";
        storage.Items[Refresh] = "r";
        storage.Items[Expires] = "e";
        var prefs = new FakePreferences();
        var sut = CreateSut(storage, prefs, out _);

        var outcome = await sut.ClearAsync();

        Assert.True(outcome.CredentialsCleared);
        Assert.Empty(outcome.UnclearedKeys);
        Assert.Empty(storage.Items);
        Assert.False(sut.IsCleanupPending);
    }

    /// <summary>
    /// No false success: a key that survives removal must produce a thrown failure, not a cheerful
    /// "signed out" log line.
    /// </summary>
    [Fact]
    public async Task ClearAsync_KeySurvivesRemoval_ThrowsAndKeepsTheLatchSet()
    {
        var storage = new FakeSecureStorage();
        storage.Items[Jwt] = "a";
        storage.Items[Refresh] = "r";
        storage.Items[Expires] = "e";
        storage.Unremovable.Add(Refresh);
        var prefs = new FakePreferences();
        var sut = CreateSut(storage, prefs, out _);

        var ex = await Assert.ThrowsAsync<AuthTokenCleanupException>(() => sut.ClearAsync());

        Assert.Equal(new[] { Refresh }, ex.AffectedKeys);
        Assert.True(sut.IsCleanupPending);

        // The keys that could be removed still were — a partial failure is not a reason to leave
        // the rest of the credentials in place.
        Assert.False(storage.Items.ContainsKey(Jwt));
        Assert.False(storage.Items.ContainsKey(Expires));
        Assert.True(storage.Items.ContainsKey(Refresh));
    }

    /// <summary>
    /// The interface's default <c>TryGetAsync</c> collapses every non-value outcome to "not found".
    /// If verification accepted that, a keystore that refuses to answer would read as proof of
    /// deletion. These are the outcomes that must NOT count as proof.
    /// </summary>
    [Theory]
    [InlineData(SecureStorageReadStatus.InteractionRequired)]
    [InlineData(SecureStorageReadStatus.Failed)]
    [InlineData(SecureStorageReadStatus.Cancelled)]
    [InlineData(SecureStorageReadStatus.Found)]
    [InlineData(SecureStorageReadStatus.Malformed)]
    public async Task ClearAsync_VerificationCannotProveAbsence_ReportsFailure(SecureStorageReadStatus status)
    {
        var storage = new FakeSecureStorage();
        storage.ReadStatusOverride[Refresh] = status;
        var sut = CreateSut(storage, new FakePreferences(), out _);

        var ex = await Assert.ThrowsAsync<AuthTokenCleanupException>(() => sut.ClearAsync());

        Assert.Equal(new[] { Refresh }, ex.AffectedKeys);
    }

    [Fact]
    public async Task ClearAsync_RemoveThrows_ButTheItemIsActuallyGone_ReportsClean()
    {
        // Some platform implementations throw after having removed the item. The read-back is what
        // decides, not the exception.
        var storage = new FakeSecureStorage();
        storage.ThrowOnRemoveAfterDeleting.Add(Refresh);
        storage.Items[Refresh] = "r";
        var sut = CreateSut(storage, new FakePreferences(), out _);

        var outcome = await sut.ClearAsync();

        Assert.True(outcome.CredentialsCleared);
        Assert.Empty(storage.Items);
    }

    [Fact]
    public async Task ClearAsync_RemoveThrowsAndTheItemSurvives_ReportsFailure()
    {
        var storage = new FakeSecureStorage();
        storage.ThrowOnRemove.Add(Refresh);
        storage.Items[Refresh] = "r";
        var sut = CreateSut(storage, new FakePreferences(), out _);

        var ex = await Assert.ThrowsAsync<AuthTokenCleanupException>(() => sut.ClearAsync());

        Assert.Equal(new[] { Refresh }, ex.AffectedKeys);
    }

    /// <summary>
    /// Retries are bounded. Sign-out can run from a UI handler, and an unbounded loop against a
    /// keystore that will never let go is a hang, not a fix.
    /// </summary>
    [Fact]
    public async Task ClearAsync_RetriesAreBounded()
    {
        var storage = new FakeSecureStorage();
        storage.Items[Refresh] = "r";
        storage.Unremovable.Add(Refresh);
        var sut = CreateSut(storage, new FakePreferences(), out _);

        await Assert.ThrowsAsync<AuthTokenCleanupException>(() => sut.ClearAsync());

        Assert.Equal(2, storage.RemoveCalls.Count(k => k == Refresh));
    }

    /// <summary>
    /// A read that will not answer cannot be improved by trying again, so it stops after one round
    /// rather than burning the retry budget on the same refusal.
    /// </summary>
    [Fact]
    public async Task ClearAsync_UnverifiableRead_DoesNotRetry()
    {
        var storage = new FakeSecureStorage();
        storage.ReadStatusOverride[Refresh] = SecureStorageReadStatus.InteractionRequired;
        var sut = CreateSut(storage, new FakePreferences(), out _);

        await Assert.ThrowsAsync<AuthTokenCleanupException>(() => sut.ClearAsync());

        Assert.Equal(1, storage.RemoveCalls.Count(k => k == Refresh));
    }

    [Fact]
    public async Task ClearAsync_LatchesBeforeTheFirstRemoval()
    {
        var journal = new List<string>();
        var storage = new FakeSecureStorage(journal);
        storage.Items[Jwt] = "a";
        var prefs = new FakePreferences(journal);
        var sut = CreateSut(storage, prefs, out _);

        await sut.ClearAsync();

        var latchSet = journal.IndexOf($"pref-set:{Latch}");
        var firstRemove = journal.IndexOf($"remove:{Jwt}");

        Assert.True(latchSet >= 0);
        Assert.True(
            latchSet < firstRemove,
            "a crash part-way through removal must still leave the latch set");
    }

    [Fact]
    public async Task ClearAsync_AttemptsEveryOwnedKeyEvenAfterOneFails()
    {
        var storage = new FakeSecureStorage();
        storage.Items[Jwt] = "a";
        storage.Items[Refresh] = "r";
        storage.Items[Expires] = "e";
        storage.Unremovable.Add(Jwt);
        var sut = CreateSut(storage, new FakePreferences(), out _);

        await Assert.ThrowsAsync<AuthTokenCleanupException>(() => sut.ClearAsync());

        Assert.Contains(Refresh, storage.RemoveCalls);
        Assert.Contains(Expires, storage.RemoveCalls);
    }

    /// <summary>Removal is scoped to the three owned keys; nothing else in the store is touched.</summary>
    [Fact]
    public async Task ClearAsync_NeverTouchesKeysThisAppDoesNotOwn()
    {
        var storage = new FakeSecureStorage();
        storage.Items[Jwt] = "a";
        storage.Items["Some Other MAUI App Vault"] = "theirs";
        var sut = CreateSut(storage, new FakePreferences(), out _);

        await sut.ClearAsync();

        Assert.Equal("theirs", storage.Items["Some Other MAUI App Vault"]);
        Assert.DoesNotContain("Some Other MAUI App Vault", storage.RemoveCalls);
        Assert.Equal(0, storage.RemoveAllCalls);
    }

    // -------------------------------------------------------------- TryClear

    [Fact]
    public async Task TryClearAsync_Failure_ReportsOutcomeWithoutThrowing()
    {
        var storage = new FakeSecureStorage();
        storage.Items[Refresh] = "r";
        storage.Unremovable.Add(Refresh);
        var sut = CreateSut(storage, new FakePreferences(), out _);

        var outcome = await sut.TryClearAsync();

        Assert.False(outcome.CredentialsCleared);
        Assert.Equal(new[] { Refresh }, outcome.UnclearedKeys);
        Assert.True(sut.IsCleanupPending);
    }

    [Fact]
    public async Task TryClearAsync_Success_ClearsTheLatch()
    {
        var storage = new FakeSecureStorage();
        var prefs = new FakePreferences();
        prefs.Set(Latch, true);
        var sut = CreateSut(storage, prefs, out _);

        var outcome = await sut.TryClearAsync();

        Assert.True(outcome.CredentialsCleared);
        Assert.False(sut.IsCleanupPending);
    }

    // ----------------------------------------------------------------- latch

    [Fact]
    public void IsCleanupPending_UnsetPreference_IsFalse()
    {
        var sut = CreateSut(new FakeSecureStorage(), new FakePreferences(), out _);
        Assert.False(sut.IsCleanupPending);
    }

    /// <summary>
    /// An unreadable latch is treated as set. Guessing "false" would restore a session on exactly
    /// the device whose storage layer is misbehaving.
    /// </summary>
    [Fact]
    public void IsCleanupPending_PreferenceReadThrows_AssumesPending()
    {
        var prefs = new FakePreferences { ThrowOnGet = true };
        var sut = CreateSut(new FakeSecureStorage(), prefs, out _);

        Assert.True(sut.IsCleanupPending);
    }

    /// <summary>A latch that cannot be written must not take the operation down with it.</summary>
    [Fact]
    public async Task PersistAsync_LatchWriteThrows_StillPersists()
    {
        var storage = new FakeSecureStorage();
        var prefs = new FakePreferences { ThrowOnSet = true };
        var sut = CreateSut(storage, prefs, out _);

        await sut.PersistAsync("access-1", "refresh-1", Expiry);

        Assert.Equal("access-1", storage.Items[Jwt]);
    }

    // --------------------------------------------------------------- privacy

    /// <summary>
    /// A token must never reach a log, and neither must its length: a length narrows a brute-force
    /// search and distinguishes an access token from a refresh token in a log an operator reads.
    /// </summary>
    [Fact]
    public async Task Logging_NeverIncludesTokenValuesOrLengths()
    {
        const string access = "ACCESS-TOKEN-SECRET-abcdefghijklmnop";
        const string refresh = "REFRESH-TOKEN-SECRET-zyxwvutsrqponml";

        var storage = new FakeSecureStorage();
        storage.FailWrites.Add(Expires);
        var sut = CreateSut(storage, new FakePreferences(), out var log);

        var ex = await Assert.ThrowsAsync<AuthTokenPersistenceException>(
            () => sut.PersistAsync(access, refresh, Expiry));

        var text = log.Text + "\n" + ex.Message;

        Assert.DoesNotContain(access, text, StringComparison.Ordinal);
        Assert.DoesNotContain(refresh, text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(access.Length.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("length", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logging_OnSuccessfulPersist_NeverIncludesTokenValues()
    {
        const string access = "ACCESS-TOKEN-SECRET-abcdefghijklmnop";
        const string refresh = "REFRESH-TOKEN-SECRET-zyxwvutsrqponml";

        var sut = CreateSut(new FakeSecureStorage(), new FakePreferences(), out var log);

        await sut.PersistAsync(access, refresh, Expiry);

        Assert.DoesNotContain("SECRET", log.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PersistAsync_BlankToken_Throws()
    {
        var sut = CreateSut(new FakeSecureStorage(), new FakePreferences(), out _);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.PersistAsync("", "r", Expiry));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.PersistAsync("a", "", Expiry));
    }

    // ------------------------------------------------------------------ fakes

    private sealed class FakeSecureStorage : ISecureStorageService
    {
        private readonly List<string>? _journal;

        public FakeSecureStorage(List<string>? journal = null) => _journal = journal;

        public Dictionary<string, string> Items { get; } = new(StringComparer.Ordinal);

        /// <summary>Keys whose <c>SetAsync</c> throws.</summary>
        public HashSet<string> FailWrites { get; } = new(StringComparer.Ordinal);

        /// <summary>Keys whose <c>Remove</c> silently does nothing.</summary>
        public HashSet<string> Unremovable { get; } = new(StringComparer.Ordinal);

        /// <summary>Keys whose <c>Remove</c> throws without removing.</summary>
        public HashSet<string> ThrowOnRemove { get; } = new(StringComparer.Ordinal);

        /// <summary>Keys whose <c>Remove</c> removes the item and then throws.</summary>
        public HashSet<string> ThrowOnRemoveAfterDeleting { get; } = new(StringComparer.Ordinal);

        /// <summary>Forces a specific status out of <c>TryGetAsync</c>.</summary>
        public Dictionary<string, SecureStorageReadStatus> ReadStatusOverride { get; } =
            new(StringComparer.Ordinal);

        /// <summary>Models the macOS gate, whose write is a delete followed by an add.</summary>
        public bool DestroyOnFailedWrite { get; set; }

        public List<string> RemoveCalls { get; } = new();

        public int RemoveAllCalls { get; private set; }

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(Items.TryGetValue(key, out var v) ? v : null);

        public Task<SecureStorageReadResult> TryGetAsync(
            string key,
            SecureStorageAccess access,
            CancellationToken cancellationToken = default)
        {
            if (ReadStatusOverride.TryGetValue(key, out var forced))
            {
                var value = forced == SecureStorageReadStatus.Found
                    ? Items.GetValueOrDefault(key, "still-here")
                    : null;
                return Task.FromResult(new SecureStorageReadResult(forced, value));
            }

            return Task.FromResult(
                Items.TryGetValue(key, out var stored)
                    ? SecureStorageReadResult.FromValue(stored)
                    : SecureStorageReadResult.Missing);
        }

        public Task SetAsync(string key, string value)
        {
            if (FailWrites.Contains(key))
            {
                if (DestroyOnFailedWrite)
                    Items.Remove(key);

                throw new SecureStorageWriteException(key);
            }

            Items[key] = value;
            _journal?.Add($"write:{key}");
            return Task.CompletedTask;
        }

        public bool Remove(string key)
        {
            RemoveCalls.Add(key);
            _journal?.Add($"remove:{key}");

            if (ThrowOnRemove.Contains(key))
                throw new InvalidOperationException("platform removal failed");

            if (ThrowOnRemoveAfterDeleting.Contains(key))
            {
                Items.Remove(key);
                throw new InvalidOperationException("removed, then failed");
            }

            if (Unremovable.Contains(key))
                return false;

            return Items.Remove(key);
        }

        public void RemoveAll()
        {
            RemoveAllCalls++;
            Items.Clear();
        }
    }

    private sealed class FakePreferences : IPreferencesService
    {
        private readonly List<string>? _journal;

        public FakePreferences(List<string>? journal = null) => _journal = journal;

        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

        public bool ThrowOnGet { get; set; }

        public bool ThrowOnSet { get; set; }

        public T Get<T>(string key, T defaultValue)
        {
            if (ThrowOnGet)
                throw new InvalidOperationException("preferences unavailable");

            return Values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        }

        public void Set<T>(string key, T value)
        {
            if (ThrowOnSet)
                throw new InvalidOperationException("preferences unavailable");

            Values[key] = value;
            _journal?.Add($"pref-set:{key}");
        }

        public void Remove(string key)
        {
            Values.Remove(key);
            _journal?.Add($"pref-remove:{key}");
        }

        public void Clear() => Values.Clear();
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _lines = new();

        public string Text => string.Join("\n", _lines);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _lines.Add(formatter(state, exception));

            if (exception is not null)
                _lines.Add(exception.ToString());

            // Also capture the raw structured values — a template argument could leak a secret even
            // when the rendered message looks clean.
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                _lines.AddRange(pairs.Select(p => $"{p.Key}={p.Value}"));
        }
    }
}
