using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Services;

namespace SentenceStudio.Native.iOS.Features.Import;

/// <summary>
/// The shared Import Content coordinator on iOS, with the one drawn node its rows update in
/// place. (Its awaits resume on the main thread: UIKit's synchronization context is installed
/// before Main runs.)
/// </summary>
internal sealed class ImportContentScreen(
    INativeContentImport import, DeviceUserScopeProvider userScope,
    ILogger<ImportContentCoordinator> logger, Func<string, string> text)
    : ImportContentCoordinator(import, userScope, logger, text)
{
    /// <summary>
    /// The drawn "Selected for import" text. Row checkboxes update it in place; the page sets
    /// it on every draw, so rows kept from an earlier draw update the current one.
    /// </summary>
    internal SwiftView? SelectedCountLabel { get; set; }
}
