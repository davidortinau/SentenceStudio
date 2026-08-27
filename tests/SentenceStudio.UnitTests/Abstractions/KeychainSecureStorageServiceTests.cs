using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Abstractions.Keychain;
using Xunit;

namespace SentenceStudio.UnitTests.Abstractions;

/// <summary>
/// Regression tests for the macOS AppKit startup hang.
///
/// The app wedged on "Checking authentication..." because MAUI's macOS SecureStorage reads the
/// legacy file-based keychain through <c>SecItemCopyMatching</c>. Legacy items are ACL-gated on
/// the creating binary's code signature; Debug builds of the macOS head are ad-hoc signed, so
/// every rebuild changes the cdhash, macOS raises a modal SecurityAgent prompt, and the native
/// call blocks forever — it neither returns nor throws, so no try/catch and no Task.Run could
/// save it.
///
/// <see cref="KeychainSecureStorageService"/> is the fix: automatic reads run with the platform
/// prompt suppressed and fail fast with a typed status. These tests pin that behaviour.
/// </summary>
public class KeychainSecureStorageServiceTests
{
    private const string Key = "auth_refresh";

    /// <summary>
    /// The account name the service actually stores <see cref="Key"/> under. Items are namespaced
    /// so this app cannot collide with another MAUI app in the machine-global keychain service.
    /// </summary>
    private static string Scoped(string key) => KeychainSecureStorageService.AccountNamespace + key;

    private static KeychainSecureStorageService CreateSut(FakeKeychainGate gate, out CapturingLogger log)
    {
        log = new CapturingLogger();
        return new KeychainSecureStorageService(gate, log);
    }

    // ---------------------------------------------------------------- success

    [Fact]
    public async Task TryGetAsync_ItemPresent_ReturnsFoundWithValue()
    {
        var gate = new FakeKeychainGate();
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("refresh-token-value");
        var sut = CreateSut(gate, out _);

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.Found, result.Status);
        Assert.Equal("refresh-token-value", result.Value);
        Assert.True(result.IsFound);
        Assert.False(result.RequiresInteraction);
    }

    [Fact]
    public async Task GetAsync_ItemPresent_ReturnsValue_ForBackCompat()
    {
        var gate = new FakeKeychainGate();
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("refresh-token-value");
        var sut = CreateSut(gate, out _);

        Assert.Equal("refresh-token-value", await sut.GetAsync(Key));
    }

    // ------------------------------------------------------------- not found

    [Fact]
    public async Task TryGetAsync_NoItem_ReturnsNotFound()
    {
        var sut = CreateSut(new FakeKeychainGate(), out _);

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.NotFound, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task GetAsync_NoItem_ReturnsNull()
    {
        var sut = CreateSut(new FakeKeychainGate(), out _);

        Assert.Null(await sut.GetAsync(Key));
    }

    // -------------------------------------------------- interaction required

    /// <summary>
    /// THE regression test. errSecAuthFailed is what the legacy keychain returns when an item's
    /// ACL would require a SecurityAgent prompt but interaction has been suppressed. It must
    /// surface as a typed status, not as a hang and not as an exception.
    /// </summary>
    [Theory]
    [InlineData(KeychainStatus.AuthFailed)]              // legacy keychain, prompt suppressed
    [InlineData(KeychainStatus.InteractionNotAllowed)]   // keychain locked / UI disallowed
    [InlineData(KeychainStatus.InteractionRequired)]
    [InlineData(KeychainStatus.UserCanceled)]
    [InlineData(KeychainStatus.MissingEntitlement)]      // data-protection keychain, ad-hoc binary
    public async Task TryGetAsync_PlatformWantsUser_ReturnsInteractionRequired(int osStatus)
    {
        var gate = new FakeKeychainGate { ReadStatusOverride = osStatus };
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("refresh-token-value");
        var sut = CreateSut(gate, out _);

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.InteractionRequired, result.Status);
        Assert.True(result.RequiresInteraction);
        Assert.Null(result.Value);
    }

    /// <summary>
    /// Non-destructive contract: a refused read must not delete, overwrite, or otherwise disturb
    /// the stored credential. The user's session survives; only this read is skipped.
    /// </summary>
    [Fact]
    public async Task TryGetAsync_InteractionRequired_PreservesTheStoredItem()
    {
        var gate = new FakeKeychainGate { ReadStatusOverride = KeychainStatus.AuthFailed };
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("refresh-token-value");
        var sut = CreateSut(gate, out _);

        await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.True(gate.Items.ContainsKey(Scoped(Key)));
        Assert.Equal(0, gate.WriteCount);
        Assert.Equal(0, gate.DeleteCount);
    }

    /// <summary>
    /// The no-UI flag must actually be applied for an automatic read, and must be restored
    /// afterwards so a later user-initiated operation can still prompt.
    /// </summary>
    [Fact]
    public async Task TryGetAsync_NoInteraction_SuppressesPromptAroundTheReadAndRestoresIt()
    {
        var gate = new FakeKeychainGate();
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("v");
        var sut = CreateSut(gate, out _);

        await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal(new[] { false, true }, gate.InteractionCalls);
        Assert.False(gate.InteractionAllowedDuringRead);
        Assert.True(gate.InteractionAllowed); // restored
    }

    [Fact]
    public async Task TryGetAsync_NoInteraction_RestoresPromptEvenWhenTheReadThrows()
    {
        var gate = new FakeKeychainGate { ThrowOnRead = new InvalidOperationException("boom") };
        var sut = CreateSut(gate, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction));

        Assert.Equal(new[] { false, true }, gate.InteractionCalls);
        Assert.True(gate.InteractionAllowed);
    }

    /// <summary>User-initiated reads are allowed to prompt, so the flag is left alone.</summary>
    [Fact]
    public async Task TryGetAsync_AllowInteraction_DoesNotTouchThePromptFlag()
    {
        var gate = new FakeKeychainGate();
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("v");
        var sut = CreateSut(gate, out _);

        await sut.TryGetAsync(Key, SecureStorageAccess.AllowInteraction);

        Assert.Empty(gate.InteractionCalls);
    }

    /// <summary>
    /// If the platform will not let us turn the prompt off, we must skip the read rather than
    /// make a call that could block on a dialog nobody can answer.
    /// </summary>
    [Fact]
    public async Task TryGetAsync_CannotSuppressPrompt_SkipsTheReadEntirely()
    {
        var gate = new FakeKeychainGate { CanSetInteraction = false };
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("v");
        var sut = CreateSut(gate, out _);

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.InteractionRequired, result.Status);
        Assert.Equal(0, gate.ReadCount);
    }

    // ------------------------------------------------- refusal is not re-asked

    /// <summary>
    /// A refused item cannot become readable until it is rewritten, and this type is the only
    /// writer. Re-querying on every request wasted a native call per outgoing HTTP call and buried
    /// the log under thousands of identical lines (observed live on the macOS head).
    /// </summary>
    [Fact]
    public async Task TryGetAsync_RepeatedAfterRefusal_DoesNotHitTheKeychainAgain()
    {
        var gate = new FakeKeychainGate { ReadStatusOverride = KeychainStatus.AuthFailed };
        var sut = CreateSut(gate, out var log);

        for (var i = 0; i < 25; i++)
        {
            var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);
            Assert.Equal(SecureStorageReadStatus.InteractionRequired, result.Status);
        }

        Assert.Equal(1, gate.ReadCount);

        // Rendered message only — the format string ("...'{Key}' needs...") stays unsubstituted,
        // so matching on the substituted key counts log calls exactly once.
        Assert.Single(
            log.Lines,
            l => l.Contains($"'{Key}' needs user authorisation", StringComparison.Ordinal));
    }

    /// <summary>An explicit, user-initiated read is still allowed to try for real.</summary>
    [Fact]
    public async Task TryGetAsync_AllowInteraction_IsNotSuppressedByAPreviousRefusal()
    {
        var gate = new FakeKeychainGate { ReadStatusOverride = KeychainStatus.AuthFailed };
        var sut = CreateSut(gate, out _);

        await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);
        await sut.TryGetAsync(Key, SecureStorageAccess.AllowInteraction);

        Assert.Equal(2, gate.ReadCount);
    }

    /// <summary>
    /// Signing in rewrites the item under the running signature, so the next automatic read must
    /// go back to the keychain rather than replaying the stale refusal.
    /// </summary>
    [Fact]
    public async Task SetAsync_AfterRefusal_MakesTheKeyReadableAgain()
    {
        var gate = new FakeKeychainGate { ReadStatusOverride = KeychainStatus.AuthFailed };
        var sut = CreateSut(gate, out _);

        var refused = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);
        Assert.Equal(SecureStorageReadStatus.InteractionRequired, refused.Status);

        gate.ReadStatusOverride = null;
        await sut.SetAsync(Key, "fresh-token");

        var after = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);
        Assert.Equal(SecureStorageReadStatus.Found, after.Status);
        Assert.Equal("fresh-token", after.Value);
    }

    [Fact]
    public async Task Remove_AfterRefusal_ClearsTheRefusalRecord()
    {
        var gate = new FakeKeychainGate { ReadStatusOverride = KeychainStatus.AuthFailed };
        var sut = CreateSut(gate, out _);

        await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);
        sut.Remove(Key);

        gate.ReadStatusOverride = KeychainStatus.ItemNotFound;
        var after = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.NotFound, after.Status);

        // Exactly two reads, both of the app-scoped account. There is no third: this type never
        // probes the bare (un-namespaced) account, because a name in a machine-global service is
        // not evidence of ownership. Corroborated adoption lives in LegacyCredentialAdoption.
        Assert.Equal(2, gate.ReadCount);
    }

    // ------------------------------------------------------------- malformed

    [Fact]
    public async Task TryGetAsync_UndecodableBytes_ReturnsMalformedAndKeepsTheItem()
    {
        var gate = new FakeKeychainGate();
        // Lone continuation byte + truncated sequence: invalid UTF-8.
        gate.Items[Scoped(Key)] = new byte[] { 0x80, 0xC3 };
        var sut = CreateSut(gate, out _);

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.Malformed, result.Status);
        Assert.Null(result.Value);
        Assert.True(gate.Items.ContainsKey(Scoped(Key)));
        Assert.Equal(0, gate.DeleteCount);
        Assert.Equal(0, gate.WriteCount);
    }

    [Fact]
    public async Task TryGetAsync_SuccessButEmptyPayload_ReturnsMalformed()
    {
        var gate = new FakeKeychainGate();
        gate.Items[Scoped(Key)] = Array.Empty<byte>();
        var sut = CreateSut(gate, out _);

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.Malformed, result.Status);
    }

    // ----------------------------------------------------------- cancellation

    [Fact]
    public async Task TryGetAsync_AlreadyCancelled_ReturnsCancelledWithoutReading()
    {
        var gate = new FakeKeychainGate();
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("v");
        var sut = CreateSut(gate, out _);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction, cts.Token);

        Assert.Equal(SecureStorageReadStatus.Cancelled, result.Status);
        Assert.Equal(0, gate.ReadCount);
        Assert.Empty(gate.InteractionCalls);
    }

    // ------------------------------------------------------------ other error

    [Fact]
    public async Task TryGetAsync_UnknownPlatformError_ReturnsFailed()
    {
        var gate = new FakeKeychainGate { ReadStatusOverride = -25291 /* errSecNotAvailable */ };
        var sut = CreateSut(gate, out _);

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.Failed, result.Status);
    }

    /// <summary>
    /// An unavailable gate must never be reported as "nothing is stored". Callers treat NotFound as
    /// proof: AuthTokenStore accepts it as evidence a credential was removed, and the auth provider
    /// accepts it as evidence there is no session. An unavailable keystore proves neither.
    /// </summary>
    [Fact]
    public async Task TryGetAsync_GateUnavailable_ReportsFailedNotMissing()
    {
        var gate = new FakeKeychainGate { IsAvailable = false };
        var sut = CreateSut(gate, out _);

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.Failed, result.Status);
        Assert.NotEqual(SecureStorageReadStatus.NotFound, result.Status);
    }

    // ------------------------------------------------------- no token/PII log

    /// <summary>
    /// Nothing in the read path may put a secret in the log — not the value, not a prefix of it,
    /// and not its length.
    /// </summary>
    [Theory]
    [InlineData(KeychainStatus.Success)]
    [InlineData(KeychainStatus.ItemNotFound)]
    [InlineData(KeychainStatus.AuthFailed)]
    [InlineData(-25291)]
    public async Task TryGetAsync_NeverLogsTheStoredValue(int osStatus)
    {
        const string secret = "eyJhbGciOiJIUzI1NiJ9.super-secret-refresh-token.signature";

        var gate = new FakeKeychainGate
        {
            ReadStatusOverride = osStatus == KeychainStatus.Success ? null : osStatus,
        };
        if (osStatus != KeychainStatus.ItemNotFound)
            gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes(secret);
        var sut = CreateSut(gate, out var log);

        await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        var all = log.Text;
        Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eyJ", all, StringComparison.Ordinal);
        Assert.DoesNotContain(secret.Length.ToString(), all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAsync_NeverLogsTheStoredValue()
    {
        const string secret = "another-super-secret-token";
        var gate = new FakeKeychainGate();
        var sut = CreateSut(gate, out var log);

        await sut.SetAsync(Key, secret);

        Assert.DoesNotContain(secret, log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadResult_ToString_DoesNotLeakValue()
    {
        var result = SecureStorageReadResult.FromValue("top-secret");
        Assert.Equal("Found", result.ToString());
        Assert.DoesNotContain("top-secret", result.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ write

    [Fact]
    public async Task SetAsync_StoresValueThroughTheGate()
    {
        var gate = new FakeKeychainGate();
        var sut = CreateSut(gate, out _);

        await sut.SetAsync(Key, "new-token");

        Assert.Equal("new-token", Encoding.UTF8.GetString(gate.Items[Scoped(Key)]));
        Assert.Equal(1, gate.WriteCount);

        // Never under the bare name: that account is shared with every other MAUI app on the box,
        // so writing it would put this app's credential where anyone can address it.
        Assert.False(gate.Items.ContainsKey(Key));
    }

    [Fact]
    public async Task SetAsync_PlatformFailure_Throws()
    {
        var gate = new FakeKeychainGate { WriteStatusOverride = KeychainStatus.AuthFailed };
        var sut = CreateSut(gate, out _);

        var ex = await Assert.ThrowsAsync<SecureStorageWriteException>(() => sut.SetAsync(Key, "v"));

        Assert.Equal(Key, ex.Key);
        Assert.DoesNotContain("v", ex.Message.Replace(Key, string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_MissingItem_ReturnsFalseAndDoesNotThrow()
    {
        var sut = CreateSut(new FakeKeychainGate(), out _);
        Assert.False(sut.Remove(Key));
    }

    [Fact]
    public void Remove_ExistingItem_ReturnsTrue()
    {
        var gate = new FakeKeychainGate();
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("v");
        var sut = CreateSut(gate, out _);

        Assert.True(sut.Remove(Key));
        Assert.False(gate.Items.ContainsKey(Scoped(Key)));
    }

    /// <summary>
    /// MAUI's macOS SecureStorage uses the machine-global service name "maui_secure_storage" with
    /// no app scoping, so a real login keychain contains other MAUI apps' items under the same
    /// service. A blanket clear would destroy them, so RemoveAll must refuse rather than guess.
    /// </summary>
    [Fact]
    public void RemoveAll_IsRefused_SoOtherAppsItemsCannotBeDestroyed()
    {
        var gate = new FakeKeychainGate();
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("ours");
        gate.Items["Some Other MAUI App Vault"] = Encoding.UTF8.GetBytes("theirs");
        var sut = CreateSut(gate, out _);

        Assert.Throws<NotSupportedException>(() => sut.RemoveAll());

        Assert.True(gate.Items.ContainsKey("Some Other MAUI App Vault"));
        Assert.True(gate.Items.ContainsKey(Scoped(Key)));
    }

    // ------------------------------------------------------------ arg guards

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TryGetAsync_BlankKey_Throws(string key)
    {
        var sut = CreateSut(new FakeKeychainGate(), out _);
        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.TryGetAsync(key, SecureStorageAccess.NoInteraction));
    }

    /// <summary>
    /// Regression: the prompt flag is process-global, so a gate that mutated it and *then* reported
    /// failure must still be restored. Returning early before the <c>finally</c> stranded the whole
    /// process with the SecurityAgent disabled — every later keychain call, including a deliberate
    /// interactive one, would then fail with no way back short of a restart.
    /// </summary>
    [Fact]
    public async Task TryGetAsync_GateReportsFailureAfterMutating_StillRestoresThePromptFlag()
    {
        var gate = new LyingKeychainGate();
        var sut = new KeychainSecureStorageService(gate, new CapturingLogger());

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.InteractionRequired, result.Status);
        Assert.Equal(0, gate.ReadCount);
        Assert.True(
            gate.InteractionAllowed,
            "the process-global prompt flag must be restored even when the gate reported failure");
    }

    /// <summary>A gate that mutates the flag but reports failure — the exact hazard above.</summary>
    private sealed class LyingKeychainGate : IKeychainGate
    {
        public bool IsAvailable => true;
        public bool InteractionAllowed { get; private set; } = true;
        public int ReadCount { get; private set; }

        public bool SetUserInteractionAllowed(bool allowed)
        {
            InteractionAllowed = allowed;   // mutated...
            return false;                   // ...but reported as failed
        }

        public KeychainReadResult Read(string key)
        {
            ReadCount++;
            return KeychainReadResult.Status(KeychainStatus.Success);
        }

        public int Write(string key, byte[] data) => KeychainStatus.Success;

        public int Delete(string key) => KeychainStatus.Success;
    }

    // ------------------------------------------------------------------ fakes

    private sealed class FakeKeychainGate : IKeychainGate
    {
        public Dictionary<string, byte[]> Items { get; } = new(StringComparer.Ordinal);

        public bool IsAvailable { get; set; } = true;
        public bool CanSetInteraction { get; set; } = true;
        public bool InteractionAllowed { get; private set; } = true;
        public bool InteractionAllowedDuringRead { get; private set; } = true;
        public List<bool> InteractionCalls { get; } = new();
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }

        /// <summary>Forces a specific OSStatus out of <see cref="Read"/>.</summary>
        public int? ReadStatusOverride { get; set; }

        public int? WriteStatusOverride { get; set; }

        public Exception? ThrowOnRead { get; set; }

        public bool SetUserInteractionAllowed(bool allowed)
        {
            if (!CanSetInteraction)
                return false;

            InteractionCalls.Add(allowed);
            InteractionAllowed = allowed;
            return true;
        }

        public KeychainReadResult Read(string key)
        {
            ReadCount++;
            InteractionAllowedDuringRead = InteractionAllowed;

            if (ThrowOnRead is not null)
                throw ThrowOnRead;

            if (ReadStatusOverride is int forced)
                return KeychainReadResult.Status(forced);

            return Items.TryGetValue(key, out var data)
                ? new KeychainReadResult(KeychainStatus.Success, data)
                : KeychainReadResult.Status(KeychainStatus.ItemNotFound);
        }

        public int Write(string key, byte[] data)
        {
            WriteCount++;
            if (WriteStatusOverride is int forced)
                return forced;

            Items[key] = data;
            return KeychainStatus.Success;
        }

        public int Delete(string key)
        {
            DeleteCount++;
            return Items.Remove(key) ? KeychainStatus.Success : KeychainStatus.ItemNotFound;
        }
    }

    private sealed class CapturingLogger : ILogger<KeychainSecureStorageService>
    {
        private readonly List<string> _lines = new();

        public string Text => string.Join("\n", _lines);

        public IReadOnlyList<string> Lines => _lines;

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

            // Also capture the raw structured values — a template argument could leak a secret
            // even when the rendered message looks clean.
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                _lines.AddRange(pairs.Select(p => $"{p.Key}={p.Value}"));
        }
    }
}
