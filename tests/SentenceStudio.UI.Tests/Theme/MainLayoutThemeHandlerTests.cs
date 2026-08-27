using System.Text.RegularExpressions;
using FluentAssertions;

namespace SentenceStudio.UI.Tests.Theme;

/// <summary>
/// The layout's theme handler is an <c>async void</c> event handler, and that has consequences.
/// </summary>
/// <remarks>
/// <para>
/// <c>ThemeService.ThemeChanged</c> is an <see cref="EventHandler{TEventArgs}"/>, so the layout's
/// subscriber cannot return a <see cref="Task"/> anybody awaits. Anything it throws goes to the
/// unhandled-exception path, which on the web ends the learner's circuit — mid-lesson, over a
/// colour change. The two realistic throws are both benign races with teardown: the circuit
/// disconnecting, and the imported module being disposed while a call is in flight.
/// </para>
/// <para>
/// Asserted against the source rather than through a render harness because
/// <c>MainLayout</c> injects fourteen services and the property being defended is structural —
/// which guards exist, which exceptions are swallowed, and that the unsubscribe survived. A
/// harness able to construct the layout would prove less about those than reading them does.
/// </para>
/// </remarks>
public class MainLayoutThemeHandlerTests
{
    private static string MainLayoutSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.UI")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        return File.ReadAllText(Path.Combine(
            directory!.FullName, "src", "SentenceStudio.UI", "Layout", "MainLayout.razor"));
    }

    private static string HandlerBody()
    {
        var source = MainLayoutSource();
        var start = source.IndexOf("private async void OnThemeChanged", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "MainLayout must still subscribe to ThemeChanged");

        // Up to the next member declaration — enough to cover the whole handler.
        var end = source.IndexOf("\n    public void Dispose()", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);

        return source[start..end];
    }

    [Fact]
    public void The_handler_drops_out_when_the_layout_has_already_been_disposed()
    {
        // The event is raised by a service that outlives the component. On the MAUI BlazorWebView
        // the layout is disposed and rebuilt around exactly the transitions these events describe,
        // so "already disposed" is the common case, not the edge case.
        HandlerBody().Should().Contain("if (_disposed) return;");
    }

    [Fact]
    public void The_handler_rechecks_disposal_after_awaiting()
    {
        // Disposal can land between the two interop calls; one check at the top is not enough.
        Regex.Matches(HandlerBody(), @"if \(_disposed\) return;").Count.Should().BeGreaterThanOrEqualTo(
            2,
            "the handler awaits, so it must re-check after the first await as well as before starting");
    }

    [Fact]
    public void The_handler_swallows_the_established_teardown_exceptions()
    {
        var body = HandlerBody();

        // The same pair every other JS-touching component in this codebase catches.
        body.Should().Contain("catch (JSDisconnectedException)");
        body.Should().Contain("catch (ObjectDisposedException)");
    }

    [Fact]
    public void The_handler_does_not_swallow_everything()
    {
        // A bare catch-all would hide real interop bugs — a renamed export, a bad argument — in a
        // handler nobody awaits, which is the hardest possible place to notice them.
        var body = HandlerBody();

        body.Should().NotContain("catch (Exception");
        body.Should().NotMatchRegex(@"catch\s*\{");
    }

    [Fact]
    public void The_handler_captures_the_module_before_awaiting()
    {
        // Reading the field again after an await would race with disposal nulling it out.
        var body = HandlerBody();

        body.Should().Contain("var module = jsModule;");
        body.Should().NotContain("jsModule.InvokeVoidAsync",
            "the captured local is what the awaits must use");
    }

    [Fact]
    public void The_subscription_is_still_torn_down_on_dispose()
    {
        var source = MainLayoutSource();

        source.Should().Contain("ThemeService.ThemeChanged += OnThemeChanged;");
        source.Should().Contain("ThemeService.ThemeChanged -= OnThemeChanged;");

        // Dispose sets the flag before unsubscribing, so a handler already running on another
        // thread observes it rather than rendering into a layout that is going away.
        var disposeStart = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        var flagSet = source.IndexOf("_disposed = true;", disposeStart, StringComparison.Ordinal);
        var unsubscribe = source.IndexOf("ThemeService.ThemeChanged -= OnThemeChanged;", disposeStart, StringComparison.Ordinal);

        flagSet.Should().BeGreaterThan(-1);
        unsubscribe.Should().BeGreaterThan(flagSet, "the flag must be set before unsubscribing");
    }

    [Fact]
    public void The_layout_seeds_the_appearance_before_painting_it()
    {
        var source = MainLayoutSource();

        var ensureLoaded = source.IndexOf("await ThemeService.EnsureLoadedAsync();", StringComparison.Ordinal);
        var applyTheme = source.IndexOf(@"InvokeVoidAsync(""applyTheme""", StringComparison.Ordinal);
        var subscribe = source.IndexOf("ThemeService.ThemeChanged += OnThemeChanged;", StringComparison.Ordinal);

        ensureLoaded.Should().BeGreaterThan(-1, "inside a circuit the cookie is only reachable over interop");
        applyTheme.Should().BeGreaterThan(ensureLoaded, "painting the default first would flash");
        subscribe.Should().BeGreaterThan(applyTheme, "the initial paint is explicit, not event-driven");
    }
}
