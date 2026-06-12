using System.ComponentModel;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// AI response for the Diary daily writing prompt generator.
/// Lives in <c>SentenceStudio.Shared.Models</c> so the API project can register it
/// in <c>AiResponseTypeRegistry</c> for structured-output deserialization through
/// the AI gateway.
/// </summary>
public class DiaryPromptResponse
{
    [Description("A short, open-ended diary writing prompt in the learner's target language. One or two sentences, max ~20 target-language words.")]
    public string Prompt { get; set; } = string.Empty;

    [Description("A brief one-line hint in the learner's native language explaining what the prompt is asking, for lower-level learners.")]
    public string Hint { get; set; } = string.Empty;
}

/// <summary>
/// AI feedback response for a learner's diary entry: a recommended rewrite plus
/// short notes and strengths in the learner's native language.
/// </summary>
public class DiaryFeedbackResponse
{
    [Description("The learner's diary entry rewritten in clear, natural target-language prose. Preserves the learner's meaning and voice. Do not add content the learner did not express.")]
    public string Recommended { get; set; } = string.Empty;

    [Description("Grammar, vocabulary, and style observations written in the learner's native language. 2-4 short bullet-style sentences focused on the most useful corrections.")]
    public string Notes { get; set; } = string.Empty;

    [Description("1-2 short sentences in the learner's native language pointing out what the learner did well. Be specific and genuine.")]
    public string Strengths { get; set; } = string.Empty;
}
