using AppKit;

namespace SentenceStudio.MacOS;

public class MainClass
{
    static void Main(string[] args)
    {
#if DEBUG
        if (args.Length == 2
            && string.Equals(
                args[0],
                MigrationValidationOptions.BuildConnectionStringArgument,
                StringComparison.Ordinal))
        {
            Console.Out.WriteLine(MigrationValidationOptions.BuildConnectionString(args[1]));
            return;
        }

        DevFlowMacOSBridge.Trace($"===== process start pid={Environment.ProcessId} =====");
#endif
        NSApplication.Init();
        NSApplication.SharedApplication.Delegate = new MauiMacOSApp();
        NSApplication.Main(args);
    }
}
