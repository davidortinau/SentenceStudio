using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Plugin.Maui.HelpKit;

/// <summary>
/// Resolves the HelpKit-scoped AI services, preferring a keyed registration
/// (<see cref="HelpKitOptions.HelpKitServiceKey"/>) and falling back to the
/// unkeyed registration. Throws a descriptive error only when a caller
/// actually asks for a service and neither is registered.
/// </summary>
internal sealed class HelpKitAiResolver
{
    private readonly IServiceProvider _services;
    private readonly string _key;

    public HelpKitAiResolver(IServiceProvider services, HelpKitOptions options)
    {
        _services = services;
        _key = options.HelpKitServiceKey;
    }

    public IChatClient ResolveChatClient()
    {
        var keyed = _services.GetKeyedService<IChatClient>(_key);
        if (keyed is not null) return keyed;

        var unkeyed = _services.GetService<IChatClient>();
        if (unkeyed is not null) return unkeyed;

        throw new InvalidOperationException(
            $"Plugin.Maui.HelpKit could not resolve IChatClient. " +
            $"Register one via AddKeyedSingleton<IChatClient>(\"{_key}\", ...) " +
            $"or AddSingleton<IChatClient>(...). " +
            $"HelpKit deliberately ships no model — you bring the client.");
    }

    public IEmbeddingGenerator<string, Embedding<float>> ResolveEmbeddingGenerator()
    {
        var keyed = _services.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(_key);
        if (keyed is not null) return keyed;

        var unkeyed = _services.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
        if (unkeyed is not null) return unkeyed;

        throw new InvalidOperationException(
            $"Plugin.Maui.HelpKit could not resolve IEmbeddingGenerator<string, Embedding<float>>. " +
            $"Register one via AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(\"{_key}\", ...) " +
            $"or AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(...). " +
            $"HelpKit deliberately ships no model — you bring the generator.");
    }
}
