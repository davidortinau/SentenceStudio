namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// What the client was showing when the learner sent a turn, so a bug report or request filed
/// through Sam can say where it happened.
/// </summary>
/// <remarks>
/// <para>
/// Two halves with different audiences. The <b>public</b> fields describe the build and the
/// screen in identifiers only — no learner text — and may appear in a public GitHub issue. The
/// <see cref="State"/> map and <see cref="Route"/> may carry learner content (an item id, a
/// filter the learner typed) and are only ever stored privately, never published.
/// </para>
/// <para>
/// Optional and advisory. The server bounds and sanitises every field, keeps it for the turn
/// only, and stores it only when the learner approves a feedback report. It is never an
/// authorization source.
/// </para>
/// </remarks>
public sealed class CoachClientScreenContext
{
    /// <summary>Stable screen identifier, for example <c>native.vocabulary.list</c> or a web route template. Public.</summary>
    public string? ScreenId { get; init; }

    /// <summary>Client surface, for example <c>ios</c>, <c>android</c>, <c>web</c>, <c>maui-ios</c>. Public.</summary>
    public string? Platform { get; init; }

    /// <summary>App version and build configuration, for example <c>1.4.0 (Debug)</c>. Public.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Operating system and version, for example <c>iOS 26.1</c>. Public.</summary>
    public string? OsVersion { get; init; }

    /// <summary>Device model or browser, for example <c>iPhone18,1</c>. Public.</summary>
    public string? Device { get; init; }

    /// <summary>UI culture, for example <c>en-US</c> or <c>ko-KR</c>. Public.</summary>
    public string? Locale { get; init; }

    /// <summary><c>light</c> or <c>dark</c>. Public.</summary>
    public string? Theme { get; init; }

    /// <summary>Automation ids of the controls currently on screen. Ids only, never labels. Public.</summary>
    public IReadOnlyList<string>? VisibleControlIds { get; init; }

    /// <summary>The concrete route or destination, which may contain ids. Private.</summary>
    public string? Route { get; init; }

    /// <summary>Screen state as short key/value pairs (selected item, open sheet, filter). Private.</summary>
    public IReadOnlyList<CoachClientScreenStateEntry>? State { get; init; }
}

/// <summary>One piece of private screen state, for example <c>selectedResourceId</c> = <c>42</c>.</summary>
public sealed class CoachClientScreenStateEntry
{
    public required string Key { get; init; }

    public string? Value { get; init; }
}
