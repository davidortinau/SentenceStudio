using System;
using System.IO;
using System.Threading.Tasks;

namespace SentenceStudio.Services;

/// <summary>
/// Service for analyzing audio streams and extracting waveform data for visualization
/// </summary>
public class AudioAnalyzer
{
    private const int SAMPLE_COUNT = 100; // Number of samples for the waveform display

    /// <summary>
    /// Analyzes an audio stream and extracts waveform data
    /// </summary>
    /// <param name="audioStream">The audio stream to analyze</param>
    /// <param name="token">Cancellation token for async operations</param>
    /// <returns>Array of float values representing the audio waveform</returns>
    public async Task<float[]> AnalyzeAudioStreamAsync(Stream audioStream, CancellationToken token = default)
    {
        if (audioStream == null || !audioStream.CanRead)
            return new float[SAMPLE_COUNT]; // Return empty array if stream is invalid

        token.ThrowIfCancellationRequested();
        
        try
        {
            // Save original position to restore later
            var originalPosition = audioStream.Position;
            audioStream.Position = 0;

            // For WAV format (assuming 16-bit PCM format which is common for speech)
            // Skip WAV header (44 bytes for standard WAV)
            const int WAV_HEADER_SIZE = 44;
            if (audioStream.Length <= WAV_HEADER_SIZE)
                return GenerateFallbackWaveform();
                
            audioStream.Position = WAV_HEADER_SIZE;

            // Calculate how many bytes to skip between samples to get SAMPLE_COUNT samples
            long dataSize = audioStream.Length - WAV_HEADER_SIZE;
            long bytesToSkip = dataSize / SAMPLE_COUNT;
            
            if (bytesToSkip < 2) // Need at least 2 bytes for a 16-bit sample
            {
                // Not enough data, generate fallback
                return GenerateFallbackWaveform();
            }

            // Extract samples
            float[] samples = new float[SAMPLE_COUNT];
            byte[] buffer = new byte[2]; // 16-bit sample = 2 bytes
            
            for (int i = 0; i < SAMPLE_COUNT && audioStream.Position < audioStream.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                
                int bytesRead = await audioStream.ReadAsync(buffer, 0, 2, token);
                if (bytesRead == 2)
                {
                    // Convert 2 bytes to 16-bit signed sample and normalize to -1.0...1.0
                    short sample = (short)((buffer[1] << 8) | buffer[0]);
                    samples[i] = sample / 32768f; // Normalize to -1.0...1.0
                }

                // Skip bytes to next sample position
                if (bytesToSkip > 2)
                {
                    audioStream.Seek(bytesToSkip - 2, SeekOrigin.Current);
                }
            }

            // Restore original position
            audioStream.Position = originalPosition;
            
            // Take absolute values for waveform display
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = Math.Abs(samples[i]);
            }
            
            return samples;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error analyzing audio: {ex.Message}");
            return GenerateFallbackWaveform();
        }
    }

    /// <summary>
    /// Generates a fallback waveform when analysis fails
    /// </summary>
    private float[] GenerateFallbackWaveform()
    {
        // Create a gentle wave pattern rather than random noise
        var samples = new float[SAMPLE_COUNT];
        for (int i = 0; i < SAMPLE_COUNT; i++)
        {
            double phase = (double)i / SAMPLE_COUNT * Math.PI * 4;
            samples[i] = (float)(Math.Sin(phase) * 0.5f + 0.5f);
        }
        return samples;
    }
}