using System.ComponentModel;

namespace SentenceStudio.Services.DTOs;

/// <summary>
/// The AI's answer for a batch of duplicate groups (docs/duplicate-merge-ai-plan.md). Checked by
/// <see cref="VocabularyDuplicateResolver.Interpret"/> before anything acts on it.
/// </summary>
public class DuplicateResolutionResponse
{
    [Description("One decision for every group in the request")]
    public List<DuplicateGroupDecision> Groups { get; set; } = new();
}

public class DuplicateGroupDecision
{
    [Description("The group's id, copied unchanged from the request")]
    public string GroupId { get; set; } = string.Empty;

    [Description("SameWord when every copy means the same thing; DifferentWords when every copy means something different (a homonym or another sense); Mixed when some copies mean the same thing and others do not")]
    public string Verdict { get; set; } = string.Empty;

    [Description("The copies grouped by meaning: each set holds the ids of the copies that mean the same thing. Every copy id of the group appears in exactly one set")]
    public List<DuplicateWordSet> Sets { get; set; } = new();

    [Description("How sure you are, from 0 to 1")]
    public double Confidence { get; set; }

    [Description("Why, in at most 20 words")]
    public string Reason { get; set; } = string.Empty;
}

public class DuplicateWordSet
{
    [Description("The ids of the copies in this set, copied unchanged from the request")]
    public List<string> CopyIds { get; set; } = new();

    [Description("The meaning to keep for this set, in the learner's native language: a short dictionary-style gloss")]
    public string Meaning { get; set; } = string.Empty;

    [Description("The learner's target language when the term is clearly in it and a copy's language is Unknown, empty or wrong; otherwise empty")]
    public string? Language { get; set; }
}
