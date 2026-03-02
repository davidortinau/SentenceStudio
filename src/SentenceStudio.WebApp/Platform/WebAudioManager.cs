using System.Reflection;
using Plugin.Maui.Audio;

namespace SentenceStudio.WebApp.Platform;

/// <summary>
/// Runtime proxy for IAudioManager that returns safe defaults when MAUI audio APIs
/// are unavailable in server-side Blazor.
/// </summary>
public class WebAudioManagerProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null) return null;

        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(void)) return null;
        if (returnType.IsValueType) return Activator.CreateInstance(returnType);
        return null;
    }

    public static IAudioManager Create() =>
        DispatchProxy.Create<IAudioManager, WebAudioManagerProxy>();
}
