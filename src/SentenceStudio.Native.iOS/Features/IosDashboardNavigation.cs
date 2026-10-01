using System.Collections.Immutable;

namespace SentenceStudio.Native.iOS.Features;

internal static class IosDashboardNavigation
{
    internal static ImmutableArray<NativeDestination> TopLevelDestinations { get; } =
        [NativeDestination.Dashboard, NativeDestination.Resources];

    internal static bool UsesBottomTabs => TopLevelDestinations.Length > 1;
}
