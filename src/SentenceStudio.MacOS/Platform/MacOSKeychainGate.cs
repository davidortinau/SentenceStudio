using System;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Security;
using SentenceStudio.Abstractions.Keychain;

namespace SentenceStudio.MacOS.Platform;

/// <summary>
/// macOS AppKit implementation of <see cref="IKeychainGate"/>.
/// </summary>
/// <remarks>
/// <para>
/// Thin native shim only — every decision lives in <see cref="KeychainSecureStorageService"/>,
/// which is unit tested against a fake gate.
/// </para>
///
/// <para><b>Which keychain this talks to.</b> The AppKit head is not sandboxed and the app is
/// ad-hoc signed with no entitlements, so <c>kSecUseDataProtectionKeychain</c> is not usable:
/// the modern keychain answers <c>errSecMissingEntitlement</c> (-34018) for such a binary
/// (verified locally). Everything therefore goes through the <b>legacy file-based keychain</b>,
/// which enforces a per-item ACL and shows a modal SecurityAgent prompt when the calling binary's
/// code signature is not in the item's trusted-application list.</para>
///
/// <para><b>Suppressing the prompt.</b> <c>SecKeychainSetUserInteractionAllowed(Boolean)</c>
/// (<c>&lt;Security/SecKeychain.h&gt;</c>, <c>API_DEPRECATED(macos(10.2, 10.10))</c>,
/// <c>API_UNAVAILABLE(ios, macCatalyst, tvos, watchos)</c>) is the only API that governs the
/// legacy keychain's UI, and Security.framework still exports it. With interaction off, a read
/// that would have prompted returns <c>errSecAuthFailed</c> (-25293) in single-digit
/// milliseconds instead of blocking. It is deprecated but not removed, and there is no
/// replacement for the legacy keychain — the modern replacement is the data-protection keychain,
/// which this binary cannot use.</para>
///
/// <para><b>Why writes replace rather than update.</b> A legacy item's ACL grants
/// <c>ACLAuthorizationEncrypt</c> to <i>any</i> application (verified by dumping
/// <c>SecACLCopyContents</c>/<c>SecACLCopyAuthorizations</c>), so <c>SecItemUpdate</c> succeeds
/// from a foreign signature but leaves the old ACL in place — the value could be written and then
/// never read back. Deleting and re-adding gives the item an ACL owned by the running signature.
/// <c>SecItemDelete</c> refuses with <c>errSecInvalidOwnerEdit</c> (-25244) for foreign items,
/// but the item carries no <c>ACLAuthorizationDelete</c> entry, so the legacy
/// <c>SecKeychainFindGenericPassword</c> + <c>SecKeychainItemDelete</c> pair succeeds without any
/// prompt. That is the sequence used here.</para>
///
/// <para><b>Service name.</b> Matches MAUI's macOS SecureStorage
/// (<c>"maui_secure_storage"</c>) so items written before this type existed remain readable and
/// nothing is orphaned.</para>
/// </remarks>
public sealed class MacOSKeychainGate : IKeychainGate
{
    /// <summary>
    /// Same constant as
    /// <c>Microsoft.Maui.Platforms.MacOS.Essentials.SecureStorageImplementation.ServiceName</c>,
    /// so this gate reads and writes exactly the items MAUI's SecureStorage would.
    /// </summary>
    internal const string ServiceName = "maui_secure_storage";

    private const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Security";

    // ---- Legacy Security.framework entry points that Microsoft.macOS does not bind ----

    /// <summary>
    /// Turns the SecurityAgent prompt on/off for this process. THE no-UI flag: automatic reads
    /// must call this with <c>false</c> before touching the keychain.
    /// </summary>
    [DllImport(SecurityFramework)]
    private static extern int SecKeychainSetUserInteractionAllowed(
        [MarshalAs(UnmanagedType.I1)] bool state);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainGetUserInteractionAllowed(
        [MarshalAs(UnmanagedType.I1)] out bool state);

    /// <summary>
    /// Attribute-only lookup (both password out-params are <see cref="IntPtr.Zero"/>), so it never
    /// triggers the item's ACL data-read authorisation.
    /// </summary>
    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        IntPtr passwordLength,
        IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    /// <inheritdoc/>
    public bool IsAvailable => OperatingSystem.IsMacOS();

    /// <inheritdoc/>
    public bool? GetUserInteractionAllowed()
    {
        try
        {
            return SecKeychainGetUserInteractionAllowed(out var current) == KeychainStatus.Success
                ? current
                : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public bool SetUserInteractionAllowed(bool allowed)
    {
        try
        {
            if (SecKeychainSetUserInteractionAllowed(allowed) != KeychainStatus.Success)
                return false;

            // The flag is now changed. Verify it actually took effect — the caller must not make a
            // call that could block on a prompt nobody can answer. If it did not take effect, put
            // it back, so a `false` return always means "the process-global flag was left as it
            // was" and can never strand the process with the SecurityAgent disabled.
            try
            {
                if (SecKeychainGetUserInteractionAllowed(out var current) == KeychainStatus.Success
                    && current != allowed)
                {
                    SecKeychainSetUserInteractionAllowed(!allowed);
                    return false;
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Cannot verify on this OS build; the setter reported success, so trust it.
            }

            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public KeychainReadResult Read(string key)
    {
        using var query = new SecRecord(SecKind.GenericPassword)
        {
            Account = key,
            Service = ServiceName,
        };

        using var match = SecKeyChain.QueryAsRecord(query, out var status);

        if (status != SecStatusCode.Success)
            return KeychainReadResult.Status((int)status);

        var data = match?.ValueData;
        if (data is null)
        {
            // NOT ItemNotFound. The query succeeded — the item exists — it just came back with no
            // payload. Reporting "not found" would let a caller treat a present-but-empty
            // credential as proof that nothing is stored, which is how a sign-out gets reported as
            // verified when the item is still there. Surface it as a successful read of zero bytes
            // and let the decode step classify it as malformed.
            return new KeychainReadResult(KeychainStatus.Success, Array.Empty<byte>());
        }

        return new KeychainReadResult(KeychainStatus.Success, data.ToArray());
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Add → (duplicate) → legacy delete → add. Runs with interaction suppressed so it can never
    /// block, and leaves the item owned by the running code signature.
    /// </remarks>
    public int Write(string key, byte[] data)
    {
        var previous = GetUserInteractionAllowed();
        var suppressed = SetUserInteractionAllowed(false);
        try
        {
            // Same fail-safe as reads: never make a keychain call that could block on a prompt
            // nobody can answer. StoreTokens runs from a background refresh, and Delete runs from
            // sign-out on the AppKit main thread.
            if (!suppressed)
                return KeychainStatus.InteractionNotAllowed;

            var status = Add(key, data);
            if (status != (int)SecStatusCode.DuplicateItem)
                return status;

            // An item already exists — most likely written by a previous build with a different
            // ad-hoc signature. Replace it so the new item's ACL names the running binary.
            var deleteStatus = DeleteLegacy(key);
            if (deleteStatus != KeychainStatus.Success)
                return deleteStatus;

            return Add(key, data);
        }
        finally
        {
            // Unconditional: SetUserInteractionAllowed only reports false when it left the flag
            // untouched, so restoring here can never clobber someone else's suppression, and it
            // guarantees the process is never stranded with the SecurityAgent disabled.
            //
            // Restore what was actually there, not a flat `true`: this can be reached from inside
            // an operation that had already suppressed the prompt on purpose, and switching the
            // SecurityAgent back on underneath it re-arms the modal dialog it disabled. `true` is
            // the fallback only when the platform will not report the prior state.
            SetUserInteractionAllowed(previous ?? true);
        }
    }

    private static int Add(string key, byte[] data)
    {
        using var record = new SecRecord(SecKind.GenericPassword)
        {
            Account = key,
            Service = ServiceName,
            Label = key,
            // Same protection class MAUI's macOS SecureStorage uses — not weakened.
            Accessible = SecAccessible.AfterFirstUnlock,
            ValueData = NSData.FromArray(data),
        };

        return (int)SecKeyChain.Add(record);
    }

    /// <inheritdoc/>
    public int Delete(string key)
    {
        var previous = GetUserInteractionAllowed();
        var suppressed = SetUserInteractionAllowed(false);
        try
        {
            if (!suppressed)
                return KeychainStatus.InteractionNotAllowed;

            return DeleteLegacy(key);
        }
        finally
        {
            // Prior state, not a flat `true` — see the note in Write.
            SetUserInteractionAllowed(previous ?? true);
        }
    }

    /// <summary>
    /// Deletes via <c>SecKeychainFindGenericPassword</c> (attributes only) +
    /// <c>SecKeychainItemDelete</c>. Unlike <c>SecItemDelete</c>, this succeeds for items owned by
    /// another code signature and never prompts, because legacy generic-password items carry no
    /// <c>ACLAuthorizationDelete</c> entry.
    /// </summary>
    private static int DeleteLegacy(string key)
    {
        var service = Encoding.UTF8.GetBytes(ServiceName);
        var account = Encoding.UTF8.GetBytes(key);

        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)service.Length, service,
            (uint)account.Length, account,
            IntPtr.Zero, IntPtr.Zero,
            out var itemRef);

        if (status != KeychainStatus.Success)
            return status;

        if (itemRef == IntPtr.Zero)
            return KeychainStatus.ItemNotFound;

        try
        {
            return SecKeychainItemDelete(itemRef);
        }
        finally
        {
            CFRelease(itemRef);
        }
    }

}
