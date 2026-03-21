using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SentenceStudio.Services;

public class ReleaseNote
{
    public string Version { get; set; } = "";
    public string Date { get; set; } = "";
    public string Title { get; set; } = "";
    public string MarkdownContent { get; set; } = "";
}

public class ReleaseNotesService
{
    private List<ReleaseNote>? _cachedNotes;

    public async Task<ReleaseNote?> GetNotesForVersionAsync(string version)
    {
        var allNotes = await GetAllNotesAsync();
        return allNotes.FirstOrDefault(n => n.Version == version);
    }

    public async Task<ReleaseNote?> GetLatestNotesAsync()
    {
        var allNotes = await GetAllNotesAsync();
        return allNotes.FirstOrDefault();
    }

    public async Task<List<ReleaseNote>> GetAllNotesAsync()
    {
        if (_cachedNotes != null)
            return _cachedNotes;

        var notes = new List<ReleaseNote>();
        var assembly = typeof(ReleaseNotesService).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("ReleaseNotes") && n.EndsWith(".md"))
            .ToList();

        foreach (var resourceName in resourceNames)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;

                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync();
                var note = ParseReleaseNote(content);
                if (note != null)
                    notes.Add(note);
            }
            catch
            {
                // Skip unreadable resources
            }
        }

        notes = notes.OrderByDescending(n => n.Version).ToList();
        _cachedNotes = notes;
        return notes;
    }

    private ReleaseNote? ParseReleaseNote(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var frontmatterRegex = new Regex(@"^---\s*\n(.*?)\n---\s*\n(.*)$", RegexOptions.Singleline);
        var match = frontmatterRegex.Match(content);
        if (!match.Success)
            return null;

        var frontmatter = match.Groups[1].Value;
        var markdown = match.Groups[2].Value.Trim();

        var note = new ReleaseNote { MarkdownContent = markdown };

        var versionMatch = Regex.Match(frontmatter, @"version:\s*[""']?([^""'\n]+)[""']?");
        if (versionMatch.Success)
            note.Version = versionMatch.Groups[1].Value.Trim();

        var dateMatch = Regex.Match(frontmatter, @"date:\s*[""']?([^""'\n]+)[""']?");
        if (dateMatch.Success)
            note.Date = dateMatch.Groups[1].Value.Trim();

        var titleMatch = Regex.Match(frontmatter, @"title:\s*[""']?([^""'\n]+)[""']?");
        if (titleMatch.Success)
            note.Title = titleMatch.Groups[1].Value.Trim();

        return note;
    }
}
