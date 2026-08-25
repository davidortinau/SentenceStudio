using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.Services.Theme;

/// <summary>
/// Where one device's — or one browser's — appearance choice is kept.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope is the whole point of this abstraction.</b> The product decision is that theme, mode
/// and text size apply to the thing in front of the learner and nothing else: the phone in their
/// hand, the browser they are sitting at. Never the account. So there are exactly two
/// implementations, and each one is bounded by a physical thing:
/// </para>
/// <list type="bullet">
/// <item>
/// MAUI — <c>DevicePreferencesThemeStore</c>, over the platform preference store. One device, one
/// value, no server involvement.
/// </item>
/// <item>
/// Web — <c>BrowserAppearanceCookieStore</c>, over a per-browser cookie. One browser, one value;
/// two people signed into the same server never see each other's choice, and one person's phone
/// browser and desktop browser stay independent.
/// </item>
/// </list>
/// <para>
/// There are two read paths because the web host has two very different contexts. During the
/// server-side render the cookie is on <c>HttpContext.Request</c> and must be read
/// <b>synchronously</b>, before the <c>&lt;html&gt;</c> element is written, or the first paint
/// flashes the wrong theme. Inside an interactive circuit <c>HttpContext</c> is gone and the only
/// way to the cookie is JS interop, which is asynchronous. <see cref="TryLoad"/> serves the first,
/// <see cref="LoadAsync"/> serves the second, and the MAUI store answers both from the same
/// synchronous preference read.
/// </para>
/// </remarks>
public interface IThemePreferenceStore
{
    /// <summary>
    /// Reads the stored selection without awaiting anything.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a valid selection was reachable and parsed. <see langword="false"/>
    /// when nothing is stored, the stored value failed validation, or this context cannot reach the
    /// substrate synchronously (a Blazor circuit, where the cookie is only reachable through JS).
    /// </returns>
    bool TryLoad(out AppearanceSelection selection);

    /// <summary>
    /// Reads the stored selection, using the asynchronous path when the synchronous one is not
    /// available in this context. Returns <see langword="null"/> when nothing valid is stored.
    /// </summary>
    ValueTask<AppearanceSelection?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists <paramref name="selection"/> for this device or browser only.</summary>
    ValueTask SaveAsync(AppearanceSelection selection, CancellationToken cancellationToken = default);
}
