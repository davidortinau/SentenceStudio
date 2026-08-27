using System;

namespace SentenceStudio.Abstractions.Keychain;

/// <summary>
/// What this install has already decided about the pre-namespacing ("bare") keychain accounts.
/// </summary>
public enum LegacyAdoptionOutcome
{
    /// <summary>No decision recorded yet. The only state in which adoption may be attempted.</summary>
    Undecided = 0,

    /// <summary>
    /// Ownership was corroborated and the values were copied into this app's namespaced accounts.
    /// The bare items are left in place, and must never be read again.
    /// </summary>
    Adopted = 1,

    /// <summary>
    /// Ownership could not be corroborated. The bare items belong to somebody else, or cannot be
    /// proven to belong to us, and must never be read, copied or removed.
    /// </summary>
    Rejected = 2,

    /// <summary>
    /// The learner has signed out on this install. Whatever is under the bare accounts is not to be
    /// adopted, now or after any relaunch, because adopting it would silently sign somebody back in
    /// after they asked to be signed out.
    /// </summary>
    Retired = 3,
}

/// <summary>
/// Durable record of this install's decision about the pre-namespacing keychain accounts.
/// </summary>
/// <remarks>
/// <para>
/// Durable, not per-process: the question "did <em>this install</em> put that item there" cannot be
/// answered from the keychain, because the shared service name
/// (<c>maui_secure_storage</c>) carries no owner. The answer has to be remembered on the app's own
/// side, and it has to survive relaunch or the app would re-ask — and re-answer — every launch.
/// </para>
/// <para>
/// An abstraction rather than a direct <c>Preferences</c> call so the decision logic is testable
/// without a platform preference store, and so the storage location is one place to audit.
/// </para>
/// </remarks>
public interface ILegacyAdoptionJournal
{
    /// <summary>The recorded decision for a logical credential group.</summary>
    /// <param name="groupId">
    /// Identifies the group of accounts the decision covers — for the credential triple this is a
    /// fixed constant, not a per-key name, because the three keys are only meaningful together.
    /// </param>
    LegacyAdoptionOutcome Read(string groupId);

    /// <summary>Records a decision. Must be durable across relaunch.</summary>
    void Record(string groupId, LegacyAdoptionOutcome outcome);
}
