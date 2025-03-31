using System;
using System.IO;
using System.Threading.Tasks;

namespace SentenceStudio.Services;

/// <summary>
/// Service for analyzing audio streams and extracting waveform data for visualization
/// </summary>
public class AudioAnalyzer
{
    private const int SAMPLE_COUNT = 400; // Number of samples for the waveform display
    private const int BYTES_PER_SAMPLE = 2; // 16-bit PCM = 2 bytes per sample
    private const int WAV_HEADER_SIZE = 44; // Standard WAV header size

    /// <summary>
    /// Analyzes an audio stream and extracts waveform data that accurately represents the entire audio,
    /// including quiet sections and peaks
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
            
            // Ensure we start from the beginning to read the entire stream
            audioStream.Position = 0;

            // For WAV format (assuming 16-bit PCM format which is common for speech)
            // Skip WAV header (44 bytes for standard WAV)
            if (audioStream.Length <= WAV_HEADER_SIZE)
                return GenerateFallbackWaveform();
                
            // Calculate how much audio data we have
            long dataSize = audioStream.Length - WAV_HEADER_SIZE;
            
            // Calculate stride to evenly sample the entire audio file
            long totalSamples = dataSize / BYTES_PER_SAMPLE;
            double samplesPerSegment = totalSamples / (double)SAMPLE_COUNT;
            
            if (samplesPerSegment < 1)
            {
                // If we have fewer samples than our target count, we need a different approach
                return await AnalyzeSparseAudioAsync(audioStream, token);
            }
            
            // Move to start of audio data
            audioStream.Position = WAV_HEADER_SIZE;
            
            // Initialize result array
            float[] samples = new float[SAMPLE_COUNT];
            byte[] buffer = new byte[BYTES_PER_SAMPLE];
            
            // Process each segment
            for (int i = 0; i < SAMPLE_COUNT; i++)
            {
                token.ThrowIfCancellationRequested();
                
                // Find the peak amplitude in this segment
                float segmentPeak = 0;
                
                // Calculate segment boundaries
                long segmentStart = (long)(i * samplesPerSegment);
                long segmentEnd = (long)((i + 1) * samplesPerSegment);
                
                // Position stream at the beginning of this segment
                long bytePosition = WAV_HEADER_SIZE + (segmentStart * BYTES_PER_SAMPLE);
                audioStream.Position = bytePosition;
                
                // Sample through this segment to find the peak
                for (long j = segmentStart; j < segmentEnd && audioStream.Position < audioStream.Length - 1; j++)
                {
                    // Read a single 16-bit sample
                    if (await audioStream.ReadAsync(buffer, 0, BYTES_PER_SAMPLE, token) == BYTES_PER_SAMPLE)
                    {
                        // Convert bytes to 16-bit signed PCM sample
                        short pcmSample = (short)((buffer[1] << 8) | buffer[0]);
                        
                        // Convert to normalized float and get absolute value
                        float amplitude = Math.Abs(pcmSample / 32768f);
                        
                        // Keep track of the peak amplitude in this segment
                        segmentPeak = Math.Max(segmentPeak, amplitude);
                    }
                }
                
                // Store the peak amplitude for this segment
                samples[i] = segmentPeak;
            }
            
            // Restore original position
            audioStream.Position = originalPosition;
            
            return samples;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error analyzing audio: {ex.Message}");
            return GenerateFallbackWaveform();
        }
    }
    
    /// <summary>
    /// Analyzes a sparse audio stream where there are fewer samples than our target sample count
    /// </summary>
    private async Task<float[]> AnalyzeSparseAudioAsync(Stream audioStream, CancellationToken token = default)
    {
        try
        {
            // Save original position
            var originalPosition = audioStream.Position;
            
            // Move to start of audio data
            audioStream.Position = WAV_HEADER_SIZE;
            
            // Calculate how many samples we actually have
            long dataSize = audioStream.Length - WAV_HEADER_SIZE;
            int actualSampleCount = (int)(dataSize / BYTES_PER_SAMPLE);
            
            // Prepare result array
            float[] samples = new float[SAMPLE_COUNT];
            byte[] buffer = new byte[BYTES_PER_SAMPLE];
            
            // Read all actual samples
            float[] actualSamples = new float[actualSampleCount];
            for (int i = 0; i < actualSampleCount; i++)
            {
                if (await audioStream.ReadAsync(buffer, 0, BYTES_PER_SAMPLE, token) == BYTES_PER_SAMPLE)
                {
                    short pcmSample = (short)((buffer[1] << 8) | buffer[0]);
                    actualSamples[i] = Math.Abs(pcmSample / 32768f);
                }
            }
            
            // Interpolate to get our target sample count
            for (int i = 0; i < SAMPLE_COUNT; i++)
            {
                double position = i * (actualSampleCount - 1) / (double)(SAMPLE_COUNT - 1);
                int index = (int)position;
                double fraction = position - index;
                
                if (index < actualSampleCount - 1)
                {
                    samples[i] = (float)(actualSamples[index] * (1 - fraction) + actualSamples[index + 1] * fraction);
                }
                else if (index < actualSampleCount)
                {
                    samples[i] = actualSamples[index];
                }
            }
            
            // Restore original position
            audioStream.Position = originalPosition;
            
            return samples;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error analyzing sparse audio: {ex.Message}");
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