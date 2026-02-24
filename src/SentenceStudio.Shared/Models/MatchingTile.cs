using SentenceStudio.Shared.Models;

namespace SentenceStudio.Pages.VocabularyMatching;

/// <summary>
/// Tile model for the vocabulary matching game (used by both old MauiReactor page and Blazor page).
/// </summary>
public class MatchingTile
{
    public int Id { get; set; }
    public string Text { get; set; }
    public string Language { get; set; } // "native" or "target"
    public int VocabularyWordId { get; set; }
    public bool IsSelected { get; set; }
    public bool IsMatched { get; set; }
    public bool IsVisible { get; set; } = true;

    // Enhanced progress tracking
    public SentenceStudio.Shared.Models.VocabularyProgress? Progress { get; set; }
    public VocabularyWord? Word { get; set; }

    // Enhanced computed properties
    public bool IsKnown => Progress?.IsKnown ?? false;
    public float MasteryProgress => Progress?.MasteryScore ?? 0f;
}

public class WordWithProgress
{
    public VocabularyWord Word { get; set; }
    public SentenceStudio.Shared.Models.VocabularyProgress Progress { get; set; }
}
