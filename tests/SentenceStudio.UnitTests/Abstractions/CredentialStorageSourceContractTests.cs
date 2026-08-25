using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace SentenceStudio.UnitTests.Abstractions;

/// <summary>
/// Contract tests for the credential-handling code that lives in <c>SentenceStudio.AppLib</c>.
///
/// <para>AppLib targets <c>net11.0</c> with <c>UseMaui</c>, so this <c>net10.0</c> test project
/// cannot reference it (<c>NU1201</c>) and its types cannot be instantiated here. The behaviour
/// they encode is security-critical and previously regressed silently, so it is pinned against the
/// source text instead — the same technique <see cref="MacOSKeychainGateSourceContractTests"/> uses
/// for the macOS P/Invoke shim. The logic that <i>can</i> be executed was moved into
/// <c>SentenceStudio.Shared</c> and is covered properly by <see cref="AuthTokenStoreTests"/>.</para>
/// </summary>
public class CredentialStorageSourceContractTests
{
    private static string RepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate the repository root from the test output directory.");

        var path = Path.Combine(new[] { dir!.FullName }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Expected a source file at {path}");
        return File.ReadAllText(path);
    }

    private static string SecureStorageSource() => RepoFile(
        "src", "SentenceStudio.AppLib", "Abstractions", "MauiSecureStorageService.cs");

    private static string AuthServiceSource() => RepoFile(
        "src", "SentenceStudio.AppLib", "Services", "IdentityAuthService.cs");

    private static string StateProviderSource() => RepoFile(
        "src", "SentenceStudio.AppLib", "Services", "MauiAuthenticationStateProvider.cs");

    // ------------------------------------------ no plaintext credential fallback

    /// <summary>
    /// THE regression test for the plaintext fallback. <c>MauiSecureStorageService</c> stores the
    /// JWT, the refresh token and the expiry. It used to catch any <c>SecureStorage</c> exception,
    /// latch a process-wide flag and route every subsequent read and write to <c>Preferences</c> —
    /// putting long-lived bearer credentials into <c>NSUserDefaults</c> / <c>SharedPreferences</c>
    /// unencrypted, and keeping them there for the rest of the process after a single transient
    /// failure.
    /// </summary>
    [Fact]
    public void SecureStorage_HasNoPreferencesFallbackLatch()
    {
        var source = SecureStorageSource();

        Assert.DoesNotContain("_usePreferencesFallback", source, StringComparison.Ordinal);

        // The residue purge is allowed to name the old prefix; nothing may still *route* through it.
        Assert.DoesNotContain("Preferences.Default.Get<string?>(", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// No write to <c>Preferences</c> at all, by any spelling. The only <c>Preferences</c> call this
    /// type may make is the bounded purge of residue the old fallback left behind — a removal, never
    /// a write and never a read.
    /// </summary>
    [Fact]
    public void SecureStorage_NeverWritesOrReadsCredentialsThroughPreferences()
    {
        var source = SecureStorageSource();

        Assert.DoesNotContain("Preferences.Default.Set", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Preferences.Default.Get", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Not gated behind <c>#if DEBUG</c> either. A debug-only fallback forbidden from storing access
    /// or refresh tokens would have nothing left to store, since those keys are this type's entire
    /// purpose — it would be dead code that still reads as a sanctioned escape hatch.
    /// </summary>
    [Fact]
    public void SecureStorage_HasNoDebugGatedFallback()
    {
        Assert.DoesNotContain("#if DEBUG", SecureStorageSource(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Fail closed on write: a caller must not be able to believe a session was persisted when the
    /// keystore refused it. Silent degradation is what let a failed write reach
    /// <c>StoreTokens</c> unnoticed.
    /// </summary>
    [Fact]
    public void SecureStorage_FailedWriteThrows()
    {
        Assert.Contains(
            "throw new SecureStorageWriteException",
            SecureStorageSource(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Fail closed on read: a platform error must report as a failure, not as "nothing stored".
    /// Sign-out proves a credential is gone by reading it back, so an error mapped to "not found"
    /// would let it declare success over a credential still on disk.
    /// </summary>
    [Fact]
    public void SecureStorage_FailedReadReportsFailureNotAbsence()
    {
        var source = SecureStorageSource();

        Assert.Contains("SecureStorageReadResult.Failed", source, StringComparison.Ordinal);
        Assert.Contains("public async Task<SecureStorageReadResult> TryGetAsync", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removing the fallback stops new plaintext credentials appearing; the ones already written on
    /// existing installs stay readable until something deletes them. The purge is bounded to the
    /// three owned keys — no enumeration, no wildcard, no <c>Preferences.Clear()</c>.
    /// </summary>
    [Fact]
    public void SecureStorage_PurgesPlaintextResidueWithoutTouchingOtherPreferences()
    {
        var source = SecureStorageSource();

        Assert.Contains("PurgeLegacyPlaintextCredentials", source, StringComparison.Ordinal);
        Assert.Contains("AuthTokenStore.OwnedKeys", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Preferences.Default.Clear", source, StringComparison.Ordinal);
    }

    // -------------------------------------------------- atomic token persistence

    /// <summary>
    /// The three keys are written through <see cref="SentenceStudio.Abstractions.AuthTokenStore"/>
    /// as a unit. Three loose <c>SetAsync</c> calls are what allowed a failure on the second one to
    /// leave account B's access token beside account A's refresh token.
    /// </summary>
    [Fact]
    public void AuthService_PersistsTheTripleThroughTheAtomicStore()
    {
        var source = AuthServiceSource();

        Assert.Contains("_tokenStore.PersistAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_secureStorage.SetAsync", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The in-memory cache is populated only after persistence succeeds. Populating it first left
    /// the process holding an access token for an account whose refresh token was never stored — a
    /// session that works until the app closes and then vanishes.
    /// </summary>
    [Fact]
    public void AuthService_CachesTheTokenOnlyAfterPersistenceSucceeds()
    {
        var source = AuthServiceSource();

        var persist = source.IndexOf("_tokenStore.PersistAsync", StringComparison.Ordinal);
        var cache = source.IndexOf("_cachedToken = response.Token", StringComparison.Ordinal);

        Assert.True(persist >= 0 && cache >= 0);
        Assert.True(persist < cache, "the credential triple must be stored before the token is cached");
    }

    // ------------------------------------------------------- truthful sign-out

    /// <summary>
    /// Sign-out used to fire three <c>Remove</c> calls, discard all three return values, and log
    /// "Signed out, tokens and profile cleared" unconditionally. <c>Remove</c> returns
    /// <c>false</c> both for "wasn't there" and for "couldn't remove it", so that line was evidence
    /// of nothing.
    /// </summary>
    [Fact]
    public void AuthService_SignOutDoesNotFireAndForgetRemovals()
    {
        var source = AuthServiceSource();

        Assert.DoesNotContain("_secureStorage.Remove(JwtKey)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_secureStorage.Remove(RefreshKey)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_secureStorage.Remove(ExpiresKey)", source, StringComparison.Ordinal);
        Assert.Contains("_tokenStore.ClearAsync", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// In-memory auth is dropped first, before anything that can fail. Whatever storage does next,
    /// this process stops being authenticated.
    /// </summary>
    [Fact]
    public void AuthService_SignOutClearsInMemoryStateBeforeTouchingStorage()
    {
        var source = AuthServiceSource();

        var signOut = source.IndexOf("public async Task SignOutAsync()", StringComparison.Ordinal);
        Assert.True(signOut >= 0, "SignOutAsync must exist");

        var clearMemory = source.IndexOf("ClearInMemoryAuth();", signOut, StringComparison.Ordinal);
        var clearStorage = source.IndexOf("_tokenStore.ClearAsync", signOut, StringComparison.Ordinal);

        Assert.True(clearMemory >= 0 && clearStorage >= 0);
        Assert.True(
            clearMemory < clearStorage,
            "the in-memory session must be dropped before any step that can fail");
    }

    /// <summary>
    /// Every silent-restore path consults the pending-cleanup guard, so a sign-out that could not
    /// prove removal cannot be undone by the next cold start quietly refreshing the token that
    /// survived.
    /// </summary>
    [Theory]
    [InlineData("public async Task<bool> HasStoredSessionAsync()")]
    [InlineData("public async Task<AuthResult?> SignInAsync()")]
    [InlineData("public async Task<string?> GetAccessTokenAsync(string[] scopes)")]
    public void AuthService_SilentRestorePathsAreGuarded(string signature)
    {
        var source = AuthServiceSource();

        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find {signature}");

        var guard = source.IndexOf("IsSilentRestoreBlockedAsync", start, StringComparison.Ordinal);
        Assert.True(guard >= 0, $"{signature} must consult the pending-cleanup guard");

        // The guard has to be near the top of the method, not after the restore has happened.
        var nextMethod = source.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        Assert.True(
            nextMethod < 0 || guard < nextMethod,
            $"the guard must be inside {signature}");
    }

    /// <summary>
    /// Explicit credential sign-in deliberately is <b>not</b> guarded: a successful triple write
    /// puts storage back into a known state for a known account, and that is exactly how a learner
    /// recovers a device stuck behind a failed cleanup.
    /// </summary>
    [Fact]
    public void AuthService_CredentialSignInIsNotBlockedByThePendingCleanupGuard()
    {
        var source = AuthServiceSource();

        var start = source.IndexOf(
            "public async Task<AuthResult?> SignInAsync(string email, string password)",
            StringComparison.Ordinal);
        Assert.True(start >= 0);

        var end = source.IndexOf("public async Task<AuthResult?> RegisterAsync", start, StringComparison.Ordinal);
        Assert.True(end > start);

        var body = source[start..end];
        Assert.DoesNotContain("IsSilentRestoreBlockedAsync", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Registration must not swallow a persistence failure into its "check your email" null return
    /// — that would send the learner looking for a mail that is never coming, for an account whose
    /// credentials were rolled back.
    /// </summary>
    [Fact]
    public void AuthService_RegistrationDoesNotSwallowPersistenceFailures()
    {
        var source = AuthServiceSource();

        var start = source.IndexOf("public async Task<AuthResult?> RegisterAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);

        var end = source.IndexOf("public async Task SignOutAsync()", start, StringComparison.Ordinal);
        Assert.True(end > start);

        var body = source[start..end];

        Assert.Contains("catch (AuthTokenPersistenceException)", body, StringComparison.Ordinal);

        // No bare `catch { }` — the construct that used to absorb it.
        Assert.DoesNotMatch(new Regex(@"catch\s*\r?\n?\s*\{", RegexOptions.None), body);
    }

    /// <summary>
    /// Server-rejected credentials are removed as a set. Removing only the refresh token left an
    /// access token and an expiry belonging to a session the server had already repudiated, which
    /// the next launch would read as a restorable session.
    /// </summary>
    [Fact]
    public void AuthService_RejectedCredentialsAreClearedAsAWholeTriple()
    {
        var source = AuthServiceSource();

        Assert.DoesNotContain("_secureStorage.Remove(RefreshKey)", source, StringComparison.Ordinal);
        Assert.Contains("_tokenStore.TryClearAsync", source, StringComparison.Ordinal);
    }

    // -------------------------------------------- anonymous state on sign-out

    /// <summary>
    /// The anonymous principal is published in a <c>finally</c>. A logout that throws before
    /// <c>NotifyAuthenticationStateChanged</c> strands the learner on an authenticated-looking
    /// screen with no way forward.
    /// </summary>
    [Fact]
    public void StateProvider_PublishesAnonymousStateEvenWhenCleanupFails()
    {
        var source = StateProviderSource();

        var logout = source.IndexOf("public async Task<SignOutOutcome> LogOutAsync()", StringComparison.Ordinal);
        Assert.True(logout >= 0, "LogOutAsync must return the sign-out outcome");

        var finallyBlock = source.IndexOf("finally", logout, StringComparison.Ordinal);
        var notify = source.IndexOf("NotifyAuthenticationStateChanged", logout, StringComparison.Ordinal);

        Assert.True(finallyBlock >= 0, "the publish must be in a finally");
        Assert.True(notify > finallyBlock, "the anonymous principal must be published from the finally block");
    }

    /// <summary>
    /// Not ignored: the failure is caught by its specific type and logged at Error with key names,
    /// and the caller gets a result that cannot be mistaken for success.
    /// </summary>
    [Fact]
    public void StateProvider_SurfacesCleanupFailureRatherThanSwallowingIt()
    {
        var source = StateProviderSource();

        Assert.Contains("catch (AuthTokenCleanupException ex)", source, StringComparison.Ordinal);
        Assert.Contains("SignOutOutcome.Failed(ex.AffectedKeys)", source, StringComparison.Ordinal);
        Assert.Contains("_logger.LogError(", source, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- privacy

    /// <summary>
    /// No credential-handling source may log a token, a slice of one, or a length. A length narrows
    /// a brute-force search and distinguishes an access token from a refresh token.
    /// </summary>
    [Theory]
    [InlineData("MauiSecureStorageService.cs")]
    [InlineData("IdentityAuthService.cs")]
    public void CredentialSources_NeverLogTokenValuesOrLengths(string fileName)
    {
        var source = fileName == "MauiSecureStorageService.cs"
            ? SecureStorageSource()
            : AuthServiceSource();

        // Structured-logging arguments that would render a token or a measurement of one.
        foreach (var forbidden in new[]
                 {
                     "response.Token)",
                     "response.RefreshToken)",
                     "_cachedToken)",
                     ".Token.Length",
                     ".RefreshToken.Length",
                     "value.Length",
                 })
        {
            var pattern = new Regex(
                @"Log(?:Trace|Debug|Information|Warning|Error|Critical)\([^;]*" + Regex.Escape(forbidden),
                RegexOptions.Singleline);

            Assert.False(
                pattern.IsMatch(source),
                $"{fileName} must never pass {forbidden} to a logger.");
        }
    }
}
