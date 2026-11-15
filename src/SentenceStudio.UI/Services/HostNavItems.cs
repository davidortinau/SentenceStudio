namespace SentenceStudio.WebUI.Services;

/// <summary>A sidebar entry that belongs to the host app rather than to the shared UI.</summary>
/// <param name="Href">The route the entry opens, with a leading slash. Also its active-state prefix.</param>
/// <param name="Icon">A Bootstrap icon class, for example <c>bi-shield-lock</c>.</param>
/// <param name="Label">The text shown, already in the language the host wants.</param>
public sealed record HostNavItem(string Href, string Icon, string Label);

/// <summary>
/// Lets one host add sidebar entries the other hosts must not ship.
/// </summary>
/// <remarks>
/// The web app registers one for its admin pages. A host that registers none (every MAUI head)
/// shows the shared navigation unchanged. An implementation decides per signed-in person and must
/// return an empty list rather than throw. The sidebar asks when it is first drawn and again on
/// each navigation, so an implementation should answer from what it already knows when it can.
/// </remarks>
public interface IHostNavItemProvider
{
    ValueTask<IReadOnlyList<HostNavItem>> GetItemsAsync(CancellationToken cancellationToken = default);
}
