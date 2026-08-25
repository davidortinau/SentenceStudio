using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using SentenceStudio.Contracts;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Who the coach surfaces currently belong to, read from the authenticated principal.
/// </summary>
/// <remarks>
/// <para>
/// This exists because "the same learner" and "the same principal object" are not the same
/// question, and the coach surfaces have to answer the first one. A single sign-in produces
/// several different principals in sequence — MAUI publishes an optimistic principal carrying only
/// a remembered email while a refresh token is exchanged, then republishes a full JWT principal
/// with a profile id and a subject — and a naive equality check reads that as two learners and
/// throws away a conversation the learner is in the middle of.
/// </para>
/// <para>
/// So identity is compared field by field, and <em>only</em> like against like: a profile id
/// against a profile id, a subject against a subject, an email against an email. An earlier
/// revision pooled every readable claim value into one untyped bucket and asked whether the two
/// buckets intersected. That is cheaper and it is wrong in a way that matters: a learner whose
/// display name happens to be another learner's email address would have been recognised as that
/// other learner, and the coach surfaces would have been handed over without clearing. Display
/// names are not identifiers — they are learner-chosen text — so they are not read here at all.
/// </para>
/// <para>
/// Anonymous is its own value rather than an empty identity, so "signed out" is never mistaken for
/// "signed in with nothing to compare". A set-intersection rule over two empty identities would say
/// "same learner", which is exactly the wrong answer at the moment a session ends.
/// </para>
/// </remarks>
public readonly struct CoachAccountIdentity : IEquatable<CoachAccountIdentity>
{
    /// <summary>
    /// The claim that carries the learner's profile id — the value every owner-scoped server route
    /// keys on, and the strongest thing a principal can say about who it is.
    /// </summary>
    private static readonly string[] ProfileIdClaimTypes = [AuthClaimTypes.UserProfileId];

    /// <summary>
    /// The token's own subject.
    /// </summary>
    /// <remarks>
    /// Two claim types, one field. <c>sub</c> is what a raw JWT carries and
    /// <see cref="ClaimTypes.NameIdentifier"/> is what the inbound-claim mapping renames it to;
    /// they are the same assertion under two spellings, and this codebase produces both — MAUI
    /// reads the JWT unmapped, ASP.NET Core maps by default. Treating them as separate fields
    /// would make a mapped and an unmapped read of one token look like two learners.
    /// </remarks>
    private static readonly string[] SubjectClaimTypes = ["sub", ClaimTypes.NameIdentifier];

    /// <summary>
    /// The learner's email, under both spellings, for the same mapped/unmapped reason.
    /// </summary>
    /// <remarks>
    /// Included because it is the <em>only</em> stable identifier the MAUI optimistic principal
    /// carries — without it, every cold start would read the handoff from optimistic to real token
    /// as an account change and clear the conversation the learner had just resumed.
    /// </remarks>
    private static readonly string[] EmailClaimTypes = [ClaimTypes.Email, "email"];

    // Deliberately absent: ClaimTypes.Name, "name", "preferred_username". They are display text a
    // learner can set to anything, including somebody else's email address, so they can never be
    // an identity match. They are not read, not stored, and not compared.

    private CoachAccountIdentity(
        bool isAuthenticated,
        ImmutableHashSet<string> profileIds,
        ImmutableHashSet<string> subjects,
        ImmutableHashSet<string> emails,
        string? syntheticKey,
        string? primaryKey)
    {
        IsAuthenticated = isAuthenticated;
        ProfileIds = profileIds;
        Subjects = subjects;
        Emails = emails;
        SyntheticKey = syntheticKey;
        PrimaryKey = primaryKey;
    }

    /// <summary>Nobody is signed in.</summary>
    public static CoachAccountIdentity Anonymous { get; } = new(
        isAuthenticated: false,
        ImmutableHashSet<string>.Empty,
        ImmutableHashSet<string>.Empty,
        ImmutableHashSet<string>.Empty,
        syntheticKey: null,
        primaryKey: null);

    /// <summary>True when a principal was present and authenticated.</summary>
    public bool IsAuthenticated { get; }

    /// <summary>Profile ids asserted by this principal, normalized.</summary>
    public ImmutableHashSet<string> ProfileIds { get; } = ImmutableHashSet<string>.Empty;

    /// <summary>Token subjects asserted by this principal, normalized.</summary>
    public ImmutableHashSet<string> Subjects { get; } = ImmutableHashSet<string>.Empty;

    /// <summary>Emails asserted by this principal, normalized.</summary>
    public ImmutableHashSet<string> Emails { get; } = ImmutableHashSet<string>.Empty;

    /// <summary>
    /// A content-derived stand-in used only when a principal asserts no typed identifier at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An authenticated principal with no profile id, no subject and no email is not anonymous, and
    /// it is not everybody either. It gets a key derived from its own claim content, so it compares
    /// equal to a second read of the same principal and equal to nothing else. A shared constant
    /// would have made every unreadable principal the same learner, which is the failure this whole
    /// type exists to prevent.
    /// </para>
    /// <para>
    /// It is never used when a typed identifier is present on either side, so it can never widen a
    /// match — only stand in for one that does not exist.
    /// </para>
    /// </remarks>
    public string? SyntheticKey { get; }

    /// <summary>
    /// The single value worth logging or keying on, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Never rendered and never sent anywhere. Diagnostics only, and callers are expected to mask
    /// it rather than write an email address into a log line.
    /// </remarks>
    public string? PrimaryKey { get; }

    /// <summary>True when this principal asserts at least one typed identifier.</summary>
    public bool HasStableIdentifier =>
        !ProfileIds.IsEmpty || !Subjects.IsEmpty || !Emails.IsEmpty;

    /// <summary>Reads the identity of a principal, tolerating a null or unauthenticated one.</summary>
    public static CoachAccountIdentity From(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return Anonymous;
        }

        var profileIds = Read(principal, ProfileIdClaimTypes);
        var subjects = Read(principal, SubjectClaimTypes);
        var emails = Read(principal, EmailClaimTypes);

        var primary = profileIds.FirstOrDefault()
                      ?? subjects.FirstOrDefault()
                      ?? emails.FirstOrDefault();

        var synthetic = profileIds.IsEmpty && subjects.IsEmpty && emails.IsEmpty
            ? SyntheticKeyFor(principal)
            : null;

        return new CoachAccountIdentity(
            isAuthenticated: true, profileIds, subjects, emails, synthetic, primary);
    }

    /// <summary>
    /// True when these two identities are the same learner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two anonymous identities are the same "nobody", so a repeated signed-out notification is not
    /// a boundary. An anonymous and an authenticated identity are never the same, so both signing
    /// in and signing out cross one.
    /// </para>
    /// <para>
    /// Two authenticated identities are the same learner when any typed field agrees. A field that
    /// only one side asserts proves nothing and is skipped; a field neither asserts proves nothing
    /// either. If no field agrees and either side has a typed identifier, they are different
    /// accounts — the conservative answer, because the cost of a wrong "same" is one learner
    /// reading another's conversation and the cost of a wrong "different" is a reload.
    /// </para>
    /// </remarks>
    public bool IsSameAccountAs(CoachAccountIdentity other)
    {
        if (!IsAuthenticated || !other.IsAuthenticated)
        {
            return IsAuthenticated == other.IsAuthenticated;
        }

        // Like against like, in descending order of how much each one proves.
        if (ProfileIds.Overlaps(other.ProfileIds)
            || Subjects.Overlaps(other.Subjects)
            || Emails.Overlaps(other.Emails))
        {
            return true;
        }

        // Nothing agreed. A typed identifier on either side settles it: these are two accounts.
        if (HasStableIdentifier || other.HasStableIdentifier)
        {
            return false;
        }

        // Neither principal asserts anything typed. Fall back to content identity, which matches
        // a re-read of the same principal and nothing else.
        return SyntheticKey is not null
               && string.Equals(SyntheticKey, other.SyntheticKey, StringComparison.Ordinal);
    }

    private static ImmutableHashSet<string> Read(ClaimsPrincipal principal, string[] claimTypes)
    {
        ImmutableHashSet<string>.Builder? builder = null;

        foreach (var claimType in claimTypes)
        {
            foreach (var claim in principal.FindAll(claimType))
            {
                if (Normalize(claim.Value) is not { } value)
                {
                    continue;
                }

                builder ??= ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
                builder.Add(value);
            }
        }

        return builder?.ToImmutable() ?? ImmutableHashSet<string>.Empty;
    }

    /// <summary>
    /// Derives a stable key from the whole claim set, so the same principal read twice matches.
    /// </summary>
    /// <remarks>
    /// Hashed rather than kept verbatim so nothing here can be mistaken for a value worth showing
    /// or logging. It is an equality token, not a description.
    /// </remarks>
    private static string SyntheticKeyFor(ClaimsPrincipal principal)
    {
        var lines = principal.Claims
            .Select(c => c.Type + "\u001f" + c.Value)
            .OrderBy(line => line, StringComparer.Ordinal);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001e", lines)));
        return "principal:" + Convert.ToHexString(bytes);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant();
    }

    public bool Equals(CoachAccountIdentity other) =>
        IsAuthenticated == other.IsAuthenticated
        && ProfileIds.SetEquals(other.ProfileIds)
        && Subjects.SetEquals(other.Subjects)
        && Emails.SetEquals(other.Emails)
        && string.Equals(SyntheticKey, other.SyntheticKey, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is CoachAccountIdentity other && Equals(other);

    public override int GetHashCode() => IsAuthenticated
        ? HashCode.Combine(ProfileIds.Count, Subjects.Count, Emails.Count, SyntheticKey)
        : 0;

    public override string ToString() => IsAuthenticated
        ? $"authenticated(profile:{ProfileIds.Count} sub:{Subjects.Count} email:{Emails.Count})"
        : "anonymous";
}
