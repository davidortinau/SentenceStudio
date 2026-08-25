using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// What a piece of protected content is bound to.
/// </summary>
/// <remarks>
/// <para>
/// Every field here becomes part of the protection purpose chain, which is the
/// authenticated-additional-data equivalent for ASP.NET Data Protection: ciphertext produced
/// under one context cannot be read under another. That is what stops a row copied between
/// learners, between records, between content kinds, or across envelope versions from
/// decrypting.
/// </para>
/// <para>
/// <b>TenantId is deliberately excluded.</b> It is nullable, not an authority value in v1, and
/// may be backfilled later. Binding ciphertext to a value that is expected to change would turn
/// a routine backfill into unrecoverable content loss.
/// </para>
/// </remarks>
/// <param name="Owner">The trusted owner. Only <see cref="CoachOwner.UserProfileId"/> is bound.</param>
/// <param name="Kind">What the payload is.</param>
/// <param name="RecordId">The identity of the row the payload belongs to.</param>
/// <param name="Version">The protector envelope version.</param>
public readonly record struct CoachProtectionContext(
    CoachOwner Owner,
    CoachProtectedContentKind Kind,
    string RecordId,
    int Version);

/// <summary>
/// Protects and unprotects durable coach content, binding every payload to its owner, kind,
/// record, and envelope version.
/// </summary>
/// <remarks>
/// Implementations must never log plaintext or ciphertext. An unreadable payload is reported as
/// a failure so the caller can surface a safe recovery state; it is never silently replaced with
/// empty content.
/// </remarks>
public interface ICoachContentProtector
{
    /// <summary>The envelope version new writes are stamped with.</summary>
    int CurrentVersion { get; }

    /// <summary>Encrypts content for exactly one owner, kind, record, and version.</summary>
    string Protect(CoachProtectionContext context, string plaintext);

    /// <summary>
    /// Decrypts content written under the same context. Returns false for missing, tampered,
    /// swapped, or unreadable payloads so the caller fails closed.
    /// </summary>
    bool TryUnprotect(CoachProtectionContext context, string? protectedPayload, out string? plaintext);
}

/// <summary>
/// <see cref="IDataProtectionProvider"/>-backed implementation.
/// </summary>
/// <remarks>
/// <para>
/// The purpose chain is <c>root → version → kind → owner → record</c>. Each segment is prefixed
/// so a value can never impersonate a segment boundary, and the version segment comes first
/// after the root so a future envelope change cannot silently read older payloads.
/// </para>
/// <para>
/// The durable key provider (shared key ring, Key Vault protection, stable application
/// discriminator) is host configuration and is registered elsewhere. This type only consumes
/// whatever <see cref="IDataProtectionProvider"/> the host supplies.
/// </para>
/// </remarks>
public sealed class DataProtectionCoachContentProtector : ICoachContentProtector
{
    /// <summary>The root purpose. Changing it invalidates every stored payload.</summary>
    public const string RootPurpose = "SentenceStudio.Coach.History.Content";

    /// <summary>The envelope version stamped on new writes.</summary>
    public const int Version1 = 1;

    private readonly IDataProtectionProvider _provider;
    private readonly ILogger<DataProtectionCoachContentProtector> _logger;

    // Protectors are intentionally not cached. The record segment makes every protector unique
    // per row, so a cache would hold one entry per message and grow without bound. Building one
    // is purpose derivation only; key material is resolved lazily by the provider.

    public DataProtectionCoachContentProtector(
        IDataProtectionProvider provider,
        ILogger<DataProtectionCoachContentProtector> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public int CurrentVersion => Version1;

    public string Protect(CoachProtectionContext context, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return CreateProtector(context).Protect(plaintext);
    }

    public bool TryUnprotect(CoachProtectionContext context, string? protectedPayload, out string? plaintext)
    {
        plaintext = null;
        if (string.IsNullOrEmpty(protectedPayload))
        {
            return false;
        }

        try
        {
            plaintext = CreateProtector(context).Unprotect(protectedPayload);
            return true;
        }
        catch (CryptographicException)
        {
            // Key rotation, tampering, or a payload written under a different owner, kind,
            // record, or version. Log the operational shape only — never the payload, never the
            // ciphertext, and never the owner id.
            _logger.LogWarning(
                "[Coach] Protected {ContentKind} content (envelope v{Version}) could not be unprotected; treating it as unreadable.",
                context.Kind,
                context.Version);
            return false;
        }
        catch (ArgumentException)
        {
            // Not a Data Protection payload at all (truncated column, hand-edited row).
            _logger.LogWarning(
                "[Coach] Protected {ContentKind} content (envelope v{Version}) is not a readable payload.",
                context.Kind,
                context.Version);
            return false;
        }
    }

    private IDataProtector CreateProtector(CoachProtectionContext context)
    {
        if (context.Owner.IsEmpty)
        {
            throw new ArgumentException("Protected coach content requires a trusted owner.", nameof(context));
        }

        if (string.IsNullOrWhiteSpace(context.RecordId))
        {
            throw new ArgumentException("Protected coach content requires a record identifier.", nameof(context));
        }

        if (context.Version <= 0)
        {
            throw new ArgumentException("Protected coach content requires a positive envelope version.", nameof(context));
        }

        var versionPurpose = $"v{context.Version}";
        var kindPurpose = $"kind:{context.Kind}";
        var ownerPurpose = $"user:{context.Owner.UserProfileId}";
        var recordPurpose = $"record:{context.RecordId}";

        return _provider.CreateProtector(RootPurpose, versionPurpose, kindPurpose, ownerPurpose, recordPurpose);
    }
}
