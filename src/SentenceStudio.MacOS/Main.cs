using AppKit;

namespace SentenceStudio.MacOS;

public class MainClass
{
    static void Main(string[] args)
    {
        NSApplication.Init();
        NSApplication.SharedApplication.Delegate = new MauiMacOSApp();
        NSApplication.Main(args);
    }
}
