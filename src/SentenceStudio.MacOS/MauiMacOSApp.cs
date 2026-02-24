using Foundation;
using Microsoft.Maui.Platform.MacOS.Hosting;

namespace SentenceStudio.MacOS;

[Register("MauiMacOSApp")]
public class MauiMacOSApp : MacOSMauiApplication
{
    protected override MauiApp CreateMauiApp() => MacOSMauiProgram.CreateMauiApp();
}
