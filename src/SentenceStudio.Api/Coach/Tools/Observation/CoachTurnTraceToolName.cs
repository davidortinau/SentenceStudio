using System.Collections.Frozen;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// The write boundary for the one string a stored turn trace is allowed to carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> <c>CoachTurnTraceEntry.ToolName</c> is the single exception to
/// "the trace holds no strings", and the exception is only sound while the value provably comes
/// from a build-time <c>CoachToolRegistration</c>. "It comes from the seam, and the seam reads the
/// registration" is a fact about today's call graph, not about the type — and a call graph is
/// exactly the kind of guarantee that stops being true without anybody editing the file that
/// states it. So membership is checked here, against the frozen registry, on every value that
/// reaches the record.
/// </para>
/// <para>
/// <b>Frozen, and <c>All</c> rather than <c>Enabled</c>.</b> The set is the complete registration
/// list, computed once. Feature flags decide what a learner may call; they do not decide what a
/// name means. Validating against <c>Enabled</c> would collapse a perfectly real tool's name to the
/// stand-in on any deployment where its flag happened to be off, which would put a hole in the
/// trace that looks exactly like the hole a smuggled name produces.
/// </para>
/// <para>
/// <b>Collapse, never throw.</b> A non-member becomes <see cref="CoachToolNames.Unregistered"/> and
/// the entry keeps its ordinal. Throwing would lose the whole trace section — and, before the
/// section-scoped reader, would have lost the learner's answer with it — over a diagnostic field.
/// </para>
/// </remarks>
internal static class CoachTurnTraceToolName
{
    /// <summary>
    /// Every registered tool name, computed once from a registry built with default options.
    /// </summary>
    /// <remarks>
    /// <c>CoachToolRegistry</c> registers the core, read and write tools in its constructor and
    /// only then applies the options, so <c>All</c> is the same set whatever the flags say. Built
    /// from the registry rather than from <see cref="CoachToolNames.AllRegistered"/> so the two
    /// cannot drift: the registry is what the rest of the system treats as authoritative, and a
    /// constant list nobody re-reads is how a name gets registered and forgotten here.
    /// </remarks>
    private static readonly FrozenSet<string> Registered =
        new CoachToolRegistry(new CoachOptions())
            .All
            .Select(registration => registration.Name)
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The frozen registered-name set, for the sweep that proves this stays in step.</summary>
    internal static IReadOnlyCollection<string> RegisteredNames => Registered;

    /// <summary>
    /// <paramref name="toolName"/> when the frozen registry contains it, otherwise
    /// <see cref="CoachToolNames.Unregistered"/>.
    /// </summary>
    /// <remarks>
    /// Ordinal comparison. A tool name is an identifier, not prose, and a case-insensitive match
    /// here would accept <c>Get_Practice_Balance</c> as the registered tool and write a name no
    /// registration ever produced.
    /// </remarks>
    internal static string Normalize(string? toolName) =>
        toolName is not null && Registered.Contains(toolName)
            ? toolName
            : CoachToolNames.Unregistered;

    /// <summary>True when the frozen registry contains <paramref name="toolName"/>.</summary>
    internal static bool IsRegistered(string? toolName) =>
        toolName is not null && Registered.Contains(toolName);
}
