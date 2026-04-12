namespace SentenceStudio.Abstractions;

/// <summary>
/// Provides the active user's profile ID in a host-appropriate way.
/// MAUI: reads from device preferences (single-user per device).
/// WebApp: reads from authenticated claims / Identity (multi-user server).
/// </summary>
public interface IActiveUserProvider
{
    /// <summary>
    /// Returns the current user's profile ID, or null if unknown.
    /// </summary>
    string? GetActiveProfileId();

    /// <summary>
    /// When true, repositories may fall back to the first profile in the database
    /// when no active profile is found (safe for single-user MAUI apps).
    /// When false (server), repositories must return null to force re-authentication.
    /// </summary>
    bool ShouldFallbackToFirstProfile { get; }
}
