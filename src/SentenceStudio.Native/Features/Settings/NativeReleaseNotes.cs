using System.Text.RegularExpressions;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Features.Settings;

/// <summary>A release-notes line: a bold headline (when the line starts with one) and its text.</summary>
internal sealed record NativeReleaseNoteItem(string? Headline, string Text);

/// <summary>A release-notes section (a <c>###</c> heading) and its lines.</summary>
internal sealed record NativeReleaseNoteSection(string Title, IReadOnlyList<NativeReleaseNoteItem> Items);

/// <summary>
/// Settings > About > What's New (source Settings.razor ShowReleaseNotes): the release notes
/// in docs/release-notes, as sections of lines a native list shows (no markdown rendering).
/// </summary>
internal sealed partial record NativeReleaseNotes(
    string Version, string Date, string Title, IReadOnlyList<NativeReleaseNoteSection> Sections)
{
    /// <summary>
    /// The notes for the app's version, or the newest (the shared rule,
    /// <see cref="ReleaseNotesService.GetNotesForAppVersionAsync"/>). Null when there are none.
    /// </summary>
    internal static async Task<NativeReleaseNotes?> ForAppVersionAsync(
        ReleaseNotesService service, string? appVersion) =>
        await service.GetNotesForAppVersionAsync(appVersion) is { } note ? From(note) : null;

    internal static NativeReleaseNotes From(ReleaseNote note)
    {
        var sections = new List<NativeReleaseNoteSection>();
        var title = note.Title;
        string? sectionTitle = null;
        var items = new List<NativeReleaseNoteItem>();
        void Flush()
        {
            if (items.Count > 0)
                sections.Add(new(sectionTitle ?? string.Empty, items.ToArray()));
            items = [];
        }
        foreach (var raw in note.MarkdownContent.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith("###", StringComparison.Ordinal))
            {
                // ### and deeper headings start a section.
                Flush();
                sectionTitle = Plain(line.TrimStart('#').Trim());
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal)
                || line.StartsWith("# ", StringComparison.Ordinal))
            {
                // The page title; the front matter's title is used when it has one.
                if (string.IsNullOrWhiteSpace(title))
                    title = Plain(line.TrimStart('#').Trim());
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal)
                || line.StartsWith("* ", StringComparison.Ordinal))
                items.Add(Item(line[2..].Trim()));
            else if (NumberedItem().Match(line) is { Success: true } numbered)
                items.Add(Item(numbered.Groups[1].Value.Trim()));
            else
                items.Add(new(null, Plain(line)));
        }
        Flush();
        return new(note.Version, note.Date, title, sections);
    }

    /// <summary>
    /// "**Headline** — text" (or "**Headline:** text") keeps the headline apart; anything else
    /// is one plain text.
    /// </summary>
    private static NativeReleaseNoteItem Item(string text)
    {
        var match = LeadingHeadline().Match(text);
        if (!match.Success)
            match = ColonHeadline().Match(text);
        return match.Success
            ? new(Plain(match.Groups[1].Value), Plain(match.Groups[2].Value))
            : new(null, Plain(text));
    }

    /// <summary>The text without markdown emphasis or link targets.</summary>
    private static string Plain(string text)
    {
        text = Link().Replace(text, "$1");
        return text.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal).Trim();
    }

    [GeneratedRegex(@"^\*\*(.+?)\*\*\s*[—–:-]\s*(.+)$")]
    private static partial Regex LeadingHeadline();

    [GeneratedRegex(@"^\*\*(.+?):\*\*\s*(.+)$")]
    private static partial Regex ColonHeadline();

    [GeneratedRegex(@"^\d+[.)]\s+(.+)$")]
    private static partial Regex NumberedItem();

    [GeneratedRegex(@"\[(.+?)\]\((.+?)\)")]
    private static partial Regex Link();
}
