using System;

namespace SentenceStudio.Abstractions.Keychain;

/// <summary>
/// Apple <c>OSStatus</c> values from <c>&lt;Security/SecBase.h&gt;</c>, mirrored here as plain
/// <see cref="int"/>s so the decision logic in <see cref="KeychainStatusMapper"/> can be unit
/// tested on non-Apple target frameworks.
/// </summary>
/// <remarks>
/// Values verified against the macOS SDK header
/// <c>MacOSX.sdk/System/Library/Frameworks/Security.framework/Headers/SecBase.h</c>.
/// </remarks>
public static class KeychainStatus
{
    /// <summary>errSecSuccess — no error.</summary>
    public const int Success = 0;

    /// <summary>errSecUserCanceled — the user cancelled the operation.</summary>
    public const int UserCanceled = -128;

    /// <summary>
    /// errSecAuthFailed — "The user name or passphrase you entered is not correct."
    /// <para>
    /// This is what the <b>legacy file-based macOS keychain</b> returns when an item's ACL would
    /// require a SecurityAgent prompt but user interaction has been switched off with
    /// <c>SecKeychainSetUserInteractionAllowed(false)</c>. Measured on macOS with an ad-hoc
    /// signed binary reading another signature's item: returns in 1–14 ms, no prompt shown.
    /// </para>
    /// </summary>
    public const int AuthFailed = -25293;

    /// <summary>errSecDuplicateItem — the item already exists in the keychain.</summary>
    public const int DuplicateItem = -25299;

    /// <summary>errSecItemNotFound — no such item.</summary>
    public const int ItemNotFound = -25300;

    /// <summary>errSecInteractionNotAllowed — "User interaction is not allowed."</summary>
    public const int InteractionNotAllowed = -25308;

    /// <summary>errSecInteractionRequired — the operation requires user interaction.</summary>
    public const int InteractionRequired = -25315;

    /// <summary>
    /// errSecInvalidOwnerEdit — "Invalid attempt to change the owner of this item."
    /// Returned by <c>SecItemDelete</c> for a legacy keychain item whose ACL names a different
    /// code signature. The legacy <c>SecKeychainItemDelete</c> path succeeds where this fails.
    /// </summary>
    public const int InvalidOwnerEdit = -25244;

    /// <summary>
    /// errSecMissingEntitlement — the data-protection keychain requires an entitlement that an
    /// ad-hoc signed, entitlement-free binary cannot present.
    /// </summary>
    public const int MissingEntitlement = -34018;
}

/// <summary>
/// Maps a raw Apple <c>OSStatus</c> onto a <see cref="SecureStorageReadStatus"/>.
/// Pure function, no platform dependencies, so it is directly unit testable.
/// </summary>
public static class KeychainStatusMapper
{
    /// <summary>Classifies the result of a keychain read.</summary>
    /// <param name="osStatus">Raw <c>OSStatus</c> from <c>SecItemCopyMatching</c>.</param>
    public static SecureStorageReadStatus MapRead(int osStatus) => osStatus switch
    {
        KeychainStatus.Success => SecureStorageReadStatus.Found,

        KeychainStatus.ItemNotFound => SecureStorageReadStatus.NotFound,

        // All of these mean "the platform would have to ask the user". Which one you get depends
        // on the keychain backend that serviced the request (legacy file-based vs.
        // data-protection) and on whether the keychain itself is locked, so they are deliberately
        // collapsed into a single caller-visible outcome.
        KeychainStatus.AuthFailed
            or KeychainStatus.InteractionNotAllowed
            or KeychainStatus.InteractionRequired
            or KeychainStatus.UserCanceled
            or KeychainStatus.MissingEntitlement => SecureStorageReadStatus.InteractionRequired,

        _ => SecureStorageReadStatus.Failed,
    };

    /// <summary>
    /// True when the status means the platform refused because it wanted user authorisation —
    /// i.e. the stored item is intact and must be left alone.
    /// </summary>
    public static bool IsInteractionRequired(int osStatus) =>
        MapRead(osStatus) == SecureStorageReadStatus.InteractionRequired;
}
