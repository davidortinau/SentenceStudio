using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.App;
using SentenceStudio.Native.iOS.Platform;

namespace SentenceStudio.Native.iOS;

public static class Program
{
    public static void Main(string[] args)
    {
        using var controller = new ApplicationController();
        using var scene = new ApplicationScene(controller);
#if AILOHA_DEBUG
        AilohaDebug.Start();
#endif
        try
        {
            SwiftUIApplication.Run(scene.Root);
        }
        finally
        {
#if AILOHA_DEBUG
            AilohaDebug.Stop();
#endif
        }
    }
}
