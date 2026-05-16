using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// A freeform daily diary entry written by the user in their target language.
/// One entry per (UserProfileId, EntryDate, Language). Today's entry is editable
/// all day; prior days are read-only. AI feedback is opt-in (on-demand) and not
/// used for mastery scoring.
/// </summary>
[Table("DiaryEntry")]
public class DiaryEntry
{
    public string Id { get; set; } = string.Empty;
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Calendar date the entry belongs to (user-local). Stored as a DateTime at
    /// midnight UTC for portability across PostgreSQL and SQLite providers.
    /// </summary>
    public DateTime EntryDate { get; set; }

    /// <summary>
    /// Target language the entry is written in (e.g. "Korean"). Sourced from the
    /// active user profile at creation time.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// AI-generated writing prompt shown to the user when they started the entry,
    /// in the target language. Cached per day; the user can refresh it.
    /// </summary>
    public string? PromptText { get; set; }

    /// <summary>
    /// Brief native-language hint accompanying the prompt (optional).
    /// </summary>
    public string? PromptHint { get; set; }

    /// <summary>
    /// The diary content as the user wrote it.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Cached word count for fast list rendering; recomputed on each save.
    /// </summary>
    public int WordCount { get; set; }

    /// <summary>
    /// User-chosen length goal (words) for this entry. Defaults to 200.
    /// </summary>
    public int WordGoal { get; set; } = 200;

    /// <summary>
    /// AI feedback: recommended rewrite of the entry in the target language.
    /// Populated when the user taps "Get Feedback" on the viewer page.
    /// </summary>
    public string? FeedbackRecommended { get; set; }

    /// <summary>
    /// AI feedback: grammar and style notes for the entry.
    /// </summary>
    public string? FeedbackNotes { get; set; }

    /// <summary>
    /// AI feedback: positive observations / strengths.
    /// </summary>
    public string? FeedbackStrengths { get; set; }

    /// <summary>
    /// When feedback was generated. Null if the user has not requested feedback.
    /// </summary>
    public DateTime? FeedbackAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [NotMapped]
    public bool HasFeedback => FeedbackAt.HasValue;
}
