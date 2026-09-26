using System.ComponentModel;

namespace SentenceStudio.Contracts.Coach.Intent;

/// <summary>A model-routed topic. The application prepares the set, assigns identity, and owns approval.</summary>
public sealed class CoachVocabularySetIntent
{
    [Description("The short topic explicitly named for the direct vocabulary review, such as 'food'. Do not generate or list terms; the server does that.")]
    public string Topic { get; set; } = string.Empty;
}
