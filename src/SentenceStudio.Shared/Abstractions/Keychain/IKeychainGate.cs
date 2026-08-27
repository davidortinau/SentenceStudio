using System;

namespace SentenceStudio.Abstractions.Keychain;

/// <summary>Raw result of one native keychain read.</summary>
/// <param name="OsStatus">Apple <c>OSStatus</c>.</param>
/// <param name="Data">Raw bytes of the stored item, when the platform returned any.</param>
public readonly record struct KeychainReadResult(int OsStatus, byte[]? Data)
{
    public static KeychainReadResult Status(int osStatus) => new(osStatus, null);
}

/// <summary>
/// The whole native surface <see cref="KeychainSecureStorageService"/> needs.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately tiny: everything that can be decided in managed code lives in
/// <see cref="KeychainSecureStorageService"/> and is unit tested with a fake gate, leaving only
/// unavoidable P/Invoke behind this interface.
/// </para>
/// <para>
/// <b>Contract for <see cref="SetUserInteractionAllowed"/></b>: implementations must map onto
/// Apple's <c>SecKeychainSetUserInteractionAllowed</c> (declared in
/// <c>&lt;Security/SecKeychain.h&gt;</c>), which turns the SecurityAgent prompt off for the whole
/// process. With interaction off, a keychain call that would have prompted returns an error
/// immediately instead of blocking. It is process-global state, which is why
/// <see cref="KeychainSecureStorageService"/> serialises access and always restores it.
/// </para>
/// </remarks>
public interface IKeychainGate
{
    /// <summary>True when this gate can actually talk to a keychain on this platform.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Turns the platform's interactive authorisation UI on or off for the current process.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the flag is now in the requested state. A <c>false</c> return must mean the
    /// process-global flag was left <b>unchanged</b> — implementations that fail half-way have to
    /// put it back themselves, because callers rely on <c>false</c> meaning "nothing to undo".
    /// Callers restore unconditionally in a <c>finally</c> regardless.
    /// </returns>
    bool SetUserInteractionAllowed(bool allowed);

    /// <summary>
    /// Reports the process-global interactive-authorisation flag without changing it.
    /// </summary>
    /// <returns>
    /// The current state, or <c>null</c> when the platform cannot report it (the entry point is
    /// missing, or the call failed).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Exists so callers can put the flag back the way they found it rather than assuming it was
    /// on. Assuming <c>true</c> is wrong whenever this app is not the only thing suppressing the
    /// prompt: a caller that suppressed it around a wider operation, or an outer keychain call this
    /// one is nested inside, gets the SecurityAgent switched on underneath it — which is how an
    /// automatic background read ends up raising a modal dialog nobody is there to answer.
    /// </para>
    /// <para>
    /// A default implementation is provided so existing gates keep compiling; they simply report
    /// "unknown" and callers fall back to the historical <c>true</c>.
    /// </para>
    /// </remarks>
    bool? GetUserInteractionAllowed() => null;

    /// <summary>Reads the item's data. Never prompts when interaction has been turned off.</summary>
    KeychainReadResult Read(string key);

    /// <summary>
    /// Stores <paramref name="data"/> under <paramref name="key"/>, replacing any existing item.
    /// </summary>
    /// <remarks>
    /// Implementations must replace rather than merely overwrite, so the resulting item is owned
    /// by the currently running code signature. Otherwise a value written by this build cannot be
    /// read back by this build.
    /// </remarks>
    int Write(string key, byte[] data);

    /// <summary>Deletes the item. Returns the Apple <c>OSStatus</c>.</summary>
    int Delete(string key);
}
