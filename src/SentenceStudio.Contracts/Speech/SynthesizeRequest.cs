namespace SentenceStudio.Contracts.Speech;

public sealed class SynthesizeRequest
{
    public string Text { get; set; } = string.Empty;
    public string? VoiceId { get; set; }
}
