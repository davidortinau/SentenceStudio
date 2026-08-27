using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Security.DataProtection;

/// <summary>
/// Where the Data Protection key ring lives and what wraps it.
/// </summary>
/// <remarks>
/// <para>
/// Bound from <c>Coach:DataProtection</c>. Nothing here is a secret: the connection string comes
/// from <c>ConnectionStrings</c> (Aspire writes it), and the Key Vault key identifier is a
/// resource name, not a credential. Even so, none of these values are ever logged — a key
/// identifier in a log line is a map to the thing that decrypts every learner conversation.
/// </para>
/// </remarks>
public sealed class CoachDataProtectionOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "Coach:DataProtection";

    /// <summary>
    /// The stable application name for the key ring. Data Protection derives isolation from
    /// this, so it must not change between deployments or every existing payload becomes
    /// unreadable. It is versioned rather than derived from the content root, which is what the
    /// framework does by default and which changes when the app moves directories or images.
    /// </summary>
    public const string DefaultApplicationName = "SentenceStudio.Api.v1";

    /// <summary>
    /// Turns durable key persistence off. Intended for a developer running the API without
    /// Aspire, and for tests. It is refused in Production when durable coach content is on;
    /// see <see cref="CoachKeyRingPlanner"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The connection string name Aspire injects for the key-ring blob container.</summary>
    public string ConnectionName { get; set; } = "coach-keyring";

    /// <summary>
    /// The blob container holding the key ring. Read from the connection string when Aspire
    /// supplies a container-scoped one; this is the fallback and the value used for the URI form.
    /// </summary>
    public string ContainerName { get; set; } = "coach-dataprotection";

    /// <summary>The single blob inside the container that holds the key ring XML.</summary>
    public string BlobName { get; set; } = "keys.xml";

    /// <summary>
    /// The Key Vault key that wraps the key ring. Use a <b>versionless</b> identifier
    /// (<c>https://vault.vault.azure.net/keys/name</c>) so key auto-rotation does not strand the
    /// ring on a retired version. Required in Production when durable coach content is on.
    /// </summary>
    public string? KeyVaultKeyIdentifier { get; set; }

    /// <summary>
    /// The user-assigned managed identity to authenticate with, when the host has more than one.
    /// Null uses the ambient credential chain.
    /// </summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>
    /// Creates the container when it is missing. True for the local emulator, where nothing else
    /// provisions it. Left off outside development: a production container is created by
    /// infrastructure, and an application that can create containers has more rights than it needs.
    /// </summary>
    public bool CreateContainerIfMissing { get; set; } = true;

    /// <summary>
    /// Overrides <see cref="DefaultApplicationName"/>. Present for tests and for a future
    /// deliberate re-key; changing it in a running deployment orphans every existing payload.
    /// </summary>
    public string? ApplicationName { get; set; }
}

/// <summary>
/// Whether the coach is storing durable learner content, which is what makes an ephemeral key
/// ring a data-loss bug rather than a session-resume annoyance.
/// </summary>
/// <remarks>
/// <para>
/// This is a coordination seam, not a feature switch owned here. Durable conversation history
/// and memory are built in the history lane; this interface lets the security lane ask "is
/// there durable content?" without depending on that lane's types or shipping order. The
/// default implementation reads configuration; the history lane can register its own.
/// </para>
/// <para>
/// The distinction matters because <c>CoachSession</c> is a <em>checkpoint</em>: losing it costs
/// a learner one resumable conversation. Durable history and memory are the learner's record —
/// losing the key that reads them is unrecoverable.
/// </para>
/// </remarks>
public interface ICoachDurableContentGate
{
    /// <summary>True when the coach persists content that must survive a restart.</summary>
    bool IsDurableContentEnabled { get; }
}

/// <summary>Configuration-backed <see cref="ICoachDurableContentGate"/>.</summary>
/// <remarks>
/// <para>
/// Bound from <c>Coach:DurableHistory:Enabled</c> and <c>Coach:Memory:Enabled</c>. They are read
/// as two independent flags, and either one turning on is enough to require a durable key ring.
/// </para>
/// <para>
/// The values come from <see cref="CoachOptions"/> — the same type, section, and binder the
/// runtime uses — rather than from this class issuing its own <c>GetValue</c> calls. That is a
/// correctness requirement, not tidiness. When the gate read raw keys independently it was free
/// to disagree with the runtime about whether durable content existed, and it did: the runtime
/// read a flat <c>Coach:DurableHistory</c> and switched the ledger on while the gate read the
/// nested key, saw nothing, and let Production boot without a durable key ring. Sharing the
/// binding makes that disagreement unrepresentable.
/// </para>
/// </remarks>
public sealed class CoachDurableContentOptions : ICoachDurableContentGate
{
    /// <summary>Durable conversation history. Owned by the history lane.</summary>
    public bool DurableHistoryEnabled { get; set; }

    /// <summary>Long-term learner memory. Owned by the history lane.</summary>
    public bool MemoryEnabled { get; set; }

    /// <inheritdoc />
    public bool IsDurableContentEnabled => DurableHistoryEnabled || MemoryEnabled;

    /// <summary>
    /// Reads the two flags by binding <see cref="CoachOptions"/> from its own section, so the
    /// gate and the runtime resolve the same effective values from the same shape.
    /// </summary>
    public static CoachDurableContentOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var coach = new CoachOptions();
        configuration.GetSection(CoachOptions.SectionName).Bind(coach);

        return FromCoachOptions(coach);
    }

    /// <summary>Projects an already-bound <see cref="CoachOptions"/> onto the gate.</summary>
    /// <remarks>
    /// The single place the two flags are turned into a durable-content decision. Both the
    /// startup path and the tests go through here so neither can drift from the other.
    /// </remarks>
    public static CoachDurableContentOptions FromCoachOptions(CoachOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new CoachDurableContentOptions
        {
            DurableHistoryEnabled = options.IsDurableHistoryEnabled,
            MemoryEnabled = options.IsMemoryEnabled
        };
    }
}
