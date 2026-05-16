using Foundation;
using Microsoft.Maui.Platforms.MacOS.Hosting;
using Microsoft.Maui.Platforms.MacOS.Platform;

namespace SentenceStudio.MacOS;

[Register("MauiMacOSApp")]
public class MauiMacOSApp : MacOSMauiApplication
{
    protected override MauiApp CreateMauiApp() => MacOSMauiProgram.CreateMauiApp();
}
