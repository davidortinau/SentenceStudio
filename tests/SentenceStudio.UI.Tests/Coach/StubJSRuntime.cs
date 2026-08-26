using Microsoft.JSInterop;
#pragma warning disable BL0016

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// A JS runtime for render tests, and a recorder for the copy contract.
/// </summary>
/// <remarks>
/// The components under test import <c>./js/app.js</c> and call into it. Nothing here executes
/// JavaScript; it records what would have been invoked and returns what the module would have
/// returned, so the C# side of the interop contract can be asserted without a browser. The JS
/// side is covered separately in <c>tests/js/coach-interop.test.js</c>.
/// </remarks>
internal sealed class StubJSRuntime : IJSRuntime
{
    /// <summary>Every identifier invoked, in order, including on imported modules.</summary>
    public List<string> Invocations { get; } = [];

    /// <summary>
    /// Every module invocation with its arguments, in order. Additive companion to
    /// <see cref="Invocations"/> so tests that need to inspect args (e.g. proving
    /// <c>focusElement</c> was called with <c>{ preventScroll: true }</c>) can do so without any
    /// existing caller having to change.
    /// </summary>
    public List<(string Identifier, object?[]? Args)> ModuleCalls { get; } = new();

    /// <summary>The first argument of the first call to <paramref name="identifier"/>, if any.</summary>
    public object? FirstArgOf(string identifier) => ModuleCalls
        .Where(c => c.Identifier == identifier)
        .Select(c => c.Args?.FirstOrDefault())
        .FirstOrDefault();

    /// <summary>The text passed to the most recent clipboard call.</summary>
    public string? LastCopiedText { get; private set; }

    /// <summary>What the clipboard call reports back. False models a refused or missing clipboard.</summary>
    public bool CopySucceeds { get; set; } = true;

    /// <summary>When set, the clipboard call throws instead of returning, as a blocked API would.</summary>
    public bool CopyThrows { get; set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier,
        CancellationToken cancellationToken,
        object?[]? args)
    {
        Invocations.Add(identifier);

        if (identifier == "import")
        {
            return ValueTask.FromResult((TValue)(object)new StubJSObjectReference(this));
        }

        return ValueTask.FromResult(default(TValue)!);
    }

    internal ValueTask<TValue> InvokeModuleAsync<TValue>(string identifier, object?[]? args)
    {
        Invocations.Add(identifier);
        ModuleCalls.Add((identifier, args));

        if (identifier == "copyTextToClipboard")
        {
            if (CopyThrows)
            {
                throw new JSException("clipboard blocked");
            }

            LastCopiedText = args?.FirstOrDefault() as string;
            return ValueTask.FromResult((TValue)(object)CopySucceeds);
        }

        return ValueTask.FromResult(default(TValue)!);
    }

    private sealed class StubJSObjectReference(StubJSRuntime owner) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            owner.InvokeModuleAsync<TValue>(identifier, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            owner.InvokeModuleAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
#pragma warning restore BL0016
