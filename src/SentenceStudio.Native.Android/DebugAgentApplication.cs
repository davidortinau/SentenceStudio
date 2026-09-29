#if DEBUG
using Com.Ailoha.Android.Agent;
using Com.Ailoha.Android.Agent.Compose;

namespace SentenceStudio.Native.Android;

[global::Android.App.Application]
public sealed class DebugAgentApplication : global::Android.App.Application
{
    public DebugAgentApplication(IntPtr handle, global::Android.Runtime.JniHandleOwnership transfer)
        : base(handle, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();
        var port = AilohaAndroidAgent.Start(this, new AilohaAgentOptions());
        AilohaComposeAgent.Install();
        global::Android.Util.Log.Info("SentenceStudio.Native.Android", $"Debug inspection listening on {port}");
    }
}
#endif
