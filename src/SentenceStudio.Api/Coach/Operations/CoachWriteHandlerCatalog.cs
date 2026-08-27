using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// The set of write handlers the ledger is allowed to dispatch to.
/// </summary>
/// <remarks>
/// A closed lookup rather than a container scan. The ledger resolves a handler by the tool name
/// recorded on a proposal, and that name came from a model call, so it has to resolve against a
/// fixed set that was validated at startup rather than against whatever happens to be registered.
/// </remarks>
public interface ICoachWriteHandlerCatalog
{
    /// <summary>Every handler, in registration order.</summary>
    IReadOnlyList<ICoachWriteHandler> All { get; }

    /// <summary>The handler for the named tool, or null when the name is not a write tool.</summary>
    ICoachWriteHandler? Find(string toolName);
}

/// <inheritdoc />
public sealed class CoachWriteHandlerCatalog : ICoachWriteHandlerCatalog
{
    private readonly Dictionary<string, ICoachWriteHandler> _byName;

    public CoachWriteHandlerCatalog(IEnumerable<ICoachWriteHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        All = handlers.ToArray();
        _byName = new Dictionary<string, ICoachWriteHandler>(StringComparer.Ordinal);

        foreach (var handler in All)
        {
            if (!_byName.TryAdd(handler.ToolName, handler))
            {
                // Two handlers claiming one name would make which write runs depend on container
                // registration order, so it stops the host instead.
                throw new InvalidOperationException(
                    $"Two coach write handlers are registered for '{handler.ToolName}'.");
            }

            if (handler.RiskClass is not (CoachToolRiskClass.WriteSoft or CoachToolRiskClass.WriteHard))
            {
                throw new InvalidOperationException(
                    $"Coach write handler '{handler.ToolName}' must declare a write risk class.");
            }

            // There is deliberately no rule here tying the risk class to reversibility. Requiring
            // confirmation and being unable to reverse are different properties: changing the
            // language being learned is consequential enough to confirm and trivial to put back,
            // while importing a video is confirmed because it reaches the outside world, which no
            // local delete can un-reach. Collapsing the two would either force a needless refusal
            // on the first or invite a fake undo on the second. Which tools may be reversed is
            // pinned by name in the write tool surface tests, where a reviewer can read the list.
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ICoachWriteHandler> All { get; }

    /// <inheritdoc />
    public ICoachWriteHandler? Find(string toolName) =>
        string.IsNullOrWhiteSpace(toolName) ? null : _byName.GetValueOrDefault(toolName);
}
