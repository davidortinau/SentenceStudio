using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SentenceStudio.Abstractions;
using SentenceStudio.Abstractions.Keychain;
using Xunit;

namespace SentenceStudio.UnitTests.Abstractions;

/// <summary>
/// Pure mapping of Apple <c>OSStatus</c> values onto <see cref="SecureStorageReadStatus"/>.
/// Every constant is cross-checked against the macOS SDK header <c>SecBase.h</c>.
/// </summary>
public class KeychainStatusMapperTests
{
    [Fact]
    public void Success_MapsToFound() =>
        Assert.Equal(SecureStorageReadStatus.Found, KeychainStatusMapper.MapRead(0));

    [Fact]
    public void ItemNotFound_MapsToNotFound() =>
        Assert.Equal(SecureStorageReadStatus.NotFound, KeychainStatusMapper.MapRead(-25300));

    /// <summary>
    /// -25293 (errSecAuthFailed) is the one that matters most: it is what the legacy macOS
    /// keychain returns for an ACL-gated item once SecKeychainSetUserInteractionAllowed(false)
    /// has suppressed the SecurityAgent prompt. Measured locally against a real login keychain.
    /// </summary>
    [Theory]
    [InlineData(-25293)]  // errSecAuthFailed
    [InlineData(-25308)]  // errSecInteractionNotAllowed
    [InlineData(-25315)]  // errSecInteractionRequired
    [InlineData(-128)]    // errSecUserCanceled
    [InlineData(-34018)]  // errSecMissingEntitlement
    public void UserAuthorisationCodes_MapToInteractionRequired(int osStatus)
    {
        Assert.Equal(SecureStorageReadStatus.InteractionRequired, KeychainStatusMapper.MapRead(osStatus));
        Assert.True(KeychainStatusMapper.IsInteractionRequired(osStatus));
    }

    [Theory]
    [InlineData(-25291)]  // errSecNotAvailable
    [InlineData(-25292)]  // errSecReadOnly
    [InlineData(-50)]     // errSecParam
    [InlineData(12345)]   // unknown
    public void OtherCodes_MapToFailed(int osStatus)
    {
        Assert.Equal(SecureStorageReadStatus.Failed, KeychainStatusMapper.MapRead(osStatus));
        Assert.False(KeychainStatusMapper.IsInteractionRequired(osStatus));
    }

    [Fact]
    public void Constants_MatchAppleSecBaseHeader()
    {
        Assert.Equal(0, KeychainStatus.Success);
        Assert.Equal(-128, KeychainStatus.UserCanceled);
        Assert.Equal(-25244, KeychainStatus.InvalidOwnerEdit);
        Assert.Equal(-25293, KeychainStatus.AuthFailed);
        Assert.Equal(-25299, KeychainStatus.DuplicateItem);
        Assert.Equal(-25300, KeychainStatus.ItemNotFound);
        Assert.Equal(-25308, KeychainStatus.InteractionNotAllowed);
        Assert.Equal(-25315, KeychainStatus.InteractionRequired);
        Assert.Equal(-34018, KeychainStatus.MissingEntitlement);
    }
}

/// <summary>
/// The <see cref="ISecureStorageService.TryGetAsync"/> default implementation exists so the new
/// API is additive: every pre-existing implementation (web, MAUI, test doubles) keeps compiling
/// and keeps behaving exactly as before.
/// </summary>
public class SecureStorageServiceDefaultTryGetTests
{
    [Fact]
    public async Task Default_DelegatesToGetAsync_WhenValuePresent()
    {
        ISecureStorageService store = new LegacyOnlyStore { Value = "hello" };

        var result = await store.TryGetAsync("k", SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.Found, result.Status);
        Assert.Equal("hello", result.Value);
    }

    [Fact]
    public async Task Default_ReportsNotFound_WhenGetAsyncReturnsNull()
    {
        ISecureStorageService store = new LegacyOnlyStore { Value = null };

        var result = await store.TryGetAsync("k", SecureStorageAccess.NoInteraction);

        Assert.Equal(SecureStorageReadStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Default_HonoursCancellation()
    {
        ISecureStorageService store = new LegacyOnlyStore { Value = "hello" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await store.TryGetAsync("k", SecureStorageAccess.NoInteraction, cts.Token);

        Assert.Equal(SecureStorageReadStatus.Cancelled, result.Status);
    }

    /// <summary>An implementation written before TryGetAsync existed — must still compile.</summary>
    private sealed class LegacyOnlyStore : ISecureStorageService
    {
        public string? Value { get; set; }

        public Task<string?> GetAsync(string key) => Task.FromResult(Value);
        public Task SetAsync(string key, string value) { Value = value; return Task.CompletedTask; }
        public bool Remove(string key) { Value = null; return true; }
        public void RemoveAll() => Value = null;
    }
}

/// <summary>
/// Contract test for the one piece that cannot be unit tested: the macOS P/Invoke shim.
///
/// <see cref="KeychainSecureStorageService"/> is only safe if the gate really does suppress the
/// platform prompt. On macOS that means calling Apple's
/// <c>SecKeychainSetUserInteractionAllowed</c>. These tests assert that against the actual source
/// file, so the no-UI flag cannot be silently dropped in a refactor.
/// </summary>
public class MacOSKeychainGateSourceContractTests
{
    private static string ReadGateSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate the repository root from the test output directory.");

        var path = Path.Combine(dir!.FullName, "src", "SentenceStudio.MacOS", "Platform", "MacOSKeychainGate.cs");
        Assert.True(File.Exists(path), $"Expected the macOS keychain gate at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Gate_DeclaresApplesNoUiKeychainEntryPoint()
    {
        var source = ReadGateSource();

        Assert.Contains("SecKeychainSetUserInteractionAllowed", source, StringComparison.Ordinal);
        Assert.Contains("/System/Library/Frameworks/Security.framework/Security", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate must verify the flag actually took effect, so a failed suppression can never be
    /// mistaken for a suppressed prompt.
    /// </summary>
    [Fact]
    public void Gate_ReadsBackTheInteractionFlag()
    {
        Assert.Contains("SecKeychainGetUserInteractionAllowed", ReadGateSource(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes must replace the item so it ends up owned by the running code signature. Using the
    /// legacy delete path is what makes that possible without a prompt — SecItemDelete answers
    /// errSecInvalidOwnerEdit for items owned by another signature.
    /// </summary>
    [Fact]
    public void Gate_UsesLegacyDeletePathForReplacement()
    {
        var source = ReadGateSource();

        Assert.Contains("SecKeychainFindGenericPassword", source, StringComparison.Ordinal);
        Assert.Contains("SecKeychainItemDelete", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every operation that suppresses the prompt must restore it, or the whole process silently
    /// loses the ability to prompt for anything else. The restore has to be unconditional and in a
    /// <c>finally</c> — a guarded <c>if (restoreInteraction)</c> skips it in exactly the case where
    /// the flag may already have been mutated.
    /// </summary>
    /// <remarks>
    /// A restore now puts back the state the operation found (<c>previous ?? true</c>) rather than
    /// flatly enabling the prompt, so that a suppressed outer operation is not handed a re-armed
    /// SecurityAgent by an inner one. Both spellings count — what this test pins is that the number
    /// of restores never falls below the number of suppressions.
    /// </remarks>
    [Fact]
    public void Gate_RestoresInteractionUnconditionallyInFinallyBlocks()
    {
        var source = ReadGateSource();

        var suppressions = Regex.Matches(source, @"SetUserInteractionAllowed\(false\)").Count;
        var restores = Regex.Matches(source, @"SetUserInteractionAllowed\((?:true|previous \?\? true)\)").Count;

        Assert.True(suppressions > 0, "The gate must suppress the prompt somewhere.");
        Assert.True(
            restores >= suppressions,
            $"Every suppression needs a matching restore (suppress={suppressions}, restore={restores}).");
        Assert.Contains("finally", source, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "if (restoreInteraction)",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes and deletes reach the keychain from background token refresh and from sign-out on the
    /// AppKit main thread, so they need the same "do not call if the prompt could not be
    /// suppressed" fail-safe that reads have.
    /// </summary>
    [Fact]
    public void Gate_WriteAndDelete_RefuseWhenThePromptCannotBeSuppressed()
    {
        var source = ReadGateSource();

        var guards = Regex.Matches(
            source,
            @"if\s*\(!suppressed\)\s*\r?\n\s*return\s+KeychainStatus\.InteractionNotAllowed;").Count;

        Assert.True(
            guards >= 2,
            $"Both Write and Delete must bail out when suppression failed (found {guards}).");
    }

    /// <summary>
    /// The gate must keep using MAUI's service name so items written before this type existed are
    /// still found, and nothing is orphaned or duplicated.
    /// </summary>
    [Fact]
    public void Gate_KeepsMauiSecureStorageServiceName()
    {
        Assert.Contains("\"maui_secure_storage\"", ReadGateSource(), StringComparison.Ordinal);
    }

    /// <summary>Tokens must never be written to a log or to Preferences by the native shim.</summary>
    [Fact]
    public void Gate_DoesNotLogOrFallBackToPreferences()
    {
        var source = ReadGateSource();

        Assert.DoesNotContain("Preferences", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.WriteLine", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Debug.WriteLine", source, StringComparison.Ordinal);
    }
}
