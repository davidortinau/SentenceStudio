using System.ComponentModel;

namespace SentenceStudio.Contracts.Coach.Intent;

/// <summary>
/// One run of text the model produced, tagged with which of the learner's languages it is in.
/// </summary>
/// <remarks>
/// The model chooses a <b>role</b>, never a locale. The server maps the role onto the learner's
/// actual BCP-47 tag, so an invented or mismatched locale cannot reach a client that uses it to
/// pick a script, a font, or a speech voice.
/// </remarks>
[Description("One piece of text in one language.")]
public sealed class CoachAnswerSpanIntent
{
    [Description("The text. Plain words only, no markup and no links. The largest length is 320 characters.")]
    public string Text { get; set; } = string.Empty;

    [Description("Which language the text is in. Use Target for the language the learner studies, Display for your explanation, and Native for the learner's first language.")]
    public CoachLanguageRole Language { get; set; }
}

/// <summary>One labelled part of an answer.</summary>
[Description("One part of the answer, for example the direct answer, an example, or a contrast.")]
public sealed class CoachAnswerBlockIntent
{
    [Description("What this part is. Put the direct answer first, and use it only once.")]
    public CoachAnswerBlockKind Kind { get; set; }

    [Description("An optional short heading in the display language. The largest length is 60 characters.")]
    public string? Label { get; set; }

    [Description("The text of this part, in order. Use between one and six pieces.")]
    public List<CoachAnswerSpanIntent> Spans { get; set; } = new();
}

/// <summary>
/// The answer to a language-learning question. Set only for
/// <see cref="CoachIntentKind.PedagogicalAnswer"/> and for a mixed turn that both answers and
/// proposes a plan change.
/// </summary>
[Description("An answer to a language question. Set it only when the learner asked one.")]
public sealed class CoachPedagogicalAnswerIntent
{
    [Description("What the question is about.")]
    public CoachAnswerTopic Topic { get; set; }

    [Description("The answer in order. Use between one and eight parts. Start with the direct answer. Add at most one retrieval prompt, and put it last. The combined text across all parts must not exceed 1600 characters.")]
    public List<CoachAnswerBlockIntent> Blocks { get; set; } = new();
}
