namespace SentenceStudio.Services;

/// <summary>
/// Abstraction for AI-powered services including text generation, image analysis, and text-to-speech synthesis.
/// Supports both direct OpenAI API calls and gateway-based routing for Aspire-hosted scenarios.
/// </summary>
public interface IAiService
{
    /// <summary>
    /// Sends a text prompt to the AI model and deserializes the response to the specified type.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the AI response into. Must be compatible with JSON deserialization.</typeparam>
    /// <param name="prompt">The text prompt to send to the AI model. Should be well-formed and provide sufficient context for the model to generate a valid response of type T.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the deserialized response of type T.
    /// Returns default(T) if no internet connection is available or if an error occurs during processing.
    /// </returns>
    /// <remarks>
    /// This method routes requests through IAiGatewayClient when available (Aspire mode), otherwise uses the direct IChatClient.
    /// Checks internet connectivity before making requests and sends a ConnectivityChangedMessage(false) if offline.
    /// Logs warnings and errors for diagnostic purposes.
    /// </remarks>
    Task<T> SendPrompt<T>(string prompt);

    /// <summary>
    /// Sends an image along with a text prompt to the AI model for image analysis and returns the model's text response.
    /// </summary>
    /// <param name="imagePath">
    /// The path to the image file. Accepts:
    /// - HTTP/HTTPS URLs (will be downloaded and converted to base64 data URI)
    /// - Data URIs (data:image/png;base64,...)
    /// - Local file paths (treated as data URIs with image/jpeg media type)
    /// </param>
    /// <param name="prompt">
    /// The text prompt to send alongside the image. Should describe what information or analysis is requested about the image.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the AI model's text response analyzing the image.
    /// Returns an empty string if no internet connection is available or if an error occurs during processing.
    /// </returns>
    /// <remarks>
    /// This method constructs a ChatMessage with both text and image content, then sends it to the AI model.
    /// HTTP URLs are downloaded and converted to base64 data URIs to comply with the DataContent requirements.
    /// Media type is detected from file extension (.png → image/png, otherwise → image/jpeg).
    /// Checks internet connectivity before making requests and sends a ConnectivityChangedMessage(false) if offline.
    /// Logs errors with the image path for diagnostic purposes.
    /// </remarks>
    Task<string> SendImage(string imagePath, string prompt);

    /// <summary>
    /// Synthesizes speech from text using text-to-speech (TTS) services and returns the audio stream.
    /// </summary>
    /// <param name="text">The text to synthesize into speech. Should be well-formed for natural-sounding output.</param>
    /// <param name="voice">
    /// The voice identifier to use for synthesis. Valid values depend on the TTS provider (e.g., "alloy", "echo", "fable", "onyx", "nova", "shimmer" for OpenAI).
    /// </param>
    /// <param name="speed">
    /// The playback speed for the synthesized speech. Valid range is typically 0.25 to 4.0.
    /// Default is 1.0 (normal speed). Values less than 1.0 slow down speech, values greater than 1.0 speed it up.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a Stream of the synthesized audio data.
    /// Returns default(Stream) if no internet connection is available.
    /// </returns>
    /// <remarks>
    /// This method routes requests through ISpeechGatewayClient when available (Aspire mode), otherwise falls back to AIClient with direct OpenAI API calls.
    /// Checks internet connectivity before making requests and sends a ConnectivityChangedMessage(false) if offline.
    /// The returned Stream should be disposed by the caller after use.
    /// </remarks>
    Task<Stream> TextToSpeechAsync(string text, string voice, float speed = 1.0f);
}
