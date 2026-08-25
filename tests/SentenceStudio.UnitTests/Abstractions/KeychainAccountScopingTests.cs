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
/// Covers the two macOS keychain hardening changes.
///
/// <para><b>Account namespacing.</b> MAUI's macOS SecureStorage stores generic passwords under the
/// service name <c>maui_secure_storage</c> with no app scoping, so on a login keychain that service
/// is shared by every MAUI app on the machine and the account name is the only separator. An
/// account called <c>auth_refresh</c> in that namespace is a collision waiting to happen. Items are
/// therefore written under an app-scoped account, and pre-existing items are migrated across —
/// read-old, write-new, verify, delete-old, in that order, one key at a time, and never by sweeping
/// the service.</para>
///
/// <para><b>Prompt-flag restoration.</b> The process-global interactive-authorisation flag used to
/// be restored to a flat <c>true</c>. That re-arms the SecurityAgent underneath any caller that had
/// deliberately suppressed it, which on this head means an automatic background read can raise a
/// modal dialog nobody is present to answer. The prior state is captured and put back instead.
/// </para>
/// </summary>
public class KeychainAccountScopingTests
{
    private const string Key = "auth_refresh";

    private static string Scoped(string key) => KeychainSecureStorageService.AccountNamespace + key;

    private static KeychainSecureStorageService CreateSut(ScopingGate gate, out CapturingLogger log)
    {
        log = new CapturingLogger();
        return new KeychainSecureStorageService(gate, log);
    }

    // ------------------------------------------------------- prompt-flag state

    /// <summary>
    /// THE regression test for requirement 4. The prompt was already suppressed by an outer
    /// operation; restoring it to <c>true</c> hands that operation a re-armed SecurityAgent.
    /// </summary>
    [Fact]
    public async Task Read_PromptWasAlreadySuppressed_RestoresSuppressedNotAllowed()
    {
        var gate = new ScopingGate { InteractionAllowed = false };
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("v");
        var sut = CreateSut(gate, out _);

        await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.False(
            gate.InteractionAllowed,
            "an outer caller's suppression must survive an inner read");
    }

    [Fact]
    public async Task Read_PromptWasAllowed_RestoresAllowed()
    {
        var gate = new ScopingGate { InteractionAllowed = true };
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("v");
        var sut = CreateSut(gate, out _);

        await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.True(gate.InteractionAllowed);
    }

    /// <summary>
    /// A gate that cannot report the prior state falls back to allowed — the historical behaviour,
    /// and the fail-safe direction: a process left with the SecurityAgent disabled breaks every
    /// later keychain call, including a deliberately interactive one.
    /// </summary>
    [Fact]
    public async Task Read_GateCannotReportPriorState_RestoresAllowed()
    {
        var gate = new ScopingGate { InteractionAllowed = false, CanReportInteraction = false };
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("v");
        var sut = CreateSut(gate, out _);

        await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.True(gate.InteractionAllowed);
    }

    [Fact]
    public async Task Read_GateGetterThrows_RestoresAllowedAndStillReads()
    {
        var gate = new ScopingGate { ThrowOnGetInteraction = true };
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("v");
        var sut = CreateSut(gate, out _);

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal("v", result.Value);
        Assert.True(gate.InteractionAllowed);
    }

    [Fact]
    public async Task Read_GateRestoreThrows_DoesNotFailTheRead()
    {
        var gate = new ScopingGate { ThrowOnRestore = true };
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("v");
        var sut = CreateSut(gate, out _);

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal("v", result.Value);
    }

    /// <summary>
    /// The flag is process-global, so suppression windows must never overlap. Serialisation is what
    /// makes "restore the prior state" meaningful — without it, two concurrent reads would each
    /// capture the other's suppression as the state to restore.
    /// </summary>
    [Fact]
    public async Task ConcurrentReads_NeverOverlapInsideTheSuppressionWindow()
    {
        var gate = new ScopingGate { ReadDelay = TimeSpan.FromMilliseconds(5) };
        for (var i = 0; i < 8; i++)
            gate.Items[Scoped($"k{i}")] = Encoding.UTF8.GetBytes("v");

        var sut = CreateSut(gate, out _);

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => sut.TryGetAsync($"k{i}", SecureStorageAccess.NoInteraction)));

        Assert.Equal(1, gate.MaxConcurrentReads);
    }

    // ------------------------------------------------------------- namespacing

    [Fact]
    public async Task Read_ScopedItemPresent_DoesNotProbeTheSharedAccount()
    {
        var gate = new ScopingGate();
        gate.Items[Scoped(Key)] = Encoding.UTF8.GetBytes("scoped-value");
        var sut = CreateSut(gate, out _);

        var result = await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.Equal("scoped-value", result.Value);
        Assert.DoesNotContain(Key, gate.ReadAccounts);
    }

    [Fact]
    public async Task Write_UsesTheScopedAccountAndNeverTheSharedOne()
    {
        var gate = new ScopingGate();
        var sut = CreateSut(gate, out _);

        await sut.SetAsync(Key, "fresh-token");

        Assert.Equal("fresh-token", Encoding.UTF8.GetString(gate.Items[Scoped(Key)]));
        Assert.False(gate.Items.ContainsKey(Key));
        Assert.Equal(new[] { Scoped(Key) }, gate.WriteAccounts);
    }

    [Fact]
    public async Task Write_LeavesOtherApplicationsAccountsAlone()
    {
        var gate = new ScopingGate();
        gate.Items["Some Other MAUI App Vault"] = Encoding.UTF8.GetBytes("theirs");
        gate.Items["auth_jwt"] = Encoding.UTF8.GetBytes("a-different-key-of-ours");
        var sut = CreateSut(gate, out _);

        await sut.SetAsync(Key, "fresh-token");

        Assert.True(gate.Items.ContainsKey("Some Other MAUI App Vault"));
        Assert.True(gate.Items.ContainsKey("auth_jwt"));
        Assert.DoesNotContain("Some Other MAUI App Vault", gate.DeleteAccounts);
    }

    [Fact]
    public void RemoveAll_IsStillRefused()
    {
        var gate = new ScopingGate();
        gate.Items["Some Other MAUI App Vault"] = Encoding.UTF8.GetBytes("theirs");
        var sut = CreateSut(gate, out _);

        Assert.Throws<NotSupportedException>(() => sut.RemoveAll());
        Assert.True(gate.Items.ContainsKey("Some Other MAUI App Vault"));
    }

    // --------------------------------------------------------------- migration

    [Fact]
    public async Task Migration_NeverLogsTheStoredValue()
    {
        var gate = new ScopingGate();
        gate.Items[Key] = Encoding.UTF8.GetBytes("REFRESH-TOKEN-SECRET-abcdefghij");
        var sut = CreateSut(gate, out var log);

        await sut.TryGetAsync(Key, SecureStorageAccess.NoInteraction);

        Assert.DoesNotContain("SECRET", log.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("length", log.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ fakes

    /// <summary>
    /// Gate keyed by <b>account name</b>, so a test can tell an app-scoped item apart from one in
    /// the shared namespace. Also models the process-global prompt flag.
    /// </summary>
    private sealed class ScopingGate : IKeychainGate
    {
        private int _concurrentReads;

        public Dictionary<string, byte[]> Items { get; } = new(StringComparer.Ordinal);

        public bool IsAvailable => true;

        public bool InteractionAllowed { get; set; } = true;

        public bool CanReportInteraction { get; set; } = true;

        public bool ThrowOnGetInteraction { get; set; }

        /// <summary>Throws only when asked to switch the prompt back on.</summary>
        public bool ThrowOnRestore { get; set; }

        public int? WriteStatusOverride { get; set; }

        public int? DeleteStatusOverride { get; set; }

        public Dictionary<string, int> ReadStatusOverride { get; } = new(StringComparer.Ordinal);

        /// <summary>Makes a written item read back as something else.</summary>
        public bool CorruptWrittenValue { get; set; }

        public TimeSpan ReadDelay { get; set; }

        public List<string> Journal { get; } = new();
        public List<string> ReadAccounts { get; } = new();
        public List<string> WriteAccounts { get; } = new();
        public List<string> DeleteAccounts { get; } = new();

        public int MaxConcurrentReads { get; private set; }

        public bool? GetUserInteractionAllowed()
        {
            if (ThrowOnGetInteraction)
                throw new InvalidOperationException("cannot read the flag");

            return CanReportInteraction ? InteractionAllowed : null;
        }

        public bool SetUserInteractionAllowed(bool allowed)
        {
            if (ThrowOnRestore && allowed)
                throw new InvalidOperationException("cannot restore the flag");

            InteractionAllowed = allowed;
            return true;
        }

        public KeychainReadResult Read(string account)
        {
            var depth = Interlocked.Increment(ref _concurrentReads);
            try
            {
                if (depth > MaxConcurrentReads)
                    MaxConcurrentReads = depth;

                ReadAccounts.Add(account);
                Journal.Add($"read:{account}");

                if (ReadDelay > TimeSpan.Zero)
                    Thread.Sleep(ReadDelay);

                if (ReadStatusOverride.TryGetValue(account, out var forced))
                    return KeychainReadResult.Status(forced);

                return Items.TryGetValue(account, out var data)
                    ? new KeychainReadResult(KeychainStatus.Success, data)
                    : KeychainReadResult.Status(KeychainStatus.ItemNotFound);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentReads);
            }
        }

        public int Write(string account, byte[] data)
        {
            WriteAccounts.Add(account);
            Journal.Add($"write:{account}");

            if (WriteStatusOverride is int forced)
                return forced;

            Items[account] = CorruptWrittenValue
                ? Encoding.UTF8.GetBytes("something-else-entirely")
                : data;

            return KeychainStatus.Success;
        }

        public int Delete(string account)
        {
            DeleteAccounts.Add(account);
            Journal.Add($"delete:{account}");

            if (DeleteStatusOverride is int forced)
                return forced;

            return Items.Remove(account) ? KeychainStatus.Success : KeychainStatus.ItemNotFound;
        }
    }

    private sealed class CapturingLogger : ILogger<KeychainSecureStorageService>
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

            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                _lines.AddRange(pairs.Select(p => $"{p.Key}={p.Value}"));
        }
    }
}
