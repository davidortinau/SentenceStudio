using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace SentenceStudio.Application.Capabilities;

/// <summary>
/// Provides immutable static capability metadata and lookup only. The catalog does not
/// authorize runtime execution or model exposure.
/// </summary>
public interface IApplicationCapabilityCatalog
{
    IReadOnlyList<IApplicationCapabilityDescriptor> All { get; }

    bool TryGet(string codeOrAlias, int majorVersion, out IApplicationCapabilityDescriptor? descriptor);
}

public sealed class ApplicationCapabilityCatalogBuilder
{
    private readonly List<IApplicationCapabilityDescriptor> _definitions = [];
    private readonly Dictionary<ApplicationCapabilityKey, IApplicationCapabilityDescriptor> _identities = [];
    private bool _isFrozen;

    public bool IsFrozen => _isFrozen;

    public ApplicationCapabilityCatalogBuilder Add<TRequest, TClientResult, TCoachObservation>(
        ApplicationCapabilityDefinition<TRequest, TClientResult, TCoachObservation> definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        EnsureMutable();
        ApplicationCapabilityDescriptorValidator.Validate(definition);

        var names = new[] { definition.Code }.Concat(definition.Aliases);
        foreach (var name in names)
        {
            var key = new ApplicationCapabilityKey(name, definition.MajorVersion);
            if (_identities.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException(
                    $"Capability name or alias '{name}@{definition.MajorVersion}' collides with "
                    + $"'{existing.Code}@{existing.MajorVersion}'.");
            }
        }

        _definitions.Add(definition);
        foreach (var name in names)
        {
            _identities.Add(new ApplicationCapabilityKey(name, definition.MajorVersion), definition);
        }

        return this;
    }

    public IApplicationCapabilityCatalog Freeze()
    {
        EnsureMutable();
        _isFrozen = true;

        var ordered = _definitions
            .OrderBy(definition => definition.Code, StringComparer.Ordinal)
            .ThenBy(definition => definition.MajorVersion)
            .ToArray();

        return new FrozenApplicationCapabilityCatalog(
            new ReadOnlyCollection<IApplicationCapabilityDescriptor>(ordered),
            _identities.ToFrozenDictionary());
    }

    private void EnsureMutable()
    {
        if (_isFrozen)
        {
            throw new InvalidOperationException("The application capability catalog builder is frozen.");
        }
    }

    private readonly record struct ApplicationCapabilityKey(string Code, int MajorVersion);

    private sealed class FrozenApplicationCapabilityCatalog(
        IReadOnlyList<IApplicationCapabilityDescriptor> all,
        FrozenDictionary<ApplicationCapabilityKey, IApplicationCapabilityDescriptor> identities)
        : IApplicationCapabilityCatalog
    {
        public IReadOnlyList<IApplicationCapabilityDescriptor> All { get; } = all;

        public bool TryGet(
            string codeOrAlias,
            int majorVersion,
            out IApplicationCapabilityDescriptor? descriptor)
        {
            if (string.IsNullOrWhiteSpace(codeOrAlias) || majorVersion <= 0)
            {
                descriptor = null;
                return false;
            }

            return identities.TryGetValue(new ApplicationCapabilityKey(codeOrAlias, majorVersion), out descriptor);
        }
    }
}
