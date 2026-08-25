using System.Collections.Frozen;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Coach.Capabilities;

/// <summary>
/// Every capability the coach knows about, built once and never changed.
/// </summary>
public interface ICoachCapabilityManifest
{
    /// <summary>Every capability, tool-backed first in registry order, then standalone declarations.</summary>
    IReadOnlyList<CoachCapabilityDescriptor> All { get; }

    /// <summary>The descriptor for <paramref name="name"/>, or null when nothing declares it.</summary>
    CoachCapabilityDescriptor? Find(string name);

    /// <summary>True when <paramref name="name"/> is declared.</summary>
    bool Contains(string name);
}

/// <summary>
/// The manifest, built from the frozen registry plus the standalone declarations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built once, from a registry that can no longer grow.</b> The constructor refuses an unfrozen
/// registry outright. A manifest built over an open registry would be a snapshot of whatever had
/// been registered so far, and every guarantee downstream — the matrix passed, the census is
/// complete, the snapshot test is meaningful — would be a statement about a moment rather than
/// about the system. Freezing first is what makes "every capability was validated" true.
/// </para>
/// <para>
/// <b>Immutable and cached, because it is read on the hot path.</b> Registered as a singleton and
/// backed by a <see cref="FrozenDictionary{TKey,TValue}"/>, so a per-turn resolution is a lookup
/// rather than a walk. Nothing here allocates after construction.
/// </para>
/// <para>
/// <b>Name collisions stop the host.</b> Two capabilities answering to one name would make
/// resolution depend on insertion order, which is exactly the kind of silent ambiguity a startup
/// validator exists to prevent.
/// </para>
/// </remarks>
public sealed class CoachCapabilityManifest : ICoachCapabilityManifest
{
    private readonly FrozenDictionary<string, CoachCapabilityDescriptor> _byName;

    public CoachCapabilityManifest(ICoachToolRegistry registry)
        : this(registry, CoachCapabilityDeclarations.All)
    {
    }

    /// <summary>
    /// Test seam: builds against an explicit declaration set so a fixture can prove the builder's
    /// own rules without editing the shipped declarations.
    /// </summary>
    public CoachCapabilityManifest(
        ICoachToolRegistry registry,
        IReadOnlyList<CoachCapabilityDescriptor> declarations)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(declarations);

        if (!registry.IsFrozen)
        {
            throw new InvalidOperationException(
                "The coach capability manifest can only be built from a frozen tool registry. "
                + "Building it earlier would snapshot a registry that can still grow, and every "
                + "downstream guarantee — matrix validated, census complete, snapshot meaningful — "
                + "would describe a moment rather than the system.");
        }

        var all = new List<CoachCapabilityDescriptor>(registry.All.Count + declarations.Count);

        foreach (var registration in registry.All)
        {
            all.Add(CoachCapabilityDescriptor.FromRegistration(registration));
        }

        all.AddRange(declarations);

        var byName = new Dictionary<string, CoachCapabilityDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in all)
        {
            if (!byName.TryAdd(descriptor.Name, descriptor))
            {
                throw new InvalidOperationException(
                    $"The capability '{descriptor.Name}' is declared twice. Resolution would depend "
                    + "on insertion order, so this stops the host instead.");
            }
        }

        All = all.AsReadOnly();
        _byName = byName.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyList<CoachCapabilityDescriptor> All { get; }

    /// <inheritdoc />
    public CoachCapabilityDescriptor? Find(string name) =>
        name is not null && _byName.TryGetValue(name, out var found) ? found : null;

    /// <inheritdoc />
    public bool Contains(string name) => Find(name) is not null;
}
