using SentenceStudio.Contracts.Theme;
using SentenceStudio.Services.Theme;

namespace SentenceStudio.UI.Tests.Theme;

/// <summary>
/// An in-memory <see cref="IThemePreferenceStore"/> that records what was written to it.
/// </summary>
/// <remarks>
/// <para>
/// Two behaviours make it useful beyond "remember a value". <see cref="SaveCount"/> is what proves
/// preview does not persist — an assertion on the stored value alone cannot tell "never written"
/// apart from "written back to the same thing". And <see cref="SynchronousReadAvailable"/> models
/// the one real asymmetry between hosts: the web store cannot answer synchronously inside a
/// circuit, so <c>ThemeService</c> has to finish loading asynchronously. Setting it false is how a
/// test stands in for a circuit without a browser.
/// </para>
/// </remarks>
internal sealed class FakeThemePreferenceStore : IThemePreferenceStore
{
    private AppearanceSelection? _stored;

    public FakeThemePreferenceStore(AppearanceSelection? initial = null, bool synchronousReadAvailable = true)
    {
        _stored = initial;
        SynchronousReadAvailable = synchronousReadAvailable;
    }

    /// <summary>Whether <see cref="TryLoad"/> can see the stored value, as SSR and MAUI can.</summary>
    public bool SynchronousReadAvailable { get; set; }

    /// <summary>How many times a value was persisted.</summary>
    public int SaveCount { get; private set; }

    /// <summary>How many times the asynchronous path was used.</summary>
    public int AsyncLoadCount { get; private set; }

    public AppearanceSelection? Stored => _stored;

    public bool TryLoad(out AppearanceSelection selection)
    {
        if (SynchronousReadAvailable && _stored is not null)
        {
            selection = _stored;
            return true;
        }

        selection = null!;
        return false;
    }

    public ValueTask<AppearanceSelection?> LoadAsync(CancellationToken cancellationToken = default)
    {
        AsyncLoadCount++;
        return new ValueTask<AppearanceSelection?>(_stored);
    }

    public ValueTask SaveAsync(AppearanceSelection selection, CancellationToken cancellationToken = default)
    {
        _stored = selection;
        SaveCount++;
        return ValueTask.CompletedTask;
    }
}
