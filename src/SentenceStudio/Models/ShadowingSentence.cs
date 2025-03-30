using System.ComponentModel;

namespace SentenceStudio.Models;

public class ShadowingSentence
{
    public string TargetLanguageText { get; set; }

    public string NativeLanguageText { get; set; }

    [Description("Pronunciation notes for the target language text")]
    public string PronunciationNotes { get; set; }
}

public class ShadowingSentencesResponse
{
    public List<ShadowingSentence> Sentences { get; set; } = new();
}
