using System.Collections.Immutable;

namespace SentenceStudio.Native.iOS.Features;

internal static class IosDashboardNavigation
{
    // Add a tab only after another top-level native destination is navigable.
    internal static ImmutableArray<NativeDestination> TopLevelDestinations { get; } =
        [NativeDestination.Dashboard];

    internal static bool UsesBottomTabs => TopLevelDestinations.Length > 1;
}
