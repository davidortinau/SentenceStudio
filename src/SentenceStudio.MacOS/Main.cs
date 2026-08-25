using AppKit;

namespace SentenceStudio.MacOS;

public class MainClass
{
    static void Main(string[] args)
    {
#if DEBUG
        DevFlowMacOSBridge.Trace($"===== process start pid={Environment.ProcessId} =====");
#endif
        NSApplication.Init();
        NSApplication.SharedApplication.Delegate = new MauiMacOSApp();
        NSApplication.Main(args);
    }
}
