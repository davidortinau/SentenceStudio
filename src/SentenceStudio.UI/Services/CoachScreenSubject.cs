using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// What the open page shows, in the terms Sam's tools understand. A page that shows something its
/// route does not name (the quiz word, the card face, the sentence being translated) registers a
/// describe callback while it is open; <see cref="CoachScreenContextCollector"/> calls it when the
/// learner sends Sam a message, so the description is the page's state at that moment.
/// </summary>
/// <remarks>
/// One per circuit (web) or per BlazorWebView (MAUI). Only the page that registered last describes
/// the screen, and only until it releases the registration, so a page left behind never answers for
/// the next one. A page's callback reads its live fields, so it runs on the page's own dispatcher
/// (the <c>invoke</c> a page passes is its <c>InvokeAsync</c>): a send can reach the collector on a
/// thread-pool thread, and reading the fields there could see them mid-render.
/// </remarks>
public sealed class CoachScreenSubject
{
    private Registration? _current;

    /// <summary>
    /// Registers <paramref name="describe"/> for the open page; dispose the result when the page
    /// closes. <paramref name="invoke"/> runs it on the page's dispatcher; null runs it in place.
    /// </summary>
    public IDisposable Publish(Func<CoachScreenDescription> describe, Func<Action, Task>? invoke = null)
    {
        ArgumentNullException.ThrowIfNull(describe);
        var registration = new Registration(this, describe, invoke);
        _current = registration;
        return registration;
    }

    /// <summary>
    /// Registers <paramref name="describe"/> with the host's subject, or does nothing when the host
    /// has none (a test harness, a host without Sam). Pages call this rather than injecting the
    /// subject, so describing a page for Sam can never stop the page from opening.
    /// </summary>
    public static IDisposable? Publish(
        IServiceProvider services, Func<Action, Task> invoke, Func<CoachScreenDescription> describe) =>
        (services.GetService(typeof(CoachScreenSubject)) as CoachScreenSubject)?.Publish(describe, invoke);

    /// <summary>The open page's description, read on its dispatcher, or null when no page describes itself.</summary>
    public Task<CoachScreenDescription?> DescribeAsync() =>
        _current is { } current ? current.DescribeAsync() : Task.FromResult<CoachScreenDescription?>(null);

    private sealed class Registration(
        CoachScreenSubject owner, Func<CoachScreenDescription> describe, Func<Action, Task>? invoke) : IDisposable
    {
        public async Task<CoachScreenDescription?> DescribeAsync()
        {
            if (invoke is null)
                return describe();
            CoachScreenDescription? described = null;
            await invoke(() => described = describe());
            return described;
        }

        public void Dispose()
        {
            if (ReferenceEquals(owner._current, this))
                owner._current = null;
        }
    }
}

/// <summary>What one page shows: its items (the main one marked Focus), a hidden-answer flag and short facts.</summary>
public sealed record CoachScreenDescription(
    IReadOnlyList<CoachClientScreenItem> Items,
    bool AnswerHidden = false,
    IReadOnlyList<CoachClientScreenStateEntry>? Facts = null)
{
    public static CoachClientScreenItem Item(
        string kind, string? id, string? label, string? detail = null, bool focus = false) => new()
    {
        Kind = kind,
        Id = string.IsNullOrWhiteSpace(id) ? null : id,
        Label = string.IsNullOrWhiteSpace(label) ? null : label,
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail,
        Focus = focus
    };

    public static CoachClientScreenStateEntry Fact(string key, string? value) => new() { Key = key, Value = value };
}
