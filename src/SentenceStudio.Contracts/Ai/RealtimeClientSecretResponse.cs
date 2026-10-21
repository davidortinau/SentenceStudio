namespace SentenceStudio.Contracts.Ai;

/// <summary>
/// A short-lived credential a device uses to open one realtime voice session directly with the
/// model provider, so no long-lived key or Entra identity ever reaches the device.
/// </summary>
public sealed class RealtimeClientSecretResponse
{
    /// <summary>The ephemeral client secret. Valid only to open a session before <see cref="ExpiresAt"/>.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>When the secret stops being accepted for new sessions.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The OpenAI-compatible endpoint the session connects to (for Azure, the <c>/openai/v1</c> surface).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The realtime model or deployment the secret was minted for.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>The input transcription model or deployment.</summary>
    public string TranscriptionModel { get; set; } = string.Empty;
}
